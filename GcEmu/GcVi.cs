using System.Runtime.CompilerServices;

namespace GcEmu;

class GcVi
{
    GcBus _bus = null!;

    public ushort Vtr;
    public ushort Dcr;
    public uint Htr0, Htr1;
    public uint Vto, Vte;
    public uint BbEi, BbOi;
    public uint Tfbl, Tfbr;
    public uint Bfbl, Bfbr;

    public ushort Dpv;
    public ushort Dph;

    public uint[] Di = new uint[4];

    public ushort Hsw;
    public ushort Hsr;
    public ushort ViClk;
    public ushort ViSel;
    public ushort FbWidth;

    public uint XfbAddr => (Tfbl & 0x00FFFFFF) << 5;
    public int XfbWidth => Math.Max(1, (Hsw >> 0) & 0x3FF) * 16;
    public int DispWidth => 640;
    public int DispHeight => IsNtsc ? 480 : 574;
    public bool IsNtsc => (Dcr & 0x300) == 0;
    public bool IsProgressive => (Dcr & 4) != 0;
    public bool Enabled => (Dcr & 1) != 0;

    int _lineCounter;
    int _fieldIdx;
    public int LinesPerField => IsNtsc ? 263 : 313;
    public int TotalFields { get; private set; }
    public bool FrameReady { get; set; }

    public void Init(GcBus bus) => _bus = bus;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TickLine()
    {
        _lineCounter++;
        Dpv = (ushort)_lineCounter;

        bool fired = false;
        for (int i = 0; i < 4; i++)
        {
            if ((Di[i] & (1u << 28)) == 0) continue;
            int vct = (int)((Di[i] >> 16) & 0x7FF);
            if (vct == _lineCounter)
            {
                Di[i] |= 1u << 31;
                fired = true;
            }
        }
        if (fired) UpdateInterrupt();

        if (_lineCounter >= LinesPerField)
        {
            _lineCounter = 0;
            _fieldIdx ^= 1;
            TotalFields++;
            FrameReady = true;
        }
    }

    void UpdateInterrupt()
    {
        bool pending = false;
        for (int i = 0; i < 4; i++)
        {
            if ((Di[i] & ((1u << 28) | (1u << 31))) == ((1u << 28) | (1u << 31)))
            {
                pending = true;
                break;
            }
        }
        if (pending)
            _bus.Pi.SetInterrupt(GcPi.IRQ_VI);
        else
            _bus.Pi.ClearInterrupt(GcPi.IRQ_VI);
    }

    public ushort Read16(uint addr)
    {
        int off = (int)(addr & 0x7E);
        if (off >= 0x30 && off <= 0x3E)
        {
            int idx = (off - 0x30) >> 2;
            return (off & 2) == 0
                ? (ushort)(Di[idx] >> 16)
                : (ushort)(Di[idx] & 0xFFFF);
        }

        return off switch
        {
            0x00 => Vtr,
            0x02 => Dcr,
            0x04 => (ushort)(Htr0 >> 16),
            0x06 => (ushort)(Htr0 & 0xFFFF),
            0x08 => (ushort)(Htr1 >> 16),
            0x0A => (ushort)(Htr1 & 0xFFFF),
            0x0C => (ushort)(Vto >> 16),
            0x0E => (ushort)(Vto & 0xFFFF),
            0x10 => (ushort)(Vte >> 16),
            0x12 => (ushort)(Vte & 0xFFFF),
            0x14 => (ushort)(BbEi >> 16),
            0x16 => (ushort)(BbEi & 0xFFFF),
            0x18 => (ushort)(BbOi >> 16),
            0x1A => (ushort)(BbOi & 0xFFFF),
            0x1C => (ushort)(Tfbl >> 16),
            0x1E => (ushort)(Tfbl & 0xFFFF),
            0x20 => (ushort)(Tfbr >> 16),
            0x22 => (ushort)(Tfbr & 0xFFFF),
            0x24 => (ushort)(Bfbl >> 16),
            0x26 => (ushort)(Bfbl & 0xFFFF),
            0x28 => (ushort)(Bfbr >> 16),
            0x2A => (ushort)(Bfbr & 0xFFFF),
            0x2C => Dpv,
            0x2E => Dph,
            0x48 => Hsw,
            0x4A => Hsr,
            0x6C => ViClk,
            0x6E => ViSel,
            0x70 => FbWidth,
            _ => 0
        };
    }

