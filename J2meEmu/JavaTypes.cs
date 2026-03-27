namespace J2meEmu;

struct JValue
{
    long _bits;
    object? _ref;

    public int Int { get => (int)_bits; set => _bits = value; }
    public long Long { get => _bits; set => _bits = value; }

    public float Float
    {
        get => BitConverter.Int32BitsToSingle((int)_bits);
        set => _bits = BitConverter.SingleToInt32Bits(value);
    }

    public double Double
    {
        get => BitConverter.Int64BitsToDouble(_bits);
        set => _bits = BitConverter.DoubleToInt64Bits(value);
    }

    public object? Ref { get => _ref; set => _ref = value; }
    public bool IsNull => _ref == null && _bits == 0;

    public static JValue OfInt(int v) => new() { _bits = v };
    public static JValue OfLong(long v) => new() { _bits = v };
    public static JValue OfFloat(float v) { var j = new JValue(); j.Float = v; return j; }
    public static JValue OfDouble(double v) { var j = new JValue(); j.Double = v; return j; }
    public static JValue OfRef(object? v) => new() { _ref = v };
    public static readonly JValue Null = new();
}

class JavaObject
{
    public JavaClass Class;
    public JValue[] Fields;
    public object? NativeData;
    public readonly object Monitor = new();

    public JavaObject(JavaClass cls)
    {
        Class = cls;
        Fields = cls.InstanceFieldCount > 0 ? new JValue[cls.InstanceFieldCount] : Array.Empty<JValue>();
    }

    public JavaObject(JavaClass cls, object? nativeData) : this(cls) => NativeData = nativeData;

    public override string ToString() => $"JavaObject({Class.Name})";
}

class JavaArray : JavaObject
{
    public int Length;
    public int[] IntData = Array.Empty<int>();
    public long[] LongData = Array.Empty<long>();
    public float[] FloatData = Array.Empty<float>();
    public double[] DoubleData = Array.Empty<double>();
    public byte[] ByteData = Array.Empty<byte>();
    public char[] CharData = Array.Empty<char>();
    public short[] ShortData = Array.Empty<short>();
    public object?[] RefData = Array.Empty<object?>();
    public ArrayKind Kind;

    public enum ArrayKind : byte { Int, Long, Float, Double, Byte, Char, Short, Boolean, Ref }

    public JavaArray(JavaClass cls, ArrayKind kind, int length) : base(cls)
    {
        Kind = kind;
        Length = Math.Max(0, Math.Min(length, 4 * 1024 * 1024));
        switch (kind)
        {
            case ArrayKind.Int: IntData = new int[Length]; break;
            case ArrayKind.Long: LongData = new long[Length]; break;
            case ArrayKind.Float: FloatData = new float[Length]; break;
            case ArrayKind.Double: DoubleData = new double[Length]; break;
            case ArrayKind.Byte: case ArrayKind.Boolean: ByteData = new byte[Length]; break;
            case ArrayKind.Char: CharData = new char[Length]; break;
            case ArrayKind.Short: ShortData = new short[Length]; break;
            case ArrayKind.Ref: RefData = new object?[Length]; break;
        }
    }

    public override string ToString() => $"JavaArray({Kind}[{Length}])";
}

class JavaClass
{
    public string Name;
    public JavaClass? Super;
    public JavaClass[] Interfaces = Array.Empty<JavaClass>();
    public ClassFile? File;
    public ClassAccessFlags Flags;
    public Dictionary<string, JavaMethod> Methods = new();
    public Dictionary<string, JavaField> Fields = new();
    public JValue[] StaticFields = Array.Empty<JValue>();
    public int InstanceFieldCount;
    public bool Initialized;
    public List<JavaField> InstanceFieldList = new();
    public List<JavaField> StaticFieldList = new();

    public JavaClass(string name) => Name = name;

    public JavaMethod? FindMethod(string name, string desc)
    {
        string key = $"{name}{desc}";
        if (Methods.TryGetValue(key, out var m)) return m;
        if (Super != null) return Super.FindMethod(name, desc);
        foreach (var iface in Interfaces)
        {
            var im = iface.FindMethod(name, desc);
            if (im != null) return im;
        }
        return null;
    }

    public JavaField? FindField(string name, string? desc = null)
    {
        if (desc != null && Fields.TryGetValue($"{name}:{desc}", out var fd)) return fd;
        if (Fields.TryGetValue(name, out var f)) return f;
        if (Super != null) return Super.FindField(name, desc);
        return null;
    }

