using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RlColor = Raylib_CsLo.Color;

namespace PsxEmu;

/// <summary>
/// PSX GPU – software rasterizer into VRAM.
/// After the DMA fix, a typical frame processes only ~100-200 draw commands.
/// Software rendering is plenty fast for this workload.
/// </summary>
class PsxGpu
{
    public readonly ushort[] Vram = new ushort[1024 * 512];

    // Display registers (GP1)
    public int DispStartX, DispStartY;
    public int DispWidth = 256, DispHeight = 240;
    bool _displayEnabled;

    // Drawing state
    int _drawX1, _drawY1, _drawX2 = 1023, _drawY2 = 511;
    int _offX, _offY;
    int _texPageX, _texPageY, _texDepth, _semiTrans;

    // GP0 FIFO
    readonly uint[] _fifo = new uint[32];
    int _fifoLen;

    // CPU→VRAM transfer
    bool _xferIn;
    int _xferDstX, _xferDstY, _xferW, _xferH, _xferCount;

    // VRAM→CPU transfer
    bool _xferOut;
    int _xferSrcX, _xferSrcY;

    public long Cycle;
    public int Gp0Count { get; private set; }
    public int Gp1Count { get; private set; }
    public int XferPixels { get; private set; }
    public int VramWriteCount { get; private set; }

    // Thread-safe display snapshot
    readonly ushort[] _snapVram = new ushort[1024 * 512];
    readonly object   _snapLock = new();
    int _snapDx, _snapDy, _snapDw, _snapDh;

    // Pre-computed 5-bit to 8-bit LUT (avoids per-pixel division)
    static readonly byte[] _5to8 = Build5to8Lut();
    static byte[] Build5to8Lut()
    {
        var lut = new byte[32];
        for (int i = 0; i < 32; i++) lut[i] = (byte)((i * 255 + 15) / 31);
        return lut;
    }

    // ── External API ────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadStat()
    {
        bool vblank = (Cycle % 560000) > 500000;
        return 0x1C00_2000
             | (_displayEnabled ? 0u : (1u << 23))
             | (vblank ? 0u : (1u << 31));
    }

    public uint ReadData()
    {
        if (!_xferOut) return 0;
        uint a = (uint)((_xferSrcY * 1024) + _xferSrcX);
        ushort lo = Vram[a & (1024 * 512 - 1)];
        ushort hi = Vram[(a + 1) & (1024 * 512 - 1)];
        _xferSrcX += 2;
        return ((uint)hi << 16) | lo;
    }

    // ── VBlank snapshot ─────────────────────────────────────────────────────

    public void VBlankSnapshot()
    {
        lock (_snapLock)
        {
            Buffer.BlockCopy(Vram, 0, _snapVram, 0, Vram.Length * 2);
            _snapDx = DispStartX; _snapDy = DispStartY;
            _snapDw = DispWidth;  _snapDh = DispHeight;
        }
    }

    /// <summary>
    /// Called by the main (UI) thread to fill a pixel buffer from the VRAM snapshot.
    /// Uses unsafe pointers and a pre-computed LUT for zero-division color conversion.
    /// </summary>
    public unsafe void SnapshotDisplay(RlColor[] pixels, int outW, int outH)
    {
        lock (_snapLock)
        {
            int dx = _snapDx, dy = _snapDy, dw = _snapDw, dh = _snapDh;
            fixed (ushort* vram = _snapVram)
            fixed (RlColor* px = pixels)
            fixed (byte* lut = _5to8)
            {
                for (int y = 0; y < outH; y++)
                {
                    RlColor* row = px + y * outW;
                    if (y >= dh)
                    {
                        for (int x = 0; x < outW; x++)
                            row[x] = new RlColor { a = 255 };
                        continue;
                    }
                    int srcY = ((dy + y) & 511) * 1024;
                    int copyW = Math.Min(dw, outW);
                    for (int x = 0; x < copyW; x++)
                    {
                        ushort c = vram[srcY + ((dx + x) & 1023)];
                        row[x].r = lut[c & 0x1F];
                        row[x].g = lut[(c >> 5) & 0x1F];
                        row[x].b = lut[(c >> 10) & 0x1F];
                        row[x].a = 255;
                    }
                    for (int x = copyW; x < outW; x++)
                        row[x] = new RlColor { a = 255 };
                }
            }
        }
    }

