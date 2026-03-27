using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PspEmu;

/// <summary>
/// Reads ISO 9660 filesystem images (raw .iso and compressed .cso).
/// Used to load PSP UMD disc images and provide virtual file I/O for disc0: paths.
/// </summary>
sealed class IsoReader : IDisposable
{
    const int SectorSize = 2048;

    readonly Stream _stream;
    readonly bool _isCso;

    // CSO index
    uint[]? _csoIndex;
    int _csoBlockSize;
    byte _csoAlign;

    // Root directory LBA/size cached from PVD
    uint _rootLba;
    uint _rootSize;

    // Keep the ISO path for reference
    public string IsoPath { get; }

    IsoReader(Stream stream, bool isCso, string path)
    {
        _stream = stream;
        _isCso = isCso;
        IsoPath = path;
    }

    public static IsoReader? Open(string path)
    {
        if (!File.Exists(path)) return null;

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] magic = new byte[4];
        fs.Read(magic, 0, 4);
        fs.Position = 0;

        bool isCso = magic[0] == 'C' && magic[1] == 'I' && magic[2] == 'S' && magic[3] == 'O';

        var reader = new IsoReader(fs, isCso, path);
        if (isCso && !reader.LoadCsoIndex())
        {
            fs.Dispose();
            return null;
        }

        if (!reader.ReadPvd())
        {
            fs.Dispose();
            return null;
        }

