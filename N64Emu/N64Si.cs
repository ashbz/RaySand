namespace N64Emu;

sealed class N64Si
{
    public uint DramAddr;
    public uint PifAddrRd;
    public uint PifAddrWr;
    public uint Status;

    public N64Bus? Bus;

    public void Reset()
    {
        DramAddr = PifAddrRd = PifAddrWr = Status = 0;
        DmaBusy = false;
    }

    public bool DmaBusy;

    public uint Read(uint addr)
    {
        return (addr & 0x1F) switch
        {
            0x00 => DramAddr,
            0x18 => GetStatus(),
            _ => 0,
        };
    }

    uint GetStatus()
    {
        uint s = 0;
        if (DmaBusy) s |= 1;
        if ((Bus?.Mi.MiIntr & N64Mi.MI_INTR_SI) != 0) s |= (1u << 12);
        return s;
    }

    public void Write(uint addr, uint val)
    {
        switch (addr & 0x1F)
        {
            case 0x00:
                DramAddr = val & 0x00FF_FFF8;
                break;
            case 0x04:
                DoPifRead(val);
                break;
            case 0x10:
                DoPifWrite(val);
                break;
            case 0x18:
                Bus?.Mi.ClearInterrupt(N64Mi.MI_INTR_SI);
                Status = 0;
                break;
        }
    }

    void DoPifRead(uint pifAddr)
    {
        if (Bus == null) return;
        uint dram = DramAddr & 0x00FF_FFF8;

        // Process PIF commands, then copy PIF RAM -> RDRAM
        Bus.Pif.ProcessCommands();

        for (int i = 0; i < 64; i++)
        {
            if (dram + (uint)i < (uint)Bus.Rdram.Length)
                Bus.Rdram[dram + (uint)i] = Bus.Pif.Ram[i];
        }

        Bus.Mi.SetInterrupt(N64Mi.MI_INTR_SI);
    }

    void DoPifWrite(uint pifAddr)
    {
        if (Bus == null) return;
        uint dram = DramAddr & 0x00FF_FFF8;

        // Copy RDRAM -> PIF RAM, then process commands
        for (int i = 0; i < 64; i++)
        {
            Bus.Pif.Ram[i] = (dram + (uint)i < (uint)Bus.Rdram.Length)
                ? Bus.Rdram[dram + (uint)i] : (byte)0;
        }

        Bus.Pif.ProcessCommands();
        Bus.Mi.SetInterrupt(N64Mi.MI_INTR_SI);
    }
}
