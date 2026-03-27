using System.Diagnostics;

namespace PspEmu;

/// <summary>
/// Top-level PSP machine: owns all components, runs the emulation frame loop.
/// PSP runs at 333 MHz, 60 FPS, 480x272 display.
/// </summary>
sealed class PspMachine
{
    public PspBus Bus { get; } = new();
    public Allegrex Cpu { get; } = new();
    public HleKernel Kernel { get; private set; } = null!;
    public HleModules Modules { get; private set; } = null!;
    public HleIo Io { get; private set; } = null!;
    public PspDisplay Display { get; private set; } = null!;
    public PspAudio Audio { get; } = new();
    public PspCtrl Ctrl { get; } = new();
    public PspGe Ge { get; private set; } = null!;
    public GeRenderer Renderer { get; private set; } = null!;
    public ElfLoader Loader { get; private set; } = null!;

    public bool Running { get; set; }
    public int TargetFps = 60;
    public double LastFrameMs;
    public long TotalFrames;

    const int CyclesPerFrame = 5_550_000;
    const int CpuBatchSize = 4096;
    const int KernelUpdateInterval = 16384;

    static readonly int ProfFrame   = Profiler.Register("Frame");
    static readonly int ProfCpu     = Profiler.Register("CPU");
    static readonly int ProfGe      = Profiler.Register("GE");
    static readonly int ProfDisplay = Profiler.Register("Display");

    readonly Stopwatch _frameSw = new();

    public PspMachine()
    {
        Init();
    }

    void Init()
    {
        Kernel = new HleKernel(Cpu, Bus);
        Display = new PspDisplay(Bus);
        Io = new HleIo(Bus);
        Ge = new PspGe(Bus);
        Renderer = new GeRenderer(Bus);
        Ge.Renderer = Renderer;

        Bus.Ge = Ge;
        Bus.Display = Display;
        Bus.Audio = Audio;
        Display.Cpu = Cpu;

        Cpu.Bus = Bus;
        Cpu.Kernel = Kernel;

        Modules = new HleModules(Kernel, Bus, Display, Audio, Ctrl, Io, Ge);
        Kernel.SetModules(Modules);

        Loader = new ElfLoader(Bus, Modules);
    }

    public void Reset()
    {
        Array.Clear(Bus.Ram);
        Array.Clear(Bus.Vram);
        Array.Clear(Bus.Scratchpad);
        Cpu.Reset();
        Ctrl.Reset();
        Running = false;
        TotalFrames = 0;
    }

    /// <summary>Load an ELF/PBP/ISO and start execution.</summary>
    public bool LoadAndStart(string path)
    {
        Reset();

        // Set up I/O paths
        string? dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            Io.GameDir = dir;
            Io.MemStickDir = Path.Combine(dir, "memstick");
        }

        bool ok;
        if (Directory.Exists(path))
            ok = Loader.LoadFromDirectory(path);
        else
            ok = Loader.LoadFile(path);

        if (!ok)
        {
            Log.Error(LogCat.Loader, "Failed to load game");
            return false;
        }

        // If the loader opened an ISO, pass it to HleIo for disc0: access
        if (Loader.Iso != null)
            Io.Iso = Loader.Iso;

        // Set up initial thread
        uint stackTop = 0x09FF_F000;
        Kernel.CreateMainThread(Loader.EntryPoint, Loader.GpValue, stackTop);

        Running = true;
        Log.Write(LogCat.Kernel, $"Emulation started: entry={Loader.EntryPoint:X8}");
        return true;
    }

    /// <summary>Run one frame of emulation. Called from emu thread.</summary>
    public void Tick()
    {
        if (!Running) return;

        Profiler.Begin(ProfFrame);
        _frameSw.Restart();

        // Update system timer
        Bus.SystemTimeLow = (uint)(Kernel.GetSystemTimeMicroseconds() & 0xFFFFFFFF);

        Cpu.WaitingVblank = false;

        Profiler.Begin(ProfCpu);
        int cyclesLeft = CyclesPerFrame;
        int sinceKernelUpdate = 0;
        while (cyclesLeft > 0 && !Cpu.Halted && !Cpu.WaitingVblank)
        {
            int batch = Math.Min(cyclesLeft, CpuBatchSize);
            Cpu.StepN(batch);
            cyclesLeft -= batch;
            sinceKernelUpdate += batch;

            if (sinceKernelUpdate >= KernelUpdateInterval)
            {
                Kernel.Update();
                sinceKernelUpdate = 0;
            }
        }
        Kernel.Update();
        Profiler.End(ProfCpu);

        // Process pending GE display lists
        Profiler.Begin(ProfGe);
        Ge.ProcessAllLists();
        Profiler.End(ProfGe);

        // VBlank + snapshot
        Profiler.Begin(ProfDisplay);
        Display.VBlank();
        Profiler.End(ProfDisplay);

        TotalFrames++;

        // FPS throttle
        _frameSw.Stop();
        LastFrameMs = _frameSw.Elapsed.TotalMilliseconds;

        double targetMs2 = 1000.0 / TargetFps;
        double sleepMs = targetMs2 - LastFrameMs;
        if (sleepMs > 1)
            Thread.Sleep((int)sleepMs);

        Profiler.End(ProfFrame);
        Profiler.FrameEnd();

        // Check for exit
        if (Kernel.ExitRequested)
        {
            Running = false;
            Log.Write(LogCat.Kernel, "Game requested exit");
        }
    }
}
