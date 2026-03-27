using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PspEmu;

/// <summary>
/// PSP memory bus: 32 MB main RAM, 2 MB VRAM, 16 KB scratchpad, MMIO.
/// Uses unsafe pointers on hot paths for maximum throughput.
/// </summary>
sealed class PspBus
{
    public const uint RamSize        = 32 * 1024 * 1024;
    public const uint VramSize       = 2 * 1024 * 1024;
    public const uint ScratchpadSize = 16 * 1024;

    public readonly byte[] Ram        = new byte[RamSize];
    public readonly byte[] Vram       = new byte[VramSize];
    public readonly byte[] Scratchpad = new byte[ScratchpadSize];

    // HW register backing (simplified)
    readonly uint[] _sysCtrl  = new uint[256];
    readonly uint[] _intrCtrl = new uint[64];

    public PspGe  Ge   { get; set; } = null!;
    public PspDisplay Display { get; set; } = null!;
    public PspAudio   Audio   { get; set; } = null!;

    // Interrupt state
    public uint InterruptStatus;
    public uint InterruptMask;

    // System timer
    public uint SystemTimeLow;

    /// <summary>Translate virtual address to physical, stripping cache/segment bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint VirtToPhys(uint vaddr)
    {
        // KSEG0/KSEG1 (0x80000000-0xBFFFFFFF): mask off top 3 bits
        if (vaddr >= 0x80000000 && vaddr < 0xC0000000)
            return vaddr & 0x1FFF_FFFF;

        // User cached/uncached RAM: 0x08000000-0x0BFFFFFF → physical 0x00000000+
        if (vaddr >= 0x0800_0000 && vaddr < 0x0C00_0000)
            return vaddr - 0x0800_0000;

        // User uncached RAM mirror: 0x48000000-0x4BFFFFFF
        if (vaddr >= 0x4800_0000 && vaddr < 0x4C00_0000)
            return vaddr - 0x4800_0000;

        // VRAM cached: 0x04000000-0x041FFFFF
        if (vaddr >= 0x0400_0000 && vaddr < 0x0420_0000)
            return vaddr;

        // VRAM uncached: 0x44000000-0x441FFFFF
        if (vaddr >= 0x4400_0000 && vaddr < 0x4420_0000)
            return 0x0400_0000 + (vaddr - 0x4400_0000);

        // Scratchpad: 0x00010000-0x00013FFF
        if (vaddr >= 0x0001_0000 && vaddr < 0x0001_4000)
            return vaddr;

        // HW I/O: 0xBC000000-0xBFFFFFFF (already handled by KSEG1 mask)
        // Pass through for MMIO
        return vaddr;
    }

