namespace PsxEmu;

/// <summary>
/// Reads raw BIN disc images (2352 bytes per sector).
/// Supports single-track BIN files. CUE parsing is not implemented.
/// </summary>
class DiscImage : IDisposable
{
    public const int BytesPerSector = 2352;
    public const int DataOffset = 24;   // Mode2 Form1: 12 sync + 4 header + 8 subheader
    public const int DataSize = 0x800;  // 2048 bytes of user data per sector

    readonly FileStream _stream;
    readonly byte[] _sectorBuf = new byte[BytesPerSector];

    public int TrackCount => 1;
    public int TotalSectors { get; }
    public int LbaStart => 150; // standard 2-second pregap
    public string FilePath { get; }

    public DiscImage(string path)
    {
        FilePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        TotalSectors = (int)(_stream.Length / BytesPerSector);
        Log.Info($"[CD] Disc loaded: {Path.GetFileName(path)}, {TotalSectors} sectors ({_stream.Length / 1024 / 1024} MB)");
    }

    /// <summary>
    /// Reads a raw 2352-byte sector at the given LBA (absolute, including 150 pregap).
    /// Returns the internal buffer — caller must consume before next Read.
    /// </summary>
    public byte[] ReadSector(int lba)
    {
        int physSector = lba - LbaStart;
        if (physSector < 0) physSector = 0;
        if (physSector >= TotalSectors) physSector = TotalSectors - 1;

        _stream.Seek((long)physSector * BytesPerSector, SeekOrigin.Begin);
        _stream.Read(_sectorBuf, 0, BytesPerSector);
        return _sectorBuf;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
