using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GcEmu;

unsafe class GcBus
{
    public readonly byte[] Ram;
    readonly byte* _ramPtr;
    readonly GCHandle _ramH;
    readonly byte[] _l2Cache = new byte[0x4000];
    readonly byte* _l2Ptr;
    readonly GCHandle _l2H;

    public readonly GcPi Pi;
    public readonly GcVi Vi;
    public readonly GcSi Si;
    public readonly GcExi Exi;
    public readonly GcDi Di;
    public readonly GcDsp Dsp;
    public readonly GcAi Ai;
    public readonly GcMi Mi;
    public readonly GcGpu Gpu;

    public PowerPc? Cpu;
    public GcMachine Machine = null!;
    int _lowMemWatchCount;
    int _threadSwitchCount;
    public int TotalThreadSwitches;

    const uint WgPipeAddr = 0xCC008000;

    public GcBus()
    {
        Ram = new byte[24 * 1024 * 1024];
        _ramH = GCHandle.Alloc(Ram, GCHandleType.Pinned);
        _ramPtr = (byte*)_ramH.AddrOfPinnedObject();
        _l2H = GCHandle.Alloc(_l2Cache, GCHandleType.Pinned);
        _l2Ptr = (byte*)_l2H.AddrOfPinnedObject();

        Pi = new GcPi();
        Vi = new GcVi();
        Si = new GcSi();
        Exi = new GcExi();
        Di = new GcDi();
        Dsp = new GcDsp();
        Ai = new GcAi();
        Mi = new GcMi();
        Gpu = new GcGpu();

        Pi.Init(this);
        Vi.Init(this);
        Si.Init(this);
        Exi.Init(this);
        Di.Init(this);
        Dsp.Init(this);
        Ai.Init(this);
        Gpu.Init(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint TranslateAddr(uint addr)
    {
        if (addr >= 0x80000000 && addr < 0x98000000)
            return addr & 0x01FFFFFF;
        if (addr >= 0xC0000000 && addr < 0xD8000000)
            return addr & 0x01FFFFFF;
        if (addr < 0x01800000)
            return addr;
        return addr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint addr)
    {
        if (addr >= 0xCC000000 && addr < 0xCC010000)
            return (byte)IoRead(addr, 1);

        if (addr >= 0xE0000000 && addr < 0xE0004000)
            return _l2Ptr[addr & 0x3FFF];

        uint phys = TranslateAddr(addr);
        if (phys < 0x01800000)
            return _ramPtr[phys];

        if (addr >= 0xFFF00000)
            return 0;

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Read16(uint addr)
    {
        if (addr >= 0xCC000000 && addr < 0xCC010000)
            return (ushort)IoRead(addr, 2);

        if (addr >= 0xE0000000 && addr < 0xE0004000)
        {
            uint off = addr & 0x3FFF;
            return (ushort)(_l2Ptr[off] << 8 | _l2Ptr[off + 1]);
        }

        uint phys = TranslateAddr(addr);
        if (phys < 0x01800000)
            return (ushort)(_ramPtr[phys] << 8 | _ramPtr[phys + 1]);

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint addr)
    {
        if (addr >= 0xCC000000 && addr < 0xCC010000)
            return IoRead(addr, 4);

        if (addr >= 0xE0000000 && addr < 0xE0004000)
        {
            uint off = addr & 0x3FFF;
            return (uint)(_l2Ptr[off] << 24 | _l2Ptr[off + 1] << 16 | _l2Ptr[off + 2] << 8 | _l2Ptr[off + 3]);
        }

        uint phys = TranslateAddr(addr);
        if (phys < 0x01800000)
            return (uint)(_ramPtr[phys] << 24 | _ramPtr[phys + 1] << 16 | _ramPtr[phys + 2] << 8 | _ramPtr[phys + 3]);

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Read64(uint addr)
    {
        return ((ulong)Read32(addr) << 32) | Read32(addr + 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(uint addr, byte val)
    {
        if (addr >= 0xCC000000 && addr < 0xCC010000)
        {
            IoWrite(addr, val, 1);
            return;
        }

        if (addr >= 0xE0000000 && addr < 0xE0004000)
        {
            _l2Ptr[addr & 0x3FFF] = val;
            return;
        }

        uint phys = TranslateAddr(addr);
        if (phys < 0x01800000)
        {
            if ((phys >= 0x28 && phys <= 0x3F) && _lowMemWatchCount < 10)
            {
                _lowMemWatchCount++;
                Console.Error.WriteLine($"  LOWMEM W8: [0x{phys:X}] = 0x{val:X2} from PC=0x{Machine.Cpu.PC:X8} LR=0x{Machine.Cpu.LR:X8}");
            }
            if (phys >= 0x001393C0 && phys <= 0x001393FF && _watchFlagCount < 50)
            {
                _watchFlagCount++;
                Console.Error.WriteLine($"  FLAG_WATCH W8: [0x{phys:X}] = 0x{val:X2} from PC=0x{Machine.Cpu.PC:X8} LR=0x{Machine.Cpu.LR:X8}");
            }
            _ramPtr[phys] = val;
        }
    }

    int _watchFlagCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write16(uint addr, ushort val)
    {
        if (addr >= 0xCC000000 && addr < 0xCC010000)
        {
            IoWrite(addr, val, 2);
            return;
        }

        if (addr >= 0xE0000000 && addr < 0xE0004000)
        {
            uint off = addr & 0x3FFF;
            _l2Ptr[off]     = (byte)(val >> 8);
            _l2Ptr[off + 1] = (byte)val;
            return;
        }

        uint phys = TranslateAddr(addr);
        if (phys < 0x01800000)
        {
            if ((phys >= 0x28 && phys <= 0x3F) && _lowMemWatchCount < 10)
            {
                _lowMemWatchCount++;
                Console.Error.WriteLine($"  LOWMEM W16: [0x{phys:X}] = 0x{val:X4} from PC=0x{Machine.Cpu.PC:X8} LR=0x{Machine.Cpu.LR:X8}");
            }
            if (phys >= 0x001393C0 && phys <= 0x001393FF && _watchFlagCount < 50)
            {
                _watchFlagCount++;
                Console.Error.WriteLine($"  FLAG_WATCH W16: [0x{phys:X}] = 0x{val:X4} from PC=0x{Machine.Cpu.PC:X8} LR=0x{Machine.Cpu.LR:X8} MSR=0x{Machine.Cpu.MSR:X8}");
            }
            _ramPtr[phys]     = (byte)(val >> 8);
            _ramPtr[phys + 1] = (byte)val;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(uint addr, uint val)
    {
        if (addr == WgPipeAddr || (addr >= WgPipeAddr && addr < WgPipeAddr + 4))
        {
            Gpu.WriteFifo32(val);
            return;
        }

        if (addr >= 0xCC000000 && addr < 0xCC010000)
        {
            IoWrite(addr, val, 4);
            return;
        }

        if (addr >= 0xE0000000 && addr < 0xE0004000)
        {
            uint off = addr & 0x3FFF;
            _l2Ptr[off]     = (byte)(val >> 24);
            _l2Ptr[off + 1] = (byte)(val >> 16);
            _l2Ptr[off + 2] = (byte)(val >> 8);
            _l2Ptr[off + 3] = (byte)val;
            return;
        }

        uint phys = TranslateAddr(addr);
        if (phys < 0x01800000)
        {
            if ((phys >= 0x28 && phys <= 0x3F) && _lowMemWatchCount < 10)
            {
                _lowMemWatchCount++;
                Console.Error.WriteLine($"  LOWMEM WRITE: [0x{phys:X}] = 0x{val:X8} from PC=0x{Machine.Cpu.PC:X8} LR=0x{Machine.Cpu.LR:X8}");
            }
            if (phys == 0xC0)
            {
                TotalThreadSwitches++;
                if (_threadSwitchCount < 30)
                {
                    _threadSwitchCount++;
                    Console.Error.WriteLine($"  THREAD_SWITCH #{TotalThreadSwitches}: [0xC0] = 0x{val:X8} from PC=0x{Machine.Cpu.PC:X8} LR=0x{Machine.Cpu.LR:X8} MSR=0x{Machine.Cpu.MSR:X8}");
                }
            }
            if ((phys >= 0x001393C0 && phys <= 0x001393FF) && _watchFlagCount < 50)
            {
                _watchFlagCount++;
                Console.Error.WriteLine($"  FLAG_WATCH W32: [0x{phys:X}] = 0x{val:X8} from PC=0x{Machine.Cpu.PC:X8} LR=0x{Machine.Cpu.LR:X8} MSR=0x{Machine.Cpu.MSR:X8}");
            }
            _ramPtr[phys]     = (byte)(val >> 24);
            _ramPtr[phys + 1] = (byte)(val >> 16);
            _ramPtr[phys + 2] = (byte)(val >> 8);
            _ramPtr[phys + 3] = (byte)val;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write64(uint addr, ulong val)
    {
        Write32(addr, (uint)(val >> 32));
        Write32(addr + 4, (uint)val);
    }

    uint IoRead(uint addr, int size)
    {
        uint block = addr & 0xFFFF0000;
        if (block != 0xCC000000) return 0;

        uint off = addr & 0xFFFF;

        if (off < 0x0080) return Gpu.ReadCp16(addr);
        if (off >= 0x1000 && off < 0x1100) return Gpu.ReadPe16(addr);
        if (off >= 0x2000 && off < 0x2100) return size == 2 ? (uint)Vi.Read16(addr) : Vi.Read32(addr);
        if (off >= 0x3000 && off < 0x3100) return Pi.Read32(addr);
        if (off >= 0x4000 && off < 0x4080) return size == 2 ? (uint)Mi.Read16(addr) : Mi.Read32(addr);
        if (off >= 0x5000 && off < 0x5200)
            return size == 4 ? (uint)(Dsp.Read16(addr) << 16 | Dsp.Read16(addr + 2)) : Dsp.Read16(addr);
        if (off >= 0x6000 && off < 0x6040) return Di.Read32(addr);
        if (off >= 0x6400 && off < 0x6500) return Si.Read32(addr);
        if (off >= 0x6800 && off < 0x6840) return Exi.Read32(addr);
        if (off >= 0x6C00 && off < 0x6C20) return Ai.Read32(addr);

        return 0;
    }

    void IoWrite(uint addr, uint val, int size)
    {
        uint off = addr & 0xFFFF;

        if (off < 0x0080)
        {
            if (size == 4) { Gpu.WriteCp16(addr, (ushort)(val >> 16)); Gpu.WriteCp16(addr + 2, (ushort)val); }
            else Gpu.WriteCp16(addr, (ushort)val);
            return;
        }
        if (off >= 0x1000 && off < 0x1100)
        {
            if (size == 4) { Gpu.WritePe16(addr, (ushort)(val >> 16)); Gpu.WritePe16(addr + 2, (ushort)val); }
            else Gpu.WritePe16(addr, (ushort)val);
            return;
        }
        if (off >= 0x2000 && off < 0x2100) { if (size == 2) Vi.Write16(addr, (ushort)val); else Vi.Write32(addr, val); return; }
        if (off >= 0x3000 && off < 0x3100) { Pi.Write32(addr, val); return; }
        if (off >= 0x4000 && off < 0x4080) { if (size == 2) Mi.Write16(addr, (ushort)val); else Mi.Write32(addr, val); return; }
        if (off >= 0x5000 && off < 0x5200)
        {
            if (size == 4)
            {
                Dsp.Write16(addr, (ushort)(val >> 16));
                Dsp.Write16(addr + 2, (ushort)val);
            }
            else
            {
                Dsp.Write16(addr, (ushort)val);
            }
            return;
        }
        if (off >= 0x6000 && off < 0x6040) { Di.Write32(addr, val); return; }
        if (off >= 0x6400 && off < 0x6500) { Si.Write32(addr, val); return; }
        if (off >= 0x6800 && off < 0x6840) { Exi.Write32(addr, val); return; }
        if (off >= 0x6C00 && off < 0x6C20) { Ai.Write32(addr, val); return; }

        if (off >= 0x8000 && off < 0x8004) { Gpu.WriteFifo32(val); return; }
    }

    public void SetupLowMem()
    {
        WriteBe32(Ram, 0x0028, 0x01800000); // Physical memory size (24 MB)
        WriteBe32(Ram, 0x002C, 0x00000003); // Console type: retail PAL
        WriteBe32(Ram, 0x00CC, 0x00000001); // VI init: 1=PAL
        WriteBe32(Ram, 0x00D0, 0x01000000); // ARAM size (16 MB)
        WriteBe32(Ram, 0x00F0, 0x01800000); // Simulated memory size
        WriteBe32(Ram, 0x00F8, 0x09A7EC80); // Bus clock (162 MHz)
        WriteBe32(Ram, 0x00FC, 0x1CF7C580); // CPU clock (486 MHz)

        // Memory protection/info registers
        WriteBe32(Ram, 0x3100, 0x01800000);
        WriteBe32(Ram, 0x3104, 0x01800000);
        WriteBe32(Ram, 0x3108, 0x01000000);

        // Time base initialization (Dolphin does this at 0x30D8)
        ulong tbTicks = 40500000UL * 1000;
        WriteBe32(Ram, 0x30D8, (uint)(tbTicks >> 32));
        WriteBe32(Ram, 0x30DC, (uint)tbTicks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadBe32(byte[] buf, int offset) =>
        (uint)(buf[offset] << 24 | buf[offset + 1] << 16 | buf[offset + 2] << 8 | buf[offset + 3]);

    public static void WriteBe32(byte[] buf, int offset, uint val)
    {
        buf[offset]     = (byte)(val >> 24);
        buf[offset + 1] = (byte)(val >> 16);
        buf[offset + 2] = (byte)(val >> 8);
        buf[offset + 3] = (byte)val;
    }

    public void Reset()
    {
        Array.Clear(Ram);
        Pi.Reset();
        Vi.Reset();
        Si.Reset();
        Exi.Reset();
        Di.Reset();
        Dsp.Reset();
        Ai.Reset();
        Mi.Reset();
        Gpu.Reset();
    }
}
