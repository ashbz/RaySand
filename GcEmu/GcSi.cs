using System.Runtime.CompilerServices;

namespace GcEmu;

class GcSi
{
    GcBus _bus = null!;

    public struct ChannelState
    {
        public uint OutBuf;
        public uint InBufH;
        public uint InBufL;
    }

    public ChannelState[] Chan = new ChannelState[4];
    public uint Poll;
    public uint ComCsr;
    public uint Status;
    public uint ExiLock;
    public byte[] IoBuf = new byte[128];

    public ushort Buttons;
    public byte StickX = 128, StickY = 128;
    public byte CStickX = 128, CStickY = 128;
    public byte TriggerL, TriggerR;

    public void Init(GcBus bus) => _bus = bus;

    void BuildControllerResponse(int ch)
    {
        if (ch != 0)
        {
            Chan[ch].InBufH = 0x80000000;
            Chan[ch].InBufL = 0;
            return;
        }

        uint h = 0u;
        h |= (uint)((Buttons >> 8) & 0xFF) << 16;
        h |= (uint)(Buttons & 0xFF) << 8;
        h |= StickX;

        uint l = 0u;
        l |= (uint)StickY << 24;
        l |= (uint)CStickX << 16;
        l |= (uint)CStickY << 8;
        l |= (uint)((TriggerL > TriggerR) ? TriggerL : TriggerR);

        Chan[ch].InBufH = h;
        Chan[ch].InBufL = l;
    }

    public uint Read32(uint addr)
    {
        int off = (int)(addr & 0xFF);

        if (off >= 0x80 && off < 0x100)
            return ReadIoBuf32(off - 0x80);

        if (off < 0x30)
        {
            int ch = off / 12;
            int reg = off % 12;
            return reg switch
            {
                0 => Chan[ch].OutBuf,
                4 => Chan[ch].InBufH,
                8 => Chan[ch].InBufL,
                _ => 0
            };
        }

        return off switch
        {
            0x30 => Poll,
            0x34 => ComCsr,
            0x38 => Status,
            0x3C => ExiLock,
            _ => 0
        };
    }

    public void Write32(uint addr, uint val)
    {
        int off = (int)(addr & 0xFF);

        if (off >= 0x80 && off < 0x100)
        {
            WriteIoBuf32(off - 0x80, val);
            return;
        }

        if (off < 0x30)
        {
            int ch = off / 12;
            int reg = off % 12;
            switch (reg)
            {
                case 0: Chan[ch].OutBuf = val; break;
                case 4: Chan[ch].InBufH = val; break;
                case 8: Chan[ch].InBufL = val; break;
            }
            return;
        }

        switch (off)
        {
            case 0x30:
                Poll = val;
                break;
            case 0x34:
                HandleComCsr(val);
                break;
            case 0x38:
                Status = val;
                break;
            case 0x3C:
                ExiLock = val;
                break;
        }
    }

    int _deferredIntCountdown;

    void HandleComCsr(uint val)
    {
        if ((val & (1u << 31)) != 0)
        {
            ComCsr &= ~(1u << 31);
            _bus.Pi.ClearInterrupt(GcPi.IRQ_SI);
        }

        ComCsr = (ComCsr & (1u << 31)) | (val & ~(1u << 31));

        if ((val & 1) != 0)
        {
            int ch = (int)((val >> 1) & 3);
            int outLen = (int)((val >> 16) & 0x7F);
            int inLen = (int)((val >> 8) & 0x7F);

            bool ok = ExecuteTransfer(ch, outLen, inLen);

            ComCsr &= ~1u;
            if (ok)
            {
                ComCsr |= 1u << 31;
                if ((ComCsr & (1u << 30)) != 0)
                    _deferredIntCountdown = 64;
            }
            else
            {
                ComCsr |= (1u << 31) | (1u << 29);
            }
        }
    }

    public void Tick()
    {
        if (_deferredIntCountdown > 0)
        {
            _deferredIntCountdown--;
            if (_deferredIntCountdown == 0)
                _bus.Pi.SetInterrupt(GcPi.IRQ_SI);
        }
    }

    bool ExecuteTransfer(int ch, int outLen, int inLen)
    {
        if (outLen == 0) return false;
        if (ch != 0) return false;

        byte cmd = IoBuf[0];
        switch (cmd)
        {
            case 0x00:
                uint id = 0x09000000;
                IoBuf[0] = (byte)(id >> 24);
                IoBuf[1] = (byte)(id >> 16);
                IoBuf[2] = (byte)(id >> 8);
                IoBuf[3] = 0;
                break;

            case 0x40:
            case 0x41:
                for (int i = 0; i < Math.Min(inLen, 10); i++)
                    IoBuf[i] = 0;
                if (inLen >= 2) { IoBuf[0] = 128; IoBuf[1] = 128; }
                break;

            case 0x42:
                IoBuf[0] = (byte)((Buttons >> 8) & 0xFF);
                IoBuf[1] = (byte)(Buttons & 0xFF);
                IoBuf[2] = StickX;
                IoBuf[3] = StickY;
                IoBuf[4] = CStickX;
                IoBuf[5] = CStickY;
                IoBuf[6] = TriggerL;
                IoBuf[7] = TriggerR;
                break;
        }
        return true;
    }

    uint ReadIoBuf32(int off)
    {
        if (off + 3 >= IoBuf.Length) return 0;
        return (uint)(IoBuf[off] << 24 | IoBuf[off + 1] << 16 | IoBuf[off + 2] << 8 | IoBuf[off + 3]);
    }

    void WriteIoBuf32(int off, uint val)
    {
        if (off + 3 >= IoBuf.Length) return;
        IoBuf[off]     = (byte)(val >> 24);
        IoBuf[off + 1] = (byte)(val >> 16);
        IoBuf[off + 2] = (byte)(val >> 8);
        IoBuf[off + 3] = (byte)val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PollControllers()
    {
        if ((Poll & 0x80000000) != 0)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                if (((Poll >> (24 + ch)) & 1) != 0)
                    BuildControllerResponse(ch);
            }
        }
    }

    public void Reset()
    {
        Array.Clear(IoBuf);
        for (int i = 0; i < 4; i++)
            Chan[i] = default;
        Poll = 0;
        ComCsr = 0;
        Status = 0;
        ExiLock = 0;
        Buttons = 0;
        StickX = StickY = CStickX = CStickY = 128;
        TriggerL = TriggerR = 0;
        _deferredIntCountdown = 0;
    }
}
