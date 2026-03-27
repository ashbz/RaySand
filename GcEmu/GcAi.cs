using System.Runtime.CompilerServices;

namespace GcEmu;

class GcAi
{
    GcBus _bus = null!;

    uint _aicr;
    uint _aivr;
    uint _aiscnt;
    uint _aiit;
    long _lastCpuTime;
    int _cpuCyclesPerSample;

    const int CpuClock = 486_000_000;

    public void Init(GcBus bus)
    {
        _bus = bus;
        _cpuCyclesPerSample = CpuClock / 48000;
    }

    public uint Read32(uint addr)
    {
        return (addr & 0x1F) switch
        {
            0x00 => _aicr,
            0x04 => _aivr,
            0x08 => GetSampleCounter(),
            0x0C => _aiit,
            _ => 0
        };
    }

    public void Write32(uint addr, uint val)
    {
        switch (addr & 0x1F)
        {
            case 0x00:
                if ((val & (1 << 3)) != 0)
                    _aicr &= ~(1u << 3);

                uint prev = _aicr;
                _aicr = (_aicr & (1u << 3)) | (val & ~(1u << 3 | 1u << 6));

                if ((_aicr & 2) != (prev & 2))
                {
                    _cpuCyclesPerSample = (_aicr & 2) != 0
                        ? CpuClock / 48000
                        : CpuClock / 32000;
                }

                if ((_aicr & 1) != 0 && (prev & 1) == 0)
                    _lastCpuTime = _bus.Cpu!.TotalCycles;

                if ((val & (1 << 6)) != 0)
                {
                    _aiscnt = 0;
                    _lastCpuTime = _bus.Cpu!.TotalCycles;
                }

                UpdateInterrupt();
                break;
            case 0x04:
                _aivr = val;
                break;
            case 0x08:
                _aiscnt = val;
                _lastCpuTime = _bus.Cpu!.TotalCycles;
                break;
            case 0x0C:
                _aiit = val;
                break;
        }
    }

    uint GetSampleCounter()
    {
        if ((_aicr & 1) == 0) return _aiscnt;
        long elapsed = _bus.Cpu!.TotalCycles - _lastCpuTime;
        return _aiscnt + (uint)(elapsed / _cpuCyclesPerSample);
    }

    public void Tick()
    {
        if ((_aicr & 1) == 0) return;

        long elapsed = _bus.Cpu!.TotalCycles - _lastCpuTime;
        if (elapsed < _cpuCyclesPerSample) return;

        uint samples = (uint)(elapsed / _cpuCyclesPerSample);
        _lastCpuTime += samples * _cpuCyclesPerSample;

        uint oldCounter = _aiscnt + 1;
        _aiscnt += samples;

        if ((_aiit - oldCounter) <= (_aiscnt - oldCounter))
        {
            _aicr |= 1u << 3;
            UpdateInterrupt();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void UpdateInterrupt()
    {
        bool active = (_aicr & (1 << 3)) != 0 && (_aicr & (1 << 2)) != 0;
        if (active)
            _bus.Pi.SetInterrupt(GcPi.IRQ_AI);
        else
            _bus.Pi.ClearInterrupt(GcPi.IRQ_AI);
    }

    public void Reset()
    {
        _aicr = 0;
        _aivr = 0;
        _aiscnt = 0;
        _aiit = 0;
        _lastCpuTime = 0;
        _cpuCyclesPerSample = CpuClock / 48000;
    }
}
