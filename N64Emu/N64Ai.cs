using System.Buffers.Binary;

namespace N64Emu;

sealed class N64Ai
{
    public uint DramAddr;
    public uint Length;
    public uint Control;
    public uint Status;
    public uint DacRate;
    public uint BitRate;

    struct DmaEntry { public uint Addr; public uint Len; }
    DmaEntry _current, _next;
    bool _hasNext;
    int _dmaTimer;

    public N64Bus? Bus;

    const int RingSize = 1 << 17; // 128 KB
    readonly byte[] _ring = new byte[RingSize];
    int _writePos, _readPos;
    readonly object _lock = new();
    int _lastSampleRate;

    public bool SampleRateChanged { get; set; }

    public void Reset()
    {
        DramAddr = Length = Control = Status = DacRate = BitRate = 0;
        _current = _next = default;
        _hasNext = false;
        _dmaTimer = 0;
        lock (_lock) { _writePos = _readPos = 0; }
        _lastSampleRate = 0;
        SampleRateChanged = false;
    }

    public int SampleRate
    {
        get
        {
            int rate = DacRate > 0 ? (int)(48681812 / (DacRate + 1)) : 44100;
            if (rate != _lastSampleRate && _lastSampleRate != 0)
                SampleRateChanged = true;
            _lastSampleRate = rate;
            return rate;
        }
    }

    public int AvailableBytes
    {
        get { lock (_lock) { return (_writePos - _readPos + RingSize) & (RingSize - 1); } }
    }

    public int ReadSamples(byte[] dest, int offset, int count)
    {
        lock (_lock)
        {
            int avail = (_writePos - _readPos + RingSize) & (RingSize - 1);
            if (count > avail) count = avail;
            for (int i = 0; i < count; i++)
            {
                dest[offset + i] = _ring[_readPos];
                _readPos = (_readPos + 1) & (RingSize - 1);
            }
            return count;
        }
    }

    void EnqueueSamples(uint rdramAddr, uint len)
    {
        if (Bus == null || len == 0) return;
        rdramAddr &= 0x00FF_FFFF;
        lock (_lock)
        {
            for (uint i = 0; i < len && rdramAddr + i < (uint)Bus.Rdram.Length; i++)
            {
                _ring[_writePos] = Bus.Rdram[rdramAddr + i];
                _writePos = (_writePos + 1) & (RingSize - 1);
            }
        }
    }

    public uint Read(uint addr)
    {
        return (addr & 0x1F) switch
        {
            0x00 => DramAddr,
            0x04 => _current.Len,
            0x08 => Control,
            0x0C => GetStatus(),
            0x10 => DacRate,
            0x14 => BitRate,
            _ => 0,
        };
    }

    uint GetStatus()
    {
        uint s = 0;
        if (_current.Len > 0) s |= (1u << 30) | 1u;
        if (_hasNext) s |= (1u << 31);
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
                Length = val & 0x3FFF8;
                if (_current.Len == 0)
                {
                    _current = new DmaEntry { Addr = DramAddr, Len = Length };
                    _dmaTimer = (int)(Length / 4);
                }
                else if (!_hasNext)
                {
                    _next = new DmaEntry { Addr = DramAddr, Len = Length };
                    _hasNext = true;
                }
                Status |= 0x40000000;
                break;
            case 0x08:
                Control = val & 1;
                break;
            case 0x0C:
                Bus?.Mi.ClearInterrupt(N64Mi.MI_INTR_AI);
                break;
            case 0x10:
                DacRate = val & 0x3FFF;
                break;
            case 0x14:
                BitRate = val & 0xF;
                break;
        }
    }

    public void Step(int cycles)
    {
        if (_current.Len == 0) return;
        _dmaTimer -= cycles;
        if (_dmaTimer <= 0)
        {
            EnqueueSamples(_current.Addr, _current.Len);
            Bus?.Mi.SetInterrupt(N64Mi.MI_INTR_AI);
            if (_hasNext)
            {
                _current = _next;
                _hasNext = false;
                _dmaTimer = (int)(_current.Len / 4);
            }
            else
            {
                _current = default;
            }
        }
    }
}
