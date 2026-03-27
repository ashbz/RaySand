using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PsxEmu;

/// <summary>
/// PSX memory bus with unsafe raw-pointer memory for zero-overhead access.
/// Architecture modelled after ProjectPSX's BUS.cs (proven 120fps C#).
/// </summary>
unsafe class PsxBus
{
    // Pinned memory regions – pointers for zero-bounds-check access
    readonly byte* _ramPtr;
    readonly byte* _biosPtr;
    readonly byte* _scratchPtr;
    readonly GCHandle _ramH, _biosH, _scratchH;

    // Keep managed references for DMA span access and BIOS loading
    internal readonly byte[] Ram;
    internal readonly byte[] Bios;
    readonly byte[] _scratch;

    public readonly PsxGpu Gpu;
    public readonly PsxSpu Spu;
    public readonly PsxDma Dma;
    public readonly PsxCdRom CdRom;
    public MipsCpu? Cpu;

    // Memory control & misc I/O stubs (store-and-return so BIOS config works)
    readonly uint[] _memCtrl = new uint[16]; // 0x1F801000-0x1F80103F
    uint _ramSize;                           // 0x1F801060
    uint _cacheCtrl;                         // 0xFFFE0130
    uint _mdecData, _mdecStatus = 0x8000_0000; // 0x1F801820-0x1F801824

    // ── Interrupt controller ────────────────────────────────────────────────
    public uint IStat;
    public uint IMask;
    public bool IRQPending => (IStat & IMask) != 0;

    // ── Joypad / SIO0 ────────────────────────────────────────────────────
    byte _joyRxData = 0xFF;
    ushort _joyCtrl;
    ushort _joyMode;
    ushort _joyBaud = 0x88;
    bool _joyRxReady;
    bool _joyAck;
    bool _joyIrq;           // JOY_STAT bit 9 — sticky, cleared via JOY_CTRL bit 4
    int _joyIrqCountdown;   // Delayed IRQ7 delivery (cycles until fire)
    int _joyStep;

    // Button state: active LOW (0 = pressed). Set from UI thread.
    // Low byte bits: Select, L3, R3, Start, Up, Right, Down, Left
    // High byte bits: L2, R2, L1, R1, Triangle, Circle, Cross, Square
    public ushort JoyButtons = 0xFFFF;

    uint JoyStat
    {
        get
        {
            uint v = 0x01 | 0x04; // TX Ready Flag 1 + TX Ready Flag 2
            if (_joyRxReady) v |= 0x02; // RX FIFO Not Empty
            if (_joyAck) v |= 0x80;     // /ACK input level
            if (_joyIrq) v |= 0x200;    // Interrupt Request (IRQ7)
            return v;
        }
    }

    /// <summary>
    /// Tick joypad IRQ countdown by a batch of cycles.
    /// Sets IStat JOY bit externally if needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TickJoyBatch(int cycles)
    {
        if (_joyIrqCountdown <= 0) return false;
        _joyIrqCountdown -= cycles;
        if (_joyIrqCountdown <= 0)
        {
            _joyIrqCountdown = 0;
            _joyAck = false;
            _joyIrq = true;
            IStat |= 1u << 7; // ISTAT_JOY
            return true;
        }
        return false;
    }

    void JoyDataWrite(byte val)
    {
        bool selected = (_joyCtrl & 0x02) != 0; // JOY_CTRL bit 1 = /JOYn Select
        if (!selected)
        {
            _joyRxReady = true;
            _joyRxData = 0xFF;
            _joyAck = false;
            return;
        }

        _joyRxReady = true;
        _joyAck = false;

        switch (_joyStep)
        {
            case 0: // Expecting 0x01 = controller address
                if (val == 0x01) { _joyRxData = 0xFF; _joyAck = true; _joyStep = 1; }
                else { _joyRxData = 0xFF; }
                break;
            case 1: // Expecting 0x42 = Read command → reply with ID low byte
                _joyRxData = 0x41; // Digital pad ID low (0x41 = digital, 1 halfword of button data)
                _joyAck = (val == 0x42);
                _joyStep = val == 0x42 ? 2 : 0;
                break;
            case 2: // TAP byte (ignored) → reply with 0x5A (ID high byte)
                _joyRxData = 0x5A;
                _joyAck = true;
                _joyStep = 3;
                break;
            case 3: // MOT byte → reply with buttons low byte
                _joyRxData = (byte)(JoyButtons & 0xFF);
                _joyAck = true;
                _joyStep = 4;
                break;
            case 4: // MOT byte → reply with buttons high byte (last byte, no ACK)
                _joyRxData = (byte)(JoyButtons >> 8);
                _joyAck = false;
                _joyStep = 0;
                break;
            default:
                _joyRxData = 0xFF;
                _joyStep = 0;
                break;
        }

        if (_joyAck)
            _joyIrqCountdown = 500;
    }