    // ── GP1 – display control ───────────────────────────────────────────────

    public void WriteGP1(uint val)
    {
        Gp1Count++;
        uint cmd = val >> 24;
        switch (cmd)
        {
            case 0x00:
                _fifoLen = 0; _xferIn = _xferOut = false;
                DispStartX = DispStartY = 0;
                DispWidth = 256; DispHeight = 240;
                _displayEnabled = false;
                _texPageX = _texPageY = _texDepth = 0;
                Array.Clear(Vram);
                break;
            case 0x01: _fifoLen = 0; _xferIn = false; break;
            case 0x03: _displayEnabled = (val & 1) == 0; break;
            case 0x04: break;
            case 0x05:
                DispStartX = (int)(val & 0x3FE);
                DispStartY = (int)((val >> 10) & 0x1FE);
                break;
            case 0x06: break;
            case 0x07: break;
            case 0x08:
                DispWidth = (val & 3) switch { 0 => 256, 1 => 320, 2 => 512, 3 => 640, _ => 256 };
                if ((val & 0x40) != 0) DispWidth = 368;
                DispHeight = (val & 4) != 0 ? 480 : 240;
                _displayEnabled = true;
                break;
            case 0x10: break;
        }
    }

    // ── GP0 – drawing commands ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteGP0(uint val)
    {
        Gp0Count++;

        if (_xferIn)
        {
            WriteVramPixel((ushort)(val & 0xFFFF));
            WriteVramPixel((ushort)(val >> 16));
            return;
        }

        _fifo[_fifoLen++] = val;
        TryExecute();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void WriteVramPixel(ushort pix)
    {
        if (_xferCount >= _xferW * _xferH) return;
        int x = _xferDstX + (_xferCount % _xferW);
        int y = _xferDstY + (_xferCount / _xferW);
        Vram[((y & 511) * 1024) + (x & 1023)] = pix;
        _xferCount++;
        if (_xferCount >= _xferW * _xferH)
        {
            XferPixels += _xferCount;
            VramWriteCount++;
            _xferIn = false;
        }
    }

    void TryExecute()
    {
        if (_fifoLen == 0) return;
        uint cmd = _fifo[0] >> 24;
        int needed = CmdWords(cmd);
        if (needed < 0) { _fifoLen = 0; return; }
        if (_fifoLen < needed) return;
        DoCommand(cmd);
        _fifoLen = 0;
    }

    static int CmdWords(uint cmd) => cmd switch
    {
        0x00 or 0x01 => 1,
        0x02 => 3,
        0x20 or 0x21 or 0x22 or 0x23 => 4,
        0x24 or 0x25 or 0x26 or 0x27 => 7,
        0x28 or 0x29 or 0x2A or 0x2B => 5,
        0x2C or 0x2D or 0x2E or 0x2F => 9,
        0x30 or 0x31 or 0x32 or 0x33 => 6,
        0x34 or 0x35 or 0x36 or 0x37 => 9,
        0x38 or 0x39 or 0x3A or 0x3B => 8,
        0x3C or 0x3D or 0x3E or 0x3F => 12,
        0x40 or 0x41 or 0x42 or 0x43 => 3,
        0x48 or 0x4A or 0x4C or 0x4E => 3,
        0x50 or 0x51 or 0x52 or 0x53 => 4,
        0x60 or 0x61 or 0x62 or 0x63 => 3,
        0x64 or 0x65 or 0x66 or 0x67 => 4,
        0x68 or 0x69 or 0x6A or 0x6B => 2,
        0x70 or 0x71 or 0x72 or 0x73 => 2,
        0x74 or 0x75 or 0x76 or 0x77 => 3,
        0x78 or 0x79 or 0x7A or 0x7B => 2,
        0x7C or 0x7D or 0x7E or 0x7F => 3,
        0x80 => 4,
        0xA0 => 3,
        0xC0 => 3,
        >= 0xE1 and <= 0xE6 => 1,
        _ => -1,
    };

    void DoCommand(uint cmd)
    {
        uint col = _fifo[0] & 0xFF_FFFF;
        uint raw0 = _fifo[0];

        // GPU command logging removed for performance

        switch (cmd)
        {
            case 0x02: // Fill rect
            {
                int x = (int)(_fifo[1] & 0x3F0);
                int y = (int)((_fifo[1] >> 16) & 0x1FF);
                int w = (int)((_fifo[2] & 0x3FF) + 0xF) & ~0xF;
                int h = (int)((_fifo[2] >> 16) & 0x1FF);
                ushort c = To555(col);
                for (int py = y; py < y + h && py < 512; py++)
                    for (int px = x; px < x + w && px < 1024; px++)
                        Vram[(py & 511) * 1024 + (px & 1023)] = c;
                break;
            }

            case 0x20: case 0x21: case 0x22: case 0x23:
                DrawTriFlat(To555(col), _fifo[1], _fifo[2], _fifo[3]); break;
            case 0x24: case 0x25: case 0x26: case 0x27:
                SetTexPageFromAttr((int)(_fifo[4] >> 16));
                DrawTriTex(_fifo[1], _fifo[2], _fifo[3], _fifo[4], _fifo[5], _fifo[6], (int)(_fifo[2] >> 16)); break;
            case 0x28: case 0x29: case 0x2A: case 0x2B:
                DrawTriFlat(To555(col), _fifo[1], _fifo[2], _fifo[3]);
                DrawTriFlat(To555(col), _fifo[2], _fifo[3], _fifo[4]); break;
            case 0x2C: case 0x2D: case 0x2E: case 0x2F:
                SetTexPageFromAttr((int)(_fifo[4] >> 16));
                DrawTriTex(_fifo[1], _fifo[2], _fifo[3], _fifo[4], _fifo[5], _fifo[6], (int)(_fifo[2] >> 16));
                DrawTriTex(_fifo[3], _fifo[4], _fifo[5], _fifo[6], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16)); break;
            case 0x30: case 0x31: case 0x32: case 0x33:
                DrawTriFlat(To555(AvgCol(_fifo[0], _fifo[2], _fifo[4])), _fifo[1], _fifo[3], _fifo[5]); break;
            case 0x34: case 0x35: case 0x36: case 0x37:
                SetTexPageFromAttr((int)(_fifo[5] >> 16));
                DrawTriTex(_fifo[1], _fifo[2], _fifo[4], _fifo[5], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16)); break;
            case 0x38: case 0x39: case 0x3A: case 0x3B:
                DrawTriFlat(To555(AvgCol(_fifo[0], _fifo[2], _fifo[4])), _fifo[1], _fifo[3], _fifo[5]);
                DrawTriFlat(To555(AvgCol(_fifo[2], _fifo[4], _fifo[6])), _fifo[3], _fifo[5], _fifo[7]); break;
            case 0x3C: case 0x3D: case 0x3E: case 0x3F:
                SetTexPageFromAttr((int)(_fifo[5] >> 16));
                DrawTriTex(_fifo[1], _fifo[2], _fifo[4], _fifo[5], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16));
                DrawTriTex(_fifo[4], _fifo[5], _fifo[7], _fifo[8], _fifo[10], _fifo[11], (int)(_fifo[2] >> 16)); break;
            case 0x60: case 0x61: case 0x62: case 0x63:
                DrawRectFlat(To555(col), _fifo[1], _fifo[2]); break;
            case 0x64: case 0x65: case 0x66: case 0x67:
                DrawRectTex(_fifo[1], _fifo[2], _fifo[3], (int)(_fifo[2] >> 16)); break;
            case 0x68: case 0x69: case 0x6A: case 0x6B:
                DrawRectFlat(To555(col), _fifo[1], 0x0001_0001); break;
            case 0x70: case 0x71: case 0x72: case 0x73:
                DrawRectFlat(To555(col), _fifo[1], 0x0008_0008); break;
            case 0x74: case 0x75: case 0x76: case 0x77:
                DrawRectTex(_fifo[1], _fifo[2], 0x0008_0008, (int)(_fifo[2] >> 16)); break;
            case 0x78: case 0x79: case 0x7A: case 0x7B:
                DrawRectFlat(To555(col), _fifo[1], 0x0010_0010); break;
            case 0x7C: case 0x7D: case 0x7E: case 0x7F:
                DrawRectTex(_fifo[1], _fifo[2], 0x0010_0010, (int)(_fifo[2] >> 16)); break;
            case 0x80:
                CopyVV(_fifo[1], _fifo[2], _fifo[3]); break;
            case 0xA0:
            {
                int x = (int)(_fifo[1] & 0x3FF), y = (int)((_fifo[1] >> 16) & 0x1FF);
                int w = (int)(((_fifo[2] & 0xFFFF) - 1) & 0x3FF) + 1;
                int h = (int)(((_fifo[2] >> 16) - 1) & 0x1FF) + 1;
                _xferDstX = x; _xferDstY = y; _xferW = w; _xferH = h; _xferCount = 0;
                _xferIn = true;
                break;
            }
            case 0xC0:
                _xferSrcX = (int)(_fifo[1] & 0x3FF);
                _xferSrcY = (int)((_fifo[1] >> 16) & 0x1FF);
                _xferOut = true;
                break;
            case 0xE1:
            {
                uint d = raw0 & 0x00FFFFFF;
                _texPageX  = (int)((d & 0x0F) * 64);
                _texPageY  = (int)(((d >> 4) & 1) * 256);
                _semiTrans = (int)((d >> 5) & 3);
                _texDepth  = (int)((d >> 7) & 3);
                break;
            }
            case 0xE2: break;
            case 0xE3: _drawX1 = (int)(raw0 & 0x3FF); _drawY1 = (int)((raw0 >> 10) & 0x1FF); break;
            case 0xE4: _drawX2 = (int)(raw0 & 0x3FF); _drawY2 = (int)((raw0 >> 10) & 0x1FF); break;
            case 0xE5:
                _offX = Sext11((int)(raw0 & 0x7FF));
                _offY = Sext11((int)((raw0 >> 11) & 0x7FF));
                break;
            case 0xE6: break;
        }
    }

    // ── Drawing helpers ─────────────────────────────────────────────────────

    void SetTexPageFromAttr(int a)
    {
        _texPageX  = (a & 0x0F) * 64;
        _texPageY  = ((a >> 4) & 1) * 256;
        _semiTrans = (a >> 5) & 3;
        _texDepth  = (a >> 7) & 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Put(int x, int y, ushort c)
    {
        if (x >= _drawX1 && x <= _drawX2 && y >= _drawY1 && y <= _drawY2)
            Vram[(y & 511) * 1024 + (x & 1023)] = c;
    }

    void DrawRectFlat(ushort c, uint pos, uint size)
    {
        int x0 = S11(pos & 0xFFFF) + _offX;
        int y0 = S11(pos >> 16) + _offY;
        int w = (int)(size & 0xFFFF), h = (int)(size >> 16);
        int xStart = Math.Max(x0, _drawX1);
        int xEnd   = Math.Min(x0 + w, _drawX2 + 1);
        int yStart = Math.Max(y0, _drawY1);
        int yEnd   = Math.Min(y0 + h, _drawY2 + 1);
        for (int py = yStart; py < yEnd; py++)
            for (int px = xStart; px < xEnd; px++)
                Vram[(py & 511) * 1024 + (px & 1023)] = c;
    }

    void DrawRectTex(uint pos, uint uvclut, uint size, int clutAttr)
    {
        int x0 = S11(pos & 0xFFFF) + _offX;
        int y0 = S11(pos >> 16) + _offY;
        int w = (int)(size & 0xFFFF), h = (int)(size >> 16);
        byte u0 = (byte)(uvclut & 0xFF), v0 = (byte)((uvclut >> 8) & 0xFF);
        int cx = ((clutAttr & 0x3F) << 4) & 1023;
        int cy = ((clutAttr >> 6) & 0x1FF) & 511;
        int xStart = Math.Max(x0, _drawX1);
        int xEnd   = Math.Min(x0 + w, _drawX2 + 1);
        int yStart = Math.Max(y0, _drawY1);
        int yEnd   = Math.Min(y0 + h, _drawY2 + 1);
        for (int py = yStart; py < yEnd; py++)
            for (int px = xStart; px < xEnd; px++)
            {
                ushort texel = SampleTexel(u0 + (px - x0), v0 + (py - y0), cx, cy);
                if (texel != 0) Vram[(py & 511) * 1024 + (px & 1023)] = texel;
            }
    }

    void DrawTriFlat(ushort c, uint v0w, uint v1w, uint v2w)
    {
        int x0 = S11(v0w & 0xFFFF) + _offX, y0 = S11(v0w >> 16) + _offY;
        int x1 = S11(v1w & 0xFFFF) + _offX, y1 = S11(v1w >> 16) + _offY;
        int x2 = S11(v2w & 0xFFFF) + _offX, y2 = S11(v2w >> 16) + _offY;

        int area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (area == 0) return;

        if (area < 0) {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
            area = -area;
        }

        if ((Math.Max(x0, Math.Max(x1, x2)) - Math.Min(x0, Math.Min(x1, x2))) > 1024) return;
        if ((Math.Max(y0, Math.Max(y1, y2)) - Math.Min(y0, Math.Min(y1, y2))) > 512) return;

        int minX = Math.Max(_drawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(_drawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(_drawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(_drawY2, Math.Max(y0, Math.Max(y1, y2)));

        int A12 = y1 - y2, B12 = x2 - x1;
        int A20 = y2 - y0, B20 = x0 - x2;
        int A01 = y0 - y1, B01 = x1 - x0;

        int w0_row = A12 * (minX - x1) + B12 * (minY - y1);
        int w1_row = A20 * (minX - x2) + B20 * (minY - y2);
        int w2_row = A01 * (minX - x0) + B01 * (minY - y0);

        for (int py = minY; py <= maxY; py++)
        {
            int w0 = w0_row, w1 = w1_row, w2 = w2_row;
            for (int px = minX; px <= maxX; px++)
            {
                if ((w0 | w1 | w2) >= 0)
                    Vram[(py & 511) * 1024 + (px & 1023)] = c;
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }
    }

    void DrawTriTex(uint v0w, uint uv0, uint v1w, uint uv1, uint v2w, uint uv2, int clutAttr)
    {
        int x0 = S11(v0w & 0xFFFF) + _offX, y0 = S11(v0w >> 16) + _offY;
        int x1 = S11(v1w & 0xFFFF) + _offX, y1 = S11(v1w >> 16) + _offY;
        int x2 = S11(v2w & 0xFFFF) + _offX, y2 = S11(v2w >> 16) + _offY;

        byte u0b = (byte)(uv0 & 0xFF), v0b = (byte)((uv0 >> 8) & 0xFF);
        byte u1b = (byte)(uv1 & 0xFF), v1b = (byte)((uv1 >> 8) & 0xFF);
        byte u2b = (byte)(uv2 & 0xFF), v2b = (byte)((uv2 >> 8) & 0xFF);
        int cx = ((clutAttr & 0x3F) << 4) & 1023;
        int cy = ((clutAttr >> 6) & 0x1FF) & 511;

        int area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (area == 0) return;

        if (area < 0) {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
            (u1b, u2b) = (u2b, u1b);
            (v1b, v2b) = (v2b, v1b);
            area = -area;
        }

        if ((Math.Max(x0, Math.Max(x1, x2)) - Math.Min(x0, Math.Min(x1, x2))) > 1024) return;
        if ((Math.Max(y0, Math.Max(y1, y2)) - Math.Min(y0, Math.Min(y1, y2))) > 512) return;

        int minX = Math.Max(_drawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(_drawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(_drawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(_drawY2, Math.Max(y0, Math.Max(y1, y2)));

        int A12 = y1 - y2, B12 = x2 - x1;
        int A20 = y2 - y0, B20 = x0 - x2;
        int A01 = y0 - y1, B01 = x1 - x0;

        int w0_row = A12 * (minX - x1) + B12 * (minY - y1);
        int w1_row = A20 * (minX - x2) + B20 * (minY - y2);
        int w2_row = A01 * (minX - x0) + B01 * (minY - y0);

        for (int py = minY; py <= maxY; py++)
        {
            int w0 = w0_row, w1 = w1_row, w2 = w2_row;
            for (int px = minX; px <= maxX; px++)
            {
                if ((w0 | w1 | w2) >= 0)
                {
                    int u = (u0b * w0 + u1b * w1 + u2b * w2) / area;
                    int v = (v0b * w0 + v1b * w1 + v2b * w2) / area;
                    ushort texel = SampleTexel(u, v, cx, cy);
                    if (texel != 0)
                        Vram[(py & 511) * 1024 + (px & 1023)] = texel;
                }
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ushort SampleTexel(int u, int v, int clutX, int clutY)
    {
        u &= 0xFF; v &= 0xFF;
        int row = (_texPageY + v) & 511;

        if (_texDepth == 0) // 4bpp
        {
            int col = (_texPageX + u / 4) & 1023;
            ushort word = Vram[row * 1024 + col];
            int nib = (word >> ((u & 3) * 4)) & 0xF;
            return Vram[(clutY & 511) * 1024 + ((clutX + nib) & 1023)];
        }
        if (_texDepth == 1) // 8bpp
        {
            int col = (_texPageX + u / 2) & 1023;
            ushort word = Vram[row * 1024 + col];
            int idx = (u & 1) == 0 ? (word & 0xFF) : (word >> 8);
            return Vram[(clutY & 511) * 1024 + ((clutX + idx) & 1023)];
        }
        // 15bpp direct
        return Vram[row * 1024 + ((_texPageX + u) & 1023)];
    }

    void CopyVV(uint src, uint dst, uint size)
    {
        int sx = (int)(src & 0x3FF), sy = (int)((src >> 16) & 0x1FF);
        int dx = (int)(dst & 0x3FF), dy = (int)((dst >> 16) & 0x1FF);
        int w = (int)(size & 0xFFFF), h = (int)((size >> 16) & 0x1FF);
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
                Vram[((dy + py) & 511) * 1024 + ((dx + px) & 1023)] =
                    Vram[((sy + py) & 511) * 1024 + ((sx + px) & 1023)];
    }

    // ── Utility ─────────────────────────────────────────────────────────────

    static ushort To555(uint rgb) =>
        (ushort)((((byte)rgb >> 3) & 0x1F) | ((((byte)(rgb >> 8) >> 3) & 0x1F) << 5) | ((((byte)(rgb >> 16) >> 3) & 0x1F) << 10));

    static uint AvgCol(uint a, uint b, uint c)
    {
        byte r = (byte)(((a & 0xFF) + (b & 0xFF) + (c & 0xFF)) / 3);
        byte g = (byte)((((a >> 8) & 0xFF) + ((b >> 8) & 0xFF) + ((c >> 8) & 0xFF)) / 3);
        byte bl = (byte)((((a >> 16) & 0xFF) + ((b >> 16) & 0xFF) + ((c >> 16) & 0xFF)) / 3);
        return (uint)(r | (g << 8) | (bl << 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short S11(uint n) => (short)(((int)n << 21) >> 21);

    static int Sext11(int v) => (v & 0x400) != 0 ? v | unchecked((int)0xFFFF_F800) : v;
}
