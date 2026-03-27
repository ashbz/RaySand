using System.Runtime.CompilerServices;
using System.Text;

namespace PsxEmu;

/// <summary>
/// Top-level PSX machine. Drives CPU, GPU, DMA, timers, and interrupts.
/// </summary>
class PsxMachine
{
    public readonly PsxBus  Bus;
    public readonly MipsCpu Cpu;
    public readonly PsxGpu  Gpu;
    public readonly PsxDma  Dma;

    public bool    Running    { get; private set; }
    public string? BiosPath   { get; private set; }
    public string? PendingExe { get; private set; }
    public string? ExeName    { get; private set; }
    public string? Error      { get; private set; }

    const int CyclesPerFrame = 560_000;
    const int HBlanksPerFrame = 263;
    const uint ISTAT_VBLANK = 1 << 0;
    const uint ISTAT_CDROM  = 1 << 2;
    const uint ISTAT_DMA    = 1 << 3;
    const uint ISTAT_TIMER0 = 1 << 4;
    const uint ISTAT_TIMER1 = 1 << 5;
    const uint ISTAT_TIMER2 = 1 << 6;
    const uint ISTAT_JOY    = 1 << 7;
    const uint ISTAT_SPU    = 1 << 9;

    const int CdRomTickInterval = 128;
    int _cdTickAccum;

    // Batched inner loop — run N CPU steps between peripheral checks
    const int InnerBatch = 64;

    /// Current cycle offset within the frame (0..CyclesPerFrame), exposed for GPUSTAT.
    public int FrameCycle { get; private set; }

    public int    FrameCount { get; private set; }
    public double EmuFps     { get; private set; }
    public int    TargetFps  { get; set; } = 60;
    long _lastFpsTime = Environment.TickCount64;
    int _fpsFrameCounter;
    long _fpsWindowStart = Environment.TickCount64;

    public PsxMachine()
    {
        Bus = new PsxBus(new byte[512 * 1024]);
        Cpu = new MipsCpu(Bus);
        Gpu = Bus.Gpu;
        Dma = Bus.Dma;
        Bus.Cpu = Cpu;
    }

    public bool LoadFile(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length == 512 * 1024)
                return LoadBiosData(data, path);

