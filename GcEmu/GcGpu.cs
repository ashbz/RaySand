using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GcEmu;

class GcGpu
{
    GcBus _bus = null!;

    public ushort CpSr;
    public ushort CpCr;
    public ushort CpClear;
    public ushort Token;

    public uint FifoBase, FifoEnd;
    public uint FifoHiWm, FifoLoWm;
    public uint FifoRwDist;
    public uint FifoWp, FifoRp;
    public uint FifoBp;

    public ushort PeZconf;
    public ushort PeAlphaConf;
    public ushort PeDstAlpha;
    public ushort PeAlphaMode;
    public ushort PeAlphaRead;
    public ushort PeIntSr;
    public int PeFinishCount;
    public ushort PeToken;

    public uint[] BpRegs = new uint[256];
    public uint[] XfRegs = new uint[0x1000];
    public uint[] CpRegs = new uint[256];

    byte[] _fifo = new byte[32];
    int _fifoPos;
    int _cmdBytesNeeded;
    byte _currentCmd;

    public SoftwareRenderer Renderer { get; private set; } = null!;

    int _vertexCount;
    int _primitiveType;
    int _vatIndex;
    int _verticesRemaining;

    public long Gp0Count;
    public long FifoWrites;

    public void Init(GcBus bus)
    {
        _bus = bus;
        Renderer = new SoftwareRenderer();
        Renderer.Init(bus);
    }

