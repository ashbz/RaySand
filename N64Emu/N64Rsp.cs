using System.Buffers.Binary;

namespace N64Emu;

sealed class N64Rsp
{
    public readonly byte[] Dmem = new byte[0x1000]; // 4KB Data Memory
    public readonly byte[] Imem = new byte[0x1000]; // 4KB Instruction Memory

    public uint SpStatus = 0x0001; // halted
    public uint SpDmaFull;
    public uint SpDmaBusy;
    public uint SpSemaphore;
    public uint SpPc;

    public uint SpDramAddr;
    public uint SpMemAddr;
    public uint SpRdLen;
    public uint SpWrLen;

    public N64Bus? Bus;

    public void Reset()
    {
        Array.Clear(Dmem);
        Array.Clear(Imem);
        SpStatus = 0x0001;
        SpDmaFull = SpDmaBusy = SpSemaphore = SpPc = 0;
        SpDramAddr = SpMemAddr = SpRdLen = SpWrLen = 0;
        TaskCount = WriteStatusCount = WriteStatusClearHaltCount = 0;
    }

    public uint ReadReg(uint addr)
    {
        return (addr & 0x1F) switch
        {
            0x00 => SpMemAddr,
            0x04 => SpDramAddr,
            0x08 => SpRdLen,
            0x0C => SpWrLen,
            0x10 => SpStatus,
            0x14 => SpDmaFull,
            0x18 => SpDmaBusy,
            0x1C => ReadSemaphore(),
            _ => 0,
        };
    }

    uint ReadSemaphore()
    {
        uint val = SpSemaphore;
        SpSemaphore = 1;
        return val;
    }

    public void WriteReg(uint addr, uint val)
    {
        switch (addr & 0x1F)
        {
            case 0x00: SpMemAddr = val & 0x1FFF; break;
            case 0x04: SpDramAddr = val & 0x00FF_FFFF; break;
            case 0x08:
                SpRdLen = val;
                DoDmaRead();
                break;
            case 0x0C:
                SpWrLen = val;
                DoDmaWrite();
                break;
            case 0x10: WriteStatus(val); break;
            case 0x1C: SpSemaphore = 0; break;
        }
    }

    public uint ReadPcReg(uint addr)
    {
        return SpPc & 0xFFF;
    }

    public void WritePcReg(uint addr, uint val)
    {
        SpPc = val & 0xFFC;
    }

    static void ClearSet(ref uint reg, uint clearBit, uint setBit, bool doClear, bool doSet)
    {
        if (doClear && !doSet) reg &= ~clearBit;
        if (doSet && !doClear) reg |= setBit;
    }

    void WriteStatus(uint val)
    {
        WriteStatusCount++;
        uint oldStatus = SpStatus;

        ClearSet(ref SpStatus, 0x0001u, 0x0001u, (val & 0x000001) != 0, (val & 0x000002) != 0); // halt
        if ((val & 0x000004) != 0) SpStatus &= ~0x0002u; // clear broke (no set pair)
        if ((val & 0x000008) != 0 && (val & 0x000010) == 0) Bus?.Mi.ClearInterrupt(N64Mi.MI_INTR_SP);
        if ((val & 0x000010) != 0 && (val & 0x000008) == 0) Bus?.Mi.SetInterrupt(N64Mi.MI_INTR_SP);
        ClearSet(ref SpStatus, 0x0020u, 0x0020u, (val & 0x000020) != 0, (val & 0x000040) != 0); // sstep
        ClearSet(ref SpStatus, 0x0040u, 0x0040u, (val & 0x000080) != 0, (val & 0x000100) != 0); // intr on break
        ClearSet(ref SpStatus, 0x0080u, 0x0080u, (val & 0x000200) != 0, (val & 0x000400) != 0); // signal 0
        ClearSet(ref SpStatus, 0x0100u, 0x0100u, (val & 0x000800) != 0, (val & 0x001000) != 0); // signal 1
        ClearSet(ref SpStatus, 0x0200u, 0x0200u, (val & 0x002000) != 0, (val & 0x004000) != 0); // signal 2
        ClearSet(ref SpStatus, 0x0400u, 0x0400u, (val & 0x008000) != 0, (val & 0x010000) != 0); // signal 3
        ClearSet(ref SpStatus, 0x0800u, 0x0800u, (val & 0x020000) != 0, (val & 0x040000) != 0); // signal 4
        ClearSet(ref SpStatus, 0x1000u, 0x1000u, (val & 0x080000) != 0, (val & 0x100000) != 0); // signal 5

        bool clearHalt = (val & 0x000001) != 0 && (SpStatus & 0x0001) == 0;
        if (clearHalt) WriteStatusClearHaltCount++;

        if (WriteStatusCount <= 20)
            N64Machine.DiagWrite($"[RSP] WriteStatus #{WriteStatusCount}: val=0x{val:X6} old=0x{oldStatus:X4} new=0x{SpStatus:X4} clearHalt={clearHalt}");

        if (clearHalt)
            RunTask();
    }

