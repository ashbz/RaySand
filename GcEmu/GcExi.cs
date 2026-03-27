namespace GcEmu;

class GcExi
{
    GcBus _bus = null!;

    public struct Channel
    {
        public uint Csr;
        public uint Mar;
        public uint Length;
        public uint Cr;
        public uint Data;
    }

    public Channel[] Chan = new Channel[3];

    byte[]? _iplRom;
    uint _iplPos;
    bool _romDisabled;

    byte[]? _memCardA;
    uint _memCardPos;
    int _memCardSize = 512 * 1024;

    uint _adData;

    byte[] _sram = new byte[0x44];
    uint _sramAddr;
    uint _sramCmdPhase;
    uint _sramCmd;

    uint _rtcCounter;

    public void Init(GcBus bus)
    {
        _bus = bus;
        InitSram();
    }

    void InitSram()
    {
        Array.Clear(_sram);
        _sram[0x12] = 0x00; // language = English
        _sram[0x13] = 0x2C; // flags: OOBE done + stereo + 0x20
        FixSramChecksums();
    }

    void FixSramChecksums()
    {
        ushort checksum = 0, checksumInv = 0;
        for (int i = 0x0C; i < 0x14; i += 2)
        {
            ushort val = (ushort)(_sram[i] << 8 | _sram[i + 1]);
            checksum += val;
            checksumInv += (ushort)~val;
        }
        _sram[0x04] = (byte)(checksum >> 8);
        _sram[0x05] = (byte)checksum;
        _sram[0x06] = (byte)(checksumInv >> 8);
        _sram[0x07] = (byte)checksumInv;
    }

    public void LoadIpl(byte[] data)
    {
        _iplRom = data;
        Log.Info($"IPL ROM loaded: {data.Length / 1024} KB");
    }

    public void SetMemoryCard(byte[] data)
    {
        _memCardA = data;
        _memCardSize = data.Length;
    }

    public uint Read32(uint addr)
    {
        int off = (int)(addr & 0x3F);
        int ch = off / 20;
        if (ch >= 3) return 0;
        int reg = off % 20;

        return reg switch
        {
            0  => Chan[ch].Csr | (ch == 0 && !_romDisabled ? (1u << 12) : 0),
            4  => Chan[ch].Mar,
            8  => Chan[ch].Length,
            12 => Chan[ch].Cr,
            16 => Chan[ch].Data,
            _  => 0
        };
    }

    public void Write32(uint addr, uint val)
    {
        int off = (int)(addr & 0x3F);
        int ch = off / 20;
        if (ch >= 3) return;
        int reg = off % 20;

        switch (reg)
        {
            case 0:
                if ((val & 2) != 0) Chan[ch].Csr &= ~2u;
                if ((val & 8) != 0) Chan[ch].Csr &= ~8u;
                if ((val & (1u << 11)) != 0) Chan[ch].Csr &= ~(1u << 11);
                Chan[ch].Csr = (Chan[ch].Csr & 0x00000E0A) | (val & ~0x00000E0Au);
                if (ch == 0 && (val & (1u << 13)) != 0)
                    _romDisabled = true;
                break;
            case 4:
                Chan[ch].Mar = val;
                break;
            case 8:
                Chan[ch].Length = val;
                break;
            case 12:
                Chan[ch].Cr = val;
                if ((val & 1) != 0)
                    ExecuteTransfer(ch);
                break;
            case 16:
                Chan[ch].Data = val;
                if ((Chan[ch].Cr & 1) != 0)
                    ExecuteImmediate(ch);
                break;
        }
    }

    void ExecuteTransfer(int ch)
    {
        bool dma = (Chan[ch].Cr & 2) != 0;
        int rw = (int)((Chan[ch].Cr >> 2) & 3);
        int tlen = (int)((Chan[ch].Cr >> 4) & 3) + 1;

        if (dma)
            ExecuteDma(ch, rw);
        else
            ExecuteImmediate(ch);

        Chan[ch].Cr &= ~1u;

        Chan[ch].Csr |= 8;
        if ((Chan[ch].Csr & 4) != 0)
            _bus.Pi.SetInterrupt(GcPi.IRQ_EXI);
    }

