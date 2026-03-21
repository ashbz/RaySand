namespace PsxEmu;

/// <summary>
/// Lightweight ring-buffer log. Uses a struct entry and a pre-allocated array
/// to minimize GC pressure. File logging is off by default.
/// </summary>
static class Log
{
    const int Cap = 2000;
    public static bool FileLogging;

    public enum Cat { Info, CPU, GPU, DMA, IRQ, Error }

    public struct Entry
    {
        public DateTime When;
        public Cat Category;
        public string Text;
    }

    static readonly Entry[] _ring = new Entry[Cap];
    static int _head;
    static int _count;
    static readonly object _lock = new();
    static StreamWriter? _file;
    static int _flushCounter;
    static int _version; // incremented on every Add/Clear to detect changes

    public static int Version => _version;

    public static void Add(Cat cat, string text)
    {
        lock (_lock)
        {
            ref var e = ref _ring[_head];
            e.When = DateTime.UtcNow;
            e.Category = cat;
            e.Text = text;
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
            _version++;
        }

        if (FileLogging)
        {
            _file ??= new StreamWriter(
                Path.Combine(AppContext.BaseDirectory, "psx_debug.log"), false)
                { AutoFlush = false };
            _file.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}][{cat,-5}] {text}");
            if (++_flushCounter % 200 == 0) _file.Flush();
        }
    }

    public static void Info (string t) => Add(Cat.Info,  t);
    public static void CPU  (string t) => Add(Cat.CPU,   t);
    public static void GPU  (string t) => Add(Cat.GPU,   t);
    public static void DMA  (string t) => Add(Cat.DMA,   t);
    public static void IRQ  (string t) => Add(Cat.IRQ,   t);
    public static void Error(string t) => Add(Cat.Error, t);

    /// <summary>
    /// Copy current entries into the provided list (avoids allocation).
    /// </summary>
    public static void SnapshotInto(List<Entry> dest)
    {
        dest.Clear();
        lock (_lock)
        {
            int start = _count < Cap ? 0 : _head;
            for (int i = 0; i < _count; i++)
                dest.Add(_ring[(start + i) % Cap]);
        }
    }

    public static void Clear()
    {
        lock (_lock) { _count = 0; _head = 0; _version++; }
    }
}
