using System.Text;

namespace J2meEmu;

static class CldcNatives
{
    static readonly Dictionary<Stream, long> _markPositions = new();

    public static void Register()
    {
        var R = NativeRegistry.RegisterMethod;

        // java.lang.Object
        R("java/lang/Object", "<init>", "()V", (_, _) => JValue.Null);
        R("java/lang/Object", "getClass", "()Ljava/lang/Class;", (t, a) =>
        {
            var obj = a[0].Ref as JavaObject;
            return JValue.OfRef(obj != null ? new JavaObject(t.Loader.LoadClass("java/lang/Class"), obj.Class.Name) : null);
        });
        R("java/lang/Object", "hashCode", "()I", (_, a) =>
            JValue.OfInt(a[0].Ref?.GetHashCode() ?? 0));
        R("java/lang/Object", "equals", "(Ljava/lang/Object;)Z", (_, a) =>
            JValue.OfInt(a[0].Ref == a[1].Ref ? 1 : 0));
        R("java/lang/Object", "toString", "()Ljava/lang/String;", (t, a) =>
        {
            var obj = a[0].Ref as JavaObject;
            string s = obj?.NativeData as string ?? obj?.Class.Name ?? "null";
            return JValue.OfRef(t.Loader.CreateString(s));
        });
        R("java/lang/Object", "notify", "()V", (_, _) => JValue.Null);
        R("java/lang/Object", "notifyAll", "()V", (_, _) => JValue.Null);
        R("java/lang/Object", "wait", "()V", (_, _) => { Thread.Sleep(1); return JValue.Null; });
        R("java/lang/Object", "wait", "(J)V", (_, a) => { Thread.Sleep((int)Math.Clamp(a[1].Long, 1, 10000)); return JValue.Null; });

        // java.lang.Class
        R("java/lang/Class", "forName", "(Ljava/lang/String;)Ljava/lang/Class;", (t, a) =>
        {
            string? name = Str(a[0]);
            if (name == null) return JValue.Null;
            name = name.Replace('.', '/');
            var cls = t.Loader.LoadClass(name);
            return JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/lang/Class"), cls.Name));
        });
        R("java/lang/Class", "getName", "()Ljava/lang/String;", (t, a) =>
        {
            var obj = a[0].Ref as JavaObject;
            string name = (obj?.NativeData as string ?? "").Replace('/', '.');
            return JValue.OfRef(t.Loader.CreateString(name));
        });
        R("java/lang/Class", "newInstance", "()Ljava/lang/Object;", (t, a) =>
        {
            var obj = a[0].Ref as JavaObject;
            string? name = obj?.NativeData as string;
            if (name == null) return JValue.Null;
            var cls = t.Loader.LoadClass(name);
            t.Loader.InitializeClass(cls, t);
            var inst = new JavaObject(cls);
            var init = cls.FindMethod("<init>", "()V");
            if (init != null) t.Invoke(init, new[] { JValue.OfRef(inst) });
            return JValue.OfRef(inst);
        });
        R("java/lang/Class", "getResourceAsStream", "(Ljava/lang/String;)Ljava/io/InputStream;", (t, a) =>
        {
            string? path = Str(a[1]);
            if (path == null) return JValue.Null;
            var data = t.Loader.GetResource(path);
            if (data == null) return JValue.Null;
            var isCls = t.Loader.LoadClass("java/io/ByteArrayInputStream");
            var stream = new JavaObject(isCls, new MemoryStream(data));
            return JValue.OfRef(stream);
        });

        // java.lang.String
        R("java/lang/String", "<init>", "()V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData ??= "";
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "(Ljava/lang/String;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = Str(a[1]) ?? "";
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "([B)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
                obj.NativeData = Encoding.UTF8.GetString(arr.ByteData, 0, arr.Length);
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "([BII)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
            {
                int off = Math.Max(0, a[2].Int);
                int len = Math.Max(0, Math.Min(a[3].Int, arr.ByteData.Length - off));
                obj.NativeData = len > 0 ? Encoding.UTF8.GetString(arr.ByteData, off, len) : "";
            }
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "([BIII)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
            {
                int off = Math.Max(0, a[3].Int);
                int len = Math.Max(0, Math.Min(a[4].Int, arr.ByteData.Length - off));
                obj.NativeData = len > 0 ? Encoding.UTF8.GetString(arr.ByteData, off, len) : "";
            }
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "([BLjava/lang/String;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
                obj.NativeData = Encoding.UTF8.GetString(arr.ByteData, 0, arr.Length);
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "([BIILjava/lang/String;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
            {
                int off = Math.Max(0, a[2].Int);
                int len = Math.Max(0, Math.Min(a[3].Int, arr.ByteData.Length - off));
                obj.NativeData = Encoding.UTF8.GetString(arr.ByteData, off, len);
            }
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "(Ljava/lang/StringBuffer;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj)
                obj.NativeData = Str(a[1]) ?? ((a[1].Ref as JavaObject)?.NativeData as StringBuilder)?.ToString() ?? "";
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "([C)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
                obj.NativeData = new string(arr.CharData, 0, arr.Length);
            return JValue.Null;
        });
        R("java/lang/String", "<init>", "([CII)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
                obj.NativeData = new string(arr.CharData, a[2].Int, a[3].Int);
            return JValue.Null;
        });
        R("java/lang/String", "length", "()I", (_, a) => JValue.OfInt((Str(a[0]) ?? "").Length));
        R("java/lang/String", "charAt", "(I)C", (_, a) =>
        {
            var s = Str(a[0]) ?? "";
            int idx = a[1].Int;
            return JValue.OfInt(idx >= 0 && idx < s.Length ? s[idx] : 0);
        });
        R("java/lang/String", "indexOf", "(I)I", (_, a) => JValue.OfInt((Str(a[0]) ?? "").IndexOf((char)a[1].Int)));
        R("java/lang/String", "indexOf", "(II)I", (_, a) => JValue.OfInt((Str(a[0]) ?? "").IndexOf((char)a[1].Int, a[2].Int)));
        R("java/lang/String", "indexOf", "(Ljava/lang/String;)I", (_, a) => JValue.OfInt((Str(a[0]) ?? "").IndexOf(Str(a[1]) ?? "", StringComparison.Ordinal)));
        R("java/lang/String", "indexOf", "(Ljava/lang/String;I)I", (_, a) => JValue.OfInt((Str(a[0]) ?? "").IndexOf(Str(a[1]) ?? "", a[2].Int, StringComparison.Ordinal)));
        R("java/lang/String", "lastIndexOf", "(I)I", (_, a) => JValue.OfInt((Str(a[0]) ?? "").LastIndexOf((char)a[1].Int)));
        R("java/lang/String", "lastIndexOf", "(II)I", (_, a) =>
        {
            var s = Str(a[0]) ?? "";
            int ch = a[1].Int, from = Math.Clamp(a[2].Int, 0, s.Length - 1);
            return JValue.OfInt(from < 0 ? -1 : s.LastIndexOf((char)ch, from));
        });
        R("java/lang/String", "substring", "(I)Ljava/lang/String;", (t, a) =>
        {
            var s = Str(a[0]) ?? "";
            int begin = Math.Clamp(a[1].Int, 0, s.Length);
            return JValue.OfRef(t.Loader.CreateString(s[begin..]));
        });
        R("java/lang/String", "substring", "(II)Ljava/lang/String;", (t, a) =>
        {
            var s = Str(a[0]) ?? "";
            int begin = Math.Clamp(a[1].Int, 0, s.Length);
            int end = Math.Clamp(a[2].Int, begin, s.Length);
            return JValue.OfRef(t.Loader.CreateString(s.Substring(begin, end - begin)));
        });
        R("java/lang/String", "toLowerCase", "()Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString((Str(a[0]) ?? "").ToLowerInvariant())));
        R("java/lang/String", "toUpperCase", "()Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString((Str(a[0]) ?? "").ToUpperInvariant())));
        R("java/lang/String", "trim", "()Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString((Str(a[0]) ?? "").Trim())));
        R("java/lang/String", "equals", "(Ljava/lang/Object;)Z", (_, a) => JValue.OfInt(Str(a[0]) == Str(a[1]) ? 1 : 0));
        R("java/lang/String", "equalsIgnoreCase", "(Ljava/lang/String;)Z", (_, a) =>
            JValue.OfInt(string.Equals(Str(a[0]), Str(a[1]), StringComparison.OrdinalIgnoreCase) ? 1 : 0));
        R("java/lang/String", "compareTo", "(Ljava/lang/String;)I", (_, a) =>
            JValue.OfInt(string.Compare(Str(a[0]), Str(a[1]), StringComparison.Ordinal)));
        R("java/lang/String", "startsWith", "(Ljava/lang/String;)Z", (_, a) => JValue.OfInt((Str(a[0]) ?? "").StartsWith(Str(a[1]) ?? "") ? 1 : 0));
        R("java/lang/String", "endsWith", "(Ljava/lang/String;)Z", (_, a) => JValue.OfInt((Str(a[0]) ?? "").EndsWith(Str(a[1]) ?? "") ? 1 : 0));
        R("java/lang/String", "concat", "(Ljava/lang/String;)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString((Str(a[0]) ?? "") + (Str(a[1]) ?? ""))));
        R("java/lang/String", "replace", "(CC)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString((Str(a[0]) ?? "").Replace((char)a[1].Int, (char)a[2].Int))));
        R("java/lang/String", "toCharArray", "()[C", (t, a) =>
        {
            var s = Str(a[0]) ?? "";
            var arr = t.Loader.CreateArray(JavaArray.ArrayKind.Char, s.Length);
            s.CopyTo(0, arr.CharData, 0, s.Length);
            return JValue.OfRef(arr);
        });
        R("java/lang/String", "getChars", "(II[CI)V", (_, a) =>
        {
            var s = Str(a[0]) ?? "";
            int srcBegin = a[1].Int, srcEnd = a[2].Int;
            if (a[3].Ref is JavaArray dst) s.CopyTo(srcBegin, dst.CharData, a[4].Int, srcEnd - srcBegin);
            return JValue.Null;
        });
        R("java/lang/String", "getBytes", "()[B", (t, a) =>
        {
            var bytes = Encoding.UTF8.GetBytes(Str(a[0]) ?? "");
            var arr = t.Loader.CreateArray(JavaArray.ArrayKind.Byte, bytes.Length);
            Array.Copy(bytes, arr.ByteData, bytes.Length);
            return JValue.OfRef(arr);
        });
        R("java/lang/String", "getBytes", "(Ljava/lang/String;)[B", (t, a) =>
        {
            var bytes = Encoding.UTF8.GetBytes(Str(a[0]) ?? "");
            var arr = t.Loader.CreateArray(JavaArray.ArrayKind.Byte, bytes.Length);
            Array.Copy(bytes, arr.ByteData, bytes.Length);
            return JValue.OfRef(arr);
        });
        R("java/lang/String", "hashCode", "()I", (_, a) => JValue.OfInt((Str(a[0]) ?? "").GetHashCode()));
        R("java/lang/String", "toString", "()Ljava/lang/String;", (_, a) => a[0]);
        R("java/lang/String", "valueOf", "(I)Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString(a[0].Int.ToString())));
        R("java/lang/String", "valueOf", "(J)Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString(a[0].Long.ToString())));
        R("java/lang/String", "valueOf", "(Z)Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString(a[0].Int != 0 ? "true" : "false")));
        R("java/lang/String", "valueOf", "(C)Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString(((char)a[0].Int).ToString())));
        R("java/lang/String", "valueOf", "(Ljava/lang/Object;)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(Str(a[0]) ?? a[0].Ref?.ToString() ?? "null")));
        R("java/lang/String", "valueOf", "([C)Ljava/lang/String;", (t, a) =>
        {
            if (a[0].Ref is JavaArray arr && arr.CharData != null)
                return JValue.OfRef(t.Loader.CreateString(new string(arr.CharData, 0, arr.Length)));
            return JValue.OfRef(t.Loader.CreateString(""));
        });
        R("java/lang/String", "valueOf", "([CII)Ljava/lang/String;", (t, a) =>
        {
            if (a[0].Ref is JavaArray arr && arr.CharData != null)
            {
                int off = Math.Max(0, a[1].Int);
                int len = Math.Max(0, Math.Min(a[2].Int, arr.CharData.Length - off));
                return JValue.OfRef(t.Loader.CreateString(new string(arr.CharData, off, len)));
            }
            return JValue.OfRef(t.Loader.CreateString(""));
        });
        R("java/lang/String", "valueOf", "(F)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(a[0].Float.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        R("java/lang/String", "valueOf", "(D)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(a[0].Double.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        // java.lang.StringBuffer / StringBuilder
        foreach (var cls in new[] { "java/lang/StringBuffer", "java/lang/StringBuilder" })
        {
            R(cls, "<init>", "()V", (_, a) => { SetNative(a[0], new StringBuilder()); return JValue.Null; });
            R(cls, "<init>", "(I)V", (_, a) => { SetNative(a[0], new StringBuilder(a[1].Int)); return JValue.Null; });
            R(cls, "<init>", "(Ljava/lang/String;)V", (_, a) => { SetNative(a[0], new StringBuilder(Str(a[1]) ?? "")); return JValue.Null; });
            R(cls, "append", "(Ljava/lang/String;)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append(Str(a[1])); return a[0]; });
            R(cls, "append", "(I)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append(a[1].Int); return a[0]; });
            R(cls, "append", "(J)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append(a[1].Long); return a[0]; });
            R(cls, "append", "(C)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append((char)a[1].Int); return a[0]; });
            R(cls, "append", "(Z)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append(a[1].Int != 0 ? "true" : "false"); return a[0]; });
            R(cls, "append", "(Ljava/lang/Object;)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append(Str(a[1]) ?? a[1].Ref?.ToString() ?? "null"); return a[0]; });
            R(cls, "append", "(D)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append(a[1].Double); return a[0]; });
            R(cls, "append", "(F)L" + cls + ";", (_, a) => { GetSB(a[0])?.Append(a[1].Float); return a[0]; });
            R(cls, "append", "([CII)L" + cls + ";", (_, a) =>
            {
                if (a[1].Ref is JavaArray arr)
                    GetSB(a[0])?.Append(arr.CharData, a[2].Int, a[3].Int);
                return a[0];
            });
            R(cls, "toString", "()Ljava/lang/String;", (t, a) => JValue.OfRef(t.Loader.CreateString(GetSB(a[0])?.ToString() ?? "")));
            R(cls, "length", "()I", (_, a) => JValue.OfInt(GetSB(a[0])?.Length ?? 0));
            R(cls, "charAt", "(I)C", (_, a) => JValue.OfInt(GetSB(a[0])?[a[1].Int] ?? 0));
            R(cls, "setCharAt", "(IC)V", (_, a) => { var sb = GetSB(a[0]); if (sb != null) sb[a[1].Int] = (char)a[2].Int; return JValue.Null; });
            R(cls, "deleteCharAt", "(I)L" + cls + ";", (_, a) => { GetSB(a[0])?.Remove(a[1].Int, 1); return a[0]; });
            R(cls, "delete", "(II)L" + cls + ";", (_, a) => { GetSB(a[0])?.Remove(a[1].Int, a[2].Int - a[1].Int); return a[0]; });
            R(cls, "insert", "(ILjava/lang/String;)L" + cls + ";", (_, a) => { GetSB(a[0])?.Insert(a[1].Int, Str(a[2])); return a[0]; });
            R(cls, "insert", "(IC)L" + cls + ";", (_, a) => { GetSB(a[0])?.Insert(a[1].Int, (char)a[2].Int); return a[0]; });
            R(cls, "reverse", "()L" + cls + ";", (_, a) =>
            {
                var sb = GetSB(a[0]);
                if (sb != null)
                {
                    var chars = sb.ToString().ToCharArray();
                    Array.Reverse(chars);
                    sb.Clear().Append(chars);
                }
                return a[0];
            });
            R(cls, "setLength", "(I)V", (_, a) => { var sb = GetSB(a[0]); if (sb != null) sb.Length = a[1].Int; return JValue.Null; });
            R(cls, "capacity", "()I", (_, a) => JValue.OfInt(GetSB(a[0])?.Capacity ?? 0));
            R(cls, "getChars", "(II[CI)V", (_, a) =>
            {
                var sb = GetSB(a[0]);
                if (sb != null && a[3].Ref is JavaArray dst)
                {
                    int srcBegin = a[1].Int, srcEnd = a[2].Int, dstBegin = a[4].Int;
                    int len = Math.Min(srcEnd - srcBegin, dst.CharData.Length - dstBegin);
                    for (int i = 0; i < len; i++) dst.CharData[dstBegin + i] = sb[srcBegin + i];
                }
                return JValue.Null;
            });
            R(cls, "substring", "(II)Ljava/lang/String;", (t, a) =>
            {
                var sb = GetSB(a[0]);
                if (sb == null) return JValue.OfRef(t.Loader.CreateString(""));
                int begin = Math.Clamp(a[1].Int, 0, sb.Length);
                int end = Math.Clamp(a[2].Int, begin, sb.Length);
                return JValue.OfRef(t.Loader.CreateString(sb.ToString(begin, end - begin)));
            });
        }

        // java.lang.System
        R("java/lang/System", "currentTimeMillis", "()J", (_, _) =>
            JValue.OfLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        R("java/lang/System", "arraycopy", "(Ljava/lang/Object;ILjava/lang/Object;II)V", (_, a) =>
        {
            var src = a[0].Ref as JavaArray;
            var dst = a[2].Ref as JavaArray;
            if (src == null || dst == null) return JValue.Null;
            int srcPos = a[1].Int, dstPos = a[3].Int, len = a[4].Int;
            if (len <= 0 || srcPos < 0 || dstPos < 0) return JValue.Null;
            try
            {
                int srcLen = src.Length, dstLen = dst.Length;
                len = Math.Min(len, srcLen - srcPos);
                len = Math.Min(len, dstLen - dstPos);
                if (len <= 0) return JValue.Null;
                if (src.Kind == dst.Kind)
                {
                    switch (src.Kind)
                    {
                        case JavaArray.ArrayKind.Int: Array.Copy(src.IntData, srcPos, dst.IntData, dstPos, len); break;
                        case JavaArray.ArrayKind.Byte: case JavaArray.ArrayKind.Boolean: Array.Copy(src.ByteData, srcPos, dst.ByteData, dstPos, len); break;
                        case JavaArray.ArrayKind.Char: Array.Copy(src.CharData, srcPos, dst.CharData, dstPos, len); break;
                        case JavaArray.ArrayKind.Short: Array.Copy(src.ShortData, srcPos, dst.ShortData, dstPos, len); break;
                        case JavaArray.ArrayKind.Long: Array.Copy(src.LongData, srcPos, dst.LongData, dstPos, len); break;
                        case JavaArray.ArrayKind.Float: Array.Copy(src.FloatData, srcPos, dst.FloatData, dstPos, len); break;
                        case JavaArray.ArrayKind.Double: Array.Copy(src.DoubleData, srcPos, dst.DoubleData, dstPos, len); break;
                        case JavaArray.ArrayKind.Ref: Array.Copy(src.RefData, srcPos, dst.RefData, dstPos, len); break;
                    }
                }
                else if (src.Kind == JavaArray.ArrayKind.Ref && dst.Kind == JavaArray.ArrayKind.Ref)
                    Array.Copy(src.RefData, srcPos, dst.RefData, dstPos, len);
                else
                {
                    for (int i = 0; i < len; i++)
                        dst.RefData[dstPos + i] = src.RefData[srcPos + i];
                }
            }
            catch { }
            return JValue.Null;
        });
        R("java/lang/System", "gc", "()V", (_, _) => JValue.Null);
        R("java/lang/System", "exit", "(I)V", (_, _) => JValue.Null);
        R("java/lang/System", "identityHashCode", "(Ljava/lang/Object;)I", (_, a) =>
            JValue.OfInt(a[0].Ref?.GetHashCode() ?? 0));
        R("java/lang/System", "getProperty", "(Ljava/lang/String;)Ljava/lang/String;", (t, a) =>
        {
            string? key = Str(a[0]);
            string? val = key switch
            {
                "microedition.platform" => "J2meEmu/1.0",
                "microedition.encoding" => "UTF-8",
                "microedition.configuration" => "CLDC-1.1",
                "microedition.profiles" => "MIDP-2.0",
                "microedition.locale" => "en-US",
                "file.separator" => "/",
                _ => null
            };
            return val != null ? JValue.OfRef(t.Loader.CreateString(val)) : JValue.Null;
        });

        // java.lang.Runtime
        R("java/lang/Runtime", "getRuntime", "()Ljava/lang/Runtime;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/lang/Runtime"))));
        R("java/lang/Runtime", "totalMemory", "()J", (_, _) => JValue.OfLong(2 * 1024 * 1024));
        R("java/lang/Runtime", "freeMemory", "()J", (_, _) => JValue.OfLong(1 * 1024 * 1024));
        R("java/lang/Runtime", "gc", "()V", (_, _) => JValue.Null);

        // java.lang.Math
        R("java/lang/Math", "abs", "(I)I", (_, a) => JValue.OfInt(Math.Abs(a[0].Int)));
        R("java/lang/Math", "abs", "(J)J", (_, a) => JValue.OfLong(Math.Abs(a[0].Long)));
        R("java/lang/Math", "abs", "(F)F", (_, a) => JValue.OfFloat(MathF.Abs(a[0].Float)));
        R("java/lang/Math", "abs", "(D)D", (_, a) => JValue.OfDouble(Math.Abs(a[0].Double)));
        R("java/lang/Math", "min", "(II)I", (_, a) => JValue.OfInt(Math.Min(a[0].Int, a[1].Int)));
        R("java/lang/Math", "min", "(JJ)J", (_, a) => JValue.OfLong(Math.Min(a[0].Long, a[1].Long)));
        R("java/lang/Math", "max", "(II)I", (_, a) => JValue.OfInt(Math.Max(a[0].Int, a[1].Int)));
        R("java/lang/Math", "max", "(JJ)J", (_, a) => JValue.OfLong(Math.Max(a[0].Long, a[1].Long)));
        R("java/lang/Math", "sqrt", "(D)D", (_, a) => JValue.OfDouble(Math.Sqrt(a[0].Double)));
        R("java/lang/Math", "sin", "(D)D", (_, a) => JValue.OfDouble(Math.Sin(a[0].Double)));
        R("java/lang/Math", "cos", "(D)D", (_, a) => JValue.OfDouble(Math.Cos(a[0].Double)));
        R("java/lang/Math", "tan", "(D)D", (_, a) => JValue.OfDouble(Math.Tan(a[0].Double)));
        R("java/lang/Math", "floor", "(D)D", (_, a) => JValue.OfDouble(Math.Floor(a[0].Double)));
        R("java/lang/Math", "ceil", "(D)D", (_, a) => JValue.OfDouble(Math.Ceiling(a[0].Double)));
        R("java/lang/Math", "random", "()D", (_, _) => JValue.OfDouble(Random.Shared.NextDouble()));
        R("java/lang/Math", "toRadians", "(D)D", (_, a) => JValue.OfDouble(a[0].Double * Math.PI / 180.0));
        R("java/lang/Math", "toDegrees", "(D)D", (_, a) => JValue.OfDouble(a[0].Double * 180.0 / Math.PI));

        // java.lang.Integer
        R("java/lang/Integer", "parseInt", "(Ljava/lang/String;)I", (_, a) =>
            JValue.OfInt(int.TryParse(Str(a[0]), out int v) ? v : 0));
        R("java/lang/Integer", "parseInt", "(Ljava/lang/String;I)I", (_, a) =>
        {
            try { return JValue.OfInt(Convert.ToInt32(Str(a[0]) ?? "0", a[1].Int)); }
            catch { return JValue.OfInt(0); }
        });
        R("java/lang/Integer", "toString", "(I)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(a[0].Int.ToString())));
        R("java/lang/Integer", "toString", "(II)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(Convert.ToString(a[0].Int, a[1].Int))));
        R("java/lang/Integer", "toHexString", "(I)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(a[0].Int.ToString("x"))));
        R("java/lang/Integer", "intValue", "()I", (_, a) =>
            a[0].Ref is JavaObject obj ? JValue.OfInt(obj.NativeData is int iv ? iv : 0) : JValue.OfInt(0));
        R("java/lang/Integer", "valueOf", "(I)Ljava/lang/Integer;", (t, a) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/lang/Integer"), a[0].Int)));
        R("java/lang/Integer", "<init>", "(I)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = a[1].Int;
            return JValue.Null;
        });
        R("java/lang/Integer", "MAX_VALUE", "()I", (_, _) => JValue.OfInt(int.MaxValue));
        R("java/lang/Integer", "MIN_VALUE", "()I", (_, _) => JValue.OfInt(int.MinValue));

        // java.lang.Long
        R("java/lang/Long", "parseLong", "(Ljava/lang/String;)J", (_, a) =>
            JValue.OfLong(long.TryParse(Str(a[0]), out long v) ? v : 0));
        R("java/lang/Long", "toString", "(J)Ljava/lang/String;", (t, a) =>
            JValue.OfRef(t.Loader.CreateString(a[0].Long.ToString())));

        // java.lang.Boolean, Byte, Short, Character, Float, Double (basic)
        R("java/lang/Boolean", "<init>", "(Z)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = a[1].Int != 0;
            return JValue.Null;
        });
        R("java/lang/Boolean", "booleanValue", "()Z", (_, a) =>
            a[0].Ref is JavaObject obj ? JValue.OfInt(obj.NativeData is bool b && b ? 1 : 0) : JValue.OfInt(0));
        R("java/lang/Short", "<init>", "(S)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = (short)a[1].Int;
            return JValue.Null;
        });
        R("java/lang/Short", "shortValue", "()S", (_, a) =>
            a[0].Ref is JavaObject obj && obj.NativeData is short sv ? JValue.OfInt(sv) : JValue.OfInt(0));
        R("java/lang/Short", "parseShort", "(Ljava/lang/String;)S", (_, a) =>
            JValue.OfInt(short.TryParse(Str(a[0]), out short v) ? v : 0));
        R("java/lang/Byte", "<init>", "(B)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = (byte)a[1].Int;
            return JValue.Null;
        });
        R("java/lang/Byte", "byteValue", "()B", (_, a) =>
            a[0].Ref is JavaObject obj && obj.NativeData is byte bv ? JValue.OfInt(bv) : JValue.OfInt(0));
        R("java/lang/Byte", "parseByte", "(Ljava/lang/String;)B", (_, a) =>
            JValue.OfInt(byte.TryParse(Str(a[0]), out byte v) ? v : 0));
        R("java/lang/Byte", "parseByte", "(Ljava/lang/String;I)B", (_, a) =>
        {
            try { return JValue.OfInt(Convert.ToByte(Str(a[0]), a[1].Int)); }
            catch { return JValue.OfInt(0); }
        });
        R("java/lang/Float", "floatValue", "()F", (_, a) =>
            a[0].Ref is JavaObject obj && obj.NativeData is float fv ? JValue.OfFloat(fv) : JValue.OfFloat(0f));
        R("java/lang/Float", "<init>", "(F)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = a[1].Float;
            return JValue.Null;
        });
        R("java/lang/Float", "parseFloat", "(Ljava/lang/String;)F", (_, a) =>
            JValue.OfFloat(float.TryParse(Str(a[0]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f));
        R("java/lang/Double", "doubleValue", "()D", (_, a) =>
            a[0].Ref is JavaObject obj && obj.NativeData is double dv ? JValue.OfDouble(dv) : JValue.OfDouble(0.0));
        R("java/lang/Double", "<init>", "(D)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = a[1].Double;
            return JValue.Null;
        });
        R("java/lang/Character", "isDigit", "(C)Z", (_, a) => JValue.OfInt(char.IsDigit((char)a[0].Int) ? 1 : 0));
        R("java/lang/Character", "isLetter", "(C)Z", (_, a) => JValue.OfInt(char.IsLetter((char)a[0].Int) ? 1 : 0));
        R("java/lang/Character", "isLowerCase", "(C)Z", (_, a) => JValue.OfInt(char.IsLower((char)a[0].Int) ? 1 : 0));
        R("java/lang/Character", "isUpperCase", "(C)Z", (_, a) => JValue.OfInt(char.IsUpper((char)a[0].Int) ? 1 : 0));
        R("java/lang/Character", "toLowerCase", "(C)C", (_, a) => JValue.OfInt(char.ToLowerInvariant((char)a[0].Int)));
        R("java/lang/Character", "toUpperCase", "(C)C", (_, a) => JValue.OfInt(char.ToUpperInvariant((char)a[0].Int)));

        // java.lang.Thread
        R("java/lang/Thread", "<init>", "()V", (_, _) => JValue.Null);
        R("java/lang/Thread", "<init>", "(Ljava/lang/Runnable;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = a[1].Ref;
            return JValue.Null;
        });
        R("java/lang/Thread", "<init>", "(Ljava/lang/String;)V", (_, _) => JValue.Null);
        R("java/lang/Thread", "start", "()V", (t, a) =>
        {
            var threadObj = a[0].Ref as JavaObject;
            if (threadObj == null) return JValue.Null;
            var target = threadObj.NativeData as JavaObject ?? threadObj;
            var runMethod = target.Class.FindMethod("run", "()V");
            if (runMethod != null)
            {
                var bgThread = new JvmThread(t.Loader, "bg") { Alive = true };
                var csThread = new Thread(() =>
                {
                    try { bgThread.Invoke(runMethod, new[] { JValue.OfRef(target) }); }
                    catch (Exception ex) { Log.Error($"Thread error: {ex.Message}"); }
                }) { IsBackground = true };
                csThread.Start();
            }
            return JValue.Null;
        });
        R("java/lang/Thread", "sleep", "(J)V", (_, a) =>
        {
            Thread.Sleep((int)Math.Min(a[0].Long, 5000));
            return JValue.Null;
        });
        R("java/lang/Thread", "yield", "()V", (_, _) => { Thread.Yield(); return JValue.Null; });
        R("java/lang/Thread", "currentThread", "()Ljava/lang/Thread;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/lang/Thread"))));
        R("java/lang/Thread", "isAlive", "()Z", (_, _) => JValue.OfInt(1));
        R("java/lang/Thread", "setPriority", "(I)V", (_, _) => JValue.Null);
        R("java/lang/Thread", "interrupt", "()V", (_, _) => JValue.Null);
        R("java/lang/Thread", "join", "()V", (_, _) => JValue.Null);

        // java.lang.Throwable
        R("java/lang/Throwable", "<init>", "()V", (_, a) => { SetNative(a[0], ""); return JValue.Null; });
        R("java/lang/Throwable", "<init>", "(Ljava/lang/String;)V", (_, a) => { SetNative(a[0], Str(a[1]) ?? ""); return JValue.Null; });
        R("java/lang/Throwable", "getMessage", "()Ljava/lang/String;", (t, a) =>
        {
            var msg = (a[0].Ref as JavaObject)?.NativeData as string;
            return msg != null ? JValue.OfRef(t.Loader.CreateString(msg)) : JValue.Null;
        });
        R("java/lang/Throwable", "toString", "()Ljava/lang/String;", (t, a) =>
        {
            var obj = a[0].Ref as JavaObject;
            var msg = obj?.NativeData as string;
            return JValue.OfRef(t.Loader.CreateString($"{obj?.Class.Name}: {msg}"));
        });
        R("java/lang/Throwable", "printStackTrace", "()V", (_, a) =>
        {
            var obj = a[0].Ref as JavaObject;
            Log.Error($"Exception: {obj?.Class.Name}: {obj?.NativeData}");
            return JValue.Null;
        });

        // Exception constructors
        foreach (var exc in new[] {
            "java/lang/Exception", "java/lang/RuntimeException", "java/lang/Error",
            "java/lang/NullPointerException", "java/lang/ArrayIndexOutOfBoundsException",
            "java/lang/IllegalArgumentException", "java/lang/IllegalStateException",
            "java/lang/ArithmeticException", "java/lang/ClassCastException",
            "java/lang/ClassNotFoundException", "java/lang/SecurityException",
            "java/lang/NumberFormatException", "java/lang/IndexOutOfBoundsException",
            "java/lang/StringIndexOutOfBoundsException", "java/lang/NegativeArraySizeException",
            "java/lang/InterruptedException", "java/lang/OutOfMemoryError",
            "java/lang/StackOverflowError", "java/lang/AbstractMethodError",
            "java/io/IOException", "java/io/EOFException"
        })
        {
            R(exc, "<init>", "()V", (_, a) => { SetNative(a[0], ""); return JValue.Null; });
            R(exc, "<init>", "(Ljava/lang/String;)V", (_, a) => { SetNative(a[0], Str(a[1]) ?? ""); return JValue.Null; });
        }

        // java.io streams
        R("java/io/InputStream", "read", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s?.ReadByte() ?? -1);
        });
        R("java/io/InputStream", "read", "([B)I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null || a[1].Ref is not JavaArray arr) return JValue.OfInt(-1);
            return JValue.OfInt(s.Read(arr.ByteData, 0, arr.Length));
        });
        R("java/io/InputStream", "read", "([BII)I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null || a[1].Ref is not JavaArray arr) return JValue.OfInt(-1);
            return JValue.OfInt(s.Read(arr.ByteData, a[2].Int, a[3].Int));
        });
        R("java/io/InputStream", "available", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s != null ? (int)(s.Length - s.Position) : 0);
        });
        R("java/io/InputStream", "close", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.Dispose();
            return JValue.Null;
        });
        R("java/io/InputStream", "skip", "(J)J", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfLong(0);
            long n = a[1].Long;
            long old = s.Position;
            s.Position = Math.Min(s.Position + n, s.Length);
            return JValue.OfLong(s.Position - old);
        });
        R("java/io/InputStream", "mark", "(I)V", (_, a) =>
        {
            if ((a[0].Ref as JavaObject)?.NativeData is MemoryStream ms) ms.Position = ms.Position; // mark position stored externally if needed
            return JValue.Null;
        });
        R("java/io/InputStream", "markSupported", "()Z", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s != null && s.CanSeek ? 1 : 0);
        });
        R("java/io/InputStream", "reset", "()V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s != null && s.CanSeek) s.Position = 0;
            return JValue.Null;
        });

        R("java/io/ByteArrayInputStream", "<init>", "([B)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
                obj.NativeData = new MemoryStream(arr.ByteData, 0, arr.Length, false);
            return JValue.Null;
        });
        R("java/io/ByteArrayInputStream", "<init>", "([BII)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj && a[1].Ref is JavaArray arr)
                obj.NativeData = new MemoryStream(arr.ByteData, a[2].Int, a[3].Int, false);
            return JValue.Null;
        });
        R("java/io/ByteArrayInputStream", "read", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s?.ReadByte() ?? -1);
        });
        R("java/io/ByteArrayInputStream", "read", "([BII)I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null || a[1].Ref is not JavaArray arr) return JValue.OfInt(-1);
            return JValue.OfInt(s.Read(arr.ByteData, a[2].Int, a[3].Int));
        });
        R("java/io/ByteArrayInputStream", "available", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as MemoryStream;
            if (s == null) return JValue.OfInt(0);
            return JValue.OfInt((int)(s.Length - s.Position));
        });
        R("java/io/ByteArrayInputStream", "close", "()V", (_, a) => JValue.Null);
        R("java/io/ByteArrayInputStream", "reset", "()V", (_, a) =>
        {
            if ((a[0].Ref as JavaObject)?.NativeData is MemoryStream ms) ms.Position = 0;
            return JValue.Null;
        });
        R("java/io/ByteArrayOutputStream", "<init>", "()V", (_, a) => { SetNative(a[0], new MemoryStream()); return JValue.Null; });
        R("java/io/ByteArrayOutputStream", "<init>", "(I)V", (_, a) => { SetNative(a[0], new MemoryStream(a[1].Int)); return JValue.Null; });
        R("java/io/ByteArrayOutputStream", "write", "(I)V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as MemoryStream)?.WriteByte((byte)a[1].Int);
            return JValue.Null;
        });
        R("java/io/ByteArrayOutputStream", "write", "([BII)V", (_, a) =>
        {
            if ((a[0].Ref as JavaObject)?.NativeData is MemoryStream ms && a[1].Ref is JavaArray arr)
                ms.Write(arr.ByteData, a[2].Int, a[3].Int);
            return JValue.Null;
        });
        R("java/io/ByteArrayOutputStream", "toByteArray", "()[B", (t, a) =>
        {
            var ms = (a[0].Ref as JavaObject)?.NativeData as MemoryStream;
            if (ms == null) return JValue.OfRef(t.Loader.CreateArray(JavaArray.ArrayKind.Byte, 0));
            var data = ms.ToArray();
            var arr = t.Loader.CreateArray(JavaArray.ArrayKind.Byte, data.Length);
            Array.Copy(data, arr.ByteData, data.Length);
            return JValue.OfRef(arr);
        });
        R("java/io/ByteArrayOutputStream", "size", "()I", (_, a) =>
            JValue.OfInt((int)(((a[0].Ref as JavaObject)?.NativeData as MemoryStream)?.Length ?? 0)));
        R("java/io/ByteArrayOutputStream", "reset", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as MemoryStream)?.SetLength(0);
            return JValue.Null;
        });
        R("java/io/ByteArrayOutputStream", "close", "()V", (_, a) => JValue.Null);
        R("java/io/ByteArrayOutputStream", "toString", "()Ljava/lang/String;", (t, a) =>
        {
            var ms = (a[0].Ref as JavaObject)?.NativeData as MemoryStream;
            if (ms == null) return JValue.OfRef(t.Loader.CreateString(""));
            return JValue.OfRef(t.Loader.CreateString(Encoding.UTF8.GetString(ms.ToArray())));
        });

        R("java/io/DataInputStream", "<init>", "(Ljava/io/InputStream;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = (a[1].Ref as JavaObject)?.NativeData;
            return JValue.Null;
        });
        R("java/io/DataInputStream", "read", "()I", (_, a) =>
            JValue.OfInt(((a[0].Ref as JavaObject)?.NativeData as Stream)?.ReadByte() ?? -1));
        R("java/io/DataInputStream", "read", "([B)I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null || a[1].Ref is not JavaArray arr) return JValue.OfInt(-1);
            return JValue.OfInt(s.Read(arr.ByteData, 0, arr.Length));
        });
        R("java/io/DataInputStream", "read", "([BII)I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null || a[1].Ref is not JavaArray arr) return JValue.OfInt(-1);
            return JValue.OfInt(s.Read(arr.ByteData, a[2].Int, a[3].Int));
        });
        R("java/io/DataInputStream", "readInt", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfInt(0);
            Span<byte> buf = stackalloc byte[4];
            int n = s.Read(buf);
            if (n < 4) return JValue.OfInt(0);
            return JValue.OfInt(buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3]);
        });
        R("java/io/DataInputStream", "readShort", "()S", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfInt(0);
            int hi = s.ReadByte(), lo = s.ReadByte();
            if (hi < 0 || lo < 0) return JValue.OfInt(0);
            return JValue.OfInt((short)(hi << 8 | lo));
        });
        R("java/io/DataInputStream", "readByte", "()B", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            int b = s?.ReadByte() ?? -1;
            return JValue.OfInt(b < 0 ? 0 : (sbyte)b);
        });
        R("java/io/DataInputStream", "readUnsignedByte", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            int b = s?.ReadByte() ?? -1;
            return JValue.OfInt(b < 0 ? 0 : b);
        });
        R("java/io/DataInputStream", "readUnsignedShort", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfInt(0);
            int hi = s.ReadByte(), lo = s.ReadByte();
            if (hi < 0 || lo < 0) return JValue.OfInt(0);
            return JValue.OfInt((hi << 8) | lo);
        });
        R("java/io/DataInputStream", "readLong", "()J", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfLong(0);
            Span<byte> buf = stackalloc byte[8];
            int n = s.Read(buf);
            if (n < 8) return JValue.OfLong(0);
            return JValue.OfLong((long)buf[0] << 56 | (long)buf[1] << 48 | (long)buf[2] << 40 | (long)buf[3] << 32
                               | (long)buf[4] << 24 | (long)buf[5] << 16 | (long)buf[6] << 8 | buf[7]);
        });
        R("java/io/DataInputStream", "readBoolean", "()Z", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            int b = s?.ReadByte() ?? -1;
            return JValue.OfInt(b > 0 ? 1 : 0);
        });
        R("java/io/DataInputStream", "readChar", "()C", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfInt(0);
            int hi = s.ReadByte(), lo = s.ReadByte();
            return JValue.OfInt(hi < 0 || lo < 0 ? 0 : (hi << 8) | lo);
        });
        R("java/io/DataInputStream", "readFloat", "()F", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfFloat(0f);
            Span<byte> buf = stackalloc byte[4];
            if (s.Read(buf) < 4) return JValue.OfFloat(0f);
            int bits = buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3];
            return JValue.OfFloat(BitConverter.Int32BitsToSingle(bits));
        });
        R("java/io/DataInputStream", "readDouble", "()D", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfDouble(0.0);
            Span<byte> buf = stackalloc byte[8];
            if (s.Read(buf) < 8) return JValue.OfDouble(0.0);
            long bits = (long)buf[0] << 56 | (long)buf[1] << 48 | (long)buf[2] << 40 | (long)buf[3] << 32
                      | (long)buf[4] << 24 | (long)buf[5] << 16 | (long)buf[6] << 8 | buf[7];
            return JValue.OfDouble(BitConverter.Int64BitsToDouble(bits));
        });
        R("java/io/DataInputStream", "mark", "(I)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s != null && s.CanSeek) _markPositions[s] = s.Position;
            return JValue.Null;
        });
        R("java/io/DataInputStream", "reset", "()V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s != null && s.CanSeek && _markPositions.TryGetValue(s, out long pos))
                s.Position = pos;
            return JValue.Null;
        });
        R("java/io/DataInputStream", "markSupported", "()Z", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s != null && s.CanSeek ? 1 : 0);
        });
        R("java/io/DataInputStream", "readUTF", "()Ljava/lang/String;", (t, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            int hi = s.ReadByte(), lo = s.ReadByte();
            if (hi < 0 || lo < 0) return JValue.OfRef(t.Loader.CreateString(""));
            int len = (hi << 8) | lo;
            var buf = new byte[Math.Min(len, 65535)];
            int read = s.Read(buf, 0, buf.Length);
            return JValue.OfRef(t.Loader.CreateString(Encoding.UTF8.GetString(buf, 0, read)));
        });
        R("java/io/DataInputStream", "readUTF", "(Ljava/io/DataInput;)Ljava/lang/String;", (t, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfRef(t.Loader.CreateString(""));
            int hi = s.ReadByte(), lo = s.ReadByte();
            if (hi < 0 || lo < 0) return JValue.OfRef(t.Loader.CreateString(""));
            int len = (hi << 8) | lo;
            var buf = new byte[Math.Min(len, 65535)];
            int read = s.Read(buf, 0, buf.Length);
            return JValue.OfRef(t.Loader.CreateString(Encoding.UTF8.GetString(buf, 0, read)));
        });
        R("java/io/DataInputStream", "readFully", "([B)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s != null && a[1].Ref is JavaArray arr && arr.ByteData != null)
            {
                int len = Math.Min(arr.Length, arr.ByteData.Length);
                if (len > 0) s.Read(arr.ByteData, 0, len);
            }
            return JValue.Null;
        });
        R("java/io/DataInputStream", "readFully", "([BII)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s != null && a[1].Ref is JavaArray arr)
            {
                int off = a[2].Int, count = a[3].Int;
                if (off >= 0 && count >= 0 && off + count <= arr.ByteData.Length)
                    s.Read(arr.ByteData, off, count);
            }
            return JValue.Null;
        });
        R("java/io/DataInputStream", "skipBytes", "(I)I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfInt(0);
            int n = a[1].Int;
            if (s.CanSeek) { s.Seek(n, SeekOrigin.Current); return JValue.OfInt(n); }
            int skipped = 0;
            for (int i = 0; i < n; i++) { if (s.ReadByte() < 0) break; skipped++; }
            return JValue.OfInt(skipped);
        });
        R("java/io/DataInputStream", "close", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.Dispose();
            return JValue.Null;
        });
        R("java/io/DataInputStream", "available", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s != null ? (int)(s.Length - s.Position) : 0);
        });
        R("java/io/DataInputStream", "skip", "(J)J", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.OfLong(0);
            long n = a[1].Long; long old = s.Position;
            s.Position = Math.Min(s.Position + n, s.Length);
            return JValue.OfLong(s.Position - old);
        });

        R("java/io/OutputStream", "write", "([B)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s != null && a[1].Ref is JavaArray arr && arr.ByteData != null)
                s.Write(arr.ByteData, 0, arr.Length);
            return JValue.Null;
        });
        R("java/io/OutputStream", "write", "([BII)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s != null && a[1].Ref is JavaArray arr && arr.ByteData != null)
            {
                int off = Math.Max(0, a[2].Int), len = Math.Max(0, a[3].Int);
                len = Math.Min(len, arr.ByteData.Length - off);
                if (len > 0) s.Write(arr.ByteData, off, len);
            }
            return JValue.Null;
        });
        R("java/io/OutputStream", "flush", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.Flush();
            return JValue.Null;
        });
        R("java/io/OutputStream", "close", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.Dispose();
            return JValue.Null;
        });

        R("java/io/DataOutputStream", "<init>", "(Ljava/io/OutputStream;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = (a[1].Ref as JavaObject)?.NativeData;
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "write", "(I)V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.WriteByte((byte)a[1].Int);
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeInt", "(I)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            int v = a[1].Int;
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeShort", "(I)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            s.WriteByte((byte)(a[1].Int >> 8)); s.WriteByte((byte)a[1].Int);
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeByte", "(I)V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.WriteByte((byte)a[1].Int);
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeBoolean", "(Z)V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.WriteByte(a[1].Int != 0 ? (byte)1 : (byte)0);
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeLong", "(J)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            long v = a[1].Long;
            for (int i = 56; i >= 0; i -= 8) s.WriteByte((byte)(v >> i));
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeFloat", "(F)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            int bits = BitConverter.SingleToInt32Bits(a[1].Float);
            s.WriteByte((byte)(bits >> 24)); s.WriteByte((byte)(bits >> 16));
            s.WriteByte((byte)(bits >> 8)); s.WriteByte((byte)bits);
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeDouble", "(D)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            long bits = BitConverter.DoubleToInt64Bits(a[1].Double);
            for (int i = 56; i >= 0; i -= 8) s.WriteByte((byte)(bits >> i));
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "writeUTF", "(Ljava/lang/String;)V", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null) return JValue.Null;
            var bytes = Encoding.UTF8.GetBytes(Str(a[1]) ?? "");
            s.WriteByte((byte)(bytes.Length >> 8)); s.WriteByte((byte)bytes.Length);
            s.Write(bytes);
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "flush", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.Flush();
            return JValue.Null;
        });
        R("java/io/DataOutputStream", "close", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.Dispose();
            return JValue.Null;
        });

        R("java/io/PrintStream", "println", "(Ljava/lang/String;)V", (_, a) =>
        {
            Log.Info($"[stdout] {Str(a[1])}");
            return JValue.Null;
        });
        R("java/io/PrintStream", "println", "(I)V", (_, a) =>
        {
            Log.Info($"[stdout] {a[1].Int}");
            return JValue.Null;
        });
        R("java/io/PrintStream", "println", "(Ljava/lang/Object;)V", (_, a) =>
        {
            Log.Info($"[stdout] {Str(a[1]) ?? a[1].Ref?.ToString() ?? "null"}");
            return JValue.Null;
        });
        R("java/io/PrintStream", "println", "()V", (_, _) =>
        {
            Log.Info("[stdout]");
            return JValue.Null;
        });
        R("java/io/PrintStream", "print", "(Ljava/lang/String;)V", (_, a) =>
        {
            Log.Info($"[stdout] {Str(a[1])}");
            return JValue.Null;
        });

        R("java/io/InputStreamReader", "<init>", "(Ljava/io/InputStream;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = (a[1].Ref as JavaObject)?.NativeData;
            return JValue.Null;
        });
        R("java/io/InputStreamReader", "<init>", "(Ljava/io/InputStream;Ljava/lang/String;)V", (_, a) =>
        {
            if (a[0].Ref is JavaObject obj) obj.NativeData = (a[1].Ref as JavaObject)?.NativeData;
            return JValue.Null;
        });
        R("java/io/InputStreamReader", "read", "()I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s?.ReadByte() ?? -1);
        });
        R("java/io/InputStreamReader", "read", "([CII)I", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            if (s == null || a[1].Ref is not JavaArray arr) return JValue.OfInt(-1);
            int off = a[2].Int, len = a[3].Int;
            var buf = new byte[len];
            int n = s.Read(buf, 0, len);
            if (n <= 0) return JValue.OfInt(-1);
            for (int i = 0; i < n; i++) arr.CharData[off + i] = (char)buf[i];
            return JValue.OfInt(n);
        });
        R("java/io/InputStreamReader", "close", "()V", (_, a) =>
        {
            ((a[0].Ref as JavaObject)?.NativeData as Stream)?.Dispose();
            return JValue.Null;
        });
        R("java/io/InputStreamReader", "ready", "()Z", (_, a) =>
        {
            var s = (a[0].Ref as JavaObject)?.NativeData as Stream;
            return JValue.OfInt(s != null && s.Position < s.Length ? 1 : 0);
        });

        // java.util.Vector
        R("java/util/Vector", "<init>", "()V", (_, a) => { SetNative(a[0], new List<object?>()); return JValue.Null; });
        R("java/util/Vector", "<init>", "(I)V", (_, a) => { SetNative(a[0], new List<object?>(Math.Max(0, a[1].Int))); return JValue.Null; });
        R("java/util/Vector", "<init>", "(II)V", (_, a) => { SetNative(a[0], new List<object?>(Math.Max(0, a[1].Int))); return JValue.Null; });
        R("java/util/Vector", "addElement", "(Ljava/lang/Object;)V", (_, a) => { GetList(a[0])?.Add(a[1].Ref); return JValue.Null; });
        R("java/util/Vector", "add", "(Ljava/lang/Object;)Z", (_, a) => { GetList(a[0])?.Add(a[1].Ref); return JValue.OfInt(1); });
        R("java/util/Vector", "insertElementAt", "(Ljava/lang/Object;I)V", (_, a) => { GetList(a[0])?.Insert(a[2].Int, a[1].Ref); return JValue.Null; });
        R("java/util/Vector", "removeElement", "(Ljava/lang/Object;)Z", (_, a) => JValue.OfInt(GetList(a[0])?.Remove(a[1].Ref) == true ? 1 : 0));
        R("java/util/Vector", "removeElementAt", "(I)V", (_, a) => { GetList(a[0])?.RemoveAt(a[1].Int); return JValue.Null; });
        R("java/util/Vector", "removeAllElements", "()V", (_, a) => { GetList(a[0])?.Clear(); return JValue.Null; });
        R("java/util/Vector", "elementAt", "(I)Ljava/lang/Object;", (_, a) =>
        {
            var list = GetList(a[0]);
            int idx = a[1].Int;
            return JValue.OfRef(list != null && idx >= 0 && idx < list.Count ? list[idx] : null);
        });
        R("java/util/Vector", "firstElement", "()Ljava/lang/Object;", (_, a) =>
        {
            var list = GetList(a[0]);
            return JValue.OfRef(list is { Count: > 0 } ? list[0] : null);
        });
        R("java/util/Vector", "lastElement", "()Ljava/lang/Object;", (_, a) =>
        {
            var list = GetList(a[0]);
            return JValue.OfRef(list is { Count: > 0 } ? list[^1] : null);
        });
        R("java/util/Vector", "size", "()I", (_, a) => JValue.OfInt(GetList(a[0])?.Count ?? 0));
        R("java/util/Vector", "isEmpty", "()Z", (_, a) => JValue.OfInt(GetList(a[0])?.Count == 0 ? 1 : 0));
        R("java/util/Vector", "contains", "(Ljava/lang/Object;)Z", (_, a) => JValue.OfInt(GetList(a[0])?.Contains(a[1].Ref) == true ? 1 : 0));
        R("java/util/Vector", "indexOf", "(Ljava/lang/Object;)I", (_, a) => JValue.OfInt(GetList(a[0])?.IndexOf(a[1].Ref) ?? -1));
        R("java/util/Vector", "setElementAt", "(Ljava/lang/Object;I)V", (_, a) =>
        {
            var list = GetList(a[0]);
            if (list != null && a[2].Int < list.Count) list[a[2].Int] = a[1].Ref;
            return JValue.Null;
        });
        R("java/util/Vector", "copyInto", "([Ljava/lang/Object;)V", (_, a) =>
        {
            var list = GetList(a[0]);
            if (list != null && a[1].Ref is JavaArray arr)
                for (int i = 0; i < list.Count && i < arr.Length; i++) arr.RefData[i] = list[i];
            return JValue.Null;
        });
        R("java/util/Vector", "elements", "()Ljava/util/Enumeration;", (t, a) =>
        {
            var list = GetList(a[0]) ?? new List<object?>();
            var snapshot = new List<object?>(list);
            var enumObj = new JavaObject(t.Loader.LoadClass("java/util/Enumeration"), new int[] { 0 });
            var iter = new ListEnumeration(snapshot);
            enumObj.NativeData = iter;
            return JValue.OfRef(enumObj);
        });
        R("java/util/Vector", "trimToSize", "()V", (_, _) => JValue.Null);
        R("java/util/Vector", "setSize", "(I)V", (_, a) =>
        {
            var list = GetList(a[0]);
            if (list != null)
            {
                int sz = Math.Max(0, a[1].Int);
                while (list.Count > sz) list.RemoveAt(list.Count - 1);
                while (list.Count < sz) list.Add(null);
            }
            return JValue.Null;
        });

        // java.util.Hashtable
        R("java/util/Hashtable", "<init>", "()V", (_, a) => { SetNative(a[0], new Dictionary<object, object?>()); return JValue.Null; });
        R("java/util/Hashtable", "<init>", "(I)V", (_, a) => { SetNative(a[0], new Dictionary<object, object?>(a[1].Int)); return JValue.Null; });
        R("java/util/Hashtable", "put", "(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", (_, a) =>
        {
            var dict = GetDict(a[0]);
            if (dict == null || a[1].Ref == null) return JValue.Null;
            var key = GetHashKey(a[1].Ref);
            dict.TryGetValue(key, out var old);
            dict[key] = a[2].Ref;
            return JValue.OfRef(old);
        });
        R("java/util/Hashtable", "get", "(Ljava/lang/Object;)Ljava/lang/Object;", (_, a) =>
        {
            var dict = GetDict(a[0]);
            if (dict == null || a[1].Ref == null) return JValue.Null;
            return JValue.OfRef(dict.GetValueOrDefault(GetHashKey(a[1].Ref)));
        });
        R("java/util/Hashtable", "remove", "(Ljava/lang/Object;)Ljava/lang/Object;", (_, a) =>
        {
            var dict = GetDict(a[0]);
            if (dict == null || a[1].Ref == null) return JValue.Null;
            var key = GetHashKey(a[1].Ref);
            dict.Remove(key, out var old);
            return JValue.OfRef(old);
        });
        R("java/util/Hashtable", "containsKey", "(Ljava/lang/Object;)Z", (_, a) =>
        {
            var dict = GetDict(a[0]);
            return JValue.OfInt(dict != null && a[1].Ref != null && dict.ContainsKey(GetHashKey(a[1].Ref)) ? 1 : 0);
        });
        R("java/util/Hashtable", "size", "()I", (_, a) => JValue.OfInt(GetDict(a[0])?.Count ?? 0));
        R("java/util/Hashtable", "isEmpty", "()Z", (_, a) => JValue.OfInt(GetDict(a[0])?.Count == 0 ? 1 : 0));
        R("java/util/Hashtable", "clear", "()V", (_, a) => { GetDict(a[0])?.Clear(); return JValue.Null; });
        R("java/util/Hashtable", "keys", "()Ljava/util/Enumeration;", (t, a) =>
        {
            var dict = GetDict(a[0]) ?? new Dictionary<object, object?>();
            var keys = new List<object?>(dict.Keys);
            var enumObj = new JavaObject(t.Loader.LoadClass("java/util/Enumeration"), new ListEnumeration(keys));
            return JValue.OfRef(enumObj);
        });
        R("java/util/Hashtable", "elements", "()Ljava/util/Enumeration;", (t, a) =>
        {
            var dict = GetDict(a[0]) ?? new Dictionary<object, object?>();
            var vals = new List<object?>(dict.Values);
            var enumObj = new JavaObject(t.Loader.LoadClass("java/util/Enumeration"), new ListEnumeration(vals));
            return JValue.OfRef(enumObj);
        });

        // java.util.Enumeration
        R("java/util/Enumeration", "hasMoreElements", "()Z", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as ListEnumeration;
            return JValue.OfInt(e?.HasMore() == true ? 1 : 0);
        });
        R("java/util/Enumeration", "nextElement", "()Ljava/lang/Object;", (_, a) =>
        {
            var e = (a[0].Ref as JavaObject)?.NativeData as ListEnumeration;
            return JValue.OfRef(e?.Next());
        });

        // java.util.Random
        R("java/util/Random", "<init>", "()V", (_, a) => { SetNative(a[0], new Random()); return JValue.Null; });
        R("java/util/Random", "<init>", "(J)V", (_, a) => { SetNative(a[0], new Random((int)a[1].Long)); return JValue.Null; });
        R("java/util/Random", "nextInt", "()I", (_, a) => JValue.OfInt(GetRandom(a[0]).Next()));
        R("java/util/Random", "nextInt", "(I)I", (_, a) => JValue.OfInt(GetRandom(a[0]).Next(a[1].Int)));
        R("java/util/Random", "nextLong", "()J", (_, a) => JValue.OfLong(GetRandom(a[0]).NextInt64()));
        R("java/util/Random", "setSeed", "(J)V", (_, _) => JValue.Null);

        // java.util.Date
        R("java/util/Date", "<init>", "()V", (_, a) => { SetNative(a[0], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); return JValue.Null; });
        R("java/util/Date", "<init>", "(J)V", (_, a) => { SetNative(a[0], a[1].Long); return JValue.Null; });
        R("java/util/Date", "getTime", "()J", (_, a) => JValue.OfLong((a[0].Ref as JavaObject)?.NativeData is long l ? l : 0));

        // java.util.Calendar
        R("java/util/Calendar", "getInstance", "()Ljava/util/Calendar;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/util/Calendar"), DateTime.Now)));
        R("java/util/Calendar", "get", "(I)I", (_, a) =>
        {
            var dt = (a[0].Ref as JavaObject)?.NativeData is DateTime d ? d : DateTime.Now;
            return JValue.OfInt(a[1].Int switch
            {
                1 => dt.Year, 2 => dt.Month - 1, 5 => dt.Day,
                7 => (int)dt.DayOfWeek + 1, 10 => dt.Hour % 12, 11 => dt.Hour,
                12 => dt.Minute, 13 => dt.Second, 14 => dt.Millisecond,
                _ => 0
            });
        });
        R("java/util/Calendar", "setTime", "(Ljava/util/Date;)V", (_, _) => JValue.Null);
        R("java/util/Calendar", "getTime", "()Ljava/util/Date;", (t, _) =>
        {
            var d = new JavaObject(t.Loader.LoadClass("java/util/Date"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return JValue.OfRef(d);
        });
        R("java/util/Calendar", "getTimeInMillis", "()J", (_, _) =>
            JValue.OfLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        // java.util.Timer / TimerTask
        R("java/util/Timer", "<init>", "()V", (_, a) =>
        {
            var cts = new CancellationTokenSource();
            SetNative(a[0], cts);
            return JValue.Null;
        });
        R("java/util/Timer", "schedule", "(Ljava/util/TimerTask;J)V", (t, a) =>
        {
            var task = a[1].Ref as JavaObject;
            long delay = Math.Max(0, Math.Min(a[2].Long, 60000));
            if (task == null) return JValue.Null;
            var runMethod = task.Class.FindMethod("run", "()V");
            if (runMethod == null) return JValue.Null;
            var timerCts = (a[0].Ref as JavaObject)?.NativeData as CancellationTokenSource;
            var taskCts = new CancellationTokenSource();
            SetNative(a[1], taskCts);
            var bgThread = new JvmThread(t.Loader, "timer") { Alive = true };
            new Thread(() =>
            {
                try
                {
                    Thread.Sleep((int)delay);
                    if (!taskCts.IsCancellationRequested && timerCts is not { IsCancellationRequested: true })
                        bgThread.Invoke(runMethod, new[] { JValue.OfRef(task) });
                }
                catch (Exception ex) { Log.Error($"Timer error: {ex.Message}"); }
            }) { IsBackground = true }.Start();
            return JValue.Null;
        });
        R("java/util/Timer", "schedule", "(Ljava/util/TimerTask;JJ)V", (t, a) =>
        {
            var task = a[1].Ref as JavaObject;
            long delay = Math.Max(0, Math.Min(a[2].Long, 60000));
            long period = Math.Max(1, Math.Min(a[3].Long, 60000));
            if (task == null) return JValue.Null;
            var runMethod = task.Class.FindMethod("run", "()V");
            if (runMethod == null) return JValue.Null;
            var timerCts = (a[0].Ref as JavaObject)?.NativeData as CancellationTokenSource;
            var taskCts = new CancellationTokenSource();
            SetNative(a[1], taskCts);
            var bgThread = new JvmThread(t.Loader, "timer") { Alive = true };
            new Thread(() =>
            {
                try
                {
                    Thread.Sleep((int)delay);
                    while (!taskCts.IsCancellationRequested && timerCts is not { IsCancellationRequested: true })
                    {
                        bgThread.Invoke(runMethod, new[] { JValue.OfRef(task) });
                        Thread.Sleep((int)period);
                    }
                }
                catch (Exception ex) { Log.Error($"Timer error: {ex.Message}"); }
            }) { IsBackground = true }.Start();
            return JValue.Null;
        });
        R("java/util/Timer", "cancel", "()V", (_, a) =>
        {
            if ((a[0].Ref as JavaObject)?.NativeData is CancellationTokenSource cts)
                cts.Cancel();
            return JValue.Null;
        });
        R("java/util/Timer", "scheduleAtFixedRate", "(Ljava/util/TimerTask;JJ)V", (t, a) =>
        {
            var task = a[1].Ref as JavaObject;
            long delay = Math.Max(0, Math.Min(a[2].Long, 60000));
            long period = Math.Max(1, Math.Min(a[3].Long, 60000));
            if (task == null) return JValue.Null;
            var runMethod = task.Class.FindMethod("run", "()V");
            if (runMethod == null) return JValue.Null;
            var timerCts = (a[0].Ref as JavaObject)?.NativeData as CancellationTokenSource;
            var taskCts = new CancellationTokenSource();
            SetNative(a[1], taskCts);
            var bgThread = new JvmThread(t.Loader, "timer") { Alive = true };
            new Thread(() =>
            {
                try
                {
                    Thread.Sleep((int)delay);
                    while (!taskCts.IsCancellationRequested && timerCts is not { IsCancellationRequested: true })
                    {
                        long tickStart = Environment.TickCount64;
                        bgThread.Invoke(runMethod, new[] { JValue.OfRef(task) });
                        long tickElapsed = Environment.TickCount64 - tickStart;
                        int sleepMs = (int)Math.Max(1, period - tickElapsed);
                        Thread.Sleep(sleepMs);
                    }
                }
                catch (Exception ex) { Log.Error($"Timer error: {ex.Message}"); }
            }) { IsBackground = true }.Start();
            return JValue.Null;
        });
        R("java/util/TimerTask", "<init>", "()V", (_, _) => JValue.Null);
        R("java/util/TimerTask", "cancel", "()Z", (_, a) =>
        {
            if ((a[0].Ref as JavaObject)?.NativeData is CancellationTokenSource cts)
                cts.Cancel();
            return JValue.OfInt(1);
        });

        // java.util.TimeZone
        R("java/util/TimeZone", "getDefault", "()Ljava/util/TimeZone;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/util/TimeZone"))));
        R("java/util/TimeZone", "getTimeZone", "(Ljava/lang/String;)Ljava/util/TimeZone;", (t, _) =>
            JValue.OfRef(new JavaObject(t.Loader.LoadClass("java/util/TimeZone"))));
        R("java/util/TimeZone", "getID", "()Ljava/lang/String;", (t, _) =>
            JValue.OfRef(t.Loader.CreateString("UTC")));

        // java.util.Stack
        R("java/util/Stack", "<init>", "()V", (_, a) => { SetNative(a[0], new List<object?>()); return JValue.Null; });
        R("java/util/Stack", "push", "(Ljava/lang/Object;)Ljava/lang/Object;", (_, a) => { GetList(a[0])?.Add(a[1].Ref); return a[1]; });
        R("java/util/Stack", "pop", "()Ljava/lang/Object;", (_, a) =>
        {
            var list = GetList(a[0]);
            if (list == null || list.Count == 0) return JValue.Null;
            var v = list[^1]; list.RemoveAt(list.Count - 1);
            return JValue.OfRef(v);
        });
        R("java/util/Stack", "peek", "()Ljava/lang/Object;", (_, a) =>
        {
            var list = GetList(a[0]);
            return JValue.OfRef(list is { Count: > 0 } ? list[^1] : null);
        });
        R("java/util/Stack", "empty", "()Z", (_, a) => JValue.OfInt(GetList(a[0])?.Count == 0 ? 1 : 0));
        R("java/util/Stack", "size", "()I", (_, a) => JValue.OfInt(GetList(a[0])?.Count ?? 0));
    }

    static string? Str(JValue v) => (v.Ref as JavaObject)?.NativeData as string;
    static void SetNative(JValue v, object data) { if (v.Ref is JavaObject obj) obj.NativeData = data; }
    static StringBuilder? GetSB(JValue v) => (v.Ref as JavaObject)?.NativeData as StringBuilder;
    static List<object?>? GetList(JValue v) => (v.Ref as JavaObject)?.NativeData as List<object?>;
    static Dictionary<object, object?>? GetDict(JValue v) => (v.Ref as JavaObject)?.NativeData as Dictionary<object, object?>;
    static Random GetRandom(JValue v) => (v.Ref as JavaObject)?.NativeData as Random ?? Random.Shared;

    static object GetHashKey(object? obj)
    {
        if (obj is JavaObject jo && jo.NativeData is string s) return s;
        return obj ?? "null";
    }
}

class ListEnumeration
{
    readonly List<object?> _items;
    int _index;

    public ListEnumeration(List<object?> items) => _items = items;
    public bool HasMore() => _index < _items.Count;
    public object? Next() => _index < _items.Count ? _items[_index++] : null;
}
