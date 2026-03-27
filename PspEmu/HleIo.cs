using System.Text;

namespace PspEmu;

/// <summary>
/// HLE file I/O: maps PSP filesystem paths (ms0:/, host0:/, disc0:/) to host paths.
/// disc0:/umd0: paths are backed by an ISO image when loaded from an ISO/CSO file.
/// </summary>
sealed class HleIo
{
    sealed class FileHandle
    {
        public int Uid;
        public FileStream? Stream;
        public IsoReader.IsoFileStream? IsoStream;
        public string Path = "";
        public bool IsDir;
        public string[]? DirEntries;
        public int DirIndex;
    }

    readonly List<FileHandle> _handles = new();
    int _nextUid = 1;
    readonly PspBus _bus;
    string _currentDir = "";

    public string? GameDir { get; set; }
    public string? MemStickDir { get; set; }
    public IsoReader? Iso { get; set; }

    public HleIo(PspBus bus)
    {
        _bus = bus;
    }

    /// <summary>Returns null if the path targets disc0:/umd0: and we have an ISO loaded (caller should use ISO).</summary>
    static string? ExtractDiscRelPath(string pspPath)
    {
        if (pspPath.StartsWith("disc0:", StringComparison.OrdinalIgnoreCase))
            return pspPath[6..].TrimStart('/', '\\');
        if (pspPath.StartsWith("umd0:", StringComparison.OrdinalIgnoreCase))
            return pspPath[5..].TrimStart('/', '\\');
        return null;
    }

    bool IsDiscPath(string pspPath) => Iso != null && ExtractDiscRelPath(pspPath) != null;

    string ResolvePath(string pspPath)
    {
        string path = pspPath.Replace('/', Path.DirectorySeparatorChar)
                             .Replace('\\', Path.DirectorySeparatorChar);

        if (path.StartsWith("ms0:", StringComparison.OrdinalIgnoreCase))
        {
            string rel = path[4..].TrimStart(Path.DirectorySeparatorChar);
            string msDir = MemStickDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PspEmu", "memstick");
            return Path.Combine(msDir, rel);
        }

        if (path.StartsWith("host0:", StringComparison.OrdinalIgnoreCase))
        {
            string rel = path[6..].TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(GameDir ?? ".", rel);
        }

        if (path.StartsWith("disc0:", StringComparison.OrdinalIgnoreCase))
        {
            string rel = path[6..].TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(GameDir ?? ".", rel);
        }

        if (path.StartsWith("umd0:", StringComparison.OrdinalIgnoreCase))
        {
            string rel = path[5..].TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(GameDir ?? ".", rel);
        }

        if (!Path.IsPathRooted(path) && _currentDir != "")
            path = Path.Combine(_currentDir, path);

        return path;
    }

    public int Open(string pspPath, int flags, int mode)
    {
        // ISO-backed disc0:/umd0: access
        if (IsDiscPath(pspPath))
        {
            string rel = ExtractDiscRelPath(pspPath)!;
            Log.Write(LogCat.IO, $"sceIoOpen '{pspPath}' → ISO:'{rel}'");

            var isoStream = Iso!.OpenFile(rel);
            if (isoStream == null)
            {
                Log.Warn(LogCat.IO, $"ISO file not found: {rel}");
                return -1;
            }

            var handle = new FileHandle
            {
                Uid = _nextUid++,
                IsoStream = isoStream,
                Path = rel,
            };
            _handles.Add(handle);
            return handle.Uid;
        }

        string hostPath = ResolvePath(pspPath);
        Log.Write(LogCat.IO, $"sceIoOpen '{pspPath}' → '{hostPath}' flags=0x{flags:X}");

        try
        {
            FileMode fm;
            FileAccess fa;

            bool read = (flags & 0x0001) != 0;
            bool write = (flags & 0x0002) != 0;
            bool append = (flags & 0x0100) != 0;
            bool create = (flags & 0x0200) != 0;
            bool trunc = (flags & 0x0400) != 0;

            if (create && trunc) fm = FileMode.Create;
            else if (create) fm = FileMode.OpenOrCreate;
            else if (append) fm = FileMode.Append;
            else fm = FileMode.Open;

            if (read && write) fa = FileAccess.ReadWrite;
            else if (write) fa = FileAccess.Write;
            else fa = FileAccess.Read;

            if (!File.Exists(hostPath) && !create)
            {
                Log.Warn(LogCat.IO, $"File not found: {hostPath}");
                return -1;
            }

            string? dir = Path.GetDirectoryName(hostPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var stream = new FileStream(hostPath, fm, fa, FileShare.ReadWrite);
            var handle = new FileHandle
            {
                Uid = _nextUid++,
                Stream = stream,
                Path = hostPath,
            };
            _handles.Add(handle);
            return handle.Uid;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCat.IO, $"Open failed: {ex.Message}");
            return -1;
        }
    }

