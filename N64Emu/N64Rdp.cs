using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace N64Emu;

sealed class N64Rdp
{
    public uint DpStart;
    public uint DpEnd;
    public uint DpCurrent;
    public uint DpStatus;

    // RDP state
    public uint FillColor;
    public uint PrimColor;
    public uint EnvColor;
    public uint BlendColor;
    public uint FogColor;
    public ulong CombineMode;

    public uint ColorImageAddr;
    public uint ColorImageFormat;
    public uint ColorImageWidth;
    public uint ColorImageSize;

    public uint ZImageAddr;

    public uint TexImageAddr;
    public uint TexImageFormat;
    public uint TexImageWidth;
    public uint TexImageSize;

    public uint ScissorXH, ScissorYH, ScissorXL, ScissorYL;
    public uint OtherModeH, OtherModeL;

    public readonly Tile[] Tiles = new Tile[8];
    public readonly byte[] Tmem = new byte[4096];

    public N64Bus? Bus;

    public struct Tile
    {
        public uint Format, Size, Line, Addr;
        public uint Palette, Ct, Mt, MaskT, ShiftT;
        public uint Cs, Ms, MaskS, ShiftS;
        public uint SL, TL, SH, TH;
    }

    public N64Rdp()
    {
        for (int i = 0; i < 8; i++)
            Tiles[i] = new Tile();
    }

    public void Reset()
    {
        DpStart = DpEnd = DpCurrent = DpStatus = 0;
        FillColor = PrimColor = EnvColor = BlendColor = FogColor = 0;
        CombineMode = 0;
        ColorImageAddr = ColorImageFormat = ColorImageWidth = ColorImageSize = 0;
        ZImageAddr = 0;
        TexImageAddr = TexImageFormat = TexImageWidth = TexImageSize = 0;
        ScissorXH = ScissorYH = ScissorXL = ScissorYL = 0;
        OtherModeH = OtherModeL = 0;
        for (int i = 0; i < 8; i++) Tiles[i] = default;
        Array.Clear(Tmem);
    }

    public uint Read(uint addr)
    {
        return (addr & 0x1F) switch
        {
            0x00 => DpStart,
            0x04 => DpEnd,
            0x08 => DpCurrent,
            0x0C => DpStatus,
            _ => 0,
        };
    }

    public void Write(uint addr, uint val)
    {
        switch (addr & 0x1F)
        {
            case 0x00: if ((DpStatus & 0x400) == 0) DpStart = val & 0x00FF_FFF8; break;
            case 0x04:
                if ((DpStatus & 0x400) == 0)
                {
                    DpEnd = val & 0x00FF_FFF8;
                    RunCommands();
                }
                break;
            case 0x0C: WriteStatus(val); break;
        }
    }

    void WriteStatus(uint val)
    {
        if ((val & 1) != 0) DpStatus &= ~0x0001u; // clear xbus_dmem_dma
        if ((val & 2) != 0) DpStatus |= 0x0001u;  // set xbus_dmem_dma
        if ((val & 4) != 0) DpStatus &= ~0x0004u; // clear freeze
        if ((val & 8) != 0) DpStatus |= 0x0004u;  // set freeze
        if ((val & 16) != 0) DpStatus &= ~0x0008u; // clear flush
        if ((val & 32) != 0) DpStatus |= 0x0008u;  // set flush
    }

    void RunCommands()
    {
        if (Bus == null) return;
        uint cur = DpStart;
        uint end = DpEnd;

        while (cur < end)
        {
            ulong cmd = ReadCmd64(cur);
            int op = (int)(cmd >> 56) & 0x3F;
            int cmdLen = GetCmdLength(op);

            ProcessCommand(op, cmd, cur);
            cur += (uint)(cmdLen * 8);
        }

        DpCurrent = cur;
    }

    ulong ReadCmd64(uint addr)
    {
        if (Bus == null) return 0;
        if ((DpStatus & 1) != 0) // XBUS (read from RSP DMEM)
        {
            addr &= 0xFFF;
            return ((ulong)BinaryPrimitives.ReadUInt32BigEndian(Bus.Rsp.Dmem.AsSpan((int)addr)) << 32) |
                   BinaryPrimitives.ReadUInt32BigEndian(Bus.Rsp.Dmem.AsSpan((int)(addr + 4)));
        }
        else
        {
            addr &= 0x00FF_FFFF;
            if (addr + 7 < (uint)Bus.Rdram.Length)
            {
                return ((ulong)BinaryPrimitives.ReadUInt32BigEndian(Bus.Rdram.AsSpan((int)addr)) << 32) |
                       BinaryPrimitives.ReadUInt32BigEndian(Bus.Rdram.AsSpan((int)(addr + 4)));
            }
            return 0;
        }
    }

