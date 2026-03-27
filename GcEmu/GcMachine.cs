using System.Runtime.CompilerServices;
using System.Text;

namespace GcEmu;

class GcMachine
{
    public readonly GcBus Bus;
    public readonly PowerPc Cpu;

    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public string? GameName { get; private set; }
    public string? IplPath { get; private set; }

    const int CpuClockMHz = 486;
    const int CyclesPerLine = CpuClockMHz * 1000000 / 60 / 525;
    const int LinesPerFrame = 525;
    const int CyclesPerFrame = CyclesPerLine * LinesPerFrame;
    const int InnerBatch = 64;

    public int FrameCount { get; private set; }
    public double EmuFps { get; private set; }
    public int TargetFps { get; set; } = 60;

    long _lastFpsTime = Environment.TickCount64;
    int _fpsFrameCounter;
    long _fpsWindowStart = Environment.TickCount64;

    public GcMachine()
    {
        Bus = new GcBus();
        Cpu = new PowerPc();
        Cpu.Init(Bus);
        Bus.Cpu = Cpu;
        Bus.Machine = this;
    }

    public bool LoadIpl(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            Bus.Exi.LoadIpl(data);
            IplPath = path;
            Log.Info($"IPL loaded: {Path.GetFileName(path)} ({data.Length / 1024} KB)");
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"IPL load failed: {ex.Message}");
            return false;
        }
    }

    public bool LoadDisc(string path)
    {
        try
        {
            var disc = DiscImage.Open(path);
            Bus.Di.InsertDisc(disc);
            GameName = disc.GameName;

            Reset();
            BootFromDisc(disc);
            Running = true;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"Disc load failed: {ex.Message}");
            return false;
        }
    }

    void BootFromDisc(DiscImage disc)
    {
        // Only copy disc ID (0x20 bytes) like Dolphin does - NOT the full header
        // The full 0x440 header pollutes low memory (0x30 ArenaLo, 0x34 ArenaHi)
        // with game title characters that OSInit misinterprets as pointers
        disc.Read(Bus.Ram, 0, 0, 0x20);

        byte[] bi2 = disc.ReadBytes(0x440, 0x2000);
        Array.Copy(bi2, 0, Bus.Ram, 0x440, bi2.Length);

        Bus.SetupLowMem();
        InstallExceptionStubs();

        GcBus.WriteBe32(Bus.Ram, 0x001C, 0xC2339F3D);
        GcBus.WriteBe32(Bus.Ram, 0x0020, 0x0D15EA5E);
        GcBus.WriteBe32(Bus.Ram, 0x0024, 0x00000001);

        uint fstOff = disc.FstOffset;
        uint fstSize = disc.FstSize;
        uint fstAddr = 0x01800000 - fstSize;
        fstAddr &= 0xFFFFFFE0;
        if (fstSize > 0 && fstOff > 0)
        {
            disc.Read(Bus.Ram, (int)fstAddr, fstOff, (int)fstSize);
            GcBus.WriteBe32(Bus.Ram, 0x0038, fstAddr | 0x80000000);
            GcBus.WriteBe32(Bus.Ram, 0x003C, (uint)fstSize);
            Log.Info($"FST loaded at 0x{fstAddr:X8} ({fstSize} bytes)");
        }

        byte[] bi2Ram = disc.ReadBytes(0x440, 0x2000);
        uint bi2Addr = 0x01800000 - fstSize - 0x2000;
        bi2Addr &= 0xFFFFFFE0;
        Array.Copy(bi2Ram, 0, Bus.Ram, (int)bi2Addr, bi2Ram.Length);
        GcBus.WriteBe32(Bus.Ram, 0x00F4, bi2Addr | 0x80000000);

        GcBus.WriteBe32(Bus.Ram, 0x0000, 0x4E800020);

        var loader = new DolLoader();
        loader.Load(disc, Bus.Ram);

        Cpu.Reset();
        Cpu.GPR[1] = 0x816FFFF0;
        Cpu.GPR[2] = 0x80000000;
        Cpu.GPR[13] = 0x80000000;
        Cpu.MSR = 0x00002032;

        Cpu.HID0 = 0x0011C464;
        Cpu.HID2 = 0xE0000000;
        Cpu.SetPC(loader.EntryPoint);

        // BATs matching Dolphin's BS2 emulation for GameCube
        Cpu.IBAT[0] = 0x80001FFF; Cpu.IBAT[1] = 0x00000002; // 0x80000000 -> 0x00000000 cached
        Cpu.DBAT[0] = 0x80001FFF; Cpu.DBAT[1] = 0x00000002; // 0x80000000 -> 0x00000000 cached
        Cpu.DBAT[2] = 0xC0001FFF; Cpu.DBAT[3] = 0x0000002A; // 0xC0000000 -> 0x00000000 uncached

        Log.Info($"Boot: PC=0x{loader.EntryPoint:X8} SP=0x{Cpu.GPR[1]:X8}");
    }

    static uint FindStackPointer(byte[] ram, uint entryPoint)
    {
        uint ep = entryPoint & 0x01FFFFFF;
        for (int i = 0; i < 64; i++)
        {
            uint instr = GcBus.ReadBe32(ram, (int)(ep + i * 4));
            uint opcode = instr >> 26;
            if (opcode == 18)
            {
                uint li = instr & 0x03FFFFFC;
                if ((li & 0x02000000) != 0) li |= 0xFC000000;
                uint target = (entryPoint + (uint)(i * 4) + li) & 0x01FFFFFF;
                for (int j = 0; j < 16; j++)
                {
                    uint sub = GcBus.ReadBe32(ram, (int)(target + j * 4));
                    if ((sub >> 26) == 15 && ((sub >> 21) & 0x1F) == 1)
                    {
                        uint hi = (sub & 0xFFFF) << 16;
                        uint next = GcBus.ReadBe32(ram, (int)(target + (j + 1) * 4));
                        if ((next >> 26) == 24 && ((next >> 21) & 0x1F) == 1 && ((next >> 16) & 0x1F) == 1)
                        {
                            uint sp = hi | (next & 0xFFFF);
                            Log.Info($"Found stack pointer in DOL: 0x{sp:X8}");
                            return sp;
                        }
                    }
                }
            }
        }
        return 0;
    }

    void InstallExceptionStubs()
    {
        uint[] vectors = { 0x100, 0x200, 0x300, 0x400, 0x500, 0x600, 0x700, 0x800, 0x900, 0xC00, 0xD00, 0xF00, 0x1300, 0x1400, 0x1700 };
        foreach (uint v in vectors)
            GcBus.WriteBe32(Bus.Ram, (int)v, 0x4C000064); // rfi
        Log.Info("Exception vector stubs installed");
    }

    public void Reset()
    {
        Bus.Reset();
        Cpu.Reset();
        FrameCount = 0;
        Error = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick()
    {
        if (!Running) return;

        FrameCount++;
        Bus.Vi.FrameReady = false;

        try
        {
            Profiler.Begin("Frame");

            int cyclesRun = 0;
            for (int line = 0; line < LinesPerFrame; line++)
            {
                int lineEnd = (line + 1) * CyclesPerLine;
                while (cyclesRun < lineEnd)
                {
                    int batchEnd = Math.Min(cyclesRun + InnerBatch, lineEnd);
                    int batchLen = batchEnd - cyclesRun;

                    Profiler.Begin("CPU");
                    for (int j = 0; j < batchLen; j++)
                    {
                        Cpu.HandleInterrupts();
                        Cpu.Step();
                    }
                    Profiler.End("CPU");

                    cyclesRun = batchEnd;
                }

                Bus.Vi.TickLine();
                Bus.Si.Tick();
                Bus.Di.Tick();
                Bus.Dsp.TickAudioDma(CyclesPerLine);
                Bus.Ai.Tick();

                if (line == LinesPerFrame / 2)
                    Bus.Si.PollControllers();
            }

            Profiler.Begin("GPU");
            Bus.Gpu.ProcessFifoFromRam();
            Profiler.End("GPU");

            Bus.Gpu.VBlankSnapshot();

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

        _fpsFrameCounter++;
        long now = Environment.TickCount64;
        long elapsed = now - _fpsWindowStart;
        if (elapsed >= 500)
        {
            EmuFps = _fpsFrameCounter * 1000.0 / elapsed;
            _fpsFrameCounter = 0;
            _fpsWindowStart = now;
        }

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
