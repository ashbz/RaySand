using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace N64Emu;

sealed class N64Bus
{
    public const int RdramSize = 0x0080_0000; // 8MB (with expansion pak)

    public readonly byte[] Rdram = new byte[RdramSize];

    // RDRAM interface registers
    readonly uint[] _riRegs = new uint[8];

    public readonly N64Mi Mi = new();
    public readonly N64Vi Vi = new();
    public readonly N64Ai Ai = new();
    public readonly N64Pi Pi = new();
    public readonly N64Si Si = new();
    public readonly N64Pif Pif = new();
    public readonly N64Rsp Rsp = new();
    public readonly N64Rdp Rdp = new();
    public readonly N64Cart Cart = new();
    public readonly RspHle RspHle = new();

    public Vr4300? Cpu;

    public N64Bus()
    {
        Mi.Bus = this;
        Vi.Bus = this;
        Ai.Bus = this;
        Pi.Bus = this;
        Si.Bus = this;
        Pif.Bus = this;
        Rsp.Bus = this;
        Rdp.Bus = this;
        RspHle.Bus = this;
    }

    public void ResetState()
    {
        Array.Clear(Rdram);
        Array.Clear(_riRegs);
        Vi.Reset();
        Mi.Reset();
        Ai.Reset();
        Pi.Reset();
        Si.Reset();
        Rsp.Reset();
        Rdp.Reset();
        Pif.ResetRam();
        RspHle.Reset();
    }

