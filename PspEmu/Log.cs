namespace PspEmu;

enum LogCat { CPU, FPU, VFPU, Mem, GE, HLE, Kernel, IO, Audio, Ctrl, Loader, Display }

static class Log
{
    const int Cap = 4000;
    static readonly string[] _buf = new string[Cap];
    static int _head, _count;
    static readonly object _lock = new();

    public static bool[] Enabled { get; } = new bool[(int)LogCat.Display + 1];

    static Log()
    {
        for (int i = 0; i < Enabled.Length; i++) Enabled[i] = true;
    }

    public static void Write(LogCat cat, string msg)
    {
        if (!Enabled[(int)cat]) return;
        lock (_lock)
        {
            _buf[_head] = $"[{cat}] {msg}";
            _head = (_head + 1) % Cap;
            if (_count < Cap) _count++;
        }
    }

    public static void Warn(LogCat cat, string msg) => Write(cat, $"WARN: {msg}");
    public static void Error(LogCat cat, string msg) => Write(cat, $"ERROR: {msg}");

    public static (string[] lines, int count) Snapshot()
    {
        lock (_lock)
        {
            var arr = new string[_count];
            int start = (_head - _count + Cap) % Cap;
            for (int i = 0; i < _count; i++)
                arr[i] = _buf[(start + i) % Cap];
            return (arr, _count);
        }
    }

    public static void Clear()
    {
        lock (_lock) { _head = 0; _count = 0; }
    }
}