    static int GetCmdLength(int op) => op switch
    {
        0x08 => 4,  // Tri (edge only)
        0x09 => 6,  // Tri + Z
        0x0A => 12, // Tri + Tex
        0x0B => 14, // Tri + Tex + Z
        0x0C => 12, // Tri + Shade
        0x0D => 14, // Tri + Shade + Z
        0x0E => 20, // Tri + Shade + Tex
        0x0F => 22, // Tri + Shade + Tex + Z
        0x24 or 0x25 => 2, // TexRect, TexRectFlip
        _ => 1,
    };

    public void ProcessCommand(int op, ulong cmd, uint addr)
    {
        uint w0 = (uint)(cmd >> 32);
        uint w1 = (uint)(cmd);

        switch (op)
        {
            case 0x00: break; // NOP
            case 0x08: ProcessTriangle(cmd, addr, false, false, false); break;
            case 0x09: ProcessTriangle(cmd, addr, false, false, true); break;
            case 0x0A: ProcessTriangle(cmd, addr, false, true, false); break;
            case 0x0B: ProcessTriangle(cmd, addr, false, true, true); break;
            case 0x0C: ProcessTriangle(cmd, addr, true, false, false); break;
            case 0x0D: ProcessTriangle(cmd, addr, true, false, true); break;
            case 0x0E: ProcessTriangle(cmd, addr, true, true, false); break;
            case 0x0F: ProcessTriangle(cmd, addr, true, true, true); break;
            case 0x24: ProcessTexRect(cmd, addr); break;
            case 0x25: ProcessTexRectFlip(cmd, addr); break;
            case 0x26: break; // SyncLoad
            case 0x27: break; // SyncPipe
            case 0x28: break; // SyncTile
            case 0x29: // SyncFull
                Bus?.Mi.SetInterrupt(N64Mi.MI_INTR_DP);
                break;
            case 0x2D: SetScissor(w0, w1); break;
            case 0x2F: SetOtherModes(w0, w1); break;
            case 0x30: LoadTlut(w0, w1); break;
            case 0x32: SetTileSize(w0, w1); break;
            case 0x33: LoadBlock(w0, w1); break;
            case 0x34: LoadTile(w0, w1); break;
            case 0x35: SetTile(w0, w1); break;
            case 0x36: FillRect(w0, w1); break;
            case 0x37: FillColor = w1; break;
            case 0x38: FogColor = w1; break;
            case 0x39: BlendColor = w1; break;
            case 0x3A: PrimColor = w1; break;
            case 0x3B: EnvColor = w1; break;
            case 0x3C: CombineMode = cmd; break;
            case 0x3D: SetTexImage(w0, w1); break;
            case 0x3E: SetZImage(w1); break;
            case 0x3F: SetColorImage(w0, w1); break;
        }
    }

    void SetScissor(uint w0, uint w1)
    {
        ScissorXH = (w0 >> 12) & 0xFFF;
        ScissorYH = w0 & 0xFFF;
        ScissorXL = (w1 >> 12) & 0xFFF;
        ScissorYL = w1 & 0xFFF;
    }

    void SetOtherModes(uint w0, uint w1)
    {
        OtherModeH = w0 & 0x00FF_FFFF;
        OtherModeL = w1;
    }

    void SetTile(uint w0, uint w1)
    {
        int idx = (int)((w1 >> 24) & 7);
        ref var t = ref Tiles[idx];
        t.Format = (w0 >> 21) & 7;
        t.Size = (w0 >> 19) & 3;
        t.Line = (w0 >> 9) & 0x1FF;
        t.Addr = (w0 & 0x1FF) << 3;
        t.Palette = (w1 >> 20) & 0xF;
        t.Ct = (w1 >> 19) & 1;
        t.Mt = (w1 >> 18) & 1;
        t.MaskT = (w1 >> 14) & 0xF;
        t.ShiftT = (w1 >> 10) & 0xF;
        t.Cs = (w1 >> 9) & 1;
        t.Ms = (w1 >> 8) & 1;
        t.MaskS = (w1 >> 4) & 0xF;
        t.ShiftS = w1 & 0xF;
    }

