namespace GcEmu;

class GcMi
{
    uint _protAddr;
    uint _protType;
    ushort _intSr;
    ushort _intMr;
    ushort _unknown1;
    ushort _addrLo;
    ushort _addrHi;

    public ushort Read16(uint addr)
    {
        return (addr & 0xFF) switch
        {
            0x1E => _intSr,
            0x1C => _intMr,
            _ => 0
        };
    }

    public void Write16(uint addr, ushort val)
    {
        switch (addr & 0xFF)
        {
            case 0x1E: _intSr &= (ushort)~val; break;
            case 0x1C: _intMr = val; break;
        }
    }

    public uint Read32(uint addr)
    {
        return (addr & 0xFF) switch
        {
            0x00 => _protAddr,
            0x04 => _protType,
            _ => 0
        };
    }

    public void Write32(uint addr, uint val)
    {
        switch (addr & 0xFF)
        {
            case 0x00: _protAddr = val; break;
            case 0x04: _protType = val; break;
        }
    }

    public void Reset()
    {
        _protAddr = 0;
        _protType = 0;
        _intSr = 0;
        _intMr = 0;
    }
}