    public void SetRenderer(SoftwareRenderer r)
    {
        Renderer = r;
        Renderer.Init(_bus);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFifo8(byte val)
    {
        FifoWrites++;
        ProcessByte(val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFifo16(ushort val)
    {
        FifoWrites++;
        ProcessByte((byte)(val >> 8));
        ProcessByte((byte)val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteFifo32(uint val)
    {
        FifoWrites++;
        ProcessByte((byte)(val >> 24));
        ProcessByte((byte)(val >> 16));
        ProcessByte((byte)(val >> 8));
        ProcessByte((byte)val);
    }

    void ProcessByte(byte b)
    {
        if (_fifoPos == 0)
        {
            if (b == 0x00) return;
            _currentCmd = b;
            _fifo[0] = b;
            _fifoPos = 1;
            _cmdBytesNeeded = GetCommandSize(b);
            if (_cmdBytesNeeded <= 1)
            {
                ExecuteCommand();
                _fifoPos = 0;
            }
            return;
        }

        if (_fifoPos < _fifo.Length)
            _fifo[_fifoPos] = b;
        _fifoPos++;

        if (_fifoPos >= _cmdBytesNeeded)
        {
            ExecuteCommand();
            _fifoPos = 0;
        }
    }

    int GetCommandSize(byte cmd)
    {
        if (cmd == 0x00) return 1;
        if (cmd == 0x08) return 6;
        if (cmd == 0x10) return -1;
        if (cmd >= 0x20 && cmd <= 0x38 && (cmd & 7) == 0) return 5;
        if (cmd == 0x48) return 1;
        if (cmd == 0x61) return 5;
        if (cmd >= 0x80) return 3;
        return 1;
    }

    void ExecuteCommand()
    {
        Gp0Count++;
        byte cmd = _currentCmd;

        switch (cmd)
        {
            case 0x00: break;
            case 0x08: CmdLoadCpReg(); break;
            case 0x10: CmdLoadXfReg(); break;
            case 0x48: break;
            case 0x61: CmdLoadBpReg(); break;
            default:
                if (cmd >= 0x80)
                    CmdDrawPrimitive();
                break;
        }
    }

    void CmdLoadCpReg()
    {
        byte reg = _fifo[1];
        uint val = Be32(2);
        CpRegs[reg] = val;
    }

    void CmdLoadXfReg()
    {
        return;
    }

    void CmdLoadBpReg()
    {
        uint packed = Be32(1);
        int reg = (int)(packed >> 24);
        uint val = packed & 0x00FFFFFF;
        BpRegs[reg] = val;

        switch (reg)
        {
            case 0x45:
                PeFinishCount++;
                PeIntSr |= 1 << 3;
                UpdatePeInterrupts();
                break;
            case 0x47:
                PeToken = (ushort)(val & 0xFFFF);
                break;
            case 0x48:
                PeToken = (ushort)(val & 0xFFFF);
                PeIntSr |= 1 << 2;
                UpdatePeInterrupts();
                break;
            case 0x52:
                CmdEfbCopy();
                break;
        }
    }

    void CmdEfbCopy()
    {
        uint srcTl = BpRegs[0x49];
        uint srcBr = BpRegs[0x4A];
        uint dstAddr = BpRegs[0x4B];
        uint stride = BpRegs[0x4D];
        uint yScale = BpRegs[0x4E];
        uint ctrl = BpRegs[0x52];

        int srcX = (int)(srcTl & 0x3FF);
        int srcY = (int)((srcTl >> 10) & 0x3FF);
        int w = (int)(srcBr & 0x3FF) + 1;
        int h = (int)((srcBr >> 10) & 0x3FF) + 1;
        uint physDst = (dstAddr & 0x01FFFFFF) << 5;
        bool clearEfb = (ctrl & 4) != 0;
        bool isXfbCopy = (ctrl & 3) == 1;

        if (isXfbCopy)
        {
            Renderer.CopyEfbToXfb(physDst, w, h, (int)(stride & 0x3FF));
        }

        if (clearEfb)
        {
            uint clearAr = BpRegs[0x4F];
            uint clearGb = BpRegs[0x50];
            uint clearZ  = BpRegs[0x51];
            byte r = (byte)(clearAr & 0xFF);
            byte a = (byte)((clearAr >> 8) & 0xFF);
            byte b = (byte)(clearGb & 0xFF);
            byte g = (byte)((clearGb >> 8) & 0xFF);
            uint z = clearZ & 0x00FFFFFF;
            Renderer.ClearEfb(r, g, b, a, z);
        }
    }

    void CmdDrawPrimitive()
    {
        int type = (_currentCmd >> 3) & 7;
        int vat = _currentCmd & 7;
        int count = (_fifo[1] << 8) | _fifo[2];

        Renderer.BeginPrimitive(type, count, vat, CpRegs, XfRegs);
    }

    public ushort ReadCp16(uint addr)
    {
        int off = (int)(addr & 0x7F);
        return off switch
        {
            0x00 => CpSr,
            0x02 => CpCr,
            0x0E => Token,
            _ => ReadCpFifo16(off)
        };
    }

    public void WriteCp16(uint addr, ushort val)
    {
        int off = (int)(addr & 0x7F);
        switch (off)
        {
            case 0x00: CpSr = val; break;
            case 0x02: CpCr = val; break;
            case 0x04: CpClear = val; break;
            default: WriteCpFifo16(off, val); break;
        }
    }

    ushort ReadCpFifo16(int off)
    {
        return off switch
        {
            0x20 => (ushort)FifoBase,
            0x22 => (ushort)(FifoBase >> 16),
            0x24 => (ushort)FifoEnd,
            0x26 => (ushort)(FifoEnd >> 16),
            0x28 => (ushort)FifoHiWm,
            0x2A => (ushort)(FifoHiWm >> 16),
            0x2C => (ushort)FifoLoWm,
            0x2E => (ushort)(FifoLoWm >> 16),
            0x30 => (ushort)FifoRwDist,
            0x32 => (ushort)(FifoRwDist >> 16),
            0x34 => (ushort)FifoWp,
            0x36 => (ushort)(FifoWp >> 16),
            0x38 => (ushort)FifoRp,
            0x3A => (ushort)(FifoRp >> 16),
            0x3C => (ushort)FifoBp,
            0x3E => (ushort)(FifoBp >> 16),
            _ => 0
        };
    }

    void WriteCpFifo16(int off, ushort val)
    {
        switch (off)
        {
            case 0x20: FifoBase = (FifoBase & 0xFFFF0000) | val; break;
            case 0x22: FifoBase = (FifoBase & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x24: FifoEnd = (FifoEnd & 0xFFFF0000) | val; break;
            case 0x26: FifoEnd = (FifoEnd & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x28: FifoHiWm = (FifoHiWm & 0xFFFF0000) | val; break;
            case 0x2A: FifoHiWm = (FifoHiWm & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x2C: FifoLoWm = (FifoLoWm & 0xFFFF0000) | val; break;
            case 0x2E: FifoLoWm = (FifoLoWm & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x30: FifoRwDist = (FifoRwDist & 0xFFFF0000) | val; break;
            case 0x32: FifoRwDist = (FifoRwDist & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x34: FifoWp = (FifoWp & 0xFFFF0000) | val; break;
            case 0x36: FifoWp = (FifoWp & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x38: FifoRp = (FifoRp & 0xFFFF0000) | val; break;
            case 0x3A: FifoRp = (FifoRp & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x3C: FifoBp = (FifoBp & 0xFFFF0000) | val; break;
            case 0x3E: FifoBp = (FifoBp & 0x0000FFFF) | ((uint)val << 16); break;
        }
    }

    public ushort ReadPe16(uint addr)
    {
        int off = (int)(addr & 0xF);
        return off switch
        {
            0x00 => PeZconf,
            0x02 => PeAlphaConf,
            0x04 => PeDstAlpha,
            0x06 => PeAlphaMode,
            0x08 => PeAlphaRead,
            0x0A => PeIntSr,
            0x0E => PeToken,
            _ => 0
        };
    }

    void UpdatePeInterrupts()
    {
        bool finish = (PeIntSr & (1 << 3)) != 0 && (PeIntSr & (1 << 1)) != 0;
        bool token = (PeIntSr & (1 << 2)) != 0 && (PeIntSr & (1 << 0)) != 0;

        if (finish) _bus.Pi.SetInterrupt(GcPi.IRQ_PE_FINISH);
        else _bus.Pi.ClearInterrupt(GcPi.IRQ_PE_FINISH);

        if (token) _bus.Pi.SetInterrupt(GcPi.IRQ_PE_TOKEN);
        else _bus.Pi.ClearInterrupt(GcPi.IRQ_PE_TOKEN);
    }

    public void WritePe16(uint addr, ushort val)
    {
        int off = (int)(addr & 0xF);
        switch (off)
        {
            case 0x00: PeZconf = val; break;
            case 0x02: PeAlphaConf = val; break;
            case 0x04: PeDstAlpha = val; break;
            case 0x06: PeAlphaMode = val; break;
            case 0x08: PeAlphaRead = val; break;
            case 0x0A:
                if ((val & (1 << 2)) != 0) PeIntSr &= unchecked((ushort)~(1 << 2));
                if ((val & (1 << 3)) != 0) PeIntSr &= unchecked((ushort)~(1 << 3));
                PeIntSr = (ushort)((PeIntSr & ~3) | (val & 3));
                UpdatePeInterrupts();
                break;
        }
    }

    public void ProcessFifoFromRam()
    {
        if ((CpCr & 1) == 0) return;
        if (FifoRwDist == 0) return;

        uint rp = FifoRp & 0x01FFFFFF;
        int dist = (int)FifoRwDist;
        int maxProcess = Math.Min(dist, 4096);

        for (int i = 0; i < maxProcess; i++)
        {
            byte b = _bus.Ram[rp];
            ProcessByte(b);
            rp++;
            if (rp > (FifoEnd & 0x01FFFFFF))
                rp = FifoBase & 0x01FFFFFF;
        }

        FifoRp = rp;
        FifoRwDist -= (uint)maxProcess;

        CpSr |= (1 << 2) | (1 << 3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint Be32(int offset)
    {
        return (uint)(_fifo[offset] << 24 | _fifo[offset + 1] << 16 |
                      _fifo[offset + 2] << 8 | _fifo[offset + 3]);
    }

    public void VBlankSnapshot()
    {
        Renderer.VBlankSnapshot();
    }

    public void Reset()
    {
        CpSr = 0;
        CpCr = 0;
        CpClear = 0;
        Token = 0;
        FifoBase = FifoEnd = FifoHiWm = FifoLoWm = 0;
        FifoRwDist = 0;
        FifoWp = FifoRp = FifoBp = 0;
        PeZconf = PeAlphaConf = PeDstAlpha = PeAlphaMode = PeAlphaRead = 0;
        PeIntSr = 0;
        PeToken = 0;
        Array.Clear(BpRegs);
        Array.Clear(XfRegs);
        Array.Clear(CpRegs);
        Array.Clear(_fifo);
        _fifoPos = 0;
        _cmdBytesNeeded = 0;
        _currentCmd = 0;
        Gp0Count = 0;
        FifoWrites = 0;
        Renderer.Reset();
    }
}