    void SetTileSize(uint w0, uint w1)
    {
        int idx = (int)((w1 >> 24) & 7);
        ref var t = ref Tiles[idx];
        t.SL = (w0 >> 12) & 0xFFF;
        t.TL = w0 & 0xFFF;
        t.SH = (w1 >> 12) & 0xFFF;
        t.TH = w1 & 0xFFF;
    }

    void SetTexImage(uint w0, uint w1)
    {
        TexImageFormat = (w0 >> 21) & 7;
        TexImageSize = (w0 >> 19) & 3;
        TexImageWidth = (w0 & 0x3FF) + 1;
        TexImageAddr = w1 & 0x03FF_FFFF;
    }

    void SetZImage(uint w1)
    {
        ZImageAddr = w1 & 0x03FF_FFFF;
    }

    void SetColorImage(uint w0, uint w1)
    {
        ColorImageFormat = (w0 >> 21) & 7;
        ColorImageSize = (w0 >> 19) & 3;
        ColorImageWidth = (w0 & 0x3FF) + 1;
        ColorImageAddr = w1 & 0x03FF_FFFF;
    }

    void LoadBlock(uint w0, uint w1)
    {
        if (Bus == null) return;
        int tile = (int)((w1 >> 24) & 7);
        uint sl = (w0 >> 12) & 0xFFF;
        uint tl = w0 & 0xFFF;
        uint sh = (w1 >> 12) & 0xFFF;
        uint dxt = w1 & 0xFFF;

        uint texelCount = sh + 1;
        uint tmemAddr = Tiles[tile].Addr;
        uint texSize = Tiles[tile].Size;
        uint dramAddr = TexImageAddr;

        int bytes;
        switch (texSize)
        {
            case 0: bytes = (int)((texelCount + 1) / 2); break;
            case 1: bytes = (int)texelCount; break;
            case 2: bytes = (int)(texelCount * 2); break;
            case 3: bytes = (int)(texelCount * 4); break;
            default: bytes = (int)texelCount; break;
        }

        for (int i = 0; i < bytes && tmemAddr + i < 4096; i++)
        {
            uint ra = dramAddr + (uint)i;
            Tmem[tmemAddr + i] = ra < (uint)Bus.Rdram.Length ? Bus.Rdram[ra] : (byte)0;
        }
    }

    void LoadTile(uint w0, uint w1)
    {
        if (Bus == null) return;
        int tile = (int)((w1 >> 24) & 7);
        uint sl = (w0 >> 12) & 0xFFF;
        uint tl = w0 & 0xFFF;
        uint sh = (w1 >> 12) & 0xFFF;
        uint th = w1 & 0xFFF;

        ref var t = ref Tiles[tile];
        int pixelSl = (int)(sl >> 2);
        int pixelSh = (int)(sh >> 2);
        int pixelTl = (int)(tl >> 2);
        int pixelTh = (int)(th >> 2);

        int texWidth = (int)TexImageWidth;
        int bpp = 1 << (int)TexImageSize;
        int bytesPerPixel = Math.Max(1, bpp / 8);
        int tmemLineBytes = (int)(t.Line << 3);
        if (tmemLineBytes == 0) tmemLineBytes = (pixelSh - pixelSl + 1) * bytesPerPixel;

        for (int row = pixelTl; row <= pixelTh; row++)
        {
            for (int col = pixelSl; col <= pixelSh; col++)
            {
                int srcOff = (row * texWidth + col) * bytesPerPixel;
                int dstOff = (int)t.Addr + (row - pixelTl) * tmemLineBytes + (col - pixelSl) * bytesPerPixel;

                for (int b = 0; b < bytesPerPixel && dstOff + b < 4096; b++)
                {
                    uint ra = TexImageAddr + (uint)(srcOff + b);
                    Tmem[dstOff + b] = ra < (uint)Bus.Rdram.Length ? Bus.Rdram[ra] : (byte)0;
                }
            }
        }
    }

    void LoadTlut(uint w0, uint w1)
    {
        if (Bus == null) return;
        int tile = (int)((w1 >> 24) & 7);
        uint sl = (w0 >> 12) & 0xFFF;
        uint sh = (w1 >> 12) & 0xFFF;
        int count = (int)((sh >> 2) - (sl >> 2) + 1);

        uint tmemAddr = Tiles[tile].Addr;
        uint dramAddr = TexImageAddr + (sl >> 2) * 2;

        for (int i = 0; i < count; i++)
        {
            int dst = (int)(tmemAddr + i * 8) & 0xFFF; // TLUT entries are spaced 8 bytes apart in TMEM upper half
            uint src = dramAddr + (uint)(i * 2);
            if (dst + 1 < 4096 && src + 1 < (uint)Bus.Rdram.Length)
            {
                Tmem[dst] = Bus.Rdram[src];
                Tmem[dst + 1] = Bus.Rdram[src + 1];
            }
        }
    }

