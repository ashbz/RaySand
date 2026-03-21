using System.Runtime.CompilerServices;

namespace PsxEmu;

/// <summary>
/// PSX DMA controller – channels 0-6.
/// Uses bus.DmaLoadWord/DmaStoreWord for zero-overhead RAM access.
/// </summary>
class PsxDma
{
    struct Channel { public uint Base, Block, Ctrl; }

    readonly Channel[] _ch = new Channel[7];
    public uint DPCR;
    uint _dicr;
    public uint DICR
    {
        get => _dicr;
        set
        {
            // Bits 24-30: write-1-to-acknowledge (clear)
            uint ack = value & 0x7F00_0000;
            _dicr = (_dicr & ~ack) & 0x7F00_0000        // keep unacked flags
                  | (value & 0x00FF_803F);                // writable bits: 0-5, 15, 16-23
            // Recalculate bit 31 (master flag)
            bool masterEnable = (_dicr & (1u << 23)) != 0;
            bool forceIrq = (_dicr & (1u << 15)) != 0;
            uint irqFlags = (_dicr >> 24) & 0x7F;
            uint irqEnables = (_dicr >> 16) & 0x7F;
            bool channelIrq = (irqFlags & irqEnables) != 0;
            if (forceIrq || (masterEnable && channelIrq))
                _dicr |= 1u << 31;
            else
                _dicr &= ~(1u << 31);
        }
    }

    readonly PsxBus _bus;
    readonly PsxGpu _gpu;
    readonly PsxCdRom _cdrom;
    public long GpuDmaWords;

    public PsxDma(PsxBus bus, PsxGpu gpu, PsxCdRom cdrom)
    {
        _bus = bus;
        _gpu = gpu;
        _cdrom = cdrom;
        DPCR = 0x0765_4321;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read(uint addr)
    {
        int ch = (int)((addr >> 4) & 7);
        return (addr & 0xF) switch
        {
            0 => _ch[ch].Base,
            4 => _ch[ch].Block,
            8 => _ch[ch].Ctrl,
            _ => 0,
        };
    }

    public void Write(uint addr, uint val)
    {
        int ch = (int)((addr >> 4) & 7);
        switch (addr & 0xF)
        {
            case 0: _ch[ch].Base  = val & 0x00FF_FFFC; break;
            case 4: _ch[ch].Block = val; break;
            case 8:
                _ch[ch].Ctrl = val;
                uint syncMode = (val >> 9) & 3;
                bool enable = (val & 0x0100_0000) != 0;
                bool trigger = (val & 0x1000_0000) != 0;
                // SyncMode 0 requires enable AND trigger; SyncMode 1/2 only needs enable
                bool active = syncMode == 0 ? (enable && trigger) : enable;
                if (active) Run(ch);
                break;
        }
    }

    void Run(int ch)
    {
        if (ch == 6) { RunOtc(ch); DmaComplete(ch); return; }
        if (ch == 2) { RunGpu(ch); DmaComplete(ch); return; }
        if (ch == 3) { RunCdRom(ch); DmaComplete(ch); return; }
        _ch[ch].Ctrl &= ~0x0100_0000u;
        DmaComplete(ch);
    }

    void DmaComplete(int ch)
    {
        uint flag = 1u << (24 + ch);
        uint enable = 1u << (16 + ch);
        if ((_dicr & enable) != 0)
        {
            _dicr |= flag;
            bool masterEnable = (_dicr & (1u << 23)) != 0;
            uint irqFlags = (_dicr >> 24) & 0x7F;
            uint irqEnables = (_dicr >> 16) & 0x7F;
            if (masterEnable && (irqFlags & irqEnables) != 0)
            {
                _dicr |= 1u << 31;
                _bus.IStat |= 1u << 3;
            }
        }
    }

    void RunOtc(int ch)
    {
        uint addr  =  _ch[ch].Base & 0x001F_FFFC;
        uint count = _ch[ch].Block & 0xFFFF;
        if (count == 0) count = 0x10000;

        for (uint i = 1; i < count; i++, addr -= 4)
            _bus.DmaStoreWord(addr, (addr - 4) & 0x001F_FFFF);
        _bus.DmaStoreWord(addr, 0x00FF_FFFF);

        _ch[ch].Ctrl &= ~0x0100_0000u;
    }

    void RunCdRom(int ch)
    {
        uint addr = _ch[ch].Base & 0x001F_FFFC;
        uint words = _ch[ch].Block & 0xFFFF;
        uint blocks = (_ch[ch].Block >> 16) & 0xFFFF;
        uint total = blocks > 0 ? words * blocks : words;


        var data = _cdrom.DmaRead((int)total);
        if (data.IsEmpty)
        {
            _ch[ch].Ctrl &= ~0x0100_0000u;
            return;
        }

        for (int i = 0; i + 3 < data.Length; i += 4)
        {
            uint word = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            _bus.DmaStoreWord(addr, word);
            addr = (addr + 4) & 0x001F_FFFC;
        }

        _ch[ch].Ctrl &= ~0x0100_0000u;
    }

    void RunGpu(int ch)
    {
        uint syncMode = (_ch[ch].Ctrl >> 9) & 3;

        if (syncMode == 2) // Linked-list (matching ProjectPSX's termination: header bit 23)
        {
            uint addr = _ch[ch].Base & 0x001F_FFFC;

            while (true)
            {
                uint header = _bus.DmaLoadWord(addr);
                uint nWords = header >> 24;
                GpuDmaWords += nWords;

                for (uint w = 0; w < nWords; w++)
                {
                    addr = (addr + 4) & 0x1F_FFFC;
                    _gpu.WriteGP0(_bus.DmaLoadWord(addr));
                }

                if ((header & 0x80_0000) != 0) break; // end-of-list bit
                addr = header & 0x1F_FFFC;
            }
        }
        else // Block transfer
        {
            uint words  = _ch[ch].Block & 0xFFFF;
            uint blocks = (_ch[ch].Block >> 16) & 0xFFFF;
            uint total  = syncMode == 1 ? words * Math.Max(1u, blocks) : words;
            uint addr   = _ch[ch].Base & 0x001F_FFFC;
            int  step   = ((int)_ch[ch].Ctrl & 2) != 0 ? -4 : 4;

            for (uint i = 0; i < total; i++, addr = (uint)(addr + step))
                _gpu.WriteGP0(_bus.DmaLoadWord(addr & 0x001F_FFFC));
        }

        _ch[ch].Ctrl &= ~0x0100_0000u;
    }
}
