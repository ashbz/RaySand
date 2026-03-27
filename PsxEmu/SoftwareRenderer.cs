using System.Runtime.CompilerServices;
using RlColor = Raylib_CsLo.Color;

namespace PsxEmu;

unsafe class SoftwareRenderer : PsxRendererBase
{
    readonly ushort[] _vram = new ushort[1024 * 512];
    ushort[] _upVram = new ushort[2048 * 1024];

    protected readonly ushort[] _snapVram = new ushort[1024 * 512];
    protected ushort[] _snapUpVram = new ushort[2048 * 1024];
    protected readonly object _snapLock = new();
    protected int _snapDx, _snapDy, _snapDw, _snapDh, _snapScale;

    int _scale = 1;
    public override int Scale { get => _scale; set => _scale = value; }
    public override ushort[] Vram => _vram;
    public override ushort[]? SnapVramData => _snapVram;

    readonly List<PrimInfo> _workPrims = new();
    readonly int[] _workPrimIdBuf = new int[1024 * 512];
    int _curPrimId = -1;

    static readonly uint[] _15to32 = Build15to32Lut();
    static uint[] Build15to32Lut()
    {
        var lut = new uint[32768];
        for (int i = 0; i < 32768; i++)
        {
            byte r = (byte)(((i & 0x1F) * 255 + 15) / 31);
            byte g = (byte)((((i >> 5) & 0x1F) * 255 + 15) / 31);
            byte b = (byte)((((i >> 10) & 0x1F) * 255 + 15) / 31);
            lut[i] = (uint)(r | (g << 8) | (b << 16) | (0xFFu << 24));
        }
        return lut;
    }

    // ── VRAM access ──────────────────────────────────────────────────────────

