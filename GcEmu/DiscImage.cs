using System.Runtime.CompilerServices;
using System.Text;

namespace GcEmu;

abstract class DiscImage
{
    public const long GcDiscSize = 1_459_978_240;

    public byte[] Header { get; protected set; } = new byte[0x440];
    public string GameId { get; protected set; } = "";
    public string GameName { get; protected set; } = "";
    public uint DvdMagic { get; protected set; }
    public uint DolOffset { get; protected set; }
    public uint FstOffset { get; protected set; }
    public uint FstSize { get; protected set; }

    public abstract void Read(byte[] dest, int destOffset, long discOffset, int length);

    public byte[] ReadBytes(long offset, int length)
    {
        var buf = new byte[length];
        Read(buf, 0, offset, length);
        return buf;
    }

    public abstract void Close();

    protected void ParseHeader()
    {
        GameId = Encoding.ASCII.GetString(Header, 0, 6).TrimEnd('\0');
        DvdMagic = ReadBe32(Header, 0x1C);
        GameName = Encoding.ASCII.GetString(Header, 0x20, 0x3E0).TrimEnd('\0');
        DolOffset = ReadBe32(Header, 0x420);
        FstOffset = ReadBe32(Header, 0x424);
        FstSize = ReadBe32(Header, 0x428);
    }

    public static DiscImage Open(string path)
    {
        using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] magic = new byte[4];
        probe.ReadExactly(magic);
        probe.Close();

        if (magic[0] == 'C' && magic[1] == 'I' && magic[2] == 'S' && magic[3] == 'O')
            return new CisoDiscImage(path);