    public void FillRect(uint w0, uint w1)
    {
        if (Bus == null) return;
        int xh = (int)((w1 >> 12) & 0xFFF) >> 2;
        int yh = (int)(w1 & 0xFFF) >> 2;
        int xl = (int)((w0 >> 12) & 0xFFF) >> 2;
        int yl = (int)(w0 & 0xFFF) >> 2;

        int sxh = (int)(ScissorXH >> 2);
        int syh = (int)(ScissorYH >> 2);
        int sxl = (int)(ScissorXL >> 2);
        int syl = (int)(ScissorYL >> 2);

        xh = Math.Max(xh, sxh);
        yh = Math.Max(yh, syh);
        xl = Math.Min(xl, sxl);
        yl = Math.Min(yl, syl);

        int fbWidth = (int)ColorImageWidth;
        if (fbWidth == 0) return;

        if (ColorImageSize == 2) // 16-bit
        {
            ushort fill16a = (ushort)(FillColor >> 16);
            ushort fill16b = (ushort)(FillColor & 0xFFFF);

            for (int y = yh; y < yl; y++)
            {
                for (int x = xh; x < xl; x++)
                {
                    uint pxAddr = ColorImageAddr + (uint)(y * fbWidth + x) * 2;
                    ushort fc = (x & 1) == 0 ? fill16a : fill16b;
                    WriteFb16(pxAddr, fc);
                }
            }
        }
        else if (ColorImageSize == 3) // 32-bit
        {
            for (int y = yh; y < yl; y++)
            {
                for (int x = xh; x < xl; x++)
                {
                    uint pxAddr = ColorImageAddr + (uint)(y * fbWidth + x) * 4;
                    WriteFb32(pxAddr, FillColor);
                }
            }
        }
    }

    void ProcessTriangle(ulong cmd, uint addr, bool shade, bool tex, bool zbuf)
    {
        // RDP edge-walking triangle: complex multi-word command
        // Minimal stub - full triangle rasterization handled by RSP HLE path
    }

    void ProcessTexRect(ulong cmd, uint addr)
    {
        if (Bus == null) return;
        uint w0 = (uint)(cmd >> 32);
        uint w1 = (uint)cmd;

        int xl = (int)((w0 >> 12) & 0xFFF) >> 2;
        int yl = (int)(w0 & 0xFFF) >> 2;
        int tile = (int)((w0 >> 24) & 7);
        int xh = (int)((w1 >> 12) & 0xFFF) >> 2;
        int yh = (int)(w1 & 0xFFF) >> 2;

        ulong cmd2 = ReadCmd64(addr + 8);
        int s = (int)((cmd2 >> 48) & 0xFFFF);
        int t2 = (int)((cmd2 >> 32) & 0xFFFF);
        int dsdx = (int)((cmd2 >> 16) & 0xFFFF);
        int dtdy = (int)(cmd2 & 0xFFFF);

        if (s > 0x7FFF) s -= 0x10000;
        if (t2 > 0x7FFF) t2 -= 0x10000;
        if (dsdx > 0x7FFF) dsdx -= 0x10000;
        if (dtdy > 0x7FFF) dtdy -= 0x10000;

        int fbWidth = (int)ColorImageWidth;
        if (fbWidth == 0) return;

        ref var ti = ref Tiles[tile];

        int cy = t2;
        for (int y = yh; y < yl; y++)
        {
            int cx = s;
            for (int x = xh; x < xl; x++)
            {
                uint color = SampleTexel(ref ti, cx >> 5, cy >> 5);
                uint pxAddr = ColorImageAddr + (uint)(y * fbWidth + x) * 2;
                WriteFb16(pxAddr, Rgba32ToRgba16(color));
                cx += dsdx;
            }
            cy += dtdy;
        }
    }

    void ProcessTexRectFlip(ulong cmd, uint addr)
    {
        ProcessTexRect(cmd, addr); // simplified
    }

