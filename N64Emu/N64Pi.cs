using System.Buffers.Binary;

namespace N64Emu;

sealed class N64Pi
{
    public uint DramAddr;
    public uint CartAddr;
    public uint RdLen;
    public uint WrLen;
    public uint Status;
    public uint Dom1Lat;
    public uint Dom1Pwd;
    public uint Dom1Pgs;
    public uint Dom1Rls;
    public uint Dom2Lat;
    public uint Dom2Pwd;
    public uint Dom2Pgs;
    public uint Dom2Rls;

    public N64Bus? Bus;

    public void Reset()
    {
        DramAddr = CartAddr = RdLen = WrLen = Status = 0;
        Dom1Lat = Dom1Pwd = Dom1Pgs = Dom1Rls = 0;
        Dom2Lat = Dom2Pwd = Dom2Pgs = Dom2Rls = 0;
    }

    public uint Read(uint addr)
    {
        if ((addr & 0x3F) == 0x10) // PI_STATUS
        {
            uint s = 0;
            if ((Bus?.Mi.MiIntr & N64Mi.MI_INTR_PI) != 0) s |= (1u << 3);
            return s;
        }
        return (addr & 0x3F) switch
        {
            0x00 => DramAddr,
            0x04 => CartAddr,
            0x08 => RdLen,
            0x0C => WrLen,
            0x14 => Dom1Lat,
            0x18 => Dom1Pwd,
            0x1C => Dom1Pgs,
            0x20 => Dom1Rls,
            0x24 => Dom2Lat,
            0x28 => Dom2Pwd,
            0x2C => Dom2Pgs,
            0x30 => Dom2Rls,
            _ => 0,
        };
    }

    public void Write(uint addr, uint val)
    {
        switch (addr & 0x3F)
        {
            case 0x00: DramAddr = val & 0x00FF_FFFE; break;
            case 0x04: CartAddr = val; break;
            case 0x08:
                RdLen = val;
                DoDmaRead();
                break;
            case 0x0C:
                WrLen = val;
                DoDmaWrite();
                break;
            case 0x10:
                if ((val & 2) != 0) Bus?.Mi.ClearInterrupt(N64Mi.MI_INTR_PI);
                Status = 0;
                break;
            case 0x14: Dom1Lat = val & 0xFF; break;
            case 0x18: Dom1Pwd = val & 0xFF; break;
            case 0x1C: Dom1Pgs = val & 0xF; break;
            case 0x20: Dom1Rls = val & 3; break;
            case 0x24: Dom2Lat = val & 0xFF; break;
            case 0x28: Dom2Pwd = val & 0xFF; break;
            case 0x2C: Dom2Pgs = val & 0xF; break;
            case 0x30: Dom2Rls = val & 3; break;
        }
    }

    void DoDmaWrite() // Cart -> DRAM
    {
        if (Bus == null) return;
        uint len = (WrLen & 0x00FF_FFFF) + 1;
        uint dramAddr = DramAddr & 0x007F_FFFE;
        uint cartAddr = CartAddr & 0xFFFF_FFFE;

        if ((dramAddr & 0x7) != 0 && len >= 0x7)
            len -= dramAddr & 0x7;

        uint cartOff = cartAddr;
        if (cartOff >= 0x10000000) cartOff -= 0x10000000;

        for (uint i = 0; i < len; i++)
        {
            byte b = Bus.Cart.Read8(cartOff + i);
            if (dramAddr + i < (uint)Bus.Rdram.Length)
                Bus.Rdram[dramAddr + i] = b;
        }

        DramAddr = dramAddr + len;
        CartAddr = cartAddr + len;
        Bus.Mi.SetInterrupt(N64Mi.MI_INTR_PI);
    }

    void DoDmaRead() // DRAM -> Cart
    {
        if (Bus == null) return;
        uint len = (RdLen & 0x00FF_FFFF) + 1;
        uint dramAddr = DramAddr & 0x007F_FFFE;
        uint cartAddr = CartAddr & 0xFFFF_FFFE;

        if ((dramAddr & 0x7) != 0 && len >= 0x7)
            len -= dramAddr & 0x7;

        uint cartOff = cartAddr;
        if (cartOff >= 0x10000000) cartOff -= 0x10000000;

        for (uint i = 0; i < len; i++)
        {
            byte b = (dramAddr + i < (uint)Bus.Rdram.Length)
                ? Bus.Rdram[dramAddr + i] : (byte)0;
        }

        DramAddr = dramAddr + len;
        CartAddr = cartAddr + len;
        Bus.Mi.SetInterrupt(N64Mi.MI_INTR_PI);
    }
}