        return new RawDiscImage(path);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadBe32(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadBe16(byte[] data, int offset) =>
        (ushort)(data[offset] << 8 | data[offset + 1]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadLe32(byte[] data, int offset) =>
        (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
}

class RawDiscImage : DiscImage
{
    readonly FileStream _fs;
    readonly long _fileLength;

    public RawDiscImage(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _fileLength = _fs.Length;

        Header = new byte[0x440];
        _fs.Position = 0;
        _fs.ReadExactly(Header);
        ParseHeader();

        Log.Info($"GCM (raw): {GameId} \"{GameName}\"");
        Log.Info($"  DOL @ 0x{DolOffset:X8}, FST @ 0x{FstOffset:X8} ({FstSize} bytes)");
    }

    public override void Read(byte[] dest, int destOffset, long discOffset, int length)
    {
        if (discOffset + length > _fileLength)
            length = (int)Math.Max(0, _fileLength - discOffset);
        if (length <= 0) return;

        lock (_fs)
        {
            _fs.Position = discOffset;
            _fs.ReadExactly(dest, destOffset, length);
        }
    }

    public override void Close() => _fs.Dispose();
}

/// <summary>
/// CISO (Compact ISO) reader for GameCube/Wii disc images.
/// Header: 0x8000 bytes total.
///   [0..3]   magic "CISO" (little-endian 0x4F534943)
///   [4..7]   block_size (little-endian, typically 2 MiB)
///   [8..0x7FFF] block map (1 byte per block: 0=unused/zeroed, 1=present in file)
/// Data blocks follow the header in order, only for map entries == 1.
/// </summary>
class CisoDiscImage : DiscImage
{
    const int CisoHeaderSize = 0x8000;
    const int CisoMapOffset = 8;
    const int CisoMapSize = CisoHeaderSize - CisoMapOffset;

    readonly FileStream _fs;
    readonly long _fileLength;
    readonly uint _blockSize;
    readonly byte[] _map = new byte[CisoMapSize];
    readonly ushort[] _blockIndex;
    readonly int _numBlocks;

    public CisoDiscImage(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _fileLength = _fs.Length;

        byte[] hdr = new byte[CisoHeaderSize];
        _fs.Position = 0;
        _fs.ReadExactly(hdr);

        uint magic = ReadLe32(hdr, 0);
        if (magic != 0x4F534943)
            throw new InvalidDataException($"Not a CISO file (magic=0x{magic:X8})");

        _blockSize = ReadLe32(hdr, 4);
        if (_blockSize == 0 || (_blockSize & (_blockSize - 1)) != 0)
            throw new InvalidDataException($"Invalid CISO block size: {_blockSize}");

        Array.Copy(hdr, CisoMapOffset, _map, 0, CisoMapSize);

        _numBlocks = 0;
        for (int i = 0; i < CisoMapSize; i++)
            if (_map[i] != 0) _numBlocks = i + 1;
        if (_numBlocks == 0) _numBlocks = CisoMapSize;

        _blockIndex = new ushort[CisoMapSize];
        ushort fileBlockIdx = 0;
        for (int i = 0; i < CisoMapSize; i++)
        {
            if (_map[i] != 0)
            {
                _blockIndex[i] = fileBlockIdx;
                fileBlockIdx++;
            }
            else
            {
                _blockIndex[i] = ushort.MaxValue;
            }
        }

        Header = new byte[0x440];
        Read(Header, 0, 0, 0x440);
        ParseHeader();

        Log.Info($"GCM (CISO): {GameId} \"{GameName}\"");
        Log.Info($"  block_size={_blockSize / 1024}K, {fileBlockIdx} data blocks of {_numBlocks} total");
        Log.Info($"  DOL @ 0x{DolOffset:X8}, FST @ 0x{FstOffset:X8} ({FstSize} bytes)");
    }

    public override void Read(byte[] dest, int destOffset, long discOffset, int length)
    {
        if (length <= 0) return;

        int remaining = length;
        long pos = discOffset;
        int dOff = destOffset;

        while (remaining > 0)
        {
            int blockIdx = (int)(pos / _blockSize);
            int blockOff = (int)(pos % _blockSize);
            int chunkLen = (int)Math.Min(remaining, _blockSize - blockOff);

            if (blockIdx < 0 || blockIdx >= CisoMapSize)
            {
                Array.Clear(dest, dOff, chunkLen);
            }
            else if (_map[blockIdx] == 0)
            {
                Array.Clear(dest, dOff, chunkLen);
            }
            else
            {
                long fileOff = CisoHeaderSize + (long)_blockIndex[blockIdx] * _blockSize + blockOff;
                if (fileOff + chunkLen > _fileLength)
                {
                    int valid = (int)Math.Max(0, _fileLength - fileOff);
                    if (valid > 0)
                    {
                        lock (_fs) { _fs.Position = fileOff; _fs.ReadExactly(dest, dOff, valid); }
                    }
                    if (chunkLen > valid)
                        Array.Clear(dest, dOff + valid, chunkLen - valid);
                }
                else
                {
                    lock (_fs) { _fs.Position = fileOff; _fs.ReadExactly(dest, dOff, chunkLen); }
                }
            }

            pos += chunkLen;
            dOff += chunkLen;
            remaining -= chunkLen;
        }
    }

    public override void Close() => _fs.Dispose();
}

class DolLoader
{
    public uint EntryPoint { get; private set; }
    public uint BssAddr { get; private set; }
    public uint BssSize { get; private set; }

    public void Load(DiscImage disc, byte[] ram)
    {
        byte[] header = disc.ReadBytes(disc.DolOffset, 0x100);

        EntryPoint = DiscImage.ReadBe32(header, 0xE0);
        Log.Info($"DOL entry point: 0x{EntryPoint:X8}");

        for (int i = 0; i < 7; i++)
            LoadSection(disc, header, i, 0x000, 0x048, 0x090, ram);
        for (int i = 0; i < 11; i++)
            LoadSection(disc, header, i, 0x01C, 0x064, 0x0AC, ram);

        BssAddr = DiscImage.ReadBe32(header, 0x0D8);
        BssSize = DiscImage.ReadBe32(header, 0x0DC);
        Console.Error.WriteLine($"  BSS: addr=0x{BssAddr:X8} size=0x{BssSize:X8} (not cleared by loader - game's __init_data handles it)");
    }

    void LoadSection(DiscImage disc, byte[] header, int idx,
        int fileOffBase, int addrBase, int sizeBase, byte[] ram)
    {
        uint fileOff = DiscImage.ReadBe32(header, fileOffBase + idx * 4);
        uint addr    = DiscImage.ReadBe32(header, addrBase + idx * 4);
        uint size    = DiscImage.ReadBe32(header, sizeBase + idx * 4);
        if (size == 0 || fileOff == 0) return;

        uint phys = addr & 0x01FFFFFF;
        if (phys + size > (uint)ram.Length) return;
        Console.Error.WriteLine($"  DOL Section {(fileOffBase == 0 ? "T" : "D")}{idx}: file=0x{fileOff:X8} addr=0x{addr:X8} size=0x{size:X8} phys=[0x{phys:X8}-0x{phys+size:X8})");

        disc.Read(ram, (int)phys, disc.DolOffset + fileOff, (int)size);
        Log.Info($"  Section: file=0x{fileOff:X} -> 0x{addr:X8} ({size} bytes)");
    }
}
