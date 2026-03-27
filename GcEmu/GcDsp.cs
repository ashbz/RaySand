namespace GcEmu;

class GcDsp
{
    GcBus _bus = null!;

    public ushort DspMboxH, DspMboxL;
    public ushort CpuMboxH, CpuMboxL;
    bool _cpuMboxFull;
    bool _dspMboxFull;
    public ushort Csr;
    public ushort ArSize;
    public ushort ArMode = 1;
    public ushort ArRefresh = 156;

    public ushort ArDmaMmH, ArDmaMmL;
    public ushort ArDmaArH, ArDmaArL;
    public ushort ArDmaCntH, ArDmaCntL;

    public ushort AiDmaAddrH, AiDmaAddrL;
    public ushort AiDmaCtrl;

    uint _audioDmaCurrentAddr;
    int _audioDmaBlocksRemaining;
    bool _audioDmaActive;
    int _audioDmaCycleAccum;

    public byte[] Aram = new byte[16 * 1024 * 1024];

    bool _dspRunning;
    public int AramDmaCount;
    public int DspMailboxCount;

    public void Init(GcBus bus) => _bus = bus;

    public ushort Read16(uint addr)
    {
        int off = (int)(addr & 0x3F);
        switch (off)
        {
            case 0x00: return (ushort)(DspMboxH & (_dspMboxFull ? 0xFFFF : 0x7FFF));
            case 0x02: return DspMboxL;
            case 0x04: return (ushort)(_cpuMboxFull ? CpuMboxH : (CpuMboxH & 0x7FFF));
            case 0x06:
                _cpuMboxFull = false;
                return CpuMboxL;
            case 0x0A: return Csr;
            case 0x12: return ArSize;
            case 0x16: return ArMode;
            case 0x1A: return ArRefresh;
            case 0x20: return ArDmaMmH;
            case 0x22: return ArDmaMmL;
            case 0x24: return ArDmaArH;
            case 0x26: return ArDmaArL;
            case 0x28: return ArDmaCntH;
            case 0x2A: return ArDmaCntL;
            case 0x30: return AiDmaAddrH;
            case 0x32: return AiDmaAddrL;
            case 0x36: return AiDmaCtrl;
            case 0x3A: return (ushort)(_audioDmaBlocksRemaining > 0 ? _audioDmaBlocksRemaining - 1 : 0);
            default: return 0;
        }
    }

    public void Write16(uint addr, ushort val)
    {
        int off = (int)(addr & 0x3F);
        switch (off)
        {
            case 0x00: DspMboxH = val; _dspMboxFull = true; break;
            case 0x02: DspMboxL = val; HandleDspMailbox(); break;
            case 0x04: CpuMboxH = (ushort)(val & 0x7FFF); break;
            case 0x06: CpuMboxL = val; break;
            case 0x0A: HandleCsr(val); break;
            case 0x12: ArSize = (ushort)(val & 0x007F); break;
            case 0x16: break;
            case 0x1A: ArRefresh = (ushort)(val & 0x07FF); break;
            case 0x20: ArDmaMmH = val; break;
            case 0x22: ArDmaMmL = val; break;
            case 0x24: ArDmaArH = val; break;
            case 0x26: ArDmaArL = val; break;
            case 0x28: ArDmaCntH = val; break;
            case 0x2A: ArDmaCntL = val; StartAramDma(); break;
            case 0x30: AiDmaAddrH = val; break;
            case 0x32: AiDmaAddrL = val; break;
            case 0x36:
                bool wasEnabled = (AiDmaCtrl & 0x8000) != 0;
                AiDmaCtrl = val;
                if (!wasEnabled && (val & 0x8000) != 0)
                {
                    _audioDmaCurrentAddr = ((uint)AiDmaAddrH << 16) | AiDmaAddrL;
                    _audioDmaBlocksRemaining = val & 0x7FFF;
                    _audioDmaActive = true;
                    _audioDmaCycleAccum = 0;
                    Console.Error.WriteLine($"  DSP AudioDMA START: addr=0x{_audioDmaCurrentAddr:X8} blocks={_audioDmaBlocksRemaining}");
                    Csr |= 1 << 3;
                    UpdateInterrupts();
                }
                break;
        }
    }

    void SendMailToCpu(ushort hi, ushort lo)
    {
        CpuMboxH = hi;
        CpuMboxL = lo;
        _cpuMboxFull = true;
    }