    public int Close(int uid)
    {
        var h = _handles.Find(x => x.Uid == uid);
        if (h == null) return -1;
        h.Stream?.Close();
        h.IsoStream?.Dispose();
        _handles.Remove(h);
        return 0;
    }

    public int Read(int uid, uint bufAddr, uint size)
    {
        var h = _handles.Find(x => x.Uid == uid);
        if (h == null) return -1;

        byte[] buf = new byte[Math.Min(size, 0x100000)];

        int read;
        if (h.IsoStream != null)
            read = h.IsoStream.Read(buf, 0, buf.Length);
        else if (h.Stream != null)
            read = h.Stream.Read(buf, 0, buf.Length);
        else
            return -1;

        for (int i = 0; i < read; i++)
            _bus.Write8(bufAddr + (uint)i, buf[i]);
        return read;
    }

    public int Write(int uid, uint bufAddr, uint size)
    {
        var h = _handles.Find(x => x.Uid == uid);
        if (h?.Stream == null) return -1;

        byte[] buf = new byte[size];
        for (uint i = 0; i < size; i++)
            buf[i] = _bus.Read8(bufAddr + i);
        h.Stream.Write(buf, 0, buf.Length);
        return (int)size;
    }

    public long Lseek(int uid, long offset, int whence)
    {
        var h = _handles.Find(x => x.Uid == uid);
        if (h == null) return -1;

        SeekOrigin origin = whence switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin,
        };