        return reader;
    }

    bool LoadCsoIndex()
    {
        byte[] header = new byte[24];
        _stream.Position = 0;
        if (_stream.Read(header, 0, 24) < 24) return false;

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        ulong totalBytes = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        _csoBlockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        _csoAlign = header[21];

        if (_csoBlockSize == 0) _csoBlockSize = SectorSize;

        int totalBlocks = (int)((totalBytes + (ulong)_csoBlockSize - 1) / (ulong)_csoBlockSize);
        _csoIndex = new uint[totalBlocks + 1];

        _stream.Position = headerSize;
        byte[] indexBuf = new byte[(totalBlocks + 1) * 4];
        _stream.Read(indexBuf, 0, indexBuf.Length);
        for (int i = 0; i <= totalBlocks; i++)
            _csoIndex[i] = BinaryPrimitives.ReadUInt32LittleEndian(indexBuf.AsSpan(i * 4));

        return true;
    }

    /// <summary>Read a single 2048-byte sector by LBA.</summary>
    byte[] ReadSector(uint lba)
    {
        byte[] sector = new byte[SectorSize];
        ReadBytes(lba * SectorSize, sector, 0, SectorSize);
        return sector;
    }

    /// <summary>Read arbitrary bytes from the ISO image at a given offset.</summary>
    void ReadBytes(long offset, byte[] dest, int destOff, int count)
    {
        if (!_isCso)
        {
            _stream.Position = offset;
            int read = 0;
            while (read < count)
            {
                int r = _stream.Read(dest, destOff + read, count - read);
                if (r <= 0) break;
                read += r;
            }
            return;
        }

        // CSO: decompress block by block
        int blockSize = _csoBlockSize;
        while (count > 0)
        {
            int blockIdx = (int)(offset / blockSize);
            int blockOff = (int)(offset % blockSize);
            int toCopy = Math.Min(count, blockSize - blockOff);

            byte[] block = DecompressCsoBlock(blockIdx);
            Array.Copy(block, blockOff, dest, destOff, Math.Min(toCopy, block.Length - blockOff));

            offset += toCopy;
            destOff += toCopy;
            count -= toCopy;
        }
    }

    byte[] DecompressCsoBlock(int blockIdx)
    {
        if (_csoIndex == null || blockIdx >= _csoIndex.Length - 1)
            return new byte[_csoBlockSize];

        uint rawIdx = _csoIndex[blockIdx];
        uint rawNext = _csoIndex[blockIdx + 1];
        bool uncompressed = (rawIdx & 0x80000000) != 0;
        uint pos = (rawIdx & 0x7FFFFFFF) << _csoAlign;
        uint posNext = (rawNext & 0x7FFFFFFF) << _csoAlign;
        int compLen = (int)(posNext - pos);

        _stream.Position = pos;
        byte[] compressed = new byte[compLen];
        _stream.Read(compressed, 0, compLen);

        if (uncompressed)
        {
            if (compLen >= _csoBlockSize) return compressed;
            byte[] padded = new byte[_csoBlockSize];
            Array.Copy(compressed, padded, compLen);
            return padded;
        }

        byte[] output = new byte[_csoBlockSize];
        try
        {
            using var ms = new MemoryStream(compressed);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            int read = 0;
            while (read < _csoBlockSize)
            {
                int r = deflate.Read(output, read, _csoBlockSize - read);
                if (r <= 0) break;
                read += r;
            }
        }
        catch
        {
            // Decompression failure — return what we have
        }
        return output;
    }

    bool ReadPvd()
    {
        byte[] pvd = ReadSector(16);

        if (pvd[0] != 0x01 || pvd[1] != 'C' || pvd[2] != 'D' || pvd[3] != '0' || pvd[4] != '0' || pvd[5] != '1')
        {
            Log.Error(LogCat.Loader, "ISO: Primary Volume Descriptor not found");
            return false;
        }

        // Root directory record at offset 156 in PVD
        _rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2));
        _rootSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10));

        Log.Write(LogCat.Loader, $"ISO: PVD ok, root dir LBA={_rootLba} size={_rootSize}");
        return true;
    }

    // ── Directory / File access ──

    struct DirEntry
    {
        public string Name;
        public uint Lba;
        public uint Size;
        public bool IsDir;
    }

    List<DirEntry> ReadDirectory(uint lba, uint size)
    {
        var entries = new List<DirEntry>();
        byte[] dirData = new byte[size];
        ReadBytes((long)lba * SectorSize, dirData, 0, (int)size);

        int pos = 0;
        while (pos < size)
        {
            byte recLen = dirData[pos];
            if (recLen == 0)
            {
                // Skip to next sector boundary
                int nextSector = ((pos / SectorSize) + 1) * SectorSize;
                if (nextSector >= size) break;
                pos = nextSector;
                continue;
            }

            if (pos + recLen > size) break;

            uint entryLba = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 2));
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos + 10));
            byte flags = dirData[pos + 25];
            byte nameLen = dirData[pos + 32];

            bool isDir = (flags & 0x02) != 0;
            string name = "";

            if (nameLen == 1 && dirData[pos + 33] == 0)
                name = ".";
            else if (nameLen == 1 && dirData[pos + 33] == 1)
                name = "..";
            else if (nameLen > 0)
            {
                name = Encoding.ASCII.GetString(dirData, pos + 33, nameLen);
                // Strip ISO ";1" version suffix
                int semi = name.IndexOf(';');
                if (semi >= 0) name = name[..semi];
            }

            if (name != "." && name != "..")
                entries.Add(new DirEntry { Name = name, Lba = entryLba, Size = entrySize, IsDir = isDir });

            pos += recLen;
        }

        return entries;
    }

    DirEntry? FindEntry(string path)
    {
        string[] parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        uint curLba = _rootLba;
        uint curSize = _rootSize;

        for (int i = 0; i < parts.Length; i++)
        {
            var entries = ReadDirectory(curLba, curSize);
            var match = entries.Find(e => string.Equals(e.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (match.Name == null) return null;

            if (i < parts.Length - 1)
            {
                if (!match.IsDir) return null;
                curLba = match.Lba;
                curSize = match.Size;
            }
            else
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Read an entire file from the ISO by its path (e.g. "PSP_GAME/SYSDIR/EBOOT.BIN").</summary>
    public byte[]? ReadFile(string path)
    {
        var entry = FindEntry(path);
        if (entry == null || entry.Value.IsDir)
        {
            Log.Warn(LogCat.Loader, $"ISO: file not found: {path}");
            return null;
        }

        byte[] data = new byte[entry.Value.Size];
        ReadBytes((long)entry.Value.Lba * SectorSize, data, 0, (int)entry.Value.Size);
        return data;
    }

    /// <summary>Check if a path exists in the ISO.</summary>
    public bool FileExists(string path)
    {
        var entry = FindEntry(path);
        return entry != null && !entry.Value.IsDir;
    }

    /// <summary>Get file size without reading data. Returns -1 if not found.</summary>
    public long GetFileSize(string path)
    {
        var entry = FindEntry(path);
        if (entry == null || entry.Value.IsDir) return -1;
        return entry.Value.Size;
    }

    /// <summary>Check if a directory exists in the ISO.</summary>
    public bool DirectoryExists(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/" || path == "\\") return true;
        var entry = FindEntry(path);
        return entry != null && entry.Value.IsDir;
    }

    /// <summary>List files/dirs in a directory.</summary>
    public (string Name, bool IsDir, long Size)[]? ListDirectory(string path)
    {
        uint lba, size;
        if (string.IsNullOrEmpty(path) || path == "/" || path == "\\")
        {
            lba = _rootLba;
            size = _rootSize;
        }
        else
        {
            var entry = FindEntry(path);
            if (entry == null || !entry.Value.IsDir) return null;
            lba = entry.Value.Lba;
            size = entry.Value.Size;
        }

        var entries = ReadDirectory(lba, size);
        return entries.Select(e => (e.Name, e.IsDir, (long)e.Size)).ToArray();
    }

    /// <summary>Open a virtual file handle for streaming reads from the ISO.</summary>
    public IsoFileStream? OpenFile(string path)
    {
        var entry = FindEntry(path);
        if (entry == null || entry.Value.IsDir) return null;
        return new IsoFileStream(this, entry.Value.Lba, entry.Value.Size);
    }

    /// <summary>Virtual file stream that reads from a specific extent within the ISO.</summary>
    public sealed class IsoFileStream : IDisposable
    {
        readonly IsoReader _iso;
        readonly uint _lba;
        readonly uint _size;
        long _position;

        internal IsoFileStream(IsoReader iso, uint lba, uint size)
        {
            _iso = iso;
            _lba = lba;
            _size = size;
        }

        public long Length => _size;
        public long Position { get => _position; set => _position = Math.Clamp(value, 0, _size); }

        public int Read(byte[] buffer, int offset, int count)
        {
            int toRead = (int)Math.Min(count, _size - _position);
            if (toRead <= 0) return 0;
            _iso.ReadBytes((long)_lba * SectorSize + _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: _position = offset; break;
                case SeekOrigin.Current: _position += offset; break;
                case SeekOrigin.End: _position = _size + offset; break;
            }
            _position = Math.Clamp(_position, 0, _size);
            return _position;
        }

        public void Dispose() { }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