    // ── 8-bit ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint vaddr)
    {
        uint pa = VirtToPhys(vaddr);
        if (pa < RamSize) return Ram[pa];
        if (pa >= 0x0400_0000 && pa < 0x0400_0000 + VramSize) return Vram[pa - 0x0400_0000];
        if (pa >= 0x0001_0000 && pa < 0x0001_0000 + ScratchpadSize) return Scratchpad[pa - 0x0001_0000];
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write8(uint vaddr, byte val)
    {
        uint pa = VirtToPhys(vaddr);
        if (pa < RamSize) { Ram[pa] = val; return; }
        if (pa >= 0x0400_0000 && pa < 0x0400_0000 + VramSize) { Vram[pa - 0x0400_0000] = val; return; }
        if (pa >= 0x0001_0000 && pa < 0x0001_0000 + ScratchpadSize) { Scratchpad[pa - 0x0001_0000] = val; return; }
    }

    // ── 16-bit (unsafe) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ushort Read16(uint vaddr)
    {
        uint pa = VirtToPhys(vaddr);
        if (pa < RamSize - 1) { fixed (byte* p = Ram) return *(ushort*)(p + pa); }
        if (pa >= 0x0400_0000 && pa < 0x0400_0000 + VramSize - 1) { fixed (byte* p = Vram) return *(ushort*)(p + (pa - 0x0400_0000)); }
        if (pa >= 0x0001_0000 && pa < 0x0001_0000 + ScratchpadSize - 1) { fixed (byte* p = Scratchpad) return *(ushort*)(p + (pa - 0x0001_0000)); }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write16(uint vaddr, ushort val)
    {
        uint pa = VirtToPhys(vaddr);
        if (pa < RamSize - 1) { fixed (byte* p = Ram) *(ushort*)(p + pa) = val; return; }
        if (pa >= 0x0400_0000 && pa < 0x0400_0000 + VramSize - 1) { fixed (byte* p = Vram) *(ushort*)(p + (pa - 0x0400_0000)) = val; return; }
        if (pa >= 0x0001_0000 && pa < 0x0001_0000 + ScratchpadSize - 1) { fixed (byte* p = Scratchpad) *(ushort*)(p + (pa - 0x0001_0000)) = val; return; }
    }

    // ── 32-bit (unsafe hot paths) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe uint Read32(uint vaddr)
    {
        uint pa = VirtToPhys(vaddr);

        if (pa < RamSize - 3)
        {
            fixed (byte* p = Ram) return *(uint*)(p + pa);
        }
        if (pa >= 0x0400_0000 && pa < 0x0400_0000 + VramSize - 3)
        {
            fixed (byte* p = Vram) return *(uint*)(p + (pa - 0x0400_0000));
        }
        if (pa >= 0x0001_0000 && pa < 0x0001_0000 + ScratchpadSize - 3)
        {
            fixed (byte* p = Scratchpad) return *(uint*)(p + (pa - 0x0001_0000));
        }
        return ReadMmio32(pa);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write32(uint vaddr, uint val)
    {
        uint pa = VirtToPhys(vaddr);

        if (pa < RamSize - 3)
        {
            fixed (byte* p = Ram) *(uint*)(p + pa) = val;
            return;
        }
        if (pa >= 0x0400_0000 && pa < 0x0400_0000 + VramSize - 3)
        {
            fixed (byte* p = Vram) *(uint*)(p + (pa - 0x0400_0000)) = val;
            return;
        }
        if (pa >= 0x0001_0000 && pa < 0x0001_0000 + ScratchpadSize - 3)
        {
            fixed (byte* p = Scratchpad) *(uint*)(p + (pa - 0x0001_0000)) = val;
            return;
        }
        WriteMmio32(pa, val);
    }

    // ── Fast RAM access (no virtual translation, for internal use) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadRam32(uint offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Ram.AsSpan((int)(offset & (RamSize - 1))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRam32(uint offset, uint val) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Ram.AsSpan((int)(offset & (RamSize - 1))), val);

    // ── VRAM helpers ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadVram32(uint offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Vram.AsSpan((int)(offset & (VramSize - 1))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVram32(uint offset, uint val) =>
        BinaryPrimitives.WriteUInt32LittleEndian(Vram.AsSpan((int)(offset & (VramSize - 1))), val);

    // ── Block copy ──

    public void ReadBlock(uint vaddr, Span<byte> dest)
    {
        for (int i = 0; i < dest.Length; i++)
            dest[i] = Read8(vaddr + (uint)i);
    }

    public void WriteBlock(uint vaddr, ReadOnlySpan<byte> src)
    {
        for (int i = 0; i < src.Length; i++)
            Write8(vaddr + (uint)i, src[i]);
    }

    public void WriteBlock(uint physOffset, byte[] src, int srcOff, int len)
    {
        Array.Copy(src, srcOff, Ram, (int)physOffset, len);
    }

    // ── MMIO ──

    uint ReadMmio32(uint pa)
    {
        // GE registers: 0x1D400000-0x1D4FFFFF
        if (pa >= 0x1D40_0000 && pa < 0x1D50_0000)
            return Ge?.ReadReg(pa) ?? 0;

        // Display registers: 0x1D500000-0x1D5FFFFF (approximate)

        // System control
        if (pa >= 0x1C00_0000 && pa < 0x1C10_0000)
            return _sysCtrl[(pa >> 2) & 0xFF];

        // Interrupt controller
        if (pa >= 0x1C30_0000 && pa < 0x1C40_0000)
        {
            uint off = (pa - 0x1C30_0000) >> 2;
            return off switch
            {
                0 => InterruptStatus,
                1 => InterruptMask,
                _ => _intrCtrl[off & 63],
            };
        }

        // System timer
        if (pa == 0x1C00_0100)
            return SystemTimeLow;

        return 0;
    }

    void WriteMmio32(uint pa, uint val)
    {
        // GE registers
        if (pa >= 0x1D40_0000 && pa < 0x1D50_0000)
        { Ge?.WriteReg(pa, val); return; }

        // System control
        if (pa >= 0x1C00_0000 && pa < 0x1C10_0000)
        { _sysCtrl[(pa >> 2) & 0xFF] = val; return; }

        // Interrupt controller
        if (pa >= 0x1C30_0000 && pa < 0x1C40_0000)
        {
            uint off = (pa - 0x1C30_0000) >> 2;
            switch (off)
            {
                case 0: InterruptStatus &= ~val; break;
                case 1: InterruptMask = val; break;
                default: _intrCtrl[off & 63] = val; break;
            }
            return;
        }
    }
}
