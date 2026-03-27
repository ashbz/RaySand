using System.Text;

namespace J2meEmu;

enum CpTag : byte
{
    Utf8 = 1, Integer = 3, Float = 4, Long = 5, Double = 6,
    Class = 7, String = 8, Fieldref = 9, Methodref = 10,
    InterfaceMethodref = 11, NameAndType = 12
}

[Flags]
enum ClassAccessFlags : ushort
{
    Public = 0x0001, Final = 0x0010, Super = 0x0020,
    Interface = 0x0200, Abstract = 0x0400
}

[Flags]
enum MethodAccessFlags : ushort
{
    Public = 0x0001, Private = 0x0002, Protected = 0x0004,
    Static = 0x0008, Final = 0x0010, Synchronized = 0x0020,
    Native = 0x0100, Abstract = 0x0400
}

[Flags]
enum FieldAccessFlags : ushort
{
    Public = 0x0001, Private = 0x0002, Protected = 0x0004,
    Static = 0x0008, Final = 0x0010, Volatile = 0x0040,
    Transient = 0x0080
}

struct CpEntry
{
    public CpTag Tag;
    public string? Utf8;
    public int IntVal;
    public float FloatVal;
    public long LongVal;
    public double DoubleVal;
    public ushort Index1, Index2;
}

struct ExceptionEntry
{
    public ushort StartPc, EndPc, HandlerPc, CatchType;
}

class ClassFieldInfo
{
    public FieldAccessFlags Flags;
    public string Name = "";
    public string Descriptor = "";
    public object? ConstantValue;
}

class ClassMethodInfo
{
    public MethodAccessFlags Flags;
    public string Name = "";
    public string Descriptor = "";
    public byte[]? Code;
    public int MaxStack, MaxLocals;
    public ExceptionEntry[] ExceptionTable = Array.Empty<ExceptionEntry>();
}

class ClassFile
{
    public ushort MinorVersion, MajorVersion;
    public CpEntry[] ConstantPool = Array.Empty<CpEntry>();
    public ClassAccessFlags AccessFlags;
    public string ThisClass = "";
    public string? SuperClass;
    public string[] Interfaces = Array.Empty<string>();
    public ClassFieldInfo[] Fields = Array.Empty<ClassFieldInfo>();
    public ClassMethodInfo[] Methods = Array.Empty<ClassMethodInfo>();

    public string GetUtf8(int index)
    {
        if (index > 0 && index < ConstantPool.Length && ConstantPool[index].Tag == CpTag.Utf8)
            return ConstantPool[index].Utf8 ?? "";
        return "";
    }

    public string GetClassName(int index)
    {
        if (index > 0 && index < ConstantPool.Length && ConstantPool[index].Tag == CpTag.Class)
            return GetUtf8(ConstantPool[index].Index1);
        return "";
    }

    public string GetString(int index)
    {
        if (index > 0 && index < ConstantPool.Length && ConstantPool[index].Tag == CpTag.String)
            return GetUtf8(ConstantPool[index].Index1);
        return "";
    }

    public (string className, string name, string descriptor) GetMemberRef(int index)
    {
        if (index <= 0 || index >= ConstantPool.Length) return ("", "", "");
        ref var cp = ref ConstantPool[index];
        string cls = GetClassName(cp.Index1);
        if (cp.Index2 <= 0 || cp.Index2 >= ConstantPool.Length) return (cls, "", "");
        ref var nat = ref ConstantPool[cp.Index2];
        return (cls, GetUtf8(nat.Index1), GetUtf8(nat.Index2));
    }

    public static ClassFile Parse(byte[] data)
    {
        var r = new BigEndianReader(data);
        uint magic = r.U4();
        if (magic != 0xCAFEBABE) throw new Exception($"Bad class magic: 0x{magic:X8}");

        var cf = new ClassFile { MinorVersion = r.U2(), MajorVersion = r.U2() };

        int cpCount = r.U2();
        cf.ConstantPool = new CpEntry[cpCount];
        for (int i = 1; i < cpCount; i++)
        {
            var tag = (CpTag)r.U1();
            ref var e = ref cf.ConstantPool[i];
            e.Tag = tag;
            switch (tag)
            {
                case CpTag.Utf8:
                    int len = r.U2();
                    e.Utf8 = Encoding.UTF8.GetString(r.Bytes(len));
                    break;
                case CpTag.Integer: e.IntVal = (int)r.U4(); break;
                case CpTag.Float: e.FloatVal = BitConverter.Int32BitsToSingle((int)r.U4()); break;
                case CpTag.Long:
                    e.LongVal = (long)r.U4() << 32 | r.U4();
                    i++;
                    break;
                case CpTag.Double:
                    e.DoubleVal = BitConverter.Int64BitsToDouble((long)r.U4() << 32 | r.U4());
                    i++;
                    break;
                case CpTag.Class: case CpTag.String:
                    e.Index1 = r.U2();
                    break;
                case CpTag.Fieldref: case CpTag.Methodref:
                case CpTag.InterfaceMethodref: case CpTag.NameAndType:
                    e.Index1 = r.U2(); e.Index2 = r.U2();
                    break;
                default:
                    throw new Exception($"Unknown CP tag: {(int)tag} at index {i}");
            }
        }

        cf.AccessFlags = (ClassAccessFlags)r.U2();
        cf.ThisClass = cf.GetClassName(r.U2());
        ushort superIdx = r.U2();
        cf.SuperClass = superIdx != 0 ? cf.GetClassName(superIdx) : null;

        int ifCount = r.U2();
        cf.Interfaces = new string[ifCount];
        for (int i = 0; i < ifCount; i++)
            cf.Interfaces[i] = cf.GetClassName(r.U2());

        cf.Fields = ReadFields(r, cf, r.U2());
        cf.Methods = ReadMethods(r, cf, r.U2());
        SkipAttributes(r, r.U2());

        return cf;
    }