        if (h.IsoStream != null)
            return h.IsoStream.Seek(offset, origin);
        if (h.Stream != null)
            return h.Stream.Seek(offset, origin);
        return -1;
    }

    public int Mkdir(string pspPath)
    {
        string hostPath = ResolvePath(pspPath);
        try { Directory.CreateDirectory(hostPath); return 0; }
        catch { return -1; }
    }

    public int Rmdir(string pspPath)
    {
        string hostPath = ResolvePath(pspPath);
        try { Directory.Delete(hostPath); return 0; }
        catch { return -1; }
    }

    public int Dopen(string pspPath)
    {
        if (IsDiscPath(pspPath))
        {
            string rel = ExtractDiscRelPath(pspPath)!;
            Log.Write(LogCat.IO, $"sceIoDopen '{pspPath}' → ISO:'{rel}'");

            if (!Iso!.DirectoryExists(rel))
            {
                Log.Warn(LogCat.IO, $"ISO dir not found: {rel}");
                return -1;
            }

            var listing = Iso.ListDirectory(rel);
            var handle = new FileHandle
            {
                Uid = _nextUid++,
                Path = rel,
                IsDir = true,
                DirEntries = listing?.Select(e => (e.IsDir ? "D:" : "F:") + e.Size + ":" + e.Name).ToArray(),
                DirIndex = 0,
            };
            _handles.Add(handle);
            return handle.Uid;
        }

        string hostPath = ResolvePath(pspPath);
        if (!Directory.Exists(hostPath)) return -1;

        var entries = Directory.GetFileSystemEntries(hostPath);
        var hdl = new FileHandle
        {
            Uid = _nextUid++,
            Path = hostPath,
            IsDir = true,
            DirEntries = entries,
            DirIndex = 0,
        };
        _handles.Add(hdl);
        return hdl.Uid;
    }

    public int Dread(int uid, uint direntPtr)
    {
        var h = _handles.Find(x => x.Uid == uid);
        if (h == null || !h.IsDir || h.DirEntries == null) return -1;
        if (h.DirIndex >= h.DirEntries.Length) return 0;

        string entry = h.DirEntries[h.DirIndex++];

        // ISO entries are encoded as "D:size:name" or "F:size:name"
        bool isDir;
        string name;
        long sz = 0;
        if (entry.StartsWith("D:") || entry.StartsWith("F:"))
        {
            isDir = entry[0] == 'D';
            int firstColon = 2;
            int secondColon = entry.IndexOf(':', firstColon);
            if (secondColon > firstColon)
            {
                long.TryParse(entry[firstColon..secondColon], out sz);
                name = entry[(secondColon + 1)..];
            }
            else
            {
                name = entry[firstColon..];
            }
        }
        else
        {
            name = Path.GetFileName(entry);
            isDir = Directory.Exists(entry);
            if (!isDir && File.Exists(entry))
                sz = new FileInfo(entry).Length;
        }

        // SceIoDirent: offset 0 = SceIoStat (104 bytes), offset 104 = name (256 bytes)
        _bus.Write32(direntPtr, isDir ? 0x1016u : 0x2016u);
        _bus.Write32(direntPtr + 8, (uint)sz);
        _bus.Write32(direntPtr + 12, (uint)(sz >> 32));

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        for (int i = 0; i < Math.Min(nameBytes.Length, 255); i++)
            _bus.Write8(direntPtr + 104 + (uint)i, nameBytes[i]);
        _bus.Write8(direntPtr + 104 + (uint)Math.Min(nameBytes.Length, 255), 0);

        return 1;
    }

    public int Dclose(int uid)
    {
        _handles.RemoveAll(x => x.Uid == uid);
        return 0;
    }

    public int GetStat(string pspPath, uint statPtr)
    {
        if (IsDiscPath(pspPath))
        {
            string rel = ExtractDiscRelPath(pspPath)!;
            bool fileEx = Iso!.FileExists(rel);
            bool dirEx = Iso.DirectoryExists(rel);
            if (!fileEx && !dirEx) return -1;

            _bus.Write32(statPtr, dirEx ? 0x1016u : 0x2016u);
            if (fileEx)
            {
                long fileSz = Iso.GetFileSize(rel);
                if (fileSz >= 0)
                {
                    _bus.Write32(statPtr + 8, (uint)fileSz);
                    _bus.Write32(statPtr + 12, (uint)(fileSz >> 32));
                }
            }
            return 0;
        }

        string hostPath = ResolvePath(pspPath);
        if (!File.Exists(hostPath) && !Directory.Exists(hostPath)) return -1;

        bool isDir = Directory.Exists(hostPath);
        _bus.Write32(statPtr, isDir ? 0x1016u : 0x2016u);

        if (!isDir)
        {
            long sz = new FileInfo(hostPath).Length;
            _bus.Write32(statPtr + 8, (uint)sz);
            _bus.Write32(statPtr + 12, (uint)(sz >> 32));
        }

        return 0;
    }

    public int Chdir(string pspPath)
    {
        _currentDir = ResolvePath(pspPath);
        return 0;
    }

    public int Rename(string oldPath, string newPath)
    {
        try { File.Move(ResolvePath(oldPath), ResolvePath(newPath)); return 0; }
        catch { return -1; }
    }

    public int Remove(string pspPath)
    {
        try { File.Delete(ResolvePath(pspPath)); return 0; }
        catch { return -1; }
    }
}