    byte JoyDataRead()
    {
        _joyRxReady = false;
        return _joyRxData;
    }

    // ── Timers ──────────────────────────────────────────────────────────────
    public uint Timer0, Timer1, Timer2;
    public uint Tim0Mode, Tim1Mode, Tim2Mode;
    public uint Tim0Target, Tim1Target, Tim2Target;

    public PsxBus(byte[] bios)
    {
        Ram = new byte[2 * 1024 * 1024];
        Bios = bios;
        _scratch = new byte[1024];

        _ramH = GCHandle.Alloc(Ram, GCHandleType.Pinned);
        _ramPtr = (byte*)_ramH.AddrOfPinnedObject();

        _biosH = GCHandle.Alloc(Bios, GCHandleType.Pinned);
        _biosPtr = (byte*)_biosH.AddrOfPinnedObject();

        _scratchH = GCHandle.Alloc(_scratch, GCHandleType.Pinned);
        _scratchPtr = (byte*)_scratchH.AddrOfPinnedObject();

        Gpu = new PsxGpu();
        Spu = new PsxSpu();
        CdRom = new PsxCdRom();
        Dma = new PsxDma(this, Gpu, CdRom, Spu);
    }

    ~PsxBus()
    {
        if (_ramH.IsAllocated)     _ramH.Free();
        if (_biosH.IsAllocated)    _biosH.Free();
        if (_scratchH.IsAllocated) _scratchH.Free();
    }

    public void ResetState()
    {
        Array.Clear(Ram);
        Array.Clear(_scratch);
        IStat = 0;
        IMask = 0;
        Timer0 = Timer1 = Timer2 = 0;
        Tim0Mode = Tim1Mode = Tim2Mode = 0;
        Tim0Target = Tim1Target = Tim2Target = 0;
        _joyRxData = 0xFF;
        _joyCtrl = 0;
        _joyMode = 0;
        _joyBaud = 0x88;
        _joyRxReady = false;
        _joyAck = false;
        _joyIrq = false;
        _joyIrqCountdown = 0;
        _joyStep = 0;
        Dma.Reset();
        CdRom.Reset();
        Spu.Reset();
        Array.Clear(_memCtrl);
        _ramSize = 0;
        _cacheCtrl = 0;
        _mdecData = 0;
        _mdecStatus = 0x8000_0000;
    }

