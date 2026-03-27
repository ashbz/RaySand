using System.Runtime.CompilerServices;

namespace GcEmu;

class GcPi
{
    public const int IRQ_ERROR      = 0;
    public const int IRQ_RSW        = 1;
    public const int IRQ_DI         = 2;
    public const int IRQ_SI         = 3;
    public const int IRQ_EXI        = 4;
    public const int IRQ_AI         = 5;
    public const int IRQ_DSP        = 6;
    public const int IRQ_MEM        = 7;
    public const int IRQ_VI         = 8;
    public const int IRQ_PE_TOKEN   = 9;
    public const int IRQ_PE_FINISH  = 10;
    public const int IRQ_CP         = 11;
    public const int IRQ_DEBUG      = 12;
    public const int IRQ_HSP        = 13;

    public uint IntSr;
    public uint IntMr;

    public uint FifoBase;
    public uint FifoEnd;
    public uint FifoWp;

    public uint FlipperRev = 0x246500B1;

    GcBus _bus = null!;
    public void Init(GcBus bus) => _bus = bus;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInterrupt(int bit) => IntSr |= 1u << bit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearInterrupt(int bit) => IntSr &= ~(1u << bit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InterruptPending() => (IntSr & IntMr) != 0;

    public uint Read32(uint addr)
    {
        return (addr & 0xFF) switch
        {
            0x00 => IntSr,
            0x04 => IntMr,
            0x0C => FifoBase,
            0x10 => FifoEnd,
            0x14 => FifoWp,
            0x2C => FlipperRev,
            _ => 0
        };
    }

    public void Write32(uint addr, uint val)
    {
        switch (addr & 0xFF)
        {
            case 0x00:
                IntSr &= ~val;
                break;
            case 0x04:
                IntMr = val;
                break;
            case 0x0C:
                FifoBase = val & 0x03FFFFC0;
                break;
            case 0x10:
                FifoEnd = val & 0x03FFFFC0;
                break;
            case 0x14:
                FifoWp = val & 0x03FFFFC0;
                break;
            case 0x24:
                Log.Info("PI: System reset triggered");
                _bus.Machine.Reset();
                break;
        }
    }

    public void Reset()
    {
        IntSr = 0;
        IntMr = 0;
        FifoBase = 0;
        FifoEnd = 0;
        FifoWp = 0;
    }
}
