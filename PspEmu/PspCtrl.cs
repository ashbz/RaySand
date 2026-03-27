namespace PspEmu;

/// <summary>
/// PSP controller input: maps host input to PSP button state + analog stick.
/// </summary>
sealed class PspCtrl
{
    // PSP button bitmask values
    [Flags]
    public enum PspButton : uint
    {
        Select   = 0x000001,
        Start    = 0x000008,
        Up       = 0x000010,
        Right    = 0x000020,
        Down     = 0x000040,
        Left     = 0x000080,
        LTrigger = 0x000100,
        RTrigger = 0x000200,
        Triangle = 0x001000,
        Circle   = 0x002000,
        Cross    = 0x004000,
        Square   = 0x008000,
        Home     = 0x010000,
        Hold     = 0x020000,
        Note     = 0x800000,
    }

    public uint Buttons;
    public byte AnalogX = 128; // center
    public byte AnalogY = 128; // center
    public int SamplingCycle;
    public int SamplingMode; // 0 = digital, 1 = analog

    /// <summary>
    /// Write SceCtrlData to PSP memory at the given address.
    /// struct SceCtrlData { uint timeStamp; uint buttons; byte lx; byte ly; byte[6] rsrv; }
    /// </summary>
    public void WriteToMemory(PspBus bus, uint addr, bool negative = false)
    {
        uint ts = (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);
        uint buttons = negative ? ~Buttons : Buttons;

        bus.Write32(addr + 0, ts);
        bus.Write32(addr + 4, buttons);
        bus.Write8(addr + 8, AnalogX);
        bus.Write8(addr + 9, AnalogY);
        // Reserved bytes 10-15 = 0
        for (uint i = 10; i < 16; i++)
            bus.Write8(addr + i, 0);
    }

    /// <summary>Set a button state.</summary>
    public void SetButton(PspButton button, bool pressed)
    {
        if (pressed) Buttons |= (uint)button;
        else Buttons &= ~(uint)button;
    }

    public void SetAnalog(float x, float y)
    {
        AnalogX = (byte)Math.Clamp((int)(x * 127 + 128), 0, 255);
        AnalogY = (byte)Math.Clamp((int)(y * 127 + 128), 0, 255);
    }

    public void Reset()
    {
        Buttons = 0;
        AnalogX = 128;
        AnalogY = 128;
    }
}