    void HandleCsr(ushort val)
    {
        if ((val & (1 << 3)) != 0)
            Csr &= unchecked((ushort)~(1 << 3));
        if ((val & (1 << 5)) != 0)
            Csr &= unchecked((ushort)~(1 << 5));
        if ((val & (1 << 7)) != 0)
            Csr &= unchecked((ushort)~(1 << 7));

        bool wasHalted = (Csr & (1 << 2)) != 0;

        ushort writeMask = 0x0155;
        Csr = (ushort)((Csr & ~writeMask) | (val & writeMask));

        bool nowHalted = (Csr & (1 << 2)) != 0;

        if ((val & 1) != 0)
        {
            Csr &= unchecked((ushort)~1);
            _dspRunning = true;
            SendMailToCpu(0xDCD1, 0x0000);
            Log.Info("DSP: Reset -> sent init done (0xDCD10000)");
        }
        else if (wasHalted && !nowHalted)
        {
            _dspRunning = true;
            SendMailToCpu(0xDCD1, 0x0002);
            Log.Info("DSP: Unhalted -> sent resume done (0xDCD10002)");
        }

        UpdateInterrupts();
    }

    void UpdateInterrupts()
    {
        bool active = (((Csr >> 1) & Csr) & ((1 << 3) | (1 << 5) | (1 << 7))) != 0;
        if (active)
            _bus.Pi.SetInterrupt(GcPi.IRQ_DSP);
        else
            _bus.Pi.ClearInterrupt(GcPi.IRQ_DSP);
    }

    void HandleDspMailbox()
    {
        _dspMboxFull = false;
        DspMailboxCount++;

        if (!_dspRunning)
            _dspRunning = true;

        if ((DspMboxH & 0x8000) != 0)
        {
            ushort cmdType = (ushort)(DspMboxH & 0x7FFF);
            switch (cmdType)
            {
                case 0x00F3:
                    SendMailToCpu(0xDCD1, 0x0000);
                    break;
                case 0x0000:
                    break;
                default:
                    SendMailToCpu(0xDCD1, 0x0002);
                    break;
            }
        }

        Csr |= 1 << 7;
        UpdateInterrupts();
    }

    public void TickAudioDma(int cpuCycles)
    {
        if (!_audioDmaActive) return;

        const int CyclesPerBlock = 121500; // 486MHz / 4KHz
        _audioDmaCycleAccum += cpuCycles;

        while (_audioDmaCycleAccum >= CyclesPerBlock)
        {
            _audioDmaCycleAccum -= CyclesPerBlock;

            if (_audioDmaBlocksRemaining > 0)
            {
                _audioDmaBlocksRemaining--;
                _audioDmaCurrentAddr += 32;
            }

            if (_audioDmaBlocksRemaining == 0)
            {
                _audioDmaCurrentAddr = ((uint)AiDmaAddrH << 16) | AiDmaAddrL;
                _audioDmaBlocksRemaining = AiDmaCtrl & 0x7FFF;
                if (_audioDmaBlocksRemaining == 0)
                {
                    _audioDmaActive = false;
                    break;
                }
                Csr |= 1 << 3;
                UpdateInterrupts();
            }
        }
    }

    void StartAramDma()
    {
        AramDmaCount++;
        uint mmAddr = (uint)(ArDmaMmH << 16) | ArDmaMmL;
        uint arAddr = (uint)(ArDmaArH << 16) | ArDmaArL;
        uint cnt = (uint)(ArDmaCntH << 16) | ArDmaCntL;
        bool toAram = (ArDmaCntH & 0x8000) != 0;
        uint length = cnt & 0x01FFFFFF;

        uint physMm = mmAddr & 0x01FFFFFF;

        if (toAram)
        {
            int copyLen = (int)Math.Min(length, Math.Min(
                _bus.Ram.Length - physMm,
                Aram.Length - arAddr));
            if (copyLen > 0)
                Array.Copy(_bus.Ram, (int)physMm, Aram, (int)arAddr, copyLen);
        }
        else
        {
            int copyLen = (int)Math.Min(length, Math.Min(
                Aram.Length - arAddr,
                _bus.Ram.Length - physMm));
            if (copyLen > 0)
                Array.Copy(Aram, (int)arAddr, _bus.Ram, (int)physMm, copyLen);
        }

        Csr |= 1 << 5;
        UpdateInterrupts();
    }

    public void Reset()
    {
        DspMboxH = DspMboxL = 0;
        CpuMboxH = 0;
        CpuMboxL = 0;
        _cpuMboxFull = false;
        _dspMboxFull = false;
        _dspRunning = false;
        Csr = 0;
        ArSize = 0;
        ArMode = 1;
        ArRefresh = 156;
        ArDmaMmH = ArDmaMmL = 0;
        ArDmaArH = ArDmaArL = 0;
        ArDmaCntH = ArDmaCntL = 0;
        AiDmaAddrH = AiDmaAddrL = 0;
        AiDmaCtrl = 0;
        _audioDmaCurrentAddr = 0;
        _audioDmaBlocksRemaining = 0;
        _audioDmaActive = false;
        _audioDmaCycleAccum = 0;
    }
}