            Error = $"Unsupported file: {Path.GetFileName(path)} ({data.Length} bytes). " +
                    "Game disc images are not yet supported — only 512 KB BIOS dumps.";
            Log.Error(Error);
            return false;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"File load failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load a game file (EXE or disc image). Never replaces the BIOS.
    /// </summary>
    public bool LoadGameFile(string path)
    {
        try
        {
            if (BiosPath == null)
            {
                Error = "Cannot load a game without a BIOS. Load a BIOS first.";
                Log.Error(Error);
                return false;
            }

            long fileSize = new FileInfo(path).Length;

            // Check for PS-X EXE (sideload approach)
            if (fileSize >= 0x800)
            {
                byte[] header = new byte[8];
                using (var fs = File.OpenRead(path))
                    fs.Read(header, 0, 8);

                if (Encoding.ASCII.GetString(header, 0, 8) == "PS-X EXE")
                {
                    PendingExe = path;
                    ExeName = Path.GetFileName(path);
                    Error = null;
                    Log.Info($"EXE queued for sideload: {ExeName} ({fileSize} bytes)");
                    Reset();
                    return true;
                }
            }

            // Treat as disc image (BIN/IMG) — never as BIOS
            if (fileSize >= DiscImage.BytesPerSector * 16)
                return LoadDisc(path);

            Error = $"Unrecognized game file: {Path.GetFileName(path)} ({fileSize} bytes).";
            Log.Error(Error);
            return false;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"File load failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Legacy LoadExe — now delegates to LoadGameFile.
    /// </summary>
    public bool LoadExe(string path) => LoadGameFile(path);

    public bool LoadDisc(string path)
    {
        try
        {
            if (BiosPath == null)
            {
                Error = "Cannot load disc without a BIOS.";
                Log.Error(Error);
                return false;
            }

            var disc = new DiscImage(path);
            Bus.CdRom.InsertDisc(disc);
            ExeName = Path.GetFileName(path);
            PendingExe = null;
            Error = null;
            Log.Info($"Disc loaded: {ExeName}");
            Reset();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"Disc load failed: {ex.Message}");
            return false;
        }
    }

    public bool LoadBios(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            return LoadBiosData(data, path);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"BIOS load failed: {ex.Message}");
            return false;
        }
    }

    bool LoadBiosData(byte[] data, string path)
    {
        if (data.Length != 512 * 1024)
        {
            Error = $"Wrong BIOS size: expected 524288, got {data.Length}";
            Log.Error(Error);
            return false;
        }

        Array.Copy(data, Bus.Bios, 512 * 1024);
        BiosPath = path;
        Error = null;
        Log.Info($"BIOS loaded: {Path.GetFileName(path)} ({data.Length / 1024} KB)");
        Reset();
        return true;
    }

    public string? TryAutoLoad()
    {
        string[] names =
        {
            "SCPH1001.BIN", "scph1001.bin",
            "SCPH5501.BIN", "scph5501.bin",
            "SCPH7001.BIN", "scph7001.bin",
            "SCPH1002.BIN", "scph1002.bin",
            "SCPH5502.BIN", "scph5502.bin",
            "scph-1001.bin", "SCPH-1001.BIN",
        };

        var searchDirs = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "",
        };

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var name in names)
            {
                string full = Path.Combine(dir, name);
                if (File.Exists(full))
                {
                    Log.Info($"Auto-detected BIOS: {full}");
                    if (LoadBios(full)) return full;
                }
            }
        }
        Log.Info("No BIOS file found.");
        return null;
    }

    public void Reset()
    {
        Cpu.Reset();
        Gpu.WriteGP1(0x00_000000);
        Bus.ResetState();
        _cdTickAccum = 0;
        FrameCount = 0;
        Running = BiosPath != null;
        Log.Info($"Machine reset — running={Running}");
    }

    unsafe void InjectExe()
    {
        string path = PendingExe!;
        PendingExe = null;

        byte[] exe = File.ReadAllBytes(path);
        if (exe.Length < 0x800)
        {
            Error = "EXE file too small";
            Log.Error(Error);
            return;
        }

        uint initialPC  = BitConverter.ToUInt32(exe, 0x10);
        uint initialGP  = BitConverter.ToUInt32(exe, 0x14);
        uint ramDest    = BitConverter.ToUInt32(exe, 0x18) & 0x1F_FFFF;
        uint fileSize   = BitConverter.ToUInt32(exe, 0x1C);
        uint initialSP  = BitConverter.ToUInt32(exe, 0x30);

        int payloadLen = exe.Length - 0x800;
        int copyLen = (int)Math.Min(fileSize, (uint)payloadLen);
        int maxCopy = Bus.Ram.Length - (int)ramDest;
        if (maxCopy < copyLen) copyLen = maxCopy;

        Array.Copy(exe, 0x800, Bus.Ram, (int)ramDest, copyLen);

        Cpu.SetPC(initialPC);
        Cpu.GPR[28] = initialGP;
        if (initialSP != 0)
        {
            Cpu.GPR[29] = initialSP;
            Cpu.GPR[30] = initialSP;
        }

        Log.Info($"EXE injected: {Path.GetFileName(path)}");
        Log.Info($"  PC=0x{initialPC:X8} GP=0x{initialGP:X8} SP=0x{initialSP:X8}");
        Log.Info($"  RAM dest=0x{ramDest:X6} size={copyLen} bytes");
    }

    const int VBlankCycle = 100_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick()
    {
        if (!Running) return;

        FrameCount++;
        Gpu.Cycle += CyclesPerFrame;

        int hblankInterval = CyclesPerFrame / HBlanksPerFrame;
        int nextHBlank = hblankInterval;
        bool vblankDone = false;

        try
        {
            Profiler.Begin("Frame");
            int i = 0;
            while (i < CyclesPerFrame)
            {
                int batchEnd = i + InnerBatch;
                if (!vblankDone && batchEnd > VBlankCycle) batchEnd = VBlankCycle;
                if (batchEnd > nextHBlank) batchEnd = nextHBlank;
                if (batchEnd > CyclesPerFrame) batchEnd = CyclesPerFrame;
                int batchLen = batchEnd - i;

                Profiler.Begin("CPU");
                for (int j = 0; j < batchLen; j++)
                {
                    Cpu.HandleInterrupts();
                    Cpu.Step();
                }
                Profiler.End("CPU");

                FrameCycle = batchEnd;
                Gpu.FrameCycle = batchEnd;

                Profiler.Begin("Timers");
                TickTimer0(batchLen);
                TickTimer2(batchLen);
                Profiler.End("Timers");

                Profiler.Begin("CDROM");
                _cdTickAccum += batchLen;
                if (_cdTickAccum >= CdRomTickInterval)
                {
                    if (Bus.CdRom.Tick(_cdTickAccum))
                        Bus.IStat |= ISTAT_CDROM;
                    _cdTickAccum = 0;
                }
                Profiler.End("CDROM");

                Profiler.Begin("SPU");
                if (Bus.Spu.Tick(batchLen))
                    Bus.IStat |= ISTAT_SPU;
                Profiler.End("SPU");

                Bus.TickJoyBatch(batchLen);

                i = batchEnd;

                if (!vblankDone && i >= VBlankCycle)
                {
                    vblankDone = true;
                    Bus.IStat |= ISTAT_VBLANK;
                    Gpu.VBlankSnapshot();
                }

                if (i >= nextHBlank)
                {
                    nextHBlank += hblankInterval;
                    Bus.Timer1++;
                    if ((Bus.Tim1Mode & (1u << 4)) != 0 && Bus.Tim1Target != 0 && Bus.Timer1 >= Bus.Tim1Target)
                    {
                        Bus.IStat |= ISTAT_TIMER1;
                        if ((Bus.Tim1Mode & (1u << 3)) != 0) Bus.Timer1 = 0;
                    }
                    if ((Bus.Tim1Mode & (1u << 5)) != 0 && Bus.Timer1 > 0xFFFF)
                    {
                        Bus.IStat |= ISTAT_TIMER1;
                        Bus.Timer1 = 0;
                    }
                }

                if (PendingExe != null && Cpu.PC == 0x8003_0000)
                    InjectExe();
            }
            Profiler.End("Frame");
            Profiler.FrameEnd();
        }
        catch (Exception ex)
        {
            Error = $"CPU fault @ 0x{Cpu.PC:X8}: {ex.Message}";
            Log.Error(Error);
            Running = false;
            return;
        }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void TickTimer0(int cycles)
    {
        uint old = Bus.Timer0;
        Bus.Timer0 = (uint)(old + cycles);

        bool irqOnTarget = (Bus.Tim0Mode & (1u << 4)) != 0;
        bool irqOnOverflow = (Bus.Tim0Mode & (1u << 5)) != 0;
        bool resetOnTarget = (Bus.Tim0Mode & (1u << 3)) != 0;

        if (irqOnTarget && Bus.Tim0Target != 0 && old < Bus.Tim0Target && Bus.Timer0 >= Bus.Tim0Target)
        {
            Bus.IStat |= ISTAT_TIMER0;
            if (resetOnTarget) Bus.Timer0 = 0;
        }
        if (irqOnOverflow && Bus.Timer0 > 0xFFFF)
        {
            Bus.IStat |= ISTAT_TIMER0;
            Bus.Timer0 &= 0xFFFF;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void TickTimer2(int cycles)
    {
        bool useDiv8 = (Bus.Tim2Mode & (1u << 9)) != 0 || (Bus.Tim2Mode & (1u << 8)) != 0;
        uint inc = useDiv8 ? (uint)(cycles >> 3) : (uint)cycles;
        uint old = Bus.Timer2;
        Bus.Timer2 = old + inc;

        bool irqOnTarget = (Bus.Tim2Mode & (1u << 4)) != 0;
        bool irqOnOverflow = (Bus.Tim2Mode & (1u << 5)) != 0;
        bool resetOnTarget = (Bus.Tim2Mode & (1u << 3)) != 0;

        if (irqOnTarget && Bus.Tim2Target != 0 && old < Bus.Tim2Target && Bus.Timer2 >= Bus.Tim2Target)
        {
            Bus.IStat |= ISTAT_TIMER2;
            if (resetOnTarget) Bus.Timer2 = 0;
        }
        if (irqOnOverflow && Bus.Timer2 > 0xFFFF)
        {
            Bus.IStat |= ISTAT_TIMER2;
            Bus.Timer2 &= 0xFFFF;
        }
    }

        // FPS measurement (rolling window)
        _fpsFrameCounter++;
        long now = Environment.TickCount64;
        long elapsed = now - _fpsWindowStart;
        if (elapsed >= 500)
        {
            EmuFps = _fpsFrameCounter * 1000.0 / elapsed;
            _fpsFrameCounter = 0;
            _fpsWindowStart = now;
        }

        // Frame-rate throttle (emulation thread sleeps to match target)
        if (TargetFps > 0)
        {
            double frameDurationMs = 1000.0 / TargetFps;
            long frameEnd = _lastFpsTime + (long)frameDurationMs;
            long remaining = frameEnd - Environment.TickCount64;
            if (remaining > 1)
                Thread.Sleep((int)remaining);
            _lastFpsTime = Environment.TickCount64;
        }
    }
}
