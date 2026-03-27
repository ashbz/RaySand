namespace J2meEmu;

static class Log
{
    const int Cap = 4000;
    public static bool FileLogging;

    public enum Cat { Info, JVM, GFX, Class, MIDP, Error }

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
    static int _version;

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
            try
            {
                _file ??= new StreamWriter(
                    Path.Combine(AppContext.BaseDirectory, "j2me_debug.log"), false)
                    { AutoFlush = false };
                _file.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}][{cat,-5}] {text}");
                if (++_flushCounter % 200 == 0) _file.Flush();
            }
            catch { }
        }
    }

    public static void Info (string t) => Add(Cat.Info,  t);
    public static void JVM  (string t) => Add(Cat.JVM,   t);
    public static void GFX  (string t) => Add(Cat.GFX,   t);
    public static void Cls  (string t) => Add(Cat.Class,  t);
    public static void MIDP (string t) => Add(Cat.MIDP,  t);
    public static void Error(string t) => Add(Cat.Error, t);

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
