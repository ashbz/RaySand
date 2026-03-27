using System.Runtime.CompilerServices;
using RlColor = Raylib_CsLo.Color;

namespace PsxEmu;

/// <summary>
/// PSX GPU — command parser and state machine.
/// Delegates all actual rendering to a pluggable <see cref="PsxRendererBase"/> backend.
/// </summary>
class PsxGpu
{
    public PsxRendererBase Renderer;

    // Display registers (GP1)
    public int DispStartX, DispStartY;
    public int DispWidth = 256, DispHeight = 240;
    bool _displayEnabled;

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
    public int FrameCycle;   // set by PsxMachine each batch
    public int Gp0Count { get; private set; }
    public int Gp1Count { get; private set; }
    public int XferPixels { get; private set; }
    public int VramWriteCount { get; private set; }

    // Convenience accessors that delegate to the renderer
    public int Scale { get => Renderer.Scale; set => Renderer.Scale = value; }
    public ushort[] Vram => Renderer.Vram;

    // Polyline state
    bool _polyLine;
    bool _polyShaded;
    uint _polyColor;
    int _polyLastX, _polyLastY;
    uint _polyLastCol;
    int _polyWords;

    // Profiling section IDs
    static readonly int ProfTriFlat  = Profiler.Register("Gpu.TriFlat");
    static readonly int ProfTriTex   = Profiler.Register("Gpu.TriTex");
    static readonly int ProfRectFlat = Profiler.Register("Gpu.RectFlat");
    static readonly int ProfRectTex  = Profiler.Register("Gpu.RectTex");
    static readonly int ProfFillRect = Profiler.Register("Gpu.FillRect");
    static readonly int ProfVramCopy = Profiler.Register("Gpu.VramCopy");
    static readonly int ProfVBlank   = Profiler.Register("Gpu.VBlank");
    static readonly int ProfLine     = Profiler.Register("Gpu.Line");

    public PsxGpu() => Renderer = new SoftwareRenderer();

    public void SetRenderer(PsxRendererBase renderer)
    {
        Renderer = renderer;
    }

    // ── External API ────────────────────────────────────────────────────────

    bool _oddFrame;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadStat()
    {
        uint stat = 0x1C00_2000;  // bits 26 (cmd ready), 27 (VRAM ready), 28 (DMA ready), 13
        if (!_displayEnabled) stat |= 1u << 23;
        if (_oddFrame)        stat |= 1u << 31;          // Even/Odd interlace toggle
        return stat;
    }

    public uint ReadData()
    {
        if (!_xferOut) return 0;
        var vram = Renderer.Vram;
        uint a = (uint)((_xferSrcY * 1024) + _xferSrcX);
        ushort lo = vram[a & (1024 * 512 - 1)];
        ushort hi = vram[(a + 1) & (1024 * 512 - 1)];
        _xferSrcX += 2;
        return ((uint)hi << 16) | lo;
    }

    // ── VBlank / Display (delegate to renderer with profiling) ───────────────

    public void VBlankSnapshot()
    {
        _oddFrame = !_oddFrame;
        Profiler.Begin(ProfVBlank);
        Renderer.VBlankSnapshot(DispStartX, DispStartY, DispWidth, DispHeight);
        Profiler.End(ProfVBlank);
    }

    public unsafe void SnapshotDisplay(RlColor[] pixels, int outW, int outH)
        => Renderer.SnapshotDisplay(pixels, outW, outH);

    // ── GP1 – display control ───────────────────────────────────────────────