    static ClassFieldInfo[] ReadFields(BigEndianReader r, ClassFile cf, int count)
    {
        var fields = new ClassFieldInfo[count];
        for (int i = 0; i < count; i++)
        {
            var f = new ClassFieldInfo
            {
                Flags = (FieldAccessFlags)r.U2(),
                Name = cf.GetUtf8(r.U2()),
                Descriptor = cf.GetUtf8(r.U2())
            };
            int attrCount = r.U2();
            for (int a = 0; a < attrCount; a++)
            {
                string attrName = cf.GetUtf8(r.U2());
                int attrLen = (int)r.U4();
                if (attrName == "ConstantValue" && attrLen >= 2)
                {
                    ushort cvIdx = r.U2();
                    if (cvIdx > 0 && cvIdx < cf.ConstantPool.Length)
                    {
                        ref var cv = ref cf.ConstantPool[cvIdx];
                        f.ConstantValue = cv.Tag switch
                        {
                            CpTag.Integer => cv.IntVal,
                            CpTag.Long => cv.LongVal,
                            CpTag.Float => cv.FloatVal,
                            CpTag.Double => cv.DoubleVal,
                            CpTag.String => cf.GetUtf8(cv.Index1),
                            _ => null
                        };
                    }
                    else r.Skip(attrLen - 2);
                }
                else r.Skip(attrLen);
            }
            fields[i] = f;
        }
        return fields;
    }

    static ClassMethodInfo[] ReadMethods(BigEndianReader r, ClassFile cf, int count)
    {
        var methods = new ClassMethodInfo[count];
        for (int i = 0; i < count; i++)
        {
            var m = new ClassMethodInfo
            {
                Flags = (MethodAccessFlags)r.U2(),
                Name = cf.GetUtf8(r.U2()),
                Descriptor = cf.GetUtf8(r.U2())
            };
            int attrCount = r.U2();
            for (int a = 0; a < attrCount; a++)
            {
                string attrName = cf.GetUtf8(r.U2());
                int attrLen = (int)r.U4();
                if (attrName == "Code")
                {
                    m.MaxStack = r.U2();
                    m.MaxLocals = r.U2();
                    int codeLen = (int)r.U4();
                    m.Code = r.Bytes(codeLen);
                    int etLen = r.U2();
                    m.ExceptionTable = new ExceptionEntry[etLen];
                    for (int e = 0; e < etLen; e++)
                        m.ExceptionTable[e] = new ExceptionEntry
                        {
                            StartPc = r.U2(), EndPc = r.U2(),
                            HandlerPc = r.U2(), CatchType = r.U2()
                        };
                    SkipAttributes(r, r.U2());
                }
                else r.Skip(attrLen);
            }
            methods[i] = m;
        }
        return methods;
    }

    static void SkipAttributes(BigEndianReader r, int count)
    {
        for (int i = 0; i < count; i++)
        {
            r.U2();
            r.Skip((int)r.U4());
        }
    }
}

class BigEndianReader
{
    readonly byte[] _data;
    int _pos;

    public BigEndianReader(byte[] data) => _data = data;
    public int Position => _pos;

    public byte U1() => _data[_pos++];

    public ushort U2()
    {
        int v = _data[_pos] << 8 | _data[_pos + 1];
        _pos += 2;
        return (ushort)v;
    }

    public uint U4()
    {
        uint v = (uint)_data[_pos] << 24 | (uint)_data[_pos + 1] << 16
               | (uint)_data[_pos + 2] << 8 | _data[_pos + 3];
        _pos += 4;
        return v;
    }

    public byte[] Bytes(int n)
    {
        var b = new byte[n];
        Array.Copy(_data, _pos, b, 0, n);
        _pos += n;
        return b;
    }

    public void Skip(int n) => _pos += n;
}