    void ExecuteImmediate(int ch)
    {
        int rw = (int)((Chan[ch].Cr >> 2) & 3);
        int tlen = (int)((Chan[ch].Cr >> 4) & 3) + 1;
        int devSel = (int)((Chan[ch].Csr >> 7) & 7);

        if (ch == 0 && devSel == 1)
        {
            if (!_romDisabled && _iplRom != null)
            {
                if (rw == 1)
                    _iplPos = Chan[ch].Data >> 6;
                else if (rw == 0)
                {
                    uint result = 0;
                    for (int i = 0; i < tlen && _iplPos + i < _iplRom.Length; i++)
                        result |= (uint)_iplRom[_iplPos + i] << (24 - i * 8);
                    Chan[ch].Data = result;
                    _iplPos += (uint)tlen;
                }
            }
            else
            {
                if (rw == 1)
                {
                    _sramCmd = Chan[ch].Data;
                    _sramAddr = (_sramCmd >> 6) & 0x3FFFFF;
                    _sramCmdPhase = 1;
                }
                else if (rw == 0 && _sramCmdPhase > 0)
                {
                    uint result = 0;
                    if (_sramAddr >= 0x200000 && _sramAddr < 0x200000 + 0x44)
                    {
                        int off = (int)(_sramAddr - 0x200000);
                        for (int i = 0; i < tlen && off + i < _sram.Length; i++)
                            result |= (uint)_sram[off + i] << (24 - i * 8);
                        _sramAddr += (uint)tlen;
                    }
                    else if (_sramAddr < 4)
                    {
                        uint rtc = _rtcCounter++;
                        result = rtc;
                    }
                    Chan[ch].Data = result;
                }
            }
        }
        else if (ch == 0 && devSel == 0)
        {
            if (rw == 0)
            {
                Chan[ch].Data = (uint)(_memCardSize switch
                {
                    512 * 1024 => 0x00000004,
                    1024 * 1024 => 0x00000008,
                    2 * 1024 * 1024 => 0x00000010,
                    _ => 0x00000004
                });
            }
        }
        else if (ch == 2)
        {
            if (rw == 0)
                Chan[ch].Data = _adData;
        }

        Chan[ch].Cr &= ~1u;
    }

    void ExecuteDma(int ch, int rw)
    {
        uint mar = Chan[ch].Mar;
        uint len = Chan[ch].Length;
        int devSel = (int)((Chan[ch].Csr >> 7) & 7);
        uint physMar = mar & 0x01FFFFFF;

        if (ch == 0 && devSel == 1)
        {
            if (!_romDisabled && _iplRom != null && rw == 0)
            {
                int copyLen = (int)Math.Min(len, (uint)(_iplRom.Length - (int)_iplPos));
                copyLen = (int)Math.Min(copyLen, _bus.Ram.Length - (int)physMar);
                if (copyLen > 0)
                {
                    Array.Copy(_iplRom, (int)_iplPos, _bus.Ram, (int)physMar, copyLen);
                    _iplPos += (uint)copyLen;
                }
            }
            else if (_sramAddr >= 0x200000 && _sramAddr < 0x200000 + 0x44)
            {
                int off = (int)(_sramAddr - 0x200000);
                int copyLen = (int)Math.Min(len, (uint)(_sram.Length - off));
                copyLen = Math.Min(copyLen, _bus.Ram.Length - (int)physMar);
                if (copyLen > 0)
                {
                    if (rw == 0)
                        Array.Copy(_sram, off, _bus.Ram, (int)physMar, copyLen);
                    else if (rw == 1)
                    {
                        Array.Copy(_bus.Ram, (int)physMar, _sram, off, copyLen);
                        FixSramChecksums();
                    }
                }
            }
        }
    }

    public void Reset()
    {
        for (int i = 0; i < 3; i++)
            Chan[i] = default;
        _iplPos = 0;
        _romDisabled = false;
        _memCardPos = 0;
        _adData = 0;
        _sramCmdPhase = 0;
        _sramAddr = 0;
        _sramCmd = 0;
        _rtcCounter = 0;
        InitSram();
    }
}