    public uint TranslateVirtual(ulong vaddr)
    {
        uint addr = (uint)vaddr;
        uint seg = addr >> 29;
        return seg switch
        {
            4 => addr & 0x1FFF_FFFF, // KSEG0 (0x80000000-0x9FFFFFFF)
            5 => addr & 0x1FFF_FFFF, // KSEG1 (0xA0000000-0xBFFFFFFF)
            _ => addr & 0x1FFF_FFFF, // simplified: treat all as physical for now
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint paddr)
    {
        paddr &= 0x1FFF_FFFF;

        if (paddr < 0x0080_0000) // RDRAM
        {
            if (paddr + 3 < RdramSize)
                return BinaryPrimitives.ReadUInt32BigEndian(Rdram.AsSpan((int)paddr));
            return 0;
        }

        return ReadMmio32(paddr);
    }

    uint ReadMmio32(uint paddr)
    {
        if (paddr < 0x0400_0000) // RDRAM regs
        {
            if (paddr >= 0x03F0_0000)
                return _riRegs[((paddr - 0x03F0_0000) >> 2) & 7];
            return 0;
        }
        if (paddr < 0x0400_1000) // RSP DMEM
            return BinaryPrimitives.ReadUInt32BigEndian(Rsp.Dmem.AsSpan((int)(paddr & 0xFFF)));
        if (paddr < 0x0400_2000) // RSP IMEM
            return BinaryPrimitives.ReadUInt32BigEndian(Rsp.Imem.AsSpan((int)(paddr & 0xFFF)));
        if (paddr < 0x0404_0020 && paddr >= 0x0404_0000) // RSP regs
            return Rsp.ReadReg(paddr - 0x0404_0000);
        if (paddr >= 0x0408_0000 && paddr < 0x0408_0008) // RSP PC
            return Rsp.ReadPcReg(paddr - 0x0408_0000);
        if (paddr >= 0x0410_0000 && paddr < 0x0420_0000) // RDP
            return Rdp.Read(paddr - 0x0410_0000);
        if (paddr >= 0x0430_0000 && paddr < 0x0440_0000) // MI
            return Mi.Read(paddr - 0x0430_0000);
        if (paddr >= 0x0440_0000 && paddr < 0x0450_0000) // VI
            return Vi.Read(paddr - 0x0440_0000);
        if (paddr >= 0x0450_0000 && paddr < 0x0460_0000) // AI
            return Ai.Read(paddr - 0x0450_0000);
        if (paddr >= 0x0460_0000 && paddr < 0x0470_0000) // PI
            return Pi.Read(paddr - 0x0460_0000);
        if (paddr >= 0x0470_0000 && paddr < 0x0480_0000) // RI
            return _riRegs[((paddr - 0x0470_0000) >> 2) & 7];
        if (paddr >= 0x0480_0000 && paddr < 0x0490_0000) // SI
            return Si.Read(paddr - 0x0480_0000);
        if (paddr >= 0x0500_0000 && paddr < 0x0800_0000) // N64DD - not present
            return 0xFFFF_FFFF;
        if (paddr >= 0x0800_0000 && paddr < 0x1000_0000) // SRAM / FlashRAM
            return 0;
        if (paddr >= 0x1000_0000 && paddr < 0x1FC0_0000) // Cart ROM
            return Cart.Read32(paddr - 0x1000_0000);
        if (paddr >= 0x1FC0_0000 && paddr < 0x1FC0_07C0) // PIF Boot ROM
        {
            uint off = paddr - 0x1FC0_0000;
            if (off + 3 < (uint)Pif.BootRom.Length)
                return (uint)(Pif.BootRom[off] << 24 | Pif.BootRom[off + 1] << 16 |
                              Pif.BootRom[off + 2] << 8 | Pif.BootRom[off + 3]);
            return 0;
        }
        if (paddr >= 0x1FC0_07C0 && paddr < 0x1FC0_0800) // PIF RAM
            return Pif.ReadRam32(paddr - 0x1FC0_07C0);

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write32(uint paddr, uint val)
    {
        paddr &= 0x1FFF_FFFF;

        if (paddr < 0x0080_0000) // RDRAM
        {
            if (paddr + 3 < RdramSize)
                BinaryPrimitives.WriteUInt32BigEndian(Rdram.AsSpan((int)paddr), val);
            return;
        }

        WriteMmio32(paddr, val);
    }

    void WriteMmio32(uint paddr, uint val)
    {
        if (paddr < 0x0400_0000) // RDRAM regs
        {
            if (paddr >= 0x03F0_0000)
                _riRegs[((paddr - 0x03F0_0000) >> 2) & 7] = val;
            return;
        }
        if (paddr < 0x0400_1000) // RSP DMEM
        {
            BinaryPrimitives.WriteUInt32BigEndian(Rsp.Dmem.AsSpan((int)(paddr & 0xFFF)), val);
            return;
        }
        if (paddr < 0x0400_2000) // RSP IMEM
        {
            BinaryPrimitives.WriteUInt32BigEndian(Rsp.Imem.AsSpan((int)(paddr & 0xFFF)), val);
            return;
        }
        if (paddr >= 0x0404_0000 && paddr < 0x0404_0020)
        {
            Rsp.WriteReg(paddr - 0x0404_0000, val);
            return;
        }
        if (paddr >= 0x0408_0000 && paddr < 0x0408_0008)
        {
            Rsp.WritePcReg(paddr - 0x0408_0000, val);
            return;
        }
        if (paddr >= 0x0410_0000 && paddr < 0x0420_0000)
        {
            Rdp.Write(paddr - 0x0410_0000, val);
            return;
        }
        if (paddr >= 0x0430_0000 && paddr < 0x0440_0000)
        {
            Mi.Write(paddr - 0x0430_0000, val);
            return;
        }
        if (paddr >= 0x0440_0000 && paddr < 0x0450_0000)
        {
            Vi.Write(paddr - 0x0440_0000, val);
            return;
        }
        if (paddr >= 0x0450_0000 && paddr < 0x0460_0000)
        {
            Ai.Write(paddr - 0x0450_0000, val);
            return;
        }
        if (paddr >= 0x0460_0000 && paddr < 0x0470_0000)
        {
            Pi.Write(paddr - 0x0460_0000, val);
            return;
        }
        if (paddr >= 0x0470_0000 && paddr < 0x0480_0000)
        {
            _riRegs[((paddr - 0x0470_0000) >> 2) & 7] = val;
            return;
        }
        if (paddr >= 0x0480_0000 && paddr < 0x0490_0000)
        {
            Si.Write(paddr - 0x0480_0000, val);
            return;
        }
        if (paddr >= 0x1FC0_07C0 && paddr < 0x1FC0_0800)
        {
            Pif.WriteRam32(paddr - 0x1FC0_07C0, val);
            Pif.ProcessCommands();
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Read16(uint paddr)
    {
        paddr &= 0x1FFF_FFFF;
        if (paddr < 0x0080_0000 && paddr + 1 < RdramSize)
            return BinaryPrimitives.ReadUInt16BigEndian(Rdram.AsSpan((int)paddr));
        if (paddr >= 0x1000_0000 && paddr < 0x1FC0_0000)
            return Cart.Read16(paddr - 0x1000_0000);

        uint w = Read32(paddr & ~3u);
        return (paddr & 2) == 0 ? (ushort)(w >> 16) : (ushort)w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint paddr)
    {
        paddr &= 0x1FFF_FFFF;
        if (paddr < 0x0080_0000 && paddr < RdramSize)
            return Rdram[paddr];
        if (paddr >= 0x1000_0000 && paddr < 0x1FC0_0000)
            return Cart.Read8(paddr - 0x1000_0000);

        uint w = Read32(paddr & ~3u);
        int shift = (3 - (int)(paddr & 3)) * 8;
        return (byte)(w >> shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write16(uint paddr, ushort val)
    {
        paddr &= 0x1FFF_FFFF;
        if (paddr < 0x0080_0000 && paddr + 1 < RdramSize)
        {
            BinaryPrimitives.WriteUInt16BigEndian(Rdram.AsSpan((int)paddr), val);
            return;
        }

        uint old = Read32(paddr & ~3u);
        if ((paddr & 2) == 0)
            Write32(paddr & ~3u, (old & 0x0000FFFF) | ((uint)val << 16));
        else
            Write32(paddr & ~3u, (old & 0xFFFF0000) | val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(uint paddr, byte val)
    {
        paddr &= 0x1FFF_FFFF;
        if (paddr < 0x0080_0000 && paddr < RdramSize)
        {
            Rdram[paddr] = val;
            return;
        }

        uint old = Read32(paddr & ~3u);
        int shift = (3 - (int)(paddr & 3)) * 8;
        uint mask = ~(0xFFu << shift);
        Write32(paddr & ~3u, (old & mask) | ((uint)val << shift));
    }

    public ulong Read64(uint paddr)
    {
        return ((ulong)Read32(paddr) << 32) | Read32(paddr + 4);
    }

    public void Write64(uint paddr, ulong val)
    {
        Write32(paddr, (uint)(val >> 32));
        Write32(paddr + 4, (uint)val);
    }
}
