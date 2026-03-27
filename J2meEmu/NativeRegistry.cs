namespace J2meEmu;

static class NativeRegistry
{
    static readonly Dictionary<string, Func<JvmThread, JValue[], JValue>> _methods = new();
    static readonly Dictionary<string, Func<string, JvmClassLoader, JavaClass>> _classBuilders = new();
    static bool _initialized;

    public static void EnsureInit()
    {
        if (_initialized) return;
        _initialized = true;
        CldcNatives.Register();
        MidpNatives.Register();
    }

    public static void RegisterMethod(string className, string methodName, string descriptor,
        Func<JvmThread, JValue[], JValue> handler)
    {
        _methods[$"{className}/{methodName}{descriptor}"] = handler;
    }

    public static void RegisterClassBuilder(string className, Func<string, JvmClassLoader, JavaClass> builder)
    {
        _classBuilders[className] = builder;
    }

    public static Func<JvmThread, JValue[], JValue>? TryGetNativeMethod(string className, string methodName, string descriptor)
    {
        EnsureInit();
        return _methods.GetValueOrDefault($"{className}/{methodName}{descriptor}");
    }

    public static JavaClass? TryGetNativeClass(string name, JvmClassLoader loader)
    {
        EnsureInit();
        if (_classBuilders.TryGetValue(name, out var builder))
            return builder(name, loader);

        if (IsKnownStubClass(name))
            return BuildStubClass(name, loader);

        return null;
    }

    public static void InjectMissingNatives(JavaClass cls, JvmClassLoader loader)
    {
        EnsureInit();
        string prefix = cls.Name + "/";
        foreach (var (key, handler) in _methods)
        {
            if (!key.StartsWith(prefix)) continue;
            string rest = key[prefix.Length..];
            int descStart = rest.IndexOf('(');
            if (descStart < 0) continue;
            string mName = rest[..descStart];
            string mDesc = rest[descStart..];
            string mKey = $"{mName}{mDesc}";
            if (!cls.Methods.ContainsKey(mKey))
            {
                var method = new JavaMethod(mName, mDesc, cls)
                {
                    Flags = MethodAccessFlags.Public,
                    NativeHandler = handler
                };
                cls.Methods[mKey] = method;
            }
        }
    }

    static bool IsKnownStubClass(string name) => name switch
    {
        "java/lang/Object" or "java/lang/Class" or "java/lang/String"
            or "java/lang/StringBuffer" or "java/lang/StringBuilder"
            or "java/lang/System" or "java/lang/Runtime" or "java/lang/Math"
            or "java/lang/Thread" or "java/lang/Runnable"
            or "java/lang/Integer" or "java/lang/Long" or "java/lang/Short"
            or "java/lang/Byte" or "java/lang/Character" or "java/lang/Boolean"
            or "java/lang/Float" or "java/lang/Double"
            or "java/lang/Throwable" or "java/lang/Exception"
            or "java/lang/RuntimeException" or "java/lang/Error"
            or "java/lang/NullPointerException" or "java/lang/ArrayIndexOutOfBoundsException"
            or "java/lang/ClassNotFoundException" or "java/lang/IllegalArgumentException"
            or "java/lang/IllegalStateException" or "java/lang/ArithmeticException"
            or "java/lang/ClassCastException" or "java/lang/SecurityException"
            or "java/lang/InterruptedException" or "java/lang/StackOverflowError"
            or "java/lang/OutOfMemoryError" or "java/lang/AbstractMethodError"
            or "java/lang/NumberFormatException" or "java/lang/IndexOutOfBoundsException"
            or "java/lang/StringIndexOutOfBoundsException" or "java/lang/NegativeArraySizeException"
            or "java/io/InputStream" or "java/io/OutputStream" or "java/io/PrintStream"
            or "java/io/ByteArrayInputStream" or "java/io/ByteArrayOutputStream"
            or "java/io/DataInputStream" or "java/io/DataOutputStream"
            or "java/io/IOException" or "java/io/EOFException" or "java/io/Reader" or "java/io/InputStreamReader"
            or "java/util/Vector" or "java/util/Stack" or "java/util/Hashtable"
            or "java/util/Enumeration" or "java/util/Random" or "java/util/Calendar"
            or "java/util/Date" or "java/util/Timer" or "java/util/TimerTask" or "java/util/TimeZone"
            or "javax/microedition/midlet/MIDlet" or "javax/microedition/midlet/MIDletStateChangeException"
            or "javax/microedition/lcdui/Display" or "javax/microedition/lcdui/Displayable"
            or "javax/microedition/lcdui/Canvas" or "javax/microedition/lcdui/Graphics"
            or "javax/microedition/lcdui/Image" or "javax/microedition/lcdui/Font"
            or "javax/microedition/lcdui/Command" or "javax/microedition/lcdui/CommandListener"
            or "javax/microedition/lcdui/Alert" or "javax/microedition/lcdui/AlertType"
            or "javax/microedition/lcdui/Form" or "javax/microedition/lcdui/Item"
            or "javax/microedition/lcdui/StringItem" or "javax/microedition/lcdui/TextField"
            or "javax/microedition/lcdui/ChoiceGroup" or "javax/microedition/lcdui/List"
            or "javax/microedition/lcdui/Ticker" or "javax/microedition/lcdui/Gauge"
            or "javax/microedition/lcdui/Choice" or "javax/microedition/lcdui/Screen"
            or "javax/microedition/lcdui/TextBox"
            or "javax/microedition/lcdui/game/GameCanvas" or "javax/microedition/lcdui/game/Sprite"
            or "javax/microedition/lcdui/game/TiledLayer" or "javax/microedition/lcdui/game/Layer"
            or "javax/microedition/lcdui/game/LayerManager"
            or "javax/microedition/rms/RecordStore" or "javax/microedition/rms/RecordEnumeration"
            or "javax/microedition/rms/RecordStoreException" or "javax/microedition/rms/RecordStoreNotFoundException"
            or "javax/microedition/rms/RecordFilter" or "javax/microedition/rms/RecordComparator"
            or "javax/microedition/rms/InvalidRecordIDException" or "javax/microedition/rms/RecordStoreFullException"
            or "javax/microedition/io/Connector" or "javax/microedition/io/HttpConnection"
            or "javax/microedition/io/Connection"
            or "javax/microedition/media/Manager" or "javax/microedition/media/Player"
            or "javax/microedition/media/PlayerListener" or "javax/microedition/media/MediaException"
            or "javax/microedition/media/control/VolumeControl"
            or "javax/microedition/media/control/ToneControl"
            or "javax/microedition/media/Controllable"
            or "com/nokia/mid/sound/Sound"
            or "com/nokia/mid/ui/FullCanvas" or "com/nokia/mid/ui/DirectGraphics"
            or "com/nokia/mid/ui/DirectUtils" or "com/nokia/mid/ui/DeviceControl"
            => true,
        _ => name.StartsWith("java/") || name.StartsWith("javax/") || name.StartsWith("com/nokia/")
            || name.StartsWith("javax/microedition/m3g/")
    };