    public void WriteGP1(uint val)
    {
        Gp1Count++;
        uint cmd = val >> 24;
        switch (cmd)
        {
            case 0x00:
                _fifoLen = 0; _xferIn = _xferOut = false; _polyLine = false;
                DispStartX = DispStartY = 0;
                DispWidth = 256; DispHeight = 240;
                _displayEnabled = false;
                Renderer.TexPageX = Renderer.TexPageY = Renderer.TexDepth = 0;
                Renderer.DrawX1 = Renderer.DrawY1 = 0;
                Renderer.DrawX2 = 1023; Renderer.DrawY2 = 511;
                Renderer.OffX = Renderer.OffY = 0;
                Renderer.ClearVram();
                break;
            case 0x01: _fifoLen = 0; _xferIn = false; _polyLine = false; break;
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

        if (_polyLine)
        {
            HandlePolyLine(val);
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
        Renderer.WritePixel(x, y, pix);
        _xferCount++;
        if (_xferCount >= _xferW * _xferH)
        {
            XferPixels += _xferCount;
            VramWriteCount++;
            _xferIn = false;
        }
    }

    void HandlePolyLine(uint val)
    {
        if ((val & 0xF000_F000) == 0x5000_5000) { _polyLine = false; return; }

        if (_polyShaded)
        {
            _polyWords++;
            if ((_polyWords & 1) == 1) { _polyLastCol = val; return; }
            int x = S11(val); int y = S11(val >> 16);
            Profiler.Begin(ProfLine);
            Renderer.DrawLine(To555(_polyLastCol), _polyLastX, _polyLastY, x, y);
            Profiler.End(ProfLine);
            _polyLastX = x; _polyLastY = y;
        }
        else
        {
            int x = S11(val); int y = S11(val >> 16);
            Profiler.Begin(ProfLine);
            Renderer.DrawLine(To555(_polyColor), _polyLastX, _polyLastY, x, y);
            Profiler.End(ProfLine);
            _polyLastX = x; _polyLastY = y;
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
        0x48 or 0x49 or 0x4A or 0x4B or 0x4C or 0x4D or 0x4E or 0x4F => 3,
        0x50 or 0x51 or 0x52 or 0x53 => 4,
        0x58 or 0x59 or 0x5A or 0x5B or 0x5C or 0x5D or 0x5E or 0x5F => 4,
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
        var R = Renderer;

        switch (cmd)
        {
            case 0x02:
            {
                Profiler.Begin(ProfFillRect);
                int x = (int)(_fifo[1] & 0x3F0);
                int y = (int)((_fifo[1] >> 16) & 0x1FF);
                int w = (int)((_fifo[2] & 0x3FF) + 0xF) & ~0xF;
                int h = (int)((_fifo[2] >> 16) & 0x1FF);
                R.FillRect(x, y, w, h, PsxRendererBase.To555(col));
                Profiler.End(ProfFillRect);
                break;
            }

            case 0x20: case 0x21: case 0x22: case 0x23:
                Profiler.Begin(ProfTriFlat);
                R.DrawTriFlat(PsxRendererBase.To555(col), _fifo[1], _fifo[2], _fifo[3]);
                Profiler.End(ProfTriFlat);
                break;
            case 0x24: case 0x26:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[4] >> 16));
                R.DrawTriTexBlend(col, _fifo[1], _fifo[2], _fifo[3], _fifo[4], _fifo[5], _fifo[6], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;
            case 0x25: case 0x27:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[4] >> 16));
                R.DrawTriTex(_fifo[1], _fifo[2], _fifo[3], _fifo[4], _fifo[5], _fifo[6], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;
            case 0x28: case 0x29: case 0x2A: case 0x2B:
                Profiler.Begin(ProfTriFlat);
                R.DrawTriFlat(PsxRendererBase.To555(col), _fifo[1], _fifo[2], _fifo[3]);
                R.DrawTriFlat(PsxRendererBase.To555(col), _fifo[2], _fifo[3], _fifo[4]);
                Profiler.End(ProfTriFlat);
                break;
            case 0x2C: case 0x2E:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[4] >> 16));
                R.DrawTriTexBlend(col, _fifo[1], _fifo[2], _fifo[3], _fifo[4], _fifo[5], _fifo[6], (int)(_fifo[2] >> 16));
                R.DrawTriTexBlend(col, _fifo[3], _fifo[4], _fifo[5], _fifo[6], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;
            case 0x2D: case 0x2F:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[4] >> 16));
                R.DrawTriTex(_fifo[1], _fifo[2], _fifo[3], _fifo[4], _fifo[5], _fifo[6], (int)(_fifo[2] >> 16));
                R.DrawTriTex(_fifo[3], _fifo[4], _fifo[5], _fifo[6], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;
            case 0x30: case 0x31: case 0x32: case 0x33:
                Profiler.Begin(ProfTriFlat);
                R.DrawTriGouraud(_fifo[0] & 0xFF_FFFF, _fifo[2] & 0xFF_FFFF, _fifo[4] & 0xFF_FFFF, _fifo[1], _fifo[3], _fifo[5]);
                Profiler.End(ProfTriFlat);
                break;
            case 0x34: case 0x36:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[5] >> 16));
                R.DrawTriGouraudTex(_fifo[0] & 0xFF_FFFF, _fifo[3] & 0xFF_FFFF, _fifo[6] & 0xFF_FFFF,
                    _fifo[1], _fifo[2], _fifo[4], _fifo[5], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;
            case 0x35: case 0x37:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[5] >> 16));
                R.DrawTriTex(_fifo[1], _fifo[2], _fifo[4], _fifo[5], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;
            case 0x38: case 0x39: case 0x3A: case 0x3B:
                Profiler.Begin(ProfTriFlat);
                R.DrawTriGouraud(_fifo[0] & 0xFF_FFFF, _fifo[2] & 0xFF_FFFF, _fifo[4] & 0xFF_FFFF, _fifo[1], _fifo[3], _fifo[5]);
                R.DrawTriGouraud(_fifo[2] & 0xFF_FFFF, _fifo[4] & 0xFF_FFFF, _fifo[6] & 0xFF_FFFF, _fifo[3], _fifo[5], _fifo[7]);
                Profiler.End(ProfTriFlat);
                break;
            case 0x3C: case 0x3E:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[5] >> 16));
                R.DrawTriGouraudTex(_fifo[0] & 0xFF_FFFF, _fifo[3] & 0xFF_FFFF, _fifo[6] & 0xFF_FFFF,
                    _fifo[1], _fifo[2], _fifo[4], _fifo[5], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16));
                R.DrawTriGouraudTex(_fifo[3] & 0xFF_FFFF, _fifo[6] & 0xFF_FFFF, _fifo[9] & 0xFF_FFFF,
                    _fifo[4], _fifo[5], _fifo[7], _fifo[8], _fifo[10], _fifo[11], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;
            case 0x3D: case 0x3F:
                Profiler.Begin(ProfTriTex);
                SetTexPage((int)(_fifo[5] >> 16));
                R.DrawTriTex(_fifo[1], _fifo[2], _fifo[4], _fifo[5], _fifo[7], _fifo[8], (int)(_fifo[2] >> 16));
                R.DrawTriTex(_fifo[4], _fifo[5], _fifo[7], _fifo[8], _fifo[10], _fifo[11], (int)(_fifo[2] >> 16));
                Profiler.End(ProfTriTex);
                break;

            // ── Lines ────────────────────────────────────────────────────────
            case 0x40: case 0x41: case 0x42: case 0x43:
            {
                Profiler.Begin(ProfLine);
                int x0 = S11(_fifo[1]); int y0 = S11(_fifo[1] >> 16);
                int x1 = S11(_fifo[2]); int y1 = S11(_fifo[2] >> 16);
                R.DrawLine(PsxRendererBase.To555(col), x0, y0, x1, y1);
                Profiler.End(ProfLine);
                break;
            }
            case 0x48: case 0x4A: case 0x4C: case 0x4E:
            {
                Profiler.Begin(ProfLine);
                int x0 = S11(_fifo[1]); int y0 = S11(_fifo[1] >> 16);
                int x1 = S11(_fifo[2]); int y1 = S11(_fifo[2] >> 16);
                R.DrawLine(PsxRendererBase.To555(col), x0, y0, x1, y1);
                Profiler.End(ProfLine);
                _polyLine = true; _polyShaded = false;
                _polyColor = col; _polyLastX = x1; _polyLastY = y1;
                _polyWords = 0;
                break;
            }
            case 0x50: case 0x51: case 0x52: case 0x53:
            {
                Profiler.Begin(ProfLine);
                int x0 = S11(_fifo[1]); int y0 = S11(_fifo[1] >> 16);
                int x1 = S11(_fifo[3]); int y1 = S11(_fifo[3] >> 16);
                R.DrawLine(PsxRendererBase.To555(PsxRendererBase.AvgCol(_fifo[0], _fifo[2], _fifo[2])), x0, y0, x1, y1);
                Profiler.End(ProfLine);
                break;
            }
            case 0x58: case 0x5A: case 0x5C: case 0x5E:
            {
                Profiler.Begin(ProfLine);
                int x0 = S11(_fifo[1]); int y0 = S11(_fifo[1] >> 16);
                int x1 = S11(_fifo[3]); int y1 = S11(_fifo[3] >> 16);
                R.DrawLine(PsxRendererBase.To555(PsxRendererBase.AvgCol(_fifo[0], _fifo[2], _fifo[2])), x0, y0, x1, y1);
                Profiler.End(ProfLine);
                _polyLine = true; _polyShaded = true;
                _polyLastX = x1; _polyLastY = y1; _polyLastCol = _fifo[2];
                _polyWords = 0;
                break;
            }

            case 0x60: case 0x61: case 0x62: case 0x63:
                Profiler.Begin(ProfRectFlat);
                R.DrawRectFlat(PsxRendererBase.To555(col), _fifo[1], _fifo[2]);
                Profiler.End(ProfRectFlat);
                break;
            case 0x64: case 0x66:
                Profiler.Begin(ProfRectTex);
                R.DrawRectTexBlend(col, _fifo[1], _fifo[2], _fifo[3], (int)(_fifo[2] >> 16));
                Profiler.End(ProfRectTex);
                break;
            case 0x65: case 0x67:
                Profiler.Begin(ProfRectTex);
                R.DrawRectTex(_fifo[1], _fifo[2], _fifo[3], (int)(_fifo[2] >> 16));
                Profiler.End(ProfRectTex);
                break;
            case 0x68: case 0x69: case 0x6A: case 0x6B:
                Profiler.Begin(ProfRectFlat);
                R.DrawRectFlat(PsxRendererBase.To555(col), _fifo[1], 0x0001_0001);
                Profiler.End(ProfRectFlat);
                break;
            case 0x70: case 0x71: case 0x72: case 0x73:
                Profiler.Begin(ProfRectFlat);
                R.DrawRectFlat(PsxRendererBase.To555(col), _fifo[1], 0x0008_0008);
                Profiler.End(ProfRectFlat);
                break;
            case 0x74: case 0x76:
                Profiler.Begin(ProfRectTex);
                R.DrawRectTexBlend(col, _fifo[1], _fifo[2], 0x0008_0008, (int)(_fifo[2] >> 16));
                Profiler.End(ProfRectTex);
                break;
            case 0x75: case 0x77:
                Profiler.Begin(ProfRectTex);
                R.DrawRectTex(_fifo[1], _fifo[2], 0x0008_0008, (int)(_fifo[2] >> 16));
                Profiler.End(ProfRectTex);
                break;
            case 0x78: case 0x79: case 0x7A: case 0x7B:
                Profiler.Begin(ProfRectFlat);
                R.DrawRectFlat(PsxRendererBase.To555(col), _fifo[1], 0x0010_0010);
                Profiler.End(ProfRectFlat);
                break;
            case 0x7C: case 0x7E:
                Profiler.Begin(ProfRectTex);
                R.DrawRectTexBlend(col, _fifo[1], _fifo[2], 0x0010_0010, (int)(_fifo[2] >> 16));
                Profiler.End(ProfRectTex);
                break;
            case 0x7D: case 0x7F:
                Profiler.Begin(ProfRectTex);
                R.DrawRectTex(_fifo[1], _fifo[2], 0x0010_0010, (int)(_fifo[2] >> 16));
                Profiler.End(ProfRectTex);
                break;
            case 0x80:
            {
                Profiler.Begin(ProfVramCopy);
                int sx = (int)(_fifo[1] & 0x3FF), sy = (int)((_fifo[1] >> 16) & 0x1FF);
                int dx = (int)(_fifo[2] & 0x3FF), dy = (int)((_fifo[2] >> 16) & 0x1FF);
                int cw = (int)(_fifo[3] & 0xFFFF), ch = (int)((_fifo[3] >> 16) & 0x1FF);
                R.CopyVram(sx, sy, dx, dy, cw, ch);
                Profiler.End(ProfVramCopy);
                break;
            }
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
                R.TexPageX  = (int)((d & 0x0F) * 64);
                R.TexPageY  = (int)(((d >> 4) & 1) * 256);
                R.SemiTrans = (int)((d >> 5) & 3);
                R.TexDepth  = (int)((d >> 7) & 3);
                break;
            }
            case 0xE2: break;
            case 0xE3: R.DrawX1 = (int)(raw0 & 0x3FF); R.DrawY1 = (int)((raw0 >> 10) & 0x1FF); break;
            case 0xE4: R.DrawX2 = (int)(raw0 & 0x3FF); R.DrawY2 = (int)((raw0 >> 10) & 0x1FF); break;
            case 0xE5:
                R.OffX = PsxRendererBase.Sext11((int)(raw0 & 0x7FF));
                R.OffY = PsxRendererBase.Sext11((int)((raw0 >> 11) & 0x7FF));
                break;
            case 0xE6: break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short S11(uint n) => PsxRendererBase.S11(n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ushort To555(uint rgb) => PsxRendererBase.To555(rgb);

    void SetTexPage(int a)
    {
        Renderer.TexPageX  = (a & 0x0F) * 64;
        Renderer.TexPageY  = ((a >> 4) & 1) * 256;
        Renderer.SemiTrans = (a >> 5) & 3;
        Renderer.TexDepth  = (a >> 7) & 3;
    }
}
