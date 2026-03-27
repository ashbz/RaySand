using System.Diagnostics;

namespace N64Emu;

sealed class N64Machine
{
    public readonly N64Bus Bus;
    public readonly Vr4300 Cpu;

    public bool Running;
    public string? Error;
    public int FrameCount;
    public double EmuFps;
    public int TargetFps = 60;
    public string? RomName;

    const int CpuCyclesPerFrame = 1_562_500; // 93.75MHz / 60fps
    const int ViLinesNtsc = 525;
    int _cyclesPerLine;

    readonly Stopwatch _fpsSw = Stopwatch.StartNew();
    int _fpsFrames;
    bool _diagDone;
    static readonly string DiagPath = @"C:\Users\ashot\Desktop\n64_diag.txt";

    public static void DiagWrite(string msg)
    {
        Console.WriteLine("[DIAG] " + msg);
        try { File.AppendAllText(DiagPath, msg + "\n"); }
        catch (Exception ex) { Console.WriteLine("[DIAG-ERR] " + ex.Message); }
    }

    static readonly int ProfFrame = Profiler.Register("Frame");
    static readonly int ProfCpu   = Profiler.Register("CPU");

    public N64Machine()
    {
        Bus = new N64Bus();
        Cpu = new Vr4300(Bus);
        Bus.Cpu = Cpu;
        _cyclesPerLine = CpuCyclesPerFrame / ViLinesNtsc;
    }

    public bool LoadRom(string path)
    {
        DiagWrite($"[BOOT] LoadRom: {path} at {DateTime.Now:HH:mm:ss}");
        DiagWrite($"[BOOT] DiagPath: {DiagPath}");
        try
        {
            if (!Bus.Cart.Load(path))
            {
                Error = "Failed to load ROM";
                return false;
            }

            RomName = Bus.Cart.Title;
            Reset();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    public bool LoadPifRom(string path)
    {
        return Bus.Pif.LoadBootRom(path);
    }

    public bool UseDirectBoot = true;

    public void Reset()
    {
        Error = null;
        FrameCount = 0;
        _fpsFrames = 0;
        _fpsSw.Restart();
        _diagDone = false;

        Bus.ResetState();
        Cpu.Reset();

        if (Bus.Pif.BootRomLoaded)
        {
            Cpu.PC = 0xBFC00000;
        }
        else if (Bus.Cart.Loaded)
        {
            if (UseDirectBoot)
                Cpu.SetupDirectBoot();
            else
                Cpu.SetupHleBoot();
        }

        Running = Bus.Cart.Loaded;
    }

    public void Tick()
    {
        if (!Running) return;

        Profiler.Begin(ProfFrame);

        try
        {
            Profiler.Begin(ProfCpu);

            for (int line = 0; line < ViLinesNtsc; line++)
            {
                for (int c = 0; c < _cyclesPerLine; c++)
                    Cpu.Step();

                Bus.Vi.AdvanceLine();
                Bus.Ai.Step(_cyclesPerLine);
            }

            Profiler.End(ProfCpu);
        }
        catch (Exception ex)
        {
            Error = $"CPU exception at PC=0x{Cpu.PC:X8}: {ex.Message}";
            Running = false;
            Log.Error(Error);
            DiagWrite($"[CRASH] {Error}");
            DiagWrite($"[CRASH] {ex.StackTrace}");
        }

        FrameCount++;
        Profiler.End(ProfFrame);
        Profiler.FrameEnd();

        _fpsFrames++;
        double elapsed = _fpsSw.Elapsed.TotalSeconds;
        if (elapsed >= 1.0)
        {
            EmuFps = _fpsFrames / elapsed;
            DiagWrite($"[F{FrameCount}] PC=0x{Cpu.PC:X8} SR=0x{Cpu.COP0[12]:X8} " +
                $"VI origin=0x{Bus.Vi.Origin:X8} viSwap={Bus.Vi.Swaps} " +
                $"exc={Cpu.ExceptionCount} eret={Cpu.EretCount} " +
                $"tmr={Cpu.TimerFireCount} cmpW={Cpu.CompareWriteCount} rspTask={Bus.Rsp.TaskCount} " +
                $"fps={EmuFps:F1}\n" +
                $"  intrSets: SP={Bus.Mi.SpIntrSets} SI={Bus.Mi.SiIntrSets} VI={Bus.Mi.ViIntrSets} PI={Bus.Mi.PiIntrSets} DP={Bus.Mi.DpIntrSets}\n" +
                $"  intrClrs: SP={Bus.Mi.SpIntrClears} SI={Bus.Mi.SiIntrClears} VI={Bus.Mi.ViIntrClears} PI={Bus.Mi.PiIntrClears} DP={Bus.Mi.DpIntrClears}\n" +
                $"  miReads={Bus.Mi.MiIntrReads} rdSP={Bus.Mi.MiIntrReadsWithSp} rdDP={Bus.Mi.MiIntrReadsWithDp} " +
                $"spWrStat={Bus.Rsp.WriteStatusCount} spClrHalt={Bus.Rsp.WriteStatusClearHaltCount}\n" +
                $"  MiIntrMask=0x{Bus.Mi.MiIntrMask:X2} MiIntr=0x{Bus.Mi.MiIntr:X2} " +
                $"SpStatus=0x{Bus.Rsp.SpStatus:X4} Count=0x{Cpu.COP0[9]:X8} Compare=0x{Cpu.COP0[11]:X8}");
            _fpsFrames = 0;
            _fpsSw.Restart();

            if (!_diagDone && FrameCount > 5)
            {
                _diagDone = true;
                // Dump exception vector contents
                DiagWrite("=== Exception Vector @ 0x80000180 (phys 0x180) ===");
                for (int off = 0; off < 32; off += 4)
                {
                    uint instr = Bus.Read32((uint)(0x180 + off));
                    DiagWrite($"  0x{0x80000180 + off:X8}: 0x{instr:X8}");
                }
                DiagWrite("=== Exception Vector @ 0x80000000 (phys 0x000) ===");
                for (int off = 0; off < 16; off += 4)
                {
                    uint instr = Bus.Read32((uint)(0x000 + off));
                    DiagWrite($"  0x{0x80000000 + off:X8}: 0x{instr:X8}");
                }
                DiagWrite($"=== RDRAM[0x318..0x31B] (RDRAM size) = 0x{Bus.Read32(0x318):X8}");
                DiagWrite($"=== Entry point area: 0x{Bus.Read32(0x246000):X8} 0x{Bus.Read32(0x246004):X8}");
            }
        }
    }
}