    public override void ClearVram()
    {
        Array.Clear(_vram);
        if (_scale > 1) Array.Clear(_upVram);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void WritePixel(int x, int y, ushort pix)
    {
        _vram[((y & 511) * 1024) + (x & 1023)] = pix;
        if (_scale > 1)
        {
            int s = _scale, upW = 1024 * s;
            for (int dy = 0; dy < s; dy++)
                for (int dx = 0; dx < s; dx++)
                    _upVram[(((y * s + dy) % (512 * s)) * upW) + ((x * s + dx) % upW)] = pix;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override ushort ReadPixel(int x, int y) =>
        _vram[((y & 511) * 1024) + (x & 1023)];

    public void RebuildUpscaleVram()
    {
        if (_scale <= 1) return;
        int s = _scale, upW = 1024 * s, maxH = 512 * s;
        for (int y = 0; y < 512; y++)
            for (int x = 0; x < 1024; x++)
            {
                ushort c = _vram[y * 1024 + x];
                int bx = x * s, by = y * s;
                for (int dy = 0; dy < s; dy++)
                    for (int dx = 0; dx < s; dx++)
                        _upVram[((by + dy) % maxH) * upW + ((bx + dx) % upW)] = c;
            }
    }

    // ── Flat triangle ────────────────────────────────────────────────────────

    public override void DrawTriFlat(ushort c, uint v0w, uint v1w, uint v2w)
    {
        if (HackRandomColors) c = RandomColor(_workPrims.Count);

        int x0 = S11(v0w & 0xFFFF) + OffX, y0 = S11(v0w >> 16) + OffY;
        int x1 = S11(v1w & 0xFFFF) + OffX, y1 = S11(v1w >> 16) + OffY;
        int x2 = S11(v2w & 0xFFFF) + OffX, y2 = S11(v2w >> 16) + OffY;

        _curPrimId = -1;
        if (TrackPrimitives)
        {
            _curPrimId = _workPrims.Count;
            _workPrims.Add(new PrimInfo { Type = PrimInfo.Kind.TriFlat, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, C0 = c });
        }

        if (HackWireframe)
        {
            DrawLine(c, x0 - OffX, y0 - OffY, x1 - OffX, y1 - OffY);
            DrawLine(c, x1 - OffX, y1 - OffY, x2 - OffX, y2 - OffY);
            DrawLine(c, x2 - OffX, y2 - OffY, x0 - OffX, y0 - OffY);
            return;
        }

        int area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (area == 0) return;

        if (area < 0) {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
            area = -area;
        }

        if ((Math.Max(x0, Math.Max(x1, x2)) - Math.Min(x0, Math.Min(x1, x2))) > 1024) return;
        if ((Math.Max(y0, Math.Max(y1, y2)) - Math.Min(y0, Math.Min(y1, y2))) > 512) return;

        if (_scale > 1) { DrawTriFlatUp(c, x0, y0, x1, y1, x2, y2, area); return; }

        int minX = Math.Max(DrawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(DrawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(DrawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(DrawY2, Math.Max(y0, Math.Max(y1, y2)));

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
                    int idx = (py & 511) * 1024 + (px & 1023);
                    _vram[idx] = c;
                    if (_curPrimId >= 0) _workPrimIdBuf[idx] = _curPrimId;
                }
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }
    }

    void DrawTriFlatUp(ushort c, int x0, int y0, int x1, int y1, int x2, int y2, int area)
    {
        int s = _scale, upW = 1024 * s, maxH = 512 * s;

        int minX = Math.Max(DrawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(DrawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(DrawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(DrawY2, Math.Max(y0, Math.Max(y1, y2)));

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
                    _vram[(py & 511) * 1024 + (px & 1023)] = c;
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }

        int sx0 = x0 * s, sy0 = y0 * s, sx1 = x1 * s, sy1 = y1 * s, sx2 = x2 * s, sy2 = y2 * s;
        long sArea = (long)(sx1 - sx0) * (sy2 - sy0) - (long)(sx2 - sx0) * (sy1 - sy0);
        if (sArea == 0) return;

        int sMinX = Math.Max(DrawX1 * s, Math.Min(sx0, Math.Min(sx1, sx2)));
        int sMaxX = Math.Min((DrawX2 + 1) * s - 1, Math.Max(sx0, Math.Max(sx1, sx2)));
        int sMinY = Math.Max(DrawY1 * s, Math.Min(sy0, Math.Min(sy1, sy2)));
        int sMaxY = Math.Min((DrawY2 + 1) * s - 1, Math.Max(sy0, Math.Max(sy1, sy2)));

        int sA12 = sy1 - sy2, sB12 = sx2 - sx1;
        int sA20 = sy2 - sy0, sB20 = sx0 - sx2;
        int sA01 = sy0 - sy1, sB01 = sx1 - sx0;

        int sw0r = sA12 * (sMinX - sx1) + sB12 * (sMinY - sy1);
        int sw1r = sA20 * (sMinX - sx2) + sB20 * (sMinY - sy2);
        int sw2r = sA01 * (sMinX - sx0) + sB01 * (sMinY - sy0);

        for (int py = sMinY; py <= sMaxY; py++)
        {
            int w0 = sw0r, w1 = sw1r, w2 = sw2r;
            for (int px = sMinX; px <= sMaxX; px++)
            {
                if ((w0 | w1 | w2) >= 0)
                    _upVram[(py % maxH) * upW + (px % upW)] = c;
                w0 += sA12; w1 += sA20; w2 += sA01;
            }
            sw0r += sB12; sw1r += sB20; sw2r += sB01;
        }
    }

    // ── Gouraud-shaded triangle ──────────────────────────────────────────────

    public override void DrawTriGouraud(uint c0, uint c1, uint c2, uint v0w, uint v1w, uint v2w)
    {
        if (HackRandomColors)
        {
            DrawTriFlat(RandomColor(_workPrims.Count), v0w, v1w, v2w);
            return;
        }

        int x0 = S11(v0w & 0xFFFF) + OffX, y0 = S11(v0w >> 16) + OffY;
        int x1 = S11(v1w & 0xFFFF) + OffX, y1 = S11(v1w >> 16) + OffY;
        int x2 = S11(v2w & 0xFFFF) + OffX, y2 = S11(v2w >> 16) + OffY;

        int r0 = (int)(c0 & 0xFF), g0 = (int)((c0 >> 8) & 0xFF), b0 = (int)((c0 >> 16) & 0xFF);
        int r1 = (int)(c1 & 0xFF), g1 = (int)((c1 >> 8) & 0xFF), b1 = (int)((c1 >> 16) & 0xFF);
        int r2 = (int)(c2 & 0xFF), g2 = (int)((c2 >> 8) & 0xFF), b2 = (int)((c2 >> 16) & 0xFF);

        _curPrimId = -1;
        if (TrackPrimitives)
        {
            _curPrimId = _workPrims.Count;
            _workPrims.Add(new PrimInfo { Type = PrimInfo.Kind.TriGouraud, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, C0 = c0, C1 = c1, C2 = c2 });
        }

        if (HackWireframe)
        {
            ushort avg = To555(AvgCol(c0, c1, c2));
            DrawLine(avg, x0 - OffX, y0 - OffY, x1 - OffX, y1 - OffY);
            DrawLine(avg, x1 - OffX, y1 - OffY, x2 - OffX, y2 - OffY);
            DrawLine(avg, x2 - OffX, y2 - OffY, x0 - OffX, y0 - OffY);
            return;
        }

        int area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (area == 0) return;

        if (area < 0)
        {
            (x1, x2) = (x2, x1); (y1, y2) = (y2, y1);
            (r1, r2) = (r2, r1); (g1, g2) = (g2, g1); (b1, b2) = (b2, b1);
            area = -area;
        }

        if ((Math.Max(x0, Math.Max(x1, x2)) - Math.Min(x0, Math.Min(x1, x2))) > 1024) return;
        if ((Math.Max(y0, Math.Max(y1, y2)) - Math.Min(y0, Math.Min(y1, y2))) > 512) return;

        int minX = Math.Max(DrawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(DrawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(DrawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(DrawY2, Math.Max(y0, Math.Max(y1, y2)));

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
                    int r = Math.Clamp((r0 * w0 + r1 * w1 + r2 * w2) / area, 0, 255);
                    int g = Math.Clamp((g0 * w0 + g1 * w1 + g2 * w2) / area, 0, 255);
                    int b = Math.Clamp((b0 * w0 + b1 * w1 + b2 * w2) / area, 0, 255);
                    ushort color = (ushort)(((r >> 3) & 0x1F) | (((g >> 3) & 0x1F) << 5) | (((b >> 3) & 0x1F) << 10));
                    int idx = (py & 511) * 1024 + (px & 1023);
                    _vram[idx] = color;
                    if (_scale > 1) PutUp(px, py, color);
                    if (_curPrimId >= 0) _workPrimIdBuf[idx] = _curPrimId;
                }
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }
    }

    // ── Textured triangle (raw — no color modulation) ────────────────────────

    public override void DrawTriTex(uint v0w, uint uv0, uint v1w, uint uv1, uint v2w, uint uv2, int clutAttr)
    {
        int x0 = S11(v0w & 0xFFFF) + OffX, y0 = S11(v0w >> 16) + OffY;
        int x1 = S11(v1w & 0xFFFF) + OffX, y1 = S11(v1w >> 16) + OffY;
        int x2 = S11(v2w & 0xFFFF) + OffX, y2 = S11(v2w >> 16) + OffY;

        byte u0b = (byte)(uv0 & 0xFF), v0b = (byte)((uv0 >> 8) & 0xFF);
        byte u1b = (byte)(uv1 & 0xFF), v1b = (byte)((uv1 >> 8) & 0xFF);
        byte u2b = (byte)(uv2 & 0xFF), v2b = (byte)((uv2 >> 8) & 0xFF);
        int cx = ((clutAttr & 0x3F) << 4) & 1023;
        int cy = ((clutAttr >> 6) & 0x1FF) & 511;

        if (HackRandomColors) { DrawTriFlat(RandomColor(_workPrims.Count), v0w, v1w, v2w); return; }
        if (HackVertexColorsOnly) { DrawTriFlat(To555(0x808080), v0w, v1w, v2w); return; }

        _curPrimId = -1;
        if (TrackPrimitives)
        {
            _curPrimId = _workPrims.Count;
            _workPrims.Add(new PrimInfo
            {
                Type = PrimInfo.Kind.TriTex,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                U0 = u0b, V0 = v0b, U1 = u1b, V1 = v1b, U2 = u2b, V2 = v2b,
                TpX = TexPageX, TpY = TexPageY, TpDepth = TexDepth,
                ClutX = cx, ClutY = cy
            });
        }

        if (HackWireframe)
        {
            DrawLine(0x7FFF, x0 - OffX, y0 - OffY, x1 - OffX, y1 - OffY);
            DrawLine(0x7FFF, x1 - OffX, y1 - OffY, x2 - OffX, y2 - OffY);
            DrawLine(0x7FFF, x2 - OffX, y2 - OffY, x0 - OffX, y0 - OffY);
            return;
        }

        int area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (area == 0) return;

        if (area < 0) {
            (x1, x2) = (x2, x1); (y1, y2) = (y2, y1);
            (u1b, u2b) = (u2b, u1b); (v1b, v2b) = (v2b, v1b);
            area = -area;
        }

        if ((Math.Max(x0, Math.Max(x1, x2)) - Math.Min(x0, Math.Min(x1, x2))) > 1024) return;
        if ((Math.Max(y0, Math.Max(y1, y2)) - Math.Min(y0, Math.Min(y1, y2))) > 512) return;

        if (_scale > 1) { DrawTriTexUp(x0, y0, x1, y1, x2, y2, u0b, v0b, u1b, v1b, u2b, v2b, cx, cy, area); return; }

        int minX = Math.Max(DrawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(DrawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(DrawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(DrawY2, Math.Max(y0, Math.Max(y1, y2)));

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
                    {
                        int idx = (py & 511) * 1024 + (px & 1023);
                        _vram[idx] = texel;
                        if (_curPrimId >= 0) _workPrimIdBuf[idx] = _curPrimId;
                    }
                }
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }
    }

    void DrawTriTexUp(int x0, int y0, int x1, int y1, int x2, int y2,
                      int u0b, int v0b, int u1b, int v1b, int u2b, int v2b,
                      int cx, int cy, int area)
    {
        int s = _scale, upW = 1024 * s, maxH = 512 * s;

        int minX = Math.Max(DrawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(DrawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(DrawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(DrawY2, Math.Max(y0, Math.Max(y1, y2)));

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
                    if (texel != 0) _vram[(py & 511) * 1024 + (px & 1023)] = texel;
                }
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }

        int sx0 = x0 * s, sy0 = y0 * s, sx1 = x1 * s, sy1 = y1 * s, sx2 = x2 * s, sy2 = y2 * s;
        long sArea = (long)(sx1 - sx0) * (sy2 - sy0) - (long)(sx2 - sx0) * (sy1 - sy0);
        if (sArea == 0) return;

        int sMinX = Math.Max(DrawX1 * s, Math.Min(sx0, Math.Min(sx1, sx2)));
        int sMaxX = Math.Min((DrawX2 + 1) * s - 1, Math.Max(sx0, Math.Max(sx1, sx2)));
        int sMinY = Math.Max(DrawY1 * s, Math.Min(sy0, Math.Min(sy1, sy2)));
        int sMaxY = Math.Min((DrawY2 + 1) * s - 1, Math.Max(sy0, Math.Max(sy1, sy2)));

        int sA12 = sy1 - sy2, sB12 = sx2 - sx1;
        int sA20 = sy2 - sy0, sB20 = sx0 - sx2;
        int sA01 = sy0 - sy1, sB01 = sx1 - sx0;

        int sw0r = sA12 * (sMinX - sx1) + sB12 * (sMinY - sy1);
        int sw1r = sA20 * (sMinX - sx2) + sB20 * (sMinY - sy2);
        int sw2r = sA01 * (sMinX - sx0) + sB01 * (sMinY - sy0);

        for (int py = sMinY; py <= sMaxY; py++)
        {
            int w0 = sw0r, w1 = sw1r, w2 = sw2r;
            for (int px = sMinX; px <= sMaxX; px++)
            {
                if ((w0 | w1 | w2) >= 0)
                {
                    int u = (int)((u0b * (long)w0 + u1b * (long)w1 + u2b * (long)w2) / sArea);
                    int v = (int)((v0b * (long)w0 + v1b * (long)w1 + v2b * (long)w2) / sArea);
                    ushort texel = SampleTexel(u, v, cx, cy);
                    if (texel != 0) _upVram[(py % maxH) * upW + (px % upW)] = texel;
                }
                w0 += sA12; w1 += sA20; w2 += sA01;
            }
            sw0r += sB12; sw1r += sB20; sw2r += sB01;
        }
    }

    // ── Textured triangle with flat color modulation ─────────────────────────
    // PSX formula per channel: result = min(31, (texel_5bit * color_8bit) >> 7)
    // 0x80 = neutral (1.0x), 0xFF ~ 2.0x brightness

    public override void DrawTriTexBlend(uint modColor, uint v0w, uint uv0, uint v1w, uint uv1, uint v2w, uint uv2, int clutAttr)
    {
        int x0 = S11(v0w & 0xFFFF) + OffX, y0 = S11(v0w >> 16) + OffY;
        int x1 = S11(v1w & 0xFFFF) + OffX, y1 = S11(v1w >> 16) + OffY;
        int x2 = S11(v2w & 0xFFFF) + OffX, y2 = S11(v2w >> 16) + OffY;

        byte u0b = (byte)(uv0 & 0xFF), v0b = (byte)((uv0 >> 8) & 0xFF);
        byte u1b = (byte)(uv1 & 0xFF), v1b = (byte)((uv1 >> 8) & 0xFF);
        byte u2b = (byte)(uv2 & 0xFF), v2b = (byte)((uv2 >> 8) & 0xFF);
        int cx = ((clutAttr & 0x3F) << 4) & 1023;
        int cy = ((clutAttr >> 6) & 0x1FF) & 511;

        int mr = (int)(modColor & 0xFF), mg = (int)((modColor >> 8) & 0xFF), mb = (int)((modColor >> 16) & 0xFF);

        if (HackRandomColors) { DrawTriFlat(RandomColor(_workPrims.Count), v0w, v1w, v2w); return; }
        if (HackVertexColorsOnly) { DrawTriFlat(To555(modColor), v0w, v1w, v2w); return; }

        _curPrimId = -1;
        if (TrackPrimitives)
        {
            _curPrimId = _workPrims.Count;
            _workPrims.Add(new PrimInfo
            {
                Type = PrimInfo.Kind.TriTexBlend,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                C0 = modColor,
                U0 = u0b, V0 = v0b, U1 = u1b, V1 = v1b, U2 = u2b, V2 = v2b,
                TpX = TexPageX, TpY = TexPageY, TpDepth = TexDepth,
                ClutX = cx, ClutY = cy
            });
        }

        if (HackWireframe)
        {
            DrawLine(To555(modColor), x0 - OffX, y0 - OffY, x1 - OffX, y1 - OffY);
            DrawLine(To555(modColor), x1 - OffX, y1 - OffY, x2 - OffX, y2 - OffY);
            DrawLine(To555(modColor), x2 - OffX, y2 - OffY, x0 - OffX, y0 - OffY);
            return;
        }

        int area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (area == 0) return;

        if (area < 0) {
            (x1, x2) = (x2, x1); (y1, y2) = (y2, y1);
            (u1b, u2b) = (u2b, u1b); (v1b, v2b) = (v2b, v1b);
            area = -area;
        }

        if ((Math.Max(x0, Math.Max(x1, x2)) - Math.Min(x0, Math.Min(x1, x2))) > 1024) return;
        if ((Math.Max(y0, Math.Max(y1, y2)) - Math.Min(y0, Math.Min(y1, y2))) > 512) return;

        int minX = Math.Max(DrawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(DrawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(DrawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(DrawY2, Math.Max(y0, Math.Max(y1, y2)));

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
                    {
                        int tr = texel & 0x1F, tg = (texel >> 5) & 0x1F, tb = (texel >> 10) & 0x1F;
                        int rr = Math.Min(31, (tr * mr) >> 7);
                        int rg = Math.Min(31, (tg * mg) >> 7);
                        int rb = Math.Min(31, (tb * mb) >> 7);
                        ushort blended = (ushort)(rr | (rg << 5) | (rb << 10));
                        int idx = (py & 511) * 1024 + (px & 1023);
                        _vram[idx] = blended;
                        if (_scale > 1) PutUp(px, py, blended);
                        if (_curPrimId >= 0) _workPrimIdBuf[idx] = _curPrimId;
                    }
                }
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }
    }

    // ── Gouraud-shaded textured triangle ─────────────────────────────────────

    public override void DrawTriGouraudTex(uint c0, uint c1, uint c2, uint v0w, uint uv0, uint v1w, uint uv1, uint v2w, uint uv2, int clutAttr)
    {
        int x0 = S11(v0w & 0xFFFF) + OffX, y0 = S11(v0w >> 16) + OffY;
        int x1 = S11(v1w & 0xFFFF) + OffX, y1 = S11(v1w >> 16) + OffY;
        int x2 = S11(v2w & 0xFFFF) + OffX, y2 = S11(v2w >> 16) + OffY;

        byte u0b = (byte)(uv0 & 0xFF), v0b = (byte)((uv0 >> 8) & 0xFF);
        byte u1b = (byte)(uv1 & 0xFF), v1b = (byte)((uv1 >> 8) & 0xFF);
        byte u2b = (byte)(uv2 & 0xFF), v2b = (byte)((uv2 >> 8) & 0xFF);
        int cx = ((clutAttr & 0x3F) << 4) & 1023;
        int cy = ((clutAttr >> 6) & 0x1FF) & 511;

        int r0 = (int)(c0 & 0xFF), g0 = (int)((c0 >> 8) & 0xFF), b0 = (int)((c0 >> 16) & 0xFF);
        int r1 = (int)(c1 & 0xFF), g1 = (int)((c1 >> 8) & 0xFF), b1 = (int)((c1 >> 16) & 0xFF);
        int r2 = (int)(c2 & 0xFF), g2 = (int)((c2 >> 8) & 0xFF), b2 = (int)((c2 >> 16) & 0xFF);

        if (HackRandomColors) { DrawTriFlat(RandomColor(_workPrims.Count), v0w, v1w, v2w); return; }
        if (HackVertexColorsOnly)
        {
            DrawTriGouraud(c0, c1, c2, v0w, v1w, v2w);
            return;
        }

        _curPrimId = -1;
        if (TrackPrimitives)
        {
            _curPrimId = _workPrims.Count;
            _workPrims.Add(new PrimInfo
            {
                Type = PrimInfo.Kind.TriGouraudTex,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                C0 = c0, C1 = c1, C2 = c2,
                U0 = u0b, V0 = v0b, U1 = u1b, V1 = v1b, U2 = u2b, V2 = v2b,
                TpX = TexPageX, TpY = TexPageY, TpDepth = TexDepth,
                ClutX = cx, ClutY = cy
            });
        }

        if (HackWireframe)
        {
            ushort avg = To555(AvgCol(c0, c1, c2));
            DrawLine(avg, x0 - OffX, y0 - OffY, x1 - OffX, y1 - OffY);
            DrawLine(avg, x1 - OffX, y1 - OffY, x2 - OffX, y2 - OffY);
            DrawLine(avg, x2 - OffX, y2 - OffY, x0 - OffX, y0 - OffY);
            return;
        }

        int area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (area == 0) return;

        if (area < 0)
        {
            (x1, x2) = (x2, x1); (y1, y2) = (y2, y1);
            (u1b, u2b) = (u2b, u1b); (v1b, v2b) = (v2b, v1b);
            (r1, r2) = (r2, r1); (g1, g2) = (g2, g1); (b1, b2) = (b2, b1);
            area = -area;
        }

        if ((Math.Max(x0, Math.Max(x1, x2)) - Math.Min(x0, Math.Min(x1, x2))) > 1024) return;
        if ((Math.Max(y0, Math.Max(y1, y2)) - Math.Min(y0, Math.Min(y1, y2))) > 512) return;

        int minX = Math.Max(DrawX1, Math.Min(x0, Math.Min(x1, x2)));
        int maxX = Math.Min(DrawX2, Math.Max(x0, Math.Max(x1, x2)));
        int minY = Math.Max(DrawY1, Math.Min(y0, Math.Min(y1, y2)));
        int maxY = Math.Min(DrawY2, Math.Max(y0, Math.Max(y1, y2)));

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
                    {
                        int cr = Math.Clamp((r0 * w0 + r1 * w1 + r2 * w2) / area, 0, 255);
                        int cg = Math.Clamp((g0 * w0 + g1 * w1 + g2 * w2) / area, 0, 255);
                        int cb = Math.Clamp((b0 * w0 + b1 * w1 + b2 * w2) / area, 0, 255);
                        int tr = texel & 0x1F, tg = (texel >> 5) & 0x1F, tb = (texel >> 10) & 0x1F;
                        int rr = Math.Min(31, (tr * cr) >> 7);
                        int rg = Math.Min(31, (tg * cg) >> 7);
                        int rb = Math.Min(31, (tb * cb) >> 7);
                        ushort blended = (ushort)(rr | (rg << 5) | (rb << 10));
                        int idx = (py & 511) * 1024 + (px & 1023);
                        _vram[idx] = blended;
                        if (_scale > 1) PutUp(px, py, blended);
                        if (_curPrimId >= 0) _workPrimIdBuf[idx] = _curPrimId;
                    }
                }
                w0 += A12; w1 += A20; w2 += A01;
            }
            w0_row += B12; w1_row += B20; w2_row += B01;
        }
    }

    // ── Rectangles ───────────────────────────────────────────────────────────

    public override void DrawRectFlat(ushort c, uint pos, uint size)
    {
        if (HackRandomColors) c = RandomColor(_workPrims.Count);
        int x0 = S11(pos & 0xFFFF) + OffX;
        int y0 = S11(pos >> 16) + OffY;
        int w = (int)(size & 0xFFFF), h = (int)(size >> 16);
        int xStart = Math.Max(x0, DrawX1), xEnd = Math.Min(x0 + w, DrawX2 + 1);
        int yStart = Math.Max(y0, DrawY1), yEnd = Math.Min(y0 + h, DrawY2 + 1);
        for (int py = yStart; py < yEnd; py++)
            for (int px = xStart; px < xEnd; px++)
            {
                _vram[(py & 511) * 1024 + (px & 1023)] = c;
                if (_scale > 1) PutUp(px, py, c);
            }
    }

    public override void DrawRectTex(uint pos, uint uvclut, uint size, int clutAttr)
    {
        if (HackVertexColorsOnly)
        {
            DrawRectFlat(To555(0x808080), pos, size);
            return;
        }
        int x0 = S11(pos & 0xFFFF) + OffX;
        int y0 = S11(pos >> 16) + OffY;
        int w = (int)(size & 0xFFFF), h = (int)(size >> 16);
        byte u0 = (byte)(uvclut & 0xFF), v0 = (byte)((uvclut >> 8) & 0xFF);
        int cx = ((clutAttr & 0x3F) << 4) & 1023;
        int cy = ((clutAttr >> 6) & 0x1FF) & 511;
        int xStart = Math.Max(x0, DrawX1), xEnd = Math.Min(x0 + w, DrawX2 + 1);
        int yStart = Math.Max(y0, DrawY1), yEnd = Math.Min(y0 + h, DrawY2 + 1);
        for (int py = yStart; py < yEnd; py++)
            for (int px = xStart; px < xEnd; px++)
            {
                ushort texel = SampleTexel(u0 + (px - x0), v0 + (py - y0), cx, cy);
                if (texel != 0)
                {
                    _vram[(py & 511) * 1024 + (px & 1023)] = texel;
                    if (_scale > 1) PutUp(px, py, texel);
                }
            }
    }

    public override void DrawRectTexBlend(uint modColor, uint pos, uint uvclut, uint size, int clutAttr)
    {
        if (HackVertexColorsOnly)
        {
            DrawRectFlat(To555(modColor), pos, size);
            return;
        }
        int mr = (int)(modColor & 0xFF), mg = (int)((modColor >> 8) & 0xFF), mb = (int)((modColor >> 16) & 0xFF);
        int x0 = S11(pos & 0xFFFF) + OffX;
        int y0 = S11(pos >> 16) + OffY;
        int w = (int)(size & 0xFFFF), h = (int)(size >> 16);
        byte u0 = (byte)(uvclut & 0xFF), v0 = (byte)((uvclut >> 8) & 0xFF);
        int cx = ((clutAttr & 0x3F) << 4) & 1023;
        int cy = ((clutAttr >> 6) & 0x1FF) & 511;
        int xStart = Math.Max(x0, DrawX1), xEnd = Math.Min(x0 + w, DrawX2 + 1);
        int yStart = Math.Max(y0, DrawY1), yEnd = Math.Min(y0 + h, DrawY2 + 1);
        for (int py = yStart; py < yEnd; py++)
            for (int px = xStart; px < xEnd; px++)
            {
                ushort texel = SampleTexel(u0 + (px - x0), v0 + (py - y0), cx, cy);
                if (texel != 0)
                {
                    int tr = texel & 0x1F, tg = (texel >> 5) & 0x1F, tb = (texel >> 10) & 0x1F;
                    int rr = Math.Min(31, (tr * mr) >> 7);
                    int rg = Math.Min(31, (tg * mg) >> 7);
                    int rb = Math.Min(31, (tb * mb) >> 7);
                    ushort blended = (ushort)(rr | (rg << 5) | (rb << 10));
                    _vram[(py & 511) * 1024 + (px & 1023)] = blended;
                    if (_scale > 1) PutUp(px, py, blended);
                }
            }
    }

    // ── Line drawing (Bresenham) ──────────────────────────────────────────────

    public override void DrawLine(ushort c, int x0, int y0, int x1, int y1)
    {
        x0 += OffX; y0 += OffY;
        x1 += OffX; y1 += OffY;

        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        for (int i = 0; i < 1024; i++)
        {
            if (x0 >= DrawX1 && x0 <= DrawX2 && y0 >= DrawY1 && y0 <= DrawY2)
            {
                _vram[(y0 & 511) * 1024 + (x0 & 1023)] = c;
                if (_scale > 1)
                {
                    int s = _scale, upW = 1024 * s;
                    for (int sdy = 0; sdy < s; sdy++)
                        for (int sdx = 0; sdx < s; sdx++)
                            _upVram[((y0 * s + sdy) & (512 * s - 1)) * upW + ((x0 * s + sdx) & (upW - 1))] = c;
                }
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ── Fill / Copy ──────────────────────────────────────────────────────────

    public override void FillRect(int x, int y, int w, int h, ushort c)
    {
        for (int py = y; py < y + h && py < 512; py++)
            for (int px = x; px < x + w && px < 1024; px++)
                _vram[(py & 511) * 1024 + (px & 1023)] = c;
        if (_scale > 1) FillRectUp(x, y, w, h, c);
    }

    void FillRectUp(int x, int y, int w, int h, ushort c)
    {
        int s = _scale, upW = 1024 * s, maxH = 512 * s;
        for (int py = y * s; py < (y + h) * s && py < maxH; py++)
            for (int px = x * s; px < (x + w) * s && px < upW; px++)
                _upVram[(py % maxH) * upW + (px % upW)] = c;
    }

    public override void CopyVram(int sx, int sy, int dx, int dy, int w, int h)
    {
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                ushort pix = _vram[((sy + py) & 511) * 1024 + ((sx + px) & 1023)];
                _vram[((dy + py) & 511) * 1024 + ((dx + px) & 1023)] = pix;
                if (_scale > 1)
                {
                    int s = _scale, upW = 1024 * s, maxH = 512 * s;
                    for (int sdy = 0; sdy < s; sdy++)
                        for (int sdx = 0; sdx < s; sdx++)
                            _upVram[(((dy + py) * s + sdy) % maxH) * upW + (((dx + px) * s + sdx) % upW)] = pix;
                }
            }
    }

    // ── Texture sampling ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ushort SampleTexel(int u, int v, int clutX, int clutY)
    {
        u &= 0xFF; v &= 0xFF;
        int row = (TexPageY + v) & 511;

        if (TexDepth == 0)
        {
            int col = (TexPageX + u / 4) & 1023;
            ushort word = _vram[row * 1024 + col];
            int nib = (word >> ((u & 3) * 4)) & 0xF;
            return _vram[(clutY & 511) * 1024 + ((clutX + nib) & 1023)];
        }
        if (TexDepth == 1)
        {
            int col = (TexPageX + u / 2) & 1023;
            ushort word = _vram[row * 1024 + col];
            int idx = (u & 1) == 0 ? (word & 0xFF) : (word >> 8);
            return _vram[(clutY & 511) * 1024 + ((clutX + idx) & 1023)];
        }
        return _vram[row * 1024 + ((TexPageX + u) & 1023)];
    }

    // ── Upscale helper ───────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void PutUp(int x, int y, ushort c)
    {
        int s = _scale, upW = 1024 * s;
        int bx = x * s, by = y * s;
        for (int dy = 0; dy < s; dy++)
            for (int dx = 0; dx < s; dx++)
                _upVram[((by + dy) % (512 * s)) * upW + ((bx + dx) % upW)] = c;
    }

    // ── Display ──────────────────────────────────────────────────────────────

    public override void VBlankSnapshot(int dispX, int dispY, int dispW, int dispH)
    {
        lock (_snapLock)
        {
            _snapScale = _scale;
            Buffer.BlockCopy(_vram, 0, _snapVram, 0, _vram.Length * 2);
            if (_scale > 1)
                Buffer.BlockCopy(_upVram, 0, _snapUpVram, 0, _upVram.Length * 2);
            _snapDx = dispX; _snapDy = dispY;
            _snapDw = dispW; _snapDh = dispH;

            if (TrackPrimitives)
            {
                SnapPrims.Clear();
                SnapPrims.AddRange(_workPrims);
                Buffer.BlockCopy(_workPrimIdBuf, 0, SnapPrimIdBuf, 0, _workPrimIdBuf.Length * 4);
                _workPrims.Clear();
                Array.Fill(_workPrimIdBuf, -1);
                _curPrimId = -1;
            }
        }
    }

    public override void SnapshotDisplay(RlColor[] pixels, int outW, int outH)
    {
        lock (_snapLock)
        {
            int dx = _snapDx, dy = _snapDy, dw = _snapDw, dh = _snapDh;
            int s = _snapScale;

            if (s <= 1)
            {
                fixed (ushort* vram = _snapVram)
                fixed (RlColor* px = pixels)
                fixed (uint* lut32 = _15to32)
                {
                    for (int y = 0; y < outH; y++)
                    {
                        RlColor* row = px + y * outW;
                        if (y >= dh) { for (int x = 0; x < outW; x++) row[x] = new RlColor { a = 255 }; continue; }
                        int srcY = ((dy + y) & 511) * 1024;
                        int copyW = Math.Min(dw, outW);
                        for (int x = 0; x < copyW; x++)
                        {
                            ushort c = vram[srcY + ((dx + x) & 1023)];
                            *(uint*)(row + x) = lut32[c & 0x7FFF];
                        }
                        for (int x = copyW; x < outW; x++) row[x] = new RlColor { a = 255 };
                    }
                }
            }
            else
            {
                int upW = 1024 * s;
                fixed (ushort* upvram = _snapUpVram)
                fixed (RlColor* px = pixels)
                fixed (uint* lut32 = _15to32)
                {
                    for (int y = 0; y < outH; y++)
                    {
                        RlColor* row = px + y * outW;
                        if (y >= dh * s) { for (int x = 0; x < outW; x++) row[x] = new RlColor { a = 255 }; continue; }
                        int srcY = ((dy * s + y) % (512 * s)) * upW;
                        int copyW = Math.Min(dw * s, outW);
                        for (int x = 0; x < copyW; x++)
                        {
                            ushort c = upvram[srcY + ((dx * s + x) % upW)];
                            *(uint*)(row + x) = lut32[c & 0x7FFF];
                        }
                        for (int x = copyW; x < outW; x++) row[x] = new RlColor { a = 255 };
                    }
                }
            }
        }
    }

    public override void SnapshotFullVram(RlColor[] pixels)
    {
        lock (_snapLock)
        {
            fixed (ushort* vram = _snapVram)
            fixed (RlColor* px = pixels)
            fixed (uint* lut32 = _15to32)
            {
                for (int i = 0; i < 1024 * 512; i++)
                    *(uint*)(px + i) = lut32[vram[i] & 0x7FFF];
            }
        }
    }
}
