using System.IO.Compression;

namespace J2meEmu;

class JvmClassLoader
{
    readonly Dictionary<string, JavaClass> _classes = new();
    readonly Dictionary<string, ClassFile> _classFiles = new();
    readonly Dictionary<string, byte[]> _resources = new();
    public MidletHost? Host;
    public string? MidletClassName;
    public Dictionary<string, string> ManifestProps = new();

    public readonly HashSet<string> MissingClasses = new();
    public readonly HashSet<string> MissingMethods = new();
    public int ClassesLoaded;
    public int MethodsInvoked;

    public void LoadJar(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(ms);
                var data = ms.ToArray();
                string className = entry.FullName[..^6];
                try
                {
                    var cf = ClassFile.Parse(data);
                    _classFiles[cf.ThisClass] = cf;
                    Log.Cls($"Loaded class: {cf.ThisClass}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to parse {className}: {ex.Message}");
                }
            }
            else
            {
                using var ms = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(ms);
                string resName = entry.FullName.StartsWith("/") ? entry.FullName : "/" + entry.FullName;
                _resources[resName] = ms.ToArray();

                if (entry.FullName is "META-INF/MANIFEST.MF" or "META-INF/manifest.mf")
                    ParseManifest(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            }
        }

        if (ManifestProps.TryGetValue("MIDlet-1", out var midlet1))
        {
            var parts = midlet1.Split(',');
            if (parts.Length >= 3)
                MidletClassName = parts[2].Trim().Replace('.', '/');
            else if (parts.Length >= 1)
                MidletClassName = parts[0].Trim().Replace('.', '/');
        }
    }

    void ParseManifest(string text)
    {
        string? lastKey = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith(' ') && lastKey != null)
            {
                ManifestProps[lastKey] += line[1..];
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();
            ManifestProps[key] = val;
            lastKey = key;
        }
    }

    public byte[]? GetResource(string path)
    {
        if (_resources.TryGetValue(path, out var data)) return data;
        string alt = path.StartsWith("/") ? path[1..] : "/" + path;
        return _resources.GetValueOrDefault(alt);
    }

    public JavaClass LoadClass(string name)
    {
        if (_classes.TryGetValue(name, out var existing)) return existing;

        if (name.StartsWith("["))
            return GetOrCreateArrayClass(name);

        var cls = new JavaClass(name);
        _classes[name] = cls;

        if (_classFiles.TryGetValue(name, out var cf))
        {
            cls.File = cf;
            cls.Flags = cf.AccessFlags;
            if (cf.SuperClass != null)
                cls.Super = LoadClass(cf.SuperClass);
            cls.Interfaces = new JavaClass[cf.Interfaces.Length];
            for (int i = 0; i < cf.Interfaces.Length; i++)
                cls.Interfaces[i] = LoadClass(cf.Interfaces[i]);
            LinkClass(cls, cf);
            ClassesLoaded++;
        }
        else
        {
            var native = NativeRegistry.TryGetNativeClass(name, this);
            if (native != null)
            {
                _classes[name] = native;
                return native;
            }

            MissingClasses.Add(name);
            Log.Error($"Missing class: {name}");
            if (name != "java/lang/Object")
                cls.Super = LoadClass("java/lang/Object");
        }

        return cls;
    }

    JavaClass GetOrCreateArrayClass(string name)
    {
        var cls = new JavaClass(name);
        cls.Super = LoadClass("java/lang/Object");
        _classes[name] = cls;
        return cls;
    }

    void LinkClass(JavaClass cls, ClassFile cf)
    {
        int instanceIdx = cls.Super?.InstanceFieldCount ?? 0;
        int staticIdx = 0;

        foreach (var fi in cf.Fields)
        {
            bool isStatic = (fi.Flags & FieldAccessFlags.Static) != 0;
            int idx = isStatic ? staticIdx++ : instanceIdx++;
            var field = new JavaField(fi.Name, fi.Descriptor, cls, idx) { Flags = fi.Flags };
            cls.Fields[$"{fi.Name}:{fi.Descriptor}"] = field;
            cls.Fields.TryAdd(fi.Name, field);
            if (isStatic) cls.StaticFieldList.Add(field);
            else cls.InstanceFieldList.Add(field);
        }

        cls.InstanceFieldCount = instanceIdx;
        cls.StaticFields = new JValue[staticIdx];


        foreach (var fi in cf.Fields)
        {
            if ((fi.Flags & FieldAccessFlags.Static) != 0 && fi.ConstantValue != null)
            {
                var field = cls.Fields[fi.Name];
                cls.StaticFields[field.Index] = fi.ConstantValue switch
                {
                    int iv => JValue.OfInt(iv),
                    long lv => JValue.OfLong(lv),
                    float fv => JValue.OfFloat(fv),
                    double dv => JValue.OfDouble(dv),
                    string sv => JValue.OfRef(CreateString(sv)),
                    _ => JValue.Null
                };
            }
        }

        foreach (var mi in cf.Methods)
        {
            string key = $"{mi.Name}{mi.Descriptor}";
            var method = new JavaMethod(mi.Name, mi.Descriptor, cls)
            {
                Flags = mi.Flags,
                Code = mi.Code,
                MaxStack = mi.MaxStack,
                MaxLocals = mi.MaxLocals,
                ExceptionTable = mi.ExceptionTable
            };

            var native = NativeRegistry.TryGetNativeMethod(cls.Name, mi.Name, mi.Descriptor);
            if (native != null)
                method.NativeHandler = native;

            cls.Methods[key] = method;
        }

        NativeRegistry.InjectMissingNatives(cls, this);
    }

    public void InitializeClass(JavaClass cls, JvmThread thread)
    {
        if (cls.Initialized) return;
        cls.Initialized = true;

        if (cls.Super != null)
            InitializeClass(cls.Super, thread);

        var clinit = cls.FindMethod("<clinit>", "()V");
        if (clinit != null)
        {
            try { thread.Invoke(clinit, Array.Empty<JValue>()); }
            catch (Exception ex) { Log.Error($"<clinit> failed for {cls.Name}: {ex.Message}"); }
        }
    }

    public JavaObject CreateString(string value)
    {
        var cls = LoadClass("java/lang/String");
        return new JavaObject(cls, value);
    }

    public JavaObject CreateException(string className, string message)
    {
        var cls = LoadClass(className);
        var obj = new JavaObject(cls, message);
        return obj;
    }

    public JavaArray CreateArray(JavaArray.ArrayKind kind, int length)
    {
        string name = kind switch
        {
            JavaArray.ArrayKind.Int => "[I",
            JavaArray.ArrayKind.Long => "[J",
            JavaArray.ArrayKind.Float => "[F",
            JavaArray.ArrayKind.Double => "[D",
            JavaArray.ArrayKind.Byte => "[B",
            JavaArray.ArrayKind.Boolean => "[Z",
            JavaArray.ArrayKind.Char => "[C",
            JavaArray.ArrayKind.Short => "[S",
            _ => "[Ljava/lang/Object;"
        };
        var cls = LoadClass(name);
        return new JavaArray(cls, kind, length);
    }

    public JavaArray CreateRefArray(string elementClass, int length)
    {
        var cls = LoadClass($"[L{elementClass};");
        return new JavaArray(cls, JavaArray.ArrayKind.Ref, length);
    }

    public string? GetStringValue(JavaObject? obj)
    {
        if (obj == null) return null;
        return obj.NativeData as string;
    }

    public int GetStringValueInt(JavaObject? obj) =>
        int.TryParse(GetStringValue(obj), out int v) ? v : 0;
}