    public uint Read32(uint addr)
    {
        ushort hi = Read16(addr);
        ushort lo = Read16(addr + 2);
        return ((uint)hi << 16) | lo;
    }

    public void Write16(uint addr, ushort val)
    {
        int off = (int)(addr & 0x7E);

        if (off >= 0x30 && off <= 0x3E)
        {
            int idx = (off - 0x30) >> 2;
            if ((off & 2) == 0)
            {
                Di[idx] = (Di[idx] & 0x0000FFFF) | ((uint)val << 16);
                UpdateInterrupt();
            }
            else
            {
                Di[idx] = (Di[idx] & 0xFFFF0000) | val;
            }
            return;
        }

        switch (off)
        {
            case 0x00: Vtr = val; break;
            case 0x02:
                if ((val & 2) != 0)
                {
                    for (int i = 0; i < 4; i++) Di[i] = 0;
                    UpdateInterrupt();
                    val = (ushort)(val & ~2);
                }
                Dcr = val;
                break;
            case 0x04: Htr0 = (Htr0 & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x06: Htr0 = (Htr0 & 0xFFFF0000) | val; break;
            case 0x08: Htr1 = (Htr1 & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x0A: Htr1 = (Htr1 & 0xFFFF0000) | val; break;
            case 0x0C: Vto = (Vto & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x0E: Vto = (Vto & 0xFFFF0000) | val; break;
            case 0x10: Vte = (Vte & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x12: Vte = (Vte & 0xFFFF0000) | val; break;
            case 0x14: BbEi = (BbEi & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x16: BbEi = (BbEi & 0xFFFF0000) | val; break;
            case 0x18: BbOi = (BbOi & 0x0000FFFF) | ((uint)val << 16); break;
            case 0x1A: BbOi = (BbOi & 0xFFFF0000) | val; break;
            case 0x1C:
                Tfbl = (Tfbl & 0x0000FFFF) | ((uint)val << 16);
                if ((val & 0xE000) != 0) Tfbl &= ~(1u << 28);
                break;
            case 0x1E: Tfbl = (Tfbl & 0xFFFF0000) | val; break;
            case 0x20:
                Tfbr = (Tfbr & 0x0000FFFF) | ((uint)val << 16);
                if ((val & 0xE000) != 0) Tfbr &= ~(1u << 28);
                break;
            case 0x22: Tfbr = (Tfbr & 0xFFFF0000) | val; break;
            case 0x24:
                Bfbl = (Bfbl & 0x0000FFFF) | ((uint)val << 16);
                if ((val & 0xE000) != 0) Bfbl &= ~(1u << 28);
                break;
            case 0x26: Bfbl = (Bfbl & 0xFFFF0000) | val; break;
            case 0x28:
                Bfbr = (Bfbr & 0x0000FFFF) | ((uint)val << 16);
                if ((val & 0xE000) != 0) Bfbr &= ~(1u << 28);
                break;
            case 0x2A: Bfbr = (Bfbr & 0xFFFF0000) | val; break;
            case 0x48: Hsw = val; break;
            case 0x4A: Hsr = val; break;
            case 0x6C: ViClk = val; break;
            case 0x6E: ViSel = val; break;
            case 0x70: FbWidth = val; break;
        }
    }

    public int WriteCount;

    public void Write32(uint addr, uint val)
    {
        WriteCount++;
        Write16(addr, (ushort)(val >> 16));
        Write16(addr + 2, (ushort)(val & 0xFFFF));
    }

    public void Reset()
    {
        Vtr = 0; Dcr = 0;
        Htr0 = 0; Htr1 = 0;
        Vto = 0; Vte = 0;
        BbEi = 0; BbOi = 0;
        Tfbl = 0; Tfbr = 0;
        Bfbl = 0; Bfbr = 0;
        Array.Clear(Di);
        Dpv = 0; Dph = 0;
        Hsw = 0; Hsr = 0;
        ViClk = 0; ViSel = 0;
        FbWidth = 0;
        _lineCounter = 0;
        _fieldIdx = 0;
        TotalFields = 0;
        FrameReady = false;
    }
}