    static JavaClass BuildStubClass(string name, JvmClassLoader loader)
    {
        var cls = new JavaClass(name) { Initialized = true };

        string? superName = name switch
        {
            "java/lang/Object" => null,
            "java/lang/Throwable" => "java/lang/Object",
            "java/lang/Exception" => "java/lang/Throwable",
            "java/lang/RuntimeException" => "java/lang/Exception",
            "java/lang/Error" => "java/lang/Throwable",
            "javax/microedition/lcdui/Displayable" => "java/lang/Object",
            "javax/microedition/lcdui/Screen" => "javax/microedition/lcdui/Displayable",
            "javax/microedition/lcdui/Canvas" => "javax/microedition/lcdui/Displayable",
            "javax/microedition/lcdui/game/GameCanvas" => "javax/microedition/lcdui/Canvas",
            "javax/microedition/lcdui/Form" => "javax/microedition/lcdui/Screen",
            "javax/microedition/lcdui/List" => "javax/microedition/lcdui/Screen",
            "javax/microedition/lcdui/Alert" => "javax/microedition/lcdui/Screen",
            "javax/microedition/lcdui/TextBox" => "javax/microedition/lcdui/Screen",
            "com/nokia/mid/ui/FullCanvas" => "javax/microedition/lcdui/Canvas",
            "javax/microedition/m3g/Object3D" => "java/lang/Object",
            "javax/microedition/m3g/Transformable" => "javax/microedition/m3g/Object3D",
            "javax/microedition/m3g/Node" => "javax/microedition/m3g/Transformable",
            "javax/microedition/m3g/Group" => "javax/microedition/m3g/Node",
            "javax/microedition/m3g/World" => "javax/microedition/m3g/Group",
            "javax/microedition/m3g/Camera" => "javax/microedition/m3g/Node",
            "javax/microedition/m3g/Light" => "javax/microedition/m3g/Node",
            "javax/microedition/m3g/Mesh" => "javax/microedition/m3g/Node",
            "javax/microedition/m3g/MorphingMesh" => "javax/microedition/m3g/Mesh",
            "javax/microedition/m3g/SkinnedMesh" => "javax/microedition/m3g/Mesh",
            "javax/microedition/m3g/Sprite3D" => "javax/microedition/m3g/Node",
            _ when name.Contains("Exception") || name.Contains("Error") => "java/lang/Exception",
            _ when name.StartsWith("javax/microedition/m3g/") => "javax/microedition/m3g/Object3D",
            _ => "java/lang/Object"
        };

        if (superName != null)
            cls.Super = loader.LoadClass(superName);

        InjectMissingNatives(cls, loader);

        var defaultInit = new JavaMethod("<init>", "()V", cls)
        {
            Flags = MethodAccessFlags.Public,
            NativeHandler = (_, _) => JValue.Null
        };
        cls.Methods.TryAdd("<init>()V", defaultInit);

        return cls;
    }
}
