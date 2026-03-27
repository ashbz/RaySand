namespace N64Emu;

sealed class N64Vi
{
    public uint Status;
    public uint Origin;
    public uint Width;
    public uint VIntr;
    public uint VCurrent;
    public uint Burst;
    public uint VSync;
    public uint HSync;
    public uint HSyncLeap;
    public uint HVideo;
    public uint VVideo;
    public uint VBurst;
    public uint XScale;
    public uint YScale;

    public N64Bus? Bus;
    public int ViClearCount;

    public int FrameWidth => (int)(Width > 0 ? Width : 320);
    public int FrameHeight
    {
        get
        {
            uint vStart = (VVideo >> 16) & 0x3FF;
            uint vEnd = VVideo & 0x3FF;
            if (vEnd <= vStart) return 240;
            int lines = (int)((vEnd - vStart) / 2);
            if (lines <= 0) return 240;
            uint ys = YScale & 0xFFF;
            return ys > 0 ? Math.Max(1, (int)((uint)lines * ys / 1024)) : lines;
        }
    }

    public int ColorDepth => (Status & 3) switch
    {
        2 => 16,
        3 => 32,
        _ => 0,
    };

    public uint Read(uint addr)
    {
        return (addr & 0x3F) switch
        {
            0x00 => Status,
            0x04 => Origin,
            0x08 => Width,
            0x0C => VIntr,
            0x10 => VCurrent,
            0x14 => Burst,
            0x18 => VSync,
            0x1C => HSync,
            0x20 => HSyncLeap,
            0x24 => HVideo,
            0x28 => VVideo,
            0x2C => VBurst,
            0x30 => XScale,
            0x34 => YScale,
            _ => 0,
        };
    }

    public int Swaps;

    public void Write(uint addr, uint val)
    {
        switch (addr & 0x3F)
        {
            case 0x00: Status = val; break;
            case 0x04:
                uint masked = val & 0x00FF_FFFF;
                if (masked != Origin) Swaps++;
                Origin = masked;
                break;
            case 0x08: Width = val & 0x7FF; break;
            case 0x0C: VIntr = val & 0x3FF; break;
            case 0x10:
                ViClearCount++;
                Bus?.Mi.ClearInterrupt(N64Mi.MI_INTR_VI);
                break;
            case 0x14: Burst = val; break;
            case 0x18: VSync = val & 0x3FF; break;
            case 0x1C: HSync = val; break;
            case 0x20: HSyncLeap = val; break;
            case 0x24: HVideo = val; break;
            case 0x28: VVideo = val; break;
            case 0x2C: VBurst = val; break;
            case 0x30: XScale = val; break;
            case 0x34: YScale = val; break;
        }
    }

    public void Reset()
    {
        Status = Origin = Width = VIntr = VCurrent = 0;
        Burst = VSync = HSync = HSyncLeap = HVideo = VVideo = VBurst = XScale = YScale = 0;
        ViClearCount = 0;
        Swaps = 0;
    }

    public void AdvanceLine()
    {
        uint numHalflines = (VSync & 0x3FF);
        if (numHalflines == 0) numHalflines = 525;
        VCurrent++;
        if (VCurrent >= numHalflines)
            VCurrent = 0;

        if ((VCurrent & 0x3FE) == (VIntr & 0x3FF))
            Bus?.Mi.SetInterrupt(N64Mi.MI_INTR_VI);
    }
}