    public uint SampleTexel(ref Tile ti, int s, int t)
    {
        if (ti.MaskS != 0) s &= (1 << (int)ti.MaskS) - 1;
        if (ti.MaskT != 0) t &= (1 << (int)ti.MaskT) - 1;

        uint tmemAddr = ti.Addr;
        int line = (int)(ti.Line << 3);
        if (line == 0) line = 1;

        switch (ti.Size)
        {
            case 0: // 4-bit
            {
                int byteOff = (int)(tmemAddr + t * line + s / 2);
                if ((uint)byteOff >= 4096) return 0;
                byte b = Tmem[byteOff];
                int nibble = (s & 1) == 0 ? (b >> 4) : (b & 0xF);

                if (ti.Format == 2 || ti.Format == 3) // CI or IA
                {
                    int clutIdx = (int)((ti.Palette << 4) | nibble);
                    return SampleClut(clutIdx);
                }
                byte i4 = (byte)((nibble << 4) | nibble);
                return (uint)(i4 << 24 | i4 << 16 | i4 << 8 | 0xFF);
            }
            case 1: // 8-bit
            {
                int byteOff = (int)(tmemAddr + t * line + s);
                if ((uint)byteOff >= 4096) return 0;
                byte b = Tmem[byteOff];

                if (ti.Format == 2) // CI8
                    return SampleClut(b);
                if (ti.Format == 3) // IA8
                {
                    byte i = (byte)((b >> 4) << 4 | (b >> 4));
                    byte a = (byte)((b & 0xF) << 4 | (b & 0xF));
                    return (uint)(i << 24 | i << 16 | i << 8 | a);
                }
                return (uint)(b << 24 | b << 16 | b << 8 | 0xFF);
            }
            case 2: // 16-bit
            {
                int byteOff = (int)(tmemAddr + t * line + s * 2);
                if ((uint)(byteOff + 1) >= 4096) return 0;
                ushort texel = (ushort)(Tmem[byteOff] << 8 | Tmem[byteOff + 1]);

                if (ti.Format == 0) // RGBA16
                    return Rgba16ToRgba32(texel);
                if (ti.Format == 3) // IA16
                {
                    byte i = (byte)(texel >> 8);
                    byte a = (byte)(texel & 0xFF);
                    return (uint)(i << 24 | i << 16 | i << 8 | a);
                }
                return Rgba16ToRgba32(texel);
            }
            case 3: // 32-bit
            {
                int byteOff = (int)(tmemAddr + t * line + s * 4);
                if ((uint)(byteOff + 3) >= 4096) return 0;
                return (uint)((uint)Tmem[byteOff] << 24 | (uint)Tmem[byteOff + 1] << 16 |
                              (uint)Tmem[byteOff + 2] << 8 | (uint)Tmem[byteOff + 3]);
            }
        }
        return 0xFFFFFFFF;
    }

    uint SampleClut(int idx)
    {
        int off = 0x800 + idx * 8; // TLUT in upper half of TMEM, 8-byte spacing
        if (off + 1 >= 4096) return 0;
        ushort c = (ushort)(Tmem[off] << 8 | Tmem[off + 1]);
        return Rgba16ToRgba32(c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Rgba16ToRgba32(ushort c)
    {
        uint r = (uint)((c >> 11) & 0x1F) * 255 / 31;
        uint g = (uint)((c >> 6) & 0x1F) * 255 / 31;
        uint b = (uint)((c >> 1) & 0x1F) * 255 / 31;
        uint a = (c & 1) != 0 ? 255u : 0u;
        return (r << 24) | (g << 16) | (b << 8) | a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ushort Rgba32ToRgba16(uint c)
    {
        uint r = (c >> 24) & 0xFF;
        uint g = (c >> 16) & 0xFF;
        uint b = (c >> 8) & 0xFF;
        uint a = c & 0xFF;
        return (ushort)(((r / 8) << 11) | ((g / 8) << 6) | ((b / 8) << 1) | (a > 0 ? 1u : 0u));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void WriteFb16(uint addr, ushort val)
    {
        if (Bus == null) return;
        addr &= 0x00FF_FFFF;
        if (addr + 1 < (uint)Bus.Rdram.Length)
        {
            Bus.Rdram[addr] = (byte)(val >> 8);
            Bus.Rdram[addr + 1] = (byte)(val);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void WriteFb32(uint addr, uint val)
    {
        if (Bus == null) return;
        addr &= 0x00FF_FFFF;
        if (addr + 3 < (uint)Bus.Rdram.Length)
            BinaryPrimitives.WriteUInt32BigEndian(Bus.Rdram.AsSpan((int)addr), val);
    }
}
