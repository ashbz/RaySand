namespace GcEmu;

class GcDi
{
    GcBus _bus = null!;
    DiscImage? _disc;

    public uint DiSr;
    public uint DiCvr;
    public uint CmdBuf0, CmdBuf1, CmdBuf2;
    public uint DiMar;
    public uint DiLength;
    public uint DiCr;
    public uint ImmBuf;
    public uint DiCfg;

    int _completionCountdown;
    uint _pendingTransferSize;

    public bool HasDisc => _disc != null;

    public void Init(GcBus bus) => _bus = bus;

    public void InsertDisc(DiscImage disc)
    {
        _disc = disc;
        Log.Info($"DI: Disc inserted — {disc.GameId}");
    }

    public uint Read32(uint addr)
    {
        return (addr & 0x3F) switch
        {
            0x00 => DiSr,
            0x04 => DiCvr,
            0x08 => CmdBuf0,
            0x0C => CmdBuf1,
            0x10 => CmdBuf2,
            0x14 => DiMar,
            0x18 => DiLength,
            0x1C => DiCr,
            0x20 => ImmBuf,
            0x24 => DiCfg,
            _ => 0
        };
    }

    public void Write32(uint addr, uint val)
    {
        switch (addr & 0x3F)
        {
            case 0x00:
                // Mask bits: directly overwritten from val
                // Status bits (2,4,6): write-1-to-clear
                if ((val & (1 << 2)) != 0) DiSr &= ~(1u << 2);
                if ((val & (1 << 4)) != 0) { DiSr &= ~(1u << 4); TcintClearCount++; }
                if ((val & (1 << 6)) != 0) DiSr &= ~(1u << 6);
                DiSr = (DiSr & 0x54) | (val & ~0x54u);
                UpdateInterrupt();
                break;
            case 0x04:
                // DICVR: bit 1 = CVRINTMASK (rw), bit 2 = CVRINT (w1c), bit 0 = CVR (ro)
                if ((val & (1 << 2)) != 0) DiCvr &= ~(1u << 2);
                DiCvr = (DiCvr & 0x05) | (val & 0x02);
                UpdateInterrupt();
                break;
            case 0x08: CmdBuf0 = val; break;
            case 0x0C: CmdBuf1 = val; break;
            case 0x10: CmdBuf2 = val; break;
            case 0x14: DiMar = val & 0x03FFFFE0; break;
            case 0x18: DiLength = val & 0xFFFFFFE0u; break;
            case 0x1C:
                DiCr = val & 7;
                if ((DiCr & 1) != 0)
                    ExecuteCommand();
                break;
            case 0x20: ImmBuf = val; break;
            case 0x24: break; // DICFG is read-only
        }
    }

    public int ReadCount;

    public int TotalCmds;
    public int DiInterruptCount;
    public int TcintClearCount;
    void ExecuteCommand()
    {
        byte cmd = (byte)(CmdBuf0 >> 24);
        bool dma = (DiCr & 2) != 0;
        TotalCmds++;
        if (TotalCmds <= 20)
            Console.Error.WriteLine($"  DI cmd#{TotalCmds}: 0x{cmd:X2} dma={dma} off=0x{(long)CmdBuf1 << 2:X} len=0x{DiLength:X} mar=0x{DiMar:X8}");

        switch (cmd)
        {
            case 0xA8:
                ReadSectors(dma);
                ReadCount++;
                break;
            case 0x12:
                DoInquiry();
                break;
            case 0xE0:
                ImmBuf = 0;
                break;
            case 0xE1:
            case 0xE2:
            case 0xE3:
            case 0xE4:
                break;
            case 0xAB:
                break;
            default:
                Log.Info($"DI: Unknown cmd 0x{cmd:X2}");
                break;
        }

        _pendingTransferSize = DiLength;
        _completionCountdown = 500;
    }

    public void Tick()
    {
        if (_completionCountdown > 0)
        {
            _completionCountdown--;
            if (_completionCountdown == 0)
                FinishCommand();
        }
    }

    void FinishCommand()
    {
        if ((DiCr & 1) == 0) return; // TSTART cleared = cancelled

        DiMar += _pendingTransferSize;
        DiLength = 0;
        DiCr &= ~1u;
        DiSr |= 1u << 4;
        DiInterruptCount++;
        if (DiInterruptCount <= 10)
            Console.Error.WriteLine($"  DI FINISH cmd#{TotalCmds}: DiSr=0x{DiSr:X8} mar=0x{DiMar:X8}");
        UpdateInterrupt();
    }

    void ReadSectors(bool dma)
    {
        if (_disc == null) return;

        long offset = (long)CmdBuf1 << 2;
        int length = (int)DiLength;

        if (dma)
        {
            uint physAddr = DiMar & 0x01FFFFFF;
            int readLen = (int)Math.Min(length, _bus.Ram.Length - physAddr);
            if (readLen > 0)
            {
                _disc.Read(_bus.Ram, (int)physAddr, offset, readLen);
                if (TotalCmds <= 10)
                {
                    int peekLen = Math.Min(readLen, 32);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < peekLen; i++)
                        sb.Append($"{_bus.Ram[physAddr + i]:X2}");
                    bool allZero = true;
                    for (int i = 0; i < peekLen; i++)
                        if (_bus.Ram[physAddr + i] != 0) { allZero = false; break; }
                    Console.Error.WriteLine($"  DI DATA cmd#{TotalCmds}: first {peekLen}B: {sb} allZero={allZero}");
                }
            }
        }
        else
        {
            int immLen = Math.Min(length, 4);
            if (immLen >= 4)
            {
                byte[] tmp = _disc.ReadBytes(offset, 4);
                ImmBuf = (uint)(tmp[0] << 24 | tmp[1] << 16 | tmp[2] << 8 | tmp[3]);
            }
        }
    }

    void DoInquiry()
    {
        if (_disc == null) return;
        uint physAddr = DiMar & 0x01FFFFFF;
        int len = (int)Math.Min(DiLength, (uint)(_bus.Ram.Length - physAddr));

        void W32(int off, uint v)
        {
            if (off + 3 < len)
            {
                _bus.Ram[physAddr + off]     = (byte)(v >> 24);
                _bus.Ram[physAddr + off + 1] = (byte)(v >> 16);
                _bus.Ram[physAddr + off + 2] = (byte)(v >> 8);
                _bus.Ram[physAddr + off + 3] = (byte)v;
            }
        }

        W32(0, 0x00000002);
        W32(4, 0x20060526);
        W32(8, 0x41000000);
    }

    void UpdateInterrupt()
    {
        bool active = ((DiSr & (1 << 2)) != 0 && (DiSr & (1 << 1)) != 0) ||
                      ((DiSr & (1 << 4)) != 0 && (DiSr & (1 << 3)) != 0) ||
                      ((DiSr & (1 << 6)) != 0 && (DiSr & (1 << 5)) != 0) ||
                      ((DiCvr & (1 << 2)) != 0 && (DiCvr & (1 << 1)) != 0);
        if (active)
            _bus.Pi.SetInterrupt(GcPi.IRQ_DI);
        else
            _bus.Pi.ClearInterrupt(GcPi.IRQ_DI);
    }

    public void Reset()
    {
        DiSr = 0;
        DiCvr = 0;
        CmdBuf0 = CmdBuf1 = CmdBuf2 = 0;
        DiMar = 0;
        DiLength = 0;
        DiCr = 0;
        ImmBuf = 0;
        DiCfg = 0;
        _completionCountdown = 0;
        _pendingTransferSize = 0;
    }
}
