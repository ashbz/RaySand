namespace N64Emu;

sealed class N64Mi
{
    public const uint MI_INTR_SP = 0x01;
    public const uint MI_INTR_SI = 0x02;
    public const uint MI_INTR_AI = 0x04;
    public const uint MI_INTR_VI = 0x08;
    public const uint MI_INTR_PI = 0x10;
    public const uint MI_INTR_DP = 0x20;

    public uint MiMode;
    public uint MiVersion = 0x02020102;
    public uint MiIntr;
    public uint MiIntrMask;

    public N64Bus? Bus;
    public int MiIntrReads;
    public int MiIntrReadsWithVi, MiIntrReadsWithSp, MiIntrReadsWithDp;
    public int SpIntrSets, SiIntrSets, ViIntrSets, PiIntrSets, DpIntrSets;
    public int SpIntrClears, SiIntrClears, ViIntrClears, PiIntrClears, DpIntrClears;

    public bool InterruptPending => (MiIntr & MiIntrMask) != 0;

    public void Reset()
    {
        MiMode = 0;
        MiIntr = 0;
        MiIntrMask = 0;
        MiIntrReads = 0;
        MiIntrReadsWithVi = MiIntrReadsWithSp = MiIntrReadsWithDp = 0;
        SpIntrSets = SiIntrSets = ViIntrSets = PiIntrSets = DpIntrSets = 0;
        SpIntrClears = SiIntrClears = ViIntrClears = PiIntrClears = DpIntrClears = 0;
    }

    public uint Read(uint addr)
    {
        uint reg = addr & 0xF;
        if (reg == 0x8)
        {
            MiIntrReads++;
            if ((MiIntr & MI_INTR_VI) != 0) MiIntrReadsWithVi++;
            if ((MiIntr & MI_INTR_SP) != 0) MiIntrReadsWithSp++;
            if ((MiIntr & MI_INTR_DP) != 0) MiIntrReadsWithDp++;
        }
        return reg switch
        {
            0x0 => MiMode,
            0x4 => MiVersion,
            0x8 => MiIntr,
            0xC => MiIntrMask,
            _ => 0,
        };
    }

    public void Write(uint addr, uint val)
    {
        switch (addr & 0xF)
        {
            case 0x0: // MI_MODE
                MiMode &= ~0x7Fu;
                MiMode |= val & 0x7F;
                if ((val & 0x0080) != 0) MiMode &= ~(1u << 7);
                if ((val & 0x0100) != 0) MiMode |= (1u << 7);
                if ((val & 0x0200) != 0) MiMode &= ~(1u << 8);
                if ((val & 0x0400) != 0) MiMode |= (1u << 8);
                if ((val & 0x0800) != 0) ClearInterrupt(MI_INTR_DP);
                if ((val & 0x1000) != 0) MiMode &= ~(1u << 9);
                if ((val & 0x2000) != 0) MiMode |= (1u << 9);
                break;
            case 0xC: // MI_INTR_MASK
                if ((val & 0x001) != 0) MiIntrMask &= ~MI_INTR_SP;
                if ((val & 0x002) != 0) MiIntrMask |= MI_INTR_SP;
                if ((val & 0x004) != 0) MiIntrMask &= ~MI_INTR_SI;
                if ((val & 0x008) != 0) MiIntrMask |= MI_INTR_SI;
                if ((val & 0x010) != 0) MiIntrMask &= ~MI_INTR_AI;
                if ((val & 0x020) != 0) MiIntrMask |= MI_INTR_AI;
                if ((val & 0x040) != 0) MiIntrMask &= ~MI_INTR_VI;
                if ((val & 0x080) != 0) MiIntrMask |= MI_INTR_VI;
                if ((val & 0x100) != 0) MiIntrMask &= ~MI_INTR_PI;
                if ((val & 0x200) != 0) MiIntrMask |= MI_INTR_PI;
                if ((val & 0x400) != 0) MiIntrMask &= ~MI_INTR_DP;
                if ((val & 0x800) != 0) MiIntrMask |= MI_INTR_DP;
                Bus?.Cpu?.CheckInterrupts();
                break;
        }
    }

    public void SetInterrupt(uint flag)
    {
        MiIntr |= flag;
        if (flag == MI_INTR_SP) SpIntrSets++;
        else if (flag == MI_INTR_SI) SiIntrSets++;
        else if (flag == MI_INTR_VI) ViIntrSets++;
        else if (flag == MI_INTR_PI) PiIntrSets++;
        else if (flag == MI_INTR_DP) DpIntrSets++;
        Bus?.Cpu?.CheckInterrupts();
    }

    public void ClearInterrupt(uint flag)
    {
        MiIntr &= ~flag;
        if (flag == MI_INTR_SP) SpIntrClears++;
        else if (flag == MI_INTR_SI) SiIntrClears++;
        else if (flag == MI_INTR_VI) ViIntrClears++;
        else if (flag == MI_INTR_PI) PiIntrClears++;
        else if (flag == MI_INTR_DP) DpIntrClears++;
        Bus?.Cpu?.CheckInterrupts();
    }
}
