using System.Buffers.Binary;
using System.Text;

namespace N64Emu;

sealed class N64Cart
{
    public byte[] Rom = Array.Empty<byte>();
    public string Title = "";
    public string CountryCode = "";
    public uint EntryPoint;
    public uint CicSeed;
    public int CicType;
    public bool Loaded;

    public bool Load(string path)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 0x1000)
            {
                Log.Error($"ROM too small: {raw.Length} bytes");
                return false;
            }

            DetectAndSwap(raw);
            Rom = raw;
            ParseHeader();
            DetectCic();
            Loaded = true;
            Log.Info($"Loaded ROM: \"{Title}\" ({Rom.Length / 1024}KB) CIC={CicType} Entry=0x{EntryPoint:X8}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"ROM load failed: {ex.Message}");
            return false;
        }
    }

    static void DetectAndSwap(byte[] data)
    {
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        switch (magic)
        {
            case 0x80371240: // z64 big-endian (native)
                break;
            case 0x37804012: // v64 byte-swapped
                for (int i = 0; i < data.Length - 1; i += 2)
                    (data[i], data[i + 1]) = (data[i + 1], data[i]);
                break;
            case 0x40123780: // n64 little-endian
                for (int i = 0; i < data.Length - 3; i += 4)
                {
                    (data[i], data[i + 3]) = (data[i + 3], data[i]);
                    (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
                }
                break;
            default:
                Log.Info($"Unknown ROM format magic: 0x{magic:X8}, assuming z64");
                break;
        }
    }

    void ParseHeader()
    {
        EntryPoint = BinaryPrimitives.ReadUInt32BigEndian(Rom.AsSpan(0x08));

        var titleBytes = Rom.AsSpan(0x20, 20);
        Title = Encoding.ASCII.GetString(titleBytes).TrimEnd(' ', '\0');

        CountryCode = ((char)Rom[0x3E]).ToString();
    }

    void DetectCic()
    {
        if (Rom.Length < 0x1000) { CicType = 6102; CicSeed = 0x3F; return; }

        uint ipl3Hash = 0;
        for (int i = 0x40; i < 0x1000; i++)
            ipl3Hash = (ipl3Hash << 1) | (ipl3Hash >> 31) + Rom[i];

        (CicType, CicSeed) = ipl3Hash switch
        {
            0x6170A4A1 => (6101, 0x3Fu),
            0x90BB6CB5 => (6102, 0x3Fu),
            0x0B050EE0 => (6103, 0x78u),
            0x98BC2C86 => (6105, 0x91u),
            0xACC8580A => (6106, 0x85u),
            _ => (6102, 0x3Fu), // default to 6102 (SM64)
        };
    }

    public uint Read32(uint offset)
    {
        if (offset + 3 < (uint)Rom.Length)
            return BinaryPrimitives.ReadUInt32BigEndian(Rom.AsSpan((int)offset));
        return 0;
    }

    public ushort Read16(uint offset)
    {
        if (offset + 1 < (uint)Rom.Length)
            return BinaryPrimitives.ReadUInt16BigEndian(Rom.AsSpan((int)offset));
        return 0;
    }

    public byte Read8(uint offset)
    {
        return offset < (uint)Rom.Length ? Rom[offset] : (byte)0;
    }
}