    public int TaskCount;
    public int WriteStatusCount;
    public int WriteStatusClearHaltCount;

    void RunTask()
    {
        if (Bus == null) return;
        TaskCount++;
        uint taskType = ReadDmem32(0xFC0);
        if (TaskCount <= 10)
            N64Machine.DiagWrite($"[RSP] Task #{TaskCount}: type={taskType} SP_STATUS=0x{SpStatus:X4}");
        Bus.RspHle.ProcessTask();
        SpStatus |= 0x0003; // set halt + broke
        if ((SpStatus & 0x0040) != 0) // intr on break
            Bus.Mi.SetInterrupt(N64Mi.MI_INTR_SP);
    }

    void DoDmaRead() // RDRAM -> SP (SP reads from RDRAM)
    {
        if (Bus == null) return;
        uint memAddr = SpMemAddr & 0x1FFF;
        uint dramAddr = SpDramAddr & 0x00FF_FFFF;
        uint len = ((SpRdLen & 0xFFF) | 7) + 1;
        int count = (int)((SpRdLen >> 12) & 0xFF) + 1;
        int skip = (int)((SpRdLen >> 20) & 0xFFF);

        bool isImem = (memAddr & 0x1000) != 0;
        byte[] spMem = isImem ? Imem : Dmem;
        uint spOff = memAddr & 0xFFF;

        for (int c = 0; c < count; c++)
        {
            for (uint i = 0; i < len; i++)
            {
                uint s = (spOff + i) & 0xFFF;
                uint d = dramAddr + i;
                spMem[s] = d < (uint)Bus.Rdram.Length ? Bus.Rdram[d] : (byte)0;
            }
            dramAddr += len + (uint)skip;
            spOff = (spOff + len) & 0xFFF;
        }
    }

    void DoDmaWrite() // SP -> RDRAM (SP writes to RDRAM)
    {
        if (Bus == null) return;
        uint memAddr = SpMemAddr & 0x1FFF;
        uint dramAddr = SpDramAddr & 0x00FF_FFFF;
        uint len = ((SpWrLen & 0xFFF) | 7) + 1;
        int count = (int)((SpWrLen >> 12) & 0xFF) + 1;
        int skip = (int)((SpWrLen >> 20) & 0xFFF);

        bool isImem = (memAddr & 0x1000) != 0;
        byte[] spMem = isImem ? Imem : Dmem;
        uint spOff = memAddr & 0xFFF;

        for (int c = 0; c < count; c++)
        {
            for (uint i = 0; i < len; i++)
            {
                uint s = (spOff + i) & 0xFFF;
                uint d = dramAddr + i;
                if (d < (uint)Bus.Rdram.Length)
                    Bus.Rdram[d] = spMem[s];
            }
            dramAddr += len + (uint)skip;
            spOff = (spOff + len) & 0xFFF;
        }
    }

    public uint ReadDmem32(uint offset)
    {
        offset &= 0xFFF;
        return BinaryPrimitives.ReadUInt32BigEndian(Dmem.AsSpan((int)offset));
    }

    public void WriteDmem32(uint offset, uint val)
    {
        offset &= 0xFFF;
        BinaryPrimitives.WriteUInt32BigEndian(Dmem.AsSpan((int)offset), val);
    }
}
