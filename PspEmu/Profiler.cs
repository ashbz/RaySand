using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PspEmu;

static class Profiler
{
    public struct MethodStats
    {
        public string Name;
        public double TotalMs;
        public long Calls;
        public double AvgCallMs;
        public double PeakMs;
        public double PctFrame;
    }

    const int MaxSections = 64;
    const int WindowFrames = 60;
    static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    struct Accumulator
    {
        public string Name;
        public long StartTick;
        public double FrameMs;
        public int FrameCalls;
        public double WindowMs;
        public long WindowCalls;
        public double Peak;
        public int FrameIdx;
    }

    static readonly Accumulator[] _acc = new Accumulator[MaxSections];
    static int _count;
    static MethodStats[] _snapshot = Array.Empty<MethodStats>();

    public static MethodStats[] Snapshot => _snapshot;

    public static int Register(string name)
    {
        for (int i = 0; i < _count; i++)
            if (_acc[i].Name == name) return i;
        if (_count >= MaxSections) return -1;
        _acc[_count].Name = name;
        return _count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Begin(int id)
    {
        if ((uint)id < (uint)_count)
            _acc[id].StartTick = Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void End(int id)
    {
        if ((uint)id >= (uint)_count) return;
        long end = Stopwatch.GetTimestamp();
        _acc[id].FrameMs += (end - _acc[id].StartTick) * TicksToMs;
        _acc[id].FrameCalls++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Begin(string name)
    {
        int idx = GetOrCreate(name);
        if (idx >= 0) _acc[idx].StartTick = Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void End(string name)
    {
        long end = Stopwatch.GetTimestamp();
        int idx = GetOrCreate(name);
        if (idx < 0) return;
        _acc[idx].FrameMs += (end - _acc[idx].StartTick) * TicksToMs;
        _acc[idx].FrameCalls++;
    }

    static int GetOrCreate(string name)
    {
        for (int i = 0; i < _count; i++)
            if (_acc[i].Name == name) return i;
        if (_count >= MaxSections) return -1;
        _acc[_count].Name = name;
        return _count++;
    }

    public static void FrameEnd()
    {
        double frameMs = 0;
        for (int i = 0; i < _count; i++)
            if (_acc[i].Name == "Frame") { frameMs = _acc[i].FrameMs; break; }

        var stats = new MethodStats[_count];
        for (int i = 0; i < _count; i++)
        {
            ref var a = ref _acc[i];
            a.WindowMs += a.FrameMs;
            a.WindowCalls += a.FrameCalls;
            if (a.FrameMs > a.Peak) a.Peak = a.FrameMs;
            a.FrameIdx++;

            double avgTotal, avgCalls, peak;
            if (a.FrameIdx >= WindowFrames)
            {
                avgTotal = a.WindowMs / WindowFrames;
                avgCalls = (double)a.WindowCalls / WindowFrames;
                peak = a.Peak;
                a.WindowMs = 0; a.WindowCalls = 0; a.Peak = 0; a.FrameIdx = 0;
            }
            else
            {
                int frames = a.FrameIdx > 0 ? a.FrameIdx : 1;
                avgTotal = a.WindowMs / frames;
                avgCalls = (double)a.WindowCalls / frames;
                peak = a.Peak;
            }

            double avgPerCall = avgCalls > 0 ? avgTotal / avgCalls : 0;
            double fMs = frameMs > 0.001 ? frameMs : 16.67;
            double pct = a.Name == "Frame" ? 100.0 : (avgTotal / fMs * 100.0);

            stats[i] = new MethodStats
            {
                Name = a.Name, TotalMs = avgTotal, Calls = (long)Math.Round(avgCalls),
                AvgCallMs = avgPerCall, PeakMs = peak, PctFrame = pct,
            };
            a.FrameMs = 0; a.FrameCalls = 0;
        }

        Array.Sort(stats, (a, b) => b.TotalMs.CompareTo(a.TotalMs));
        _snapshot = stats;
    }

    public static void Reset()
    {
        _count = 0;
        _snapshot = Array.Empty<MethodStats>();
    }
}