    // ── Fast instruction fetch (used by CPU – bypasses full address decode) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint LoadFromRam(uint physAddr)
        => *(uint*)(_ramPtr + (physAddr & 0x1F_FFFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint LoadFromBios(uint physAddr)
        => *(uint*)(_biosPtr + (physAddr & 0x7_FFFF));

    // ── Region mask table (strips KSEG bits via single lookup) ──────────────
    static readonly uint[] RegionMask =
    {
        0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, 0xFFFF_FFFF, // KUSEG
        0x7FFF_FFFF,                                          // KSEG0
        0x1FFF_FFFF,                                          // KSEG1
        0xFFFF_FFFF, 0xFFFF_FFFF,                             // KSEG2
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Mask(uint addr) => addr & RegionMask[addr >> 29];

    // ── Public read ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint addr)
    {
        uint p = Mask(addr);
        if (p < 0x1F00_0000)  return *(_ramPtr + (p & 0x1F_FFFF));
        if (p < 0x1F80_0400 && p >= 0x1F80_0000) return *(_scratchPtr + (p - 0x1F80_0000));
        if (p >= 0x1FC0_0000 && p < 0x1FC8_0000) return *(_biosPtr + (p - 0x1FC0_0000));
        return IoRead8(p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Read16(uint addr)
    {
        uint p = Mask(addr);
        if (p < 0x1F00_0000)  return *(ushort*)(_ramPtr + (p & 0x1F_FFFF));
        if (p < 0x1F80_0400 && p >= 0x1F80_0000) return *(ushort*)(_scratchPtr + (p - 0x1F80_0000));
        if (p >= 0x1FC0_0000 && p < 0x1FC8_0000) return *(ushort*)(_biosPtr + (p - 0x1FC0_0000));
        return IoRead16(p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint addr)
    {
        uint p = Mask(addr);
        if (p < 0x1F00_0000)  return *(uint*)(_ramPtr + (p & 0x1F_FFFF));
        if (p < 0x1F80_0400 && p >= 0x1F80_0000) return *(uint*)(_scratchPtr + (p - 0x1F80_0000));
        if (p >= 0x1FC0_0000 && p < 0x1FC8_0000) return *(uint*)(_biosPtr + (p - 0x1FC0_0000));
        return IoRead32(p);
    }

    // ── Public write ────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(uint addr, byte val)
    {
        if (Cpu != null && (Cpu.COP0[12] & 0x10000) != 0) return;
        uint p = Mask(addr);
        if (p < 0x1F00_0000) { *(_ramPtr + (p & 0x1F_FFFF)) = val; return; }
        if (p < 0x1F80_0400 && p >= 0x1F80_0000) { *(_scratchPtr + (p - 0x1F80_0000)) = val; return; }
        IoWrite8(p, val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write16(uint addr, ushort val)
    {
        if (Cpu != null && (Cpu.COP0[12] & 0x10000) != 0) return;
        uint p = Mask(addr);
        if (p < 0x1F00_0000) { *(ushort*)(_ramPtr + (p & 0x1F_FFFF)) = val; return; }
        if (p < 0x1F80_0400 && p >= 0x1F80_0000) { *(ushort*)(_scratchPtr + (p - 0x1F80_0000)) = val; return; }
        IoWrite16(p, val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(uint addr, uint val)
    {
        if (Cpu != null && (Cpu.COP0[12] & 0x10000) != 0) return;
        uint p = Mask(addr);
        if (p < 0x1F00_0000) { *(uint*)(_ramPtr + (p & 0x1F_FFFF)) = val; return; }
        if (p < 0x1F80_0400 && p >= 0x1F80_0000) { *(uint*)(_scratchPtr + (p - 0x1F80_0000)) = val; return; }
        IoWrite32(p, val);
    }

    // ── Fast DMA RAM access ─────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DmaLoadWord(uint addr) => *(uint*)(_ramPtr + (addr & 0x1F_FFFF));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DmaStoreWord(uint addr, uint val)
        => *(uint*)(_ramPtr + (addr & 0x1F_FFFF)) = val;

    // ── I/O reads ───────────────────────────────────────────────────────────

    byte IoRead8(uint p)
    {
        if (p is >= 0x1F80_1800 and <= 0x1F80_1803)
            return CdRom.Read(p);
        if (p == 0x1F80_1040) return JoyDataRead();
        if (p == 0x1F80_1044) return (byte)JoyStat;
        if (p == 0x1F80_1070) return (byte)IStat;
        if (p == 0x1F80_1074) return (byte)IMask;
        if (p is >= 0x1F00_0000 and <= 0x1F7F_FFFF) return 0xFF; // Expansion 1
        if (p is >= 0x1F80_2000 and <= 0x1F80_2FFF) return 0xFF; // Expansion 2
        return 0xFF;
    }

    ushort IoRead16(uint p)
    {
        if (p is >= 0x1F80_1C00 and <= 0x1F80_1FFF)
            return Spu.Read(p);
        return p switch
        {
        0x1F80_1070 => (ushort)IStat,
        0x1F80_1074 => (ushort)IMask,
        0x1F80_1040 => JoyDataRead(),
        0x1F80_1044 => (ushort)JoyStat,
        0x1F80_1048 => _joyMode,
        0x1F80_104A => _joyCtrl,
        0x1F80_104E => _joyBaud,
        0x1F80_1054 => 0x0005,
        >= 0x1F80_1800 and <= 0x1F80_1803 => CdRom.Read(p),
        0x1F80_1810 => (ushort)Gpu.ReadData(),
        0x1F80_1814 => (ushort)Gpu.ReadStat(),
        0x1F80_1816 => (ushort)(Gpu.ReadStat() >> 16),
        0x1F80_1100 => (ushort)Timer0,
        0x1F80_1104 => (ushort)(Tim0Mode | 0x0400),
        0x1F80_1108 => (ushort)Tim0Target,
        0x1F80_1110 => (ushort)Timer1,
        0x1F80_1114 => (ushort)(Tim1Mode | 0x0400),
        0x1F80_1118 => (ushort)Tim1Target,
        0x1F80_1120 => (ushort)Timer2,
        0x1F80_1124 => (ushort)(Tim2Mode | 0x0400),
        0x1F80_1128 => (ushort)Tim2Target,
        >= 0x1F80_1080 and <= 0x1F80_10EF => (ushort)Dma.Read(p - 0x1F80_1080),
        _ => 0,
    };
    }

    uint IoRead32(uint p)
    {
        if (p is >= 0x1F80_1080 and <= 0x1F80_10EF) return Dma.Read(p - 0x1F80_1080);
        if (p is >= 0x1F80_1000 and <= 0x1F80_103F) return _memCtrl[(p - 0x1F80_1000) >> 2];
        if (p is >= 0x1F80_1C00 and <= 0x1F80_1FFF)
            return (uint)(Spu.Read(p) | (Spu.Read(p + 2) << 16));
        return p switch
        {
            0x1F80_10F0 => Dma.DPCR,
            0x1F80_10F4 => Dma.DICR,
            0x1F80_1810 => Gpu.ReadData(),
            0x1F80_1814 => Gpu.ReadStat(),
            0x1F80_1820 => _mdecData,
            0x1F80_1824 => _mdecStatus,
            0x1F80_1070 => IStat,
            0x1F80_1074 => IMask,
            0x1F80_1100 => Timer0,
            0x1F80_1104 => Tim0Mode | 0x0400u,
            0x1F80_1108 => Tim0Target,
            0x1F80_1110 => Timer1,
            0x1F80_1114 => Tim1Mode | 0x0400u,
            0x1F80_1118 => Tim1Target,
            0x1F80_1120 => Timer2,
            0x1F80_1124 => Tim2Mode | 0x0400u,
            0x1F80_1128 => Tim2Target,
            0x1F80_1040 => JoyDataRead(),
            0x1F80_1044 => JoyStat,
            0x1F80_1048 => _joyMode,
            0x1F80_104A => _joyCtrl,
            0x1F80_104E => _joyBaud,
            0x1F80_1050 => 0,
            0x1F80_1054 => 0x0000_0005,
            0x1F80_1060 => _ramSize,
            >= 0x1F80_1800 and <= 0x1F80_1803 => CdRom.Read(p),
            0xFFFE_0130 => _cacheCtrl,
            _ => 0,
        };
    }

    // ── I/O writes ──────────────────────────────────────────────────────────

    void IoWrite8(uint p, byte v)
    {
        if (p is >= 0x1F80_1800 and <= 0x1F80_1803)
            CdRom.Write(p, v);
        else if (p == 0x1F80_1040)
            JoyDataWrite(v);
    }

    void IoWrite16(uint p, ushort v)
    {
        if (p is >= 0x1F80_1800 and <= 0x1F80_1803) { CdRom.Write(p, (byte)v); return; }
        if (p is >= 0x1F80_1C00 and <= 0x1F80_1FFF) { Spu.Write(p, v); return; }
        switch (p)
        {
            case 0x1F80_1040: JoyDataWrite((byte)v); break;
            case 0x1F80_1048: _joyMode = v; break;
            case 0x1F80_104A:
                _joyCtrl = v;
                if ((v & 0x02) == 0) _joyStep = 0;
                if ((v & 0x10) != 0) { _joyIrq = false; }
                if ((v & 0x40) != 0) { _joyCtrl = 0; _joyMode = 0; _joyRxReady = false; _joyAck = false; _joyIrq = false; _joyIrqCountdown = 0; _joyStep = 0; }
                break;
            case 0x1F80_104E: _joyBaud = v; break;
            case 0x1F80_1070: IStat &= v; break;
            case 0x1F80_1074: IMask = v; break;
            case 0x1F80_1810: Gpu.WriteGP0(v); break;
            case 0x1F80_1814: Gpu.WriteGP1(v); break;
            case 0x1F80_1104: Tim0Mode = v; Timer0 = 0; break;
            case 0x1F80_1114: Tim1Mode = v; Timer1 = 0; break;
            case 0x1F80_1124: Tim2Mode = v; Timer2 = 0; break;
            case 0x1F80_1108: Tim0Target = v; break;
            case 0x1F80_1118: Tim1Target = v; break;
            case 0x1F80_1128: Tim2Target = v; break;
            case 0x1F80_1100: Timer0 = v; break;
            case 0x1F80_1110: Timer1 = v; break;
            case 0x1F80_1120: Timer2 = v; break;
        }
    }

    void IoWrite32(uint p, uint v)
    {
        if (p is >= 0x1F80_1800 and <= 0x1F80_1803) { CdRom.Write(p, (byte)v); return; }
        if (p is >= 0x1F80_1080 and <= 0x1F80_10EF) { Dma.Write(p - 0x1F80_1080, v); return; }
        if (p is >= 0x1F80_1000 and <= 0x1F80_103F) { _memCtrl[(p - 0x1F80_1000) >> 2] = v; return; }
        if (p is >= 0x1F80_1C00 and <= 0x1F80_1FFF)
        {
            Spu.Write(p, (ushort)v);
            Spu.Write(p + 2, (ushort)(v >> 16));
            return;
        }
        switch (p)
        {
            case 0x1F80_1040: JoyDataWrite((byte)v); return;
            case 0x1F80_1048: _joyMode = (ushort)v; return;
            case 0x1F80_104A:
                _joyCtrl = (ushort)v;
                if ((v & 0x02) == 0) _joyStep = 0;
                if ((v & 0x10) != 0) { _joyIrq = false; }
                if ((v & 0x40) != 0) { _joyCtrl = 0; _joyMode = 0; _joyRxReady = false; _joyAck = false; _joyIrq = false; _joyIrqCountdown = 0; _joyStep = 0; }
                return;
            case 0x1F80_104E: _joyBaud = (ushort)v; return;
            case 0x1F80_10F0: Dma.DPCR = v; break;
            case 0x1F80_10F4: Dma.DICR = v; break;
            case 0x1F80_1810: Gpu.WriteGP0(v); break;
            case 0x1F80_1814: Gpu.WriteGP1(v); break;
            case 0x1F80_1820: _mdecData = v; break;
            case 0x1F80_1824: _mdecStatus = v; break;
            case 0x1F80_1104: Tim0Mode = v; Timer0 = 0; break;
            case 0x1F80_1114: Tim1Mode = v; Timer1 = 0; break;
            case 0x1F80_1124: Tim2Mode = v; Timer2 = 0; break;
            case 0x1F80_1108: Tim0Target = v; break;
            case 0x1F80_1118: Tim1Target = v; break;
            case 0x1F80_1128: Tim2Target = v; break;
            case 0x1F80_1100: Timer0 = v; break;
            case 0x1F80_1110: Timer1 = v; break;
            case 0x1F80_1120: Timer2 = v; break;
            case 0x1F80_1060: _ramSize = v; break;
            case 0x1F80_1070: IStat &= v; break;
            case 0x1F80_1074: IMask = v; break;
            case 0xFFFE_0130: _cacheCtrl = v; break;
        }
    }
}