    public bool IsAssignableTo(JavaClass other)
    {
        if (this == other) return true;
        if (Name == other.Name) return true;
        if (Super?.IsAssignableTo(other) == true) return true;
        foreach (var iface in Interfaces)
            if (iface.IsAssignableTo(other)) return true;
        return false;
    }
}

class JavaMethod
{
    public string Name;
    public string Descriptor;
    public JavaClass Owner;
    public MethodAccessFlags Flags;
    public byte[]? Code;
    public int MaxStack, MaxLocals;
    public ExceptionEntry[] ExceptionTable = Array.Empty<ExceptionEntry>();
    public Func<JvmThread, JValue[], JValue>? NativeHandler;

    public bool IsStatic => (Flags & MethodAccessFlags.Static) != 0;
    public bool IsNative => NativeHandler != null;
    public bool IsAbstract => (Flags & MethodAccessFlags.Abstract) != 0;

    public JavaMethod(string name, string desc, JavaClass owner)
    {
        Name = name; Descriptor = desc; Owner = owner;
    }

    public int ParameterSlots()
    {
        int slots = 0;
        int i = 1;
        while (i < Descriptor.Length && Descriptor[i] != ')')
        {
            char c = Descriptor[i];
            if (c == 'J' || c == 'D') { slots += 2; i++; }
            else if (c == 'L') { slots++; i = Descriptor.IndexOf(';', i) + 1; }
            else if (c == '[') { i++; }
            else { slots++; i++; }
        }
        return slots;
    }
}

class JavaField
{
    public string Name;
    public string Descriptor;
    public JavaClass Owner;
    public FieldAccessFlags Flags;
    public int Index;
    public bool IsStatic => (Flags & FieldAccessFlags.Static) != 0;

    public JavaField(string name, string desc, JavaClass owner, int index)
    {
        Name = name; Descriptor = desc; Owner = owner; Index = index;
    }
}

class JvmFrame
{
    public JValue[] Locals;
    public JValue[] Stack;
    public int SP;
    public int PC;
    public byte[] Code;
    public JavaMethod Method;
    public ClassFile? ClassFile;

    public JvmFrame(JavaMethod method)
    {
        Method = method;
        int maxLocals = Math.Max(method.MaxLocals, method.ParameterSlots() + (method.IsStatic ? 0 : 1));
        int maxStack = Math.Max(method.MaxStack, 4);
        Locals = new JValue[Math.Max(maxLocals, 8)];
        Stack = new JValue[Math.Max(maxStack, 16)];
        Code = method.Code ?? Array.Empty<byte>();
        ClassFile = method.Owner.File;
    }

    public void EnsureLocal(int idx)
    {
        if (idx >= Locals.Length) Array.Resize(ref Locals, idx + 8);
    }

    public void Push(JValue v)
    {
        if (SP >= Stack.Length) Array.Resize(ref Stack, Stack.Length * 2);
        Stack[SP++] = v;
    }
    public JValue Pop() => SP > 0 ? Stack[--SP] : JValue.Null;
    public JValue Peek() => SP > 0 ? Stack[SP - 1] : JValue.Null;
}

class JvmException : Exception
{
    public JavaObject JavaEx;
    public JvmException(JavaObject ex) : base($"Java: {ex.Class.Name}: {ex.NativeData}") => JavaEx = ex;
}

class JvmThread
{
    public Stack<JvmFrame> Frames = new();
    public JvmClassLoader Loader;
    public string Name;
    public bool Alive;
    public int MaxDepth = 128;
    public long InstructionCount;
    public long MaxInstructions = long.MaxValue;

    public JvmThread(JvmClassLoader loader, string name)
    {
        Loader = loader;
        Name = name;
        Alive = true;
    }

    public JValue Invoke(JavaMethod method, JValue[] args)
    {
        if (Frames.Count >= MaxDepth)
            throw new JvmException(Loader.CreateException("java/lang/StackOverflowError", "Stack overflow"));

        if (method.NativeHandler != null)
            return method.NativeHandler(this, args);

        if (method.Code == null)
        {
            if (method.IsAbstract)
                throw new JvmException(Loader.CreateException("java/lang/AbstractMethodError", $"{method.Owner.Name}.{method.Name}{method.Descriptor}"));
            return JValue.Null;
        }

        var frame = new JvmFrame(method);
        int argIdx = 0;
        for (int i = 0; i < args.Length && i < frame.Locals.Length; i++)
            frame.Locals[i] = args[argIdx++];

        Frames.Push(frame);
        try
        {
            return JvmInterpreter.Execute(this, frame);
        }
        finally
        {
            Frames.Pop();
        }
    }
}
