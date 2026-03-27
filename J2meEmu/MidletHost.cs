using System.IO.Compression;

namespace J2meEmu;

class MidletHost
{
    public int ScreenWidth = 240;
    public int ScreenHeight = 320;
    public int[] Framebuffer;
    int[] _backBuffer;
    public bool FullScreen;
    public bool RepaintRequested;
    public bool Destroyed;

    public JavaObject? MidletObject;
    public JavaObject? DisplayObject;
    public JavaObject? CurrentDisplayable;
    public JavaObject? CanvasObject;

    int _keyStates;
    public int KeyStates { get => _keyStates; set => _keyStates = value; }
    public int PressedKeys;

    public MidletHost(int width = 240, int height = 320)
    {
        ScreenWidth = width;
        ScreenHeight = height;
        Framebuffer = new int[width * height];
        _backBuffer = new int[width * height];
    }

    public void EnsureDisplayObject(JvmThread thread)
    {
        DisplayObject ??= new JavaObject(thread.Loader.LoadClass("javax/microedition/lcdui/Display"));
    }

    public void SetCurrent(JavaObject? displayable)
    {
        CurrentDisplayable = displayable;
        if (displayable != null)
        {
            if (CanvasObject == null) CanvasObject = displayable;
            RepaintRequested = true;
        }
    }

    public void RegisterCanvas(JavaObject? canvas)
    {
        if (canvas != null)
        {
            CanvasObject = canvas;
            RepaintRequested = true;
        }
    }

    public void NotifyDestroyed() => Destroyed = true;
    public void RequestRepaint() => RepaintRequested = true;

    readonly object _paintLock = new();

    public void DoPaint(JvmThread thread)
    {
        var canvas = CanvasObject ?? CurrentDisplayable;
        if (canvas == null) return;

        lock (_paintLock)
        {
            RepaintRequested = false;
            var gfx = new GraphicsContext(_backBuffer, ScreenWidth, ScreenHeight);
            var cls = thread.Loader.LoadClass("javax/microedition/lcdui/Graphics");
            var gfxObj = new JavaObject(cls, gfx);
            var paintMethod = canvas.Class.FindMethod("paint", "(Ljavax/microedition/lcdui/Graphics;)V");
            if (paintMethod != null)
            {
                try { thread.Invoke(paintMethod, new[] { JValue.OfRef(canvas), JValue.OfRef(gfxObj) }); }
                catch (Exception ex) { Log.Error($"paint() error: {ex.Message}"); }
            }
            Array.Copy(_backBuffer, Framebuffer, _backBuffer.Length);
        }
    }

    public void SendKeyEvent(JvmThread thread, int keyCode, bool pressed)
    {
        var canvas = CanvasObject ?? CurrentDisplayable;
        if (canvas == null) return;

        int keyBit = KeyCodeToBit(keyCode);
        if (keyBit != 0) { if (pressed) PressedKeys |= keyBit; else PressedKeys &= ~keyBit; }

        string methodName = pressed ? "keyPressed" : "keyReleased";
        var method = canvas.Class.FindMethod(methodName, "(I)V");
        if (method != null)
        {
            try { thread.Invoke(method, new[] { JValue.OfRef(canvas), JValue.OfInt(keyCode) }); }
            catch (Exception ex) { Log.Error($"{methodName}({keyCode}) error: {ex.Message}"); }
        }

        int ga = KeyCodeToGameAction(keyCode);
        if (ga > 0)
        {
            int bit = 1 << ga;
            if (pressed) _keyStates |= bit;
            else _keyStates &= ~bit;
        }
    }

    public const int BIT_UP = 1, BIT_DOWN = 2, BIT_LEFT = 4, BIT_RIGHT = 8,
        BIT_FIRE = 16, BIT_SOFT_L = 32, BIT_SOFT_R = 64, BIT_NUM0 = 128,
        BIT_NUM1 = 256, BIT_NUM2 = 512, BIT_NUM3 = 1024, BIT_NUM4 = 2048,
        BIT_NUM5 = 4096, BIT_NUM6 = 8192, BIT_NUM7 = 16384, BIT_NUM8 = 32768,
        BIT_NUM9 = 65536;

    public static int KeyCodeToBit(int kc) => kc switch
    {
        KEY_UP => BIT_UP, KEY_DOWN => BIT_DOWN, KEY_LEFT => BIT_LEFT, KEY_RIGHT => BIT_RIGHT,
        KEY_FIRE => BIT_FIRE, KEY_SOFT_LEFT => BIT_SOFT_L, KEY_SOFT_RIGHT => BIT_SOFT_R,
        KEY_NUM0 => BIT_NUM0, KEY_NUM1 => BIT_NUM1, KEY_NUM2 => BIT_NUM2, KEY_NUM3 => BIT_NUM3,
        KEY_NUM4 => BIT_NUM4, KEY_NUM5 => BIT_NUM5, KEY_NUM6 => BIT_NUM6,
        KEY_NUM7 => BIT_NUM7, KEY_NUM8 => BIT_NUM8, KEY_NUM9 => BIT_NUM9,
        _ => 0
    };

    public JavaObject GetScreenGraphics(JvmClassLoader loader)
    {
        var cls = loader.LoadClass("javax/microedition/lcdui/Graphics");
        var gfx = new GraphicsContext(_backBuffer, ScreenWidth, ScreenHeight);
        return new JavaObject(cls, gfx);
    }

    public void FlushGraphics()
    {
        lock (_paintLock) Array.Copy(_backBuffer, Framebuffer, _backBuffer.Length);
    }

    public int GetKeyStates()
    {
        int ks = _keyStates;
        return ks;
    }

    public JavaObject? LoadImageFromResource(JvmThread thread, string? path)
    {
        if (path == null) return null;
        var data = thread.Loader.GetResource(path);
        if (data == null)
        {
            Log.Error($"Image resource not found: {path}");
            return null;
        }
        return LoadImageFromBytes(thread.Loader, data, 0, data.Length);
    }

    public JavaObject? LoadImageFromBytes(JvmClassLoader loader, byte[] data, int offset, int length)
    {
        try
        {
            PngDecoder.Decode(data, offset, length, out int[] pixels, out int w, out int h);
            return CreateImageObject(loader, w, h, pixels);
        }
        catch (Exception ex)
        {
            Log.Error($"Image decode failed: {ex.Message}");
            return CreateImageObject(loader, 1, 1, new[] { unchecked((int)0xFFFF00FF) });
        }
    }

    public static JavaObject CreateImageObject(JvmClassLoader loader, int w, int h, int[] pixels)
    {
        var cls = loader.LoadClass("javax/microedition/lcdui/Image");
        return new JavaObject(cls, new ImageData { Width = w, Height = h, Pixels = pixels });
    }

    public static int[] ApplyTransform(int[] pixels, int w, int h, int transform, out int outW, out int outH)
    {
        if (transform == 0) { outW = w; outH = h; return pixels; }
        bool swap = transform == 6 || transform == 4 || transform == 7 || transform == 1;
        outW = swap ? h : w;
        outH = swap ? w : h;
        var result = new int[outW * outH];
        for (int iy = 0; iy < h; iy++)
            for (int ix = 0; ix < w; ix++)
            {
                int tx, ty;
                switch (transform)
                {
                    default: tx = ix; ty = iy; break;
                    case 5: tx = w - 1 - ix; ty = iy; break;
                    case 6: tx = h - 1 - iy; ty = ix; break;
                    case 3: tx = w - 1 - ix; ty = h - 1 - iy; break;
                    case 4: tx = iy; ty = w - 1 - ix; break;
                    case 7: tx = h - 1 - iy; ty = w - 1 - ix; break;
                    case 1: tx = iy; ty = ix; break;
                    case 2: tx = w - 1 - ix; ty = h - 1 - iy; break;
                }
                if (tx >= 0 && tx < outW && ty >= 0 && ty < outH)
                    result[ty * outW + tx] = pixels[iy * w + ix];
            }
        return result;
    }

    public static JavaObject CreateGraphicsForImage(JvmClassLoader loader, ImageData img)
    {
        var cls = loader.LoadClass("javax/microedition/lcdui/Graphics");
        return new JavaObject(cls, new GraphicsContext(img.Pixels, img.Width, img.Height));
    }

    public static JavaObject CreateFontObject(JvmClassLoader loader, int face, int style, int size)
    {
        var cls = loader.LoadClass("javax/microedition/lcdui/Font");
        int h = size switch { 8 => 10, 0 => 12, 16 => 14, _ => 12 };
        int cw = size switch { 8 => 5, 0 => 6, 16 => 7, _ => 6 };
        return new JavaObject(cls, new FontData { Face = face, Style = style, Size = size, Height = h, CharWidth = cw });
    }

    public const int GA_UP = 1, GA_DOWN = 6, GA_LEFT = 2, GA_RIGHT = 5, GA_FIRE = 8;
    public const int GA_GAME_A = 9, GA_GAME_B = 10, GA_GAME_C = 11, GA_GAME_D = 12;
    public const int KEY_NUM0 = 48, KEY_NUM1 = 49, KEY_NUM2 = 50, KEY_NUM3 = 51,
        KEY_NUM4 = 52, KEY_NUM5 = 53, KEY_NUM6 = 54, KEY_NUM7 = 55,
        KEY_NUM8 = 56, KEY_NUM9 = 57, KEY_STAR = 42, KEY_POUND = 35;
    public const int KEY_UP = -1, KEY_DOWN = -2, KEY_LEFT = -3, KEY_RIGHT = -4,
        KEY_FIRE = -5, KEY_SOFT_LEFT = -6, KEY_SOFT_RIGHT = -7;

    public static int KeyCodeToGameAction(int keyCode) => keyCode switch
    {
        KEY_UP or KEY_NUM2 => GA_UP, KEY_DOWN or KEY_NUM8 => GA_DOWN,
        KEY_LEFT or KEY_NUM4 => GA_LEFT, KEY_RIGHT or KEY_NUM6 => GA_RIGHT,
        KEY_FIRE or KEY_NUM5 => GA_FIRE, KEY_NUM1 => GA_GAME_A, KEY_NUM3 => GA_GAME_B,
        KEY_NUM7 => GA_GAME_C, KEY_NUM9 => GA_GAME_D, _ => 0
    };

    public static int GameActionToKeyCode(int action) => action switch
    {
        GA_UP => KEY_UP, GA_DOWN => KEY_DOWN, GA_LEFT => KEY_LEFT,
        GA_RIGHT => KEY_RIGHT, GA_FIRE => KEY_FIRE,
        GA_GAME_A => KEY_NUM1, GA_GAME_B => KEY_NUM3,
        GA_GAME_C => KEY_NUM7, GA_GAME_D => KEY_NUM9, _ => 0
    };

    public static string GetKeyName(int keyCode) => keyCode switch
    {
        KEY_UP => "UP", KEY_DOWN => "DOWN", KEY_LEFT => "LEFT", KEY_RIGHT => "RIGHT",
        KEY_FIRE => "SELECT", KEY_SOFT_LEFT => "SOFT1", KEY_SOFT_RIGHT => "SOFT2",
        >= KEY_NUM0 and <= KEY_NUM9 => ((char)keyCode).ToString(),
        KEY_STAR => "*", KEY_POUND => "#", _ => $"KEY{keyCode}"
    };

    // ── Data types ──────────────────────────────────────────────

    public class ImageData
    {
        public int Width, Height;
        public int[] Pixels = Array.Empty<int>();
        public bool Mutable = false;
    }

    public class FontData
    {
        public int Face, Style, Size;
        public int Height = 12, CharWidth = 6;
    }

    public class SpriteData
    {
        public ImageData Source;
        public int FrameW, FrameH;
        public int X, Y;
        public int RefPixelX, RefPixelY;
        public int ColRectX, ColRectY, ColRectW, ColRectH;
        public int Transform;
        public int CurrentFrame;
        public int[] FrameSequence;
        public bool Visible = true;

        public int Cols => Source.Width / Math.Max(FrameW, 1);
        public int Rows => Source.Height / Math.Max(FrameH, 1);
        public int RawFrameCount => Math.Max(1, Cols * Rows);

        public SpriteData(ImageData src, int fw, int fh)
        {
            Source = src;
            FrameW = fw > 0 ? fw : src.Width;
            FrameH = fh > 0 ? fh : src.Height;
            ColRectW = FrameW; ColRectH = FrameH;
            int count = RawFrameCount;
            FrameSequence = new int[count];
            for (int i = 0; i < count; i++) FrameSequence[i] = i;
        }

        public void Paint(GraphicsContext g)
        {
            if (!Visible) return;
            int fi = CurrentFrame >= 0 && CurrentFrame < FrameSequence.Length
                ? FrameSequence[CurrentFrame] : 0;
            int cols = Cols;
            if (cols <= 0) return;
            int srcX = (fi % cols) * FrameW;
            int srcY = (fi / cols) * FrameH;
            g.DrawRegion(Source, srcX, srcY, FrameW, FrameH, Transform,
                X - RefPixelX, Y - RefPixelY, 0);
        }

        public bool CollidesBounds(int otherX, int otherY, int otherW, int otherH)
        {
            int ax = X - RefPixelX + ColRectX, ay = Y - RefPixelY + ColRectY;
            return ax < otherX + otherW && ax + ColRectW > otherX
                && ay < otherY + otherH && ay + ColRectH > otherY;
        }
    }

    public class TiledLayerData
    {
        public ImageData Source;
        public int TileW, TileH;
        public int GridCols, GridRows;
        public int[,] Cells;
        public int X, Y;
        public bool Visible = true;
        public int Cols => Source.Width / Math.Max(TileW, 1);
        public int Rows => Source.Height / Math.Max(TileH, 1);
        public int TileCount => Math.Max(1, Cols * Rows);
        Dictionary<int, int> _animTiles = new();
        int _nextAnimId = -1;

        public TiledLayerData(ImageData src, int tw, int th, int cols, int rows)
        {
            Source = src; TileW = tw; TileH = th;
            GridCols = cols; GridRows = rows;
            Cells = new int[rows, cols];
        }

        public int CreateAnimatedTile(int staticIdx)
        {
            int id = _nextAnimId--;
            _animTiles[id] = staticIdx;
            return id;
        }

        public void SetAnimatedTile(int animId, int staticIdx) => _animTiles[animId] = staticIdx;
        public int GetAnimatedTile(int animId) => _animTiles.TryGetValue(animId, out int v) ? v : 0;

        int ResolveTile(int cell)
        {
            if (cell < 0 && _animTiles.TryGetValue(cell, out int s)) return s;
            return cell;
        }

        public void Paint(GraphicsContext g)
        {
            if (!Visible) return;
            int tilesPerRow = Cols;
            if (tilesPerRow <= 0) return;
            for (int r = 0; r < GridRows; r++)
                for (int c = 0; c < GridCols; c++)
                {
                    int tile = ResolveTile(Cells[r, c]);
                    if (tile <= 0) continue;
                    tile--;
                    int srcX = (tile % tilesPerRow) * TileW;
                    int srcY = (tile / tilesPerRow) * TileH;
                    g.DrawRegionRaw(Source, srcX, srcY, TileW, TileH, 0,
                        X + c * TileW, Y + r * TileH);
                }
        }
    }

    public class LayerManagerData
    {
        public List<object> Layers = new();
        public int ViewX, ViewY, ViewW, ViewH;

        public LayerManagerData() { ViewW = 240; ViewH = 320; }

        public void Paint(GraphicsContext g, int x, int y)
        {
            int savedTx = g.TransX, savedTy = g.TransY;
            int savedCx = g.ClipX, savedCy = g.ClipY, savedCw = g.ClipW, savedCh = g.ClipH;
            g.TransX += x - ViewX;
            g.TransY += y - ViewY;
            g.SetClip(x, y, ViewW, ViewH);
            for (int i = Layers.Count - 1; i >= 0; i--)
            {
                if (Layers[i] is SpriteData sd) sd.Paint(g);
                else if (Layers[i] is TiledLayerData td) td.Paint(g);
            }
            g.TransX = savedTx; g.TransY = savedTy;
            g.ClipX = savedCx; g.ClipY = savedCy; g.ClipW = savedCw; g.ClipH = savedCh;
        }
    }

    // ── GraphicsContext with real software rendering ─────────────

    public class GraphicsContext
    {
        public int[] Buffer;
        public int Width, Height;
        public int Color;
        public int ClipX, ClipY, ClipW, ClipH;
        public int TransX, TransY;
        public int StrokeStyle;
        public FontData CurrentFont = new();

        public GraphicsContext(int[] buffer, int w, int h)
        {
            Buffer = buffer; Width = w; Height = h;
            ClipW = w; ClipH = h;
        }

        public void SetColor(int r, int g, int b) => Color = (r << 16) | (g << 8) | b;
        public void SetColor(int rgb) => Color = rgb & 0xFFFFFF;

        public void SetClip(int x, int y, int w, int h) { ClipX = x; ClipY = y; ClipW = w; ClipH = h; }
        public void ClipRect(int x, int y, int w, int h)
        {
            int x2 = Math.Max(ClipX, x), y2 = Math.Max(ClipY, y);
            int r = Math.Min(ClipX + ClipW, x + w), b = Math.Min(ClipY + ClipH, y + h);
            ClipX = x2; ClipY = y2; ClipW = Math.Max(0, r - x2); ClipH = Math.Max(0, b - y2);
        }

        public void PutPixel(int x, int y)
        {
            x += TransX; y += TransY;
            if (x >= ClipX && x < ClipX + ClipW && y >= ClipY && y < ClipY + ClipH
                && x >= 0 && x < Width && y >= 0 && y < Height)
                Buffer[y * Width + x] = unchecked((int)0xFF000000) | Color;
        }

        public void DrawPixel(int x, int y, int argb) => PutPixelArgb(x, y, argb);

        public void PutPixelArgb(int x, int y, int argb)
        {
            x += TransX; y += TransY;
            if (x >= ClipX && x < ClipX + ClipW && y >= ClipY && y < ClipY + ClipH
                && x >= 0 && x < Width && y >= 0 && y < Height)
            {
                int a = (argb >> 24) & 0xFF;
                if (a == 0) return;
                int idx = y * Width + x;
                if (a == 255) { Buffer[idx] = argb; return; }
                int dst = Buffer[idx];
                int inv = 255 - a;
                int r = (((argb >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv) >> 8;
                int g = (((argb >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv) >> 8;
                int b = ((argb & 0xFF) * a + (dst & 0xFF) * inv) >> 8;
                Buffer[idx] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
        }

        void PutPixelRaw(int x, int y, int argb)
        {
            if (x >= ClipX && x < ClipX + ClipW && y >= ClipY && y < ClipY + ClipH
                && x >= 0 && x < Width && y >= 0 && y < Height)
            {
                int a = (argb >> 24) & 0xFF;
                if (a == 0) return;
                int idx = y * Width + x;
                if (a == 255) { Buffer[idx] = argb; return; }
                int dst = Buffer[idx];
                int inv = 255 - a;
                Buffer[idx] = unchecked((int)0xFF000000)
                    | ((((argb >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv) >> 8 << 16)
                    | ((((argb >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv) >> 8 << 8)
                    | (((argb & 0xFF) * a + (dst & 0xFF) * inv) >> 8);
            }
        }

        public void DrawLine(int x1, int y1, int x2, int y2)
        {
            int dx = Math.Abs(x2 - x1), dy = Math.Abs(y2 - y1);
            int sx = x1 < x2 ? 1 : -1, sy = y1 < y2 ? 1 : -1;
            int err = dx - dy;
            for (int i = 0; i < dx + dy + 1; i++)
            {
                PutPixel(x1, y1);
                if (x1 == x2 && y1 == y2) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x1 += sx; }
                if (e2 < dx) { err += dx; y1 += sy; }
            }
        }

        public void DrawRect(int x, int y, int w, int h)
        {
            for (int i = 0; i <= w; i++) { PutPixel(x + i, y); PutPixel(x + i, y + h); }
            for (int i = 1; i < h; i++) { PutPixel(x, y + i); PutPixel(x + w, y + i); }
        }

        public void FillRect(int x, int y, int w, int h)
        {
            int argb = unchecked((int)0xFF000000) | Color;
            int ax = x + TransX, ay = y + TransY;
            int x1 = Math.Max(ax, Math.Max(ClipX, 0));
            int y1 = Math.Max(ay, Math.Max(ClipY, 0));
            int x2 = Math.Min(ax + w, Math.Min(ClipX + ClipW, Width));
            int y2 = Math.Min(ay + h, Math.Min(ClipY + ClipH, Height));
            for (int py = y1; py < y2; py++)
            {
                int off = py * Width;
                for (int px = x1; px < x2; px++) Buffer[off + px] = argb;
            }
        }

        public void DrawRoundRect(int x, int y, int w, int h, int aw, int ah)
        {
            int rx = aw / 2, ry = ah / 2;
            rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
            DrawLine(x + rx, y, x + w - rx, y);
            DrawLine(x + rx, y + h, x + w - rx, y + h);
            DrawLine(x, y + ry, x, y + h - ry);
            DrawLine(x + w, y + ry, x + w, y + h - ry);
            DrawArcPixels(x + rx, y + ry, rx, ry, 90, 90, false);
            DrawArcPixels(x + w - rx, y + ry, rx, ry, 0, 90, false);
            DrawArcPixels(x + w - rx, y + h - ry, rx, ry, 270, 90, false);
            DrawArcPixels(x + rx, y + h - ry, rx, ry, 180, 90, false);
        }

        public void FillRoundRect(int x, int y, int w, int h, int aw, int ah)
        {
            int rx = Math.Min(aw / 2, w / 2), ry = Math.Min(ah / 2, h / 2);
            FillRect(x, y + ry, w, h - 2 * ry);
            for (int dy = 0; dy <= ry; dy++)
            {
                int dx = ry > 0 ? (int)(rx * Math.Sqrt(1.0 - (double)(dy * dy) / (ry * ry))) : rx;
                FillRect(x + rx - dx, y + ry - dy, w - 2 * (rx - dx), 1);
                FillRect(x + rx - dx, y + h - ry + dy, w - 2 * (rx - dx), 1);
            }
        }

        public void DrawArc(int x, int y, int w, int h, int startAngle, int arcAngle)
        {
            DrawArcPixels(x + w / 2, y + h / 2, w / 2, h / 2, startAngle, arcAngle, false);
        }

        public void FillArc(int x, int y, int w, int h, int startAngle, int arcAngle)
        {
            int cx = x + w / 2, cy = y + h / 2;
            int rx = w / 2, ry = h / 2;
            if (rx <= 0 || ry <= 0) return;
            double startRad = startAngle * Math.PI / 180.0;
            double endRad = (startAngle + arcAngle) * Math.PI / 180.0;
            if (arcAngle < 0) (startRad, endRad) = (endRad, startRad);
            for (int dy = -ry; dy <= ry; dy++)
            {
                int dx = (int)(rx * Math.Sqrt(Math.Max(0, 1.0 - (double)(dy * dy) / (ry * ry))));
                for (int ddx = -dx; ddx <= dx; ddx++)
                {
                    double angle = Math.Atan2(-dy, ddx);
                    if (angle < 0) angle += 2 * Math.PI;
                    double sa = startRad % (2 * Math.PI); if (sa < 0) sa += 2 * Math.PI;
                    double ea = endRad % (2 * Math.PI); if (ea < 0) ea += 2 * Math.PI;
                    bool inside = (arcAngle >= 360 || arcAngle <= -360) ||
                        (sa <= ea ? (angle >= sa && angle <= ea) : (angle >= sa || angle <= ea));
                    if (inside) PutPixel(cx + ddx, cy + dy);
                }
            }
        }

        void DrawArcPixels(int cx, int cy, int rx, int ry, int startAngle, int arcAngle, bool fill)
        {
            if (rx <= 0 || ry <= 0) return;
            int steps = Math.Max(16, (rx + ry) * 2);
            double sa = startAngle * Math.PI / 180.0;
            double ea = (startAngle + arcAngle) * Math.PI / 180.0;
            for (int i = 0; i <= steps; i++)
            {
                double a = sa + (ea - sa) * i / steps;
                PutPixel(cx + (int)(rx * Math.Cos(a)), cy - (int)(ry * Math.Sin(a)));
            }
        }

        public void FillTriangle(int x1, int y1, int x2, int y2, int x3, int y3)
        {
            int minY = Math.Min(y1, Math.Min(y2, y3)), maxY = Math.Max(y1, Math.Max(y2, y3));
            for (int y = minY; y <= maxY; y++)
            {
                int minX = int.MaxValue, maxX = int.MinValue;
                ScanEdge(x1, y1, x2, y2, y, ref minX, ref maxX);
                ScanEdge(x2, y2, x3, y3, y, ref minX, ref maxX);
                ScanEdge(x3, y3, x1, y1, y, ref minX, ref maxX);
                if (minX <= maxX) for (int x = minX; x <= maxX; x++) PutPixel(x, y);
            }
        }

        static void ScanEdge(int x1, int y1, int x2, int y2, int y, ref int minX, ref int maxX)
        {
            if ((y1 <= y && y2 >= y) || (y2 <= y && y1 >= y))
            {
                if (y1 == y2) { minX = Math.Min(minX, Math.Min(x1, x2)); maxX = Math.Max(maxX, Math.Max(x1, x2)); }
                else
                {
                    int x = x1 + (y - y1) * (x2 - x1) / (y2 - y1);
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                }
            }
        }

        public void DrawString(string text, int x, int y, int anchor)
        {
            int cw = CurrentFont.CharWidth, ch = CurrentFont.Height;
            int sw = text.Length * cw;
            if ((anchor & 1) != 0) x -= sw / 2;       // HCENTER
            else if ((anchor & 8) != 0) x -= sw;       // RIGHT
            if ((anchor & 0x20) != 0) y -= ch;         // BOTTOM
            else if ((anchor & 0x40) != 0) y -= ch - 2; // BASELINE

            int argb = unchecked((int)0xFF000000) | Color;
            foreach (char c in text)
            {
                BitmapFont.DrawChar(this, c, x, y, cw, ch, argb);
                x += cw;
            }
        }

        public void DrawImage(ImageData img, int x, int y, int anchor)
        {
            if ((anchor & 1) != 0) x -= img.Width / 2;       // HCENTER
            else if ((anchor & 8) != 0) x -= img.Width;       // RIGHT
            if ((anchor & 2) != 0) y -= img.Height / 2;       // VCENTER
            else if ((anchor & 0x20) != 0) y -= img.Height;   // BOTTOM

            for (int iy = 0; iy < img.Height; iy++)
                for (int ix = 0; ix < img.Width; ix++)
                    PutPixelArgb(x + ix, y + iy, img.Pixels[iy * img.Width + ix]);
        }

        public void DrawRegion(ImageData img, int srcX, int srcY, int srcW, int srcH,
            int transform, int dstX, int dstY, int anchor)
        {
            int outW = srcW, outH = srcH;
            if (transform == 6 || transform == 4 || transform == 7 || transform == 1)
            { outW = srcH; outH = srcW; }

            if ((anchor & 1) != 0) dstX -= outW / 2;          // HCENTER
            else if ((anchor & 8) != 0) dstX -= outW;          // RIGHT
            if ((anchor & 2) != 0) dstY -= outH / 2;           // VCENTER
            else if ((anchor & 0x20) != 0) dstY -= outH;       // BOTTOM

            DrawRegionRaw(img, srcX, srcY, srcW, srcH, transform, dstX, dstY);
        }

        public void DrawRegionRaw(ImageData img, int srcX, int srcY, int srcW, int srcH,
            int transform, int dstX, int dstY)
        {
            for (int iy = 0; iy < srcH; iy++)
                for (int ix = 0; ix < srcW; ix++)
                {
                    int sx = srcX + ix, sy = srcY + iy;
                    if (sx < 0 || sx >= img.Width || sy < 0 || sy >= img.Height) continue;
                    TransformPoint(ix, iy, srcW, srcH, transform, out int tx, out int ty);
                    PutPixelArgb(dstX + tx, dstY + ty, img.Pixels[sy * img.Width + sx]);
                }
        }

        static void TransformPoint(int ix, int iy, int w, int h, int t, out int tx, out int ty)
        {
            switch (t)
            {
                default: tx = ix; ty = iy; break;                               // TRANS_NONE
                case 5: tx = w - 1 - ix; ty = iy; break;                        // TRANS_MIRROR
                case 6: tx = h - 1 - iy; ty = ix; break;                        // TRANS_ROT90
                case 3: tx = w - 1 - ix; ty = h - 1 - iy; break;               // TRANS_ROT180
                case 4: tx = iy; ty = w - 1 - ix; break;                        // TRANS_ROT270
                case 7: tx = h - 1 - iy; ty = w - 1 - ix; break;               // TRANS_MIRROR_ROT90
                case 1: tx = iy; ty = ix; break;                                // TRANS_MIRROR_ROT180
                case 2: tx = w - 1 - ix; ty = h - 1 - iy; break;               // TRANS_MIRROR_ROT270
            }
        }

        public void DrawRGB(int[] rgbData, int offset, int scanlength, int x, int y, int w, int h)
        {
            for (int iy = 0; iy < h; iy++)
                for (int ix = 0; ix < w; ix++)
                {
                    int srcIdx = offset + iy * scanlength + ix;
                    if ((uint)srcIdx < (uint)rgbData.Length)
                        PutPixelArgb(x + ix, y + iy, rgbData[srcIdx]);
                }
        }

        public void CopyArea(int srcX, int srcY, int w, int h, int dstX, int dstY, int anchor)
        {
            if ((anchor & 1) != 0) dstX -= w / 2;            // HCENTER
            else if ((anchor & 8) != 0) dstX -= w;            // RIGHT
            if ((anchor & 2) != 0) dstY -= h / 2;             // VCENTER
            else if ((anchor & 0x20) != 0) dstY -= h;         // BOTTOM
            var tmp = new int[w * h];
            for (int r = 0; r < h; r++)
            {
                int sy = srcY + r, dy = r;
                if (sy >= 0 && sy < Height)
                    for (int c = 0; c < w; c++)
                    {
                        int sx = srcX + c;
                        if (sx >= 0 && sx < Width) tmp[dy * w + c] = Buffer[sy * Width + sx];
                    }
            }
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    PutPixelArgb(dstX + c, dstY + r, tmp[r * w + c]);
        }
    }
}

// ── Real PNG Decoder ────────────────────────────────────────────

static class PngDecoder
{
    public static void Decode(byte[] data, int offset, int length, out int[] pixels, out int w, out int h)
    {
        int pos = offset;
        int end = offset + length;

        if (length < 8 || data[pos] != 0x89 || data[pos + 1] != 0x50)
            throw new Exception("Not a PNG");
        pos += 8;

        w = 0; h = 0;
        int bitDepth = 8, colorType = 2;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idatStream = new MemoryStream();

        while (pos + 8 <= end)
        {
            int chunkLen = R32(data, pos); pos += 4;
            string type = "" + (char)data[pos] + (char)data[pos + 1] + (char)data[pos + 2] + (char)data[pos + 3];
            pos += 4;
            int chunkData = pos;
            pos += chunkLen + 4;

            switch (type)
            {
                case "IHDR":
                    w = R32(data, chunkData);
                    h = R32(data, chunkData + 4);
                    bitDepth = data[chunkData + 8];
                    colorType = data[chunkData + 9];
                    break;
                case "PLTE":
                    palette = new byte[chunkLen];
                    Array.Copy(data, chunkData, palette, 0, chunkLen);
                    break;
                case "tRNS":
                    trns = new byte[chunkLen];
                    Array.Copy(data, chunkData, trns, 0, chunkLen);
                    break;
                case "IDAT":
                    idatStream.Write(data, chunkData, chunkLen);
                    break;
                case "IEND":
                    goto done;
            }
        }
        done:

        w = Math.Clamp(w, 1, 2048);
        h = Math.Clamp(h, 1, 2048);

        int bpp = BitsPerPixel(colorType, bitDepth);
        int bytesPP = Math.Max(1, bpp / 8);
        int stride = (w * bpp + 7) / 8;

        idatStream.Position = 0;
        idatStream.ReadByte(); idatStream.ReadByte();
        using var deflate = new DeflateStream(idatStream, CompressionMode.Decompress);
        byte[] raw = new byte[(stride + 1) * h];
        int totalRead = 0;
        while (totalRead < raw.Length)
        {
            int n = deflate.Read(raw, totalRead, raw.Length - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }

        byte[] prev = new byte[stride];
        byte[] cur = new byte[stride];
        pixels = new int[w * h];

        for (int y = 0; y < h; y++)
        {
            int rowStart = y * (stride + 1);
            if (rowStart >= totalRead) break;
            byte filterType = raw[rowStart];
            Array.Copy(raw, rowStart + 1, cur, 0, Math.Min(stride, totalRead - rowStart - 1));

            Unfilter(filterType, cur, prev, bytesPP, stride);

            DecodeRow(cur, pixels, y, w, bitDepth, colorType, palette, trns);
            (prev, cur) = (cur, prev);
        }
    }

    static int R32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

    static int BitsPerPixel(int ct, int bd) => ct switch
    {
        0 => bd,
        2 => bd * 3,
        3 => bd,
        4 => bd * 2,
        6 => bd * 4,
        _ => bd
    };

    static void Unfilter(byte ft, byte[] cur, byte[] prev, int bpp, int len)
    {
        switch (ft)
        {
            case 1:
                for (int i = bpp; i < len; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < len; i++) cur[i] = (byte)(cur[i] + prev[i]);
                break;
            case 3:
                for (int i = 0; i < len; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + (a + prev[i]) / 2);
                }
                break;
            case 4:
                for (int i = 0; i < len; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + PaethPredictor(a, b, c));
                }
                break;
        }
    }

    static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static void DecodeRow(byte[] row, int[] pixels, int y, int w, int bd, int ct, byte[]? pal, byte[]? trns)
    {
        int off = y * w;
        switch (ct)
        {
            case 0: // Grayscale
                for (int x = 0; x < w; x++)
                {
                    int g = SampleAt(row, x, bd);
                    if (bd < 8) g = g * 255 / ((1 << bd) - 1);
                    int a = (trns != null && trns.Length >= 2 && g == ((trns[0] << 8) | trns[1])) ? 0 : 255;
                    pixels[off + x] = (a << 24) | (g << 16) | (g << 8) | g;
                }
                break;
            case 2: // RGB
                for (int x = 0; x < w; x++)
                {
                    int i = x * 3;
                    if (i + 2 >= row.Length) break;
                    int r = row[i], g = row[i + 1], b = row[i + 2];
                    int a = 255;
                    if (trns != null && trns.Length >= 6 && r == trns[1] && g == trns[3] && b == trns[5]) a = 0;
                    pixels[off + x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
                break;
            case 3: // Indexed
                if (pal == null) break;
                for (int x = 0; x < w; x++)
                {
                    int idx = SampleAt(row, x, bd);
                    int pi = idx * 3;
                    if (pi + 2 >= pal.Length) break;
                    int a = (trns != null && idx < trns.Length) ? trns[idx] : 255;
                    pixels[off + x] = (a << 24) | (pal[pi] << 16) | (pal[pi + 1] << 8) | pal[pi + 2];
                }
                break;
            case 4: // Grayscale + Alpha
                for (int x = 0; x < w; x++)
                {
                    int i = x * 2;
                    if (i + 1 >= row.Length) break;
                    int g = row[i], a = row[i + 1];
                    pixels[off + x] = (a << 24) | (g << 16) | (g << 8) | g;
                }
                break;
            case 6: // RGBA
                for (int x = 0; x < w; x++)
                {
                    int i = x * 4;
                    if (i + 3 >= row.Length) break;
                    pixels[off + x] = (row[i + 3] << 24) | (row[i] << 16) | (row[i + 1] << 8) | row[i + 2];
                }
                break;
        }
    }

    static int SampleAt(byte[] row, int x, int bd)
    {
        if (bd == 8) return x < row.Length ? row[x] : 0;
        if (bd == 16) { int i = x * 2; return i + 1 < row.Length ? (row[i] << 8) | row[i + 1] : 0; }
        int samplesPerByte = 8 / bd;
        int byteIdx = x / samplesPerByte;
        int bitIdx = (samplesPerByte - 1 - (x % samplesPerByte)) * bd;
        if (byteIdx >= row.Length) return 0;
        return (row[byteIdx] >> bitIdx) & ((1 << bd) - 1);
    }
}

// ── Minimal bitmap font (5x7 ASCII 32-126) ─────────────────────

static class BitmapFont
{
    static readonly uint[] _glyphs = BuildFont();

    static uint[] BuildFont()
    {
        string[] data = {
            "0000000", "2092220", "5500000", "5F5F500", "2E8E240", "C9224C0", "6969680",
            "2200000", "1222100", "4222400", "0A4A000", "024E200", "0000220", "000E000",
            "0000020", "1224800", "6999600", "2622270", "E12E8F0", "E1E1E00", "999F110",
            "F8E1E00", "68E9600", "F112200", "6969600", "6971600", "0020200", "0020240",
            "1242100", "00E0E00", "4212400", "E102020", "69BB860", "699F990", "E99E9E0",
            "7888870", "E9999E0", "F8E88F0", "F8E8880", "7889970", "99F9990", "7222270",
            "1119960", "9ACCA90", "888888F", "9FF9990", "9DDBB90", "6999960", "E99E880",
            "6999610", "E99EA90", "78E1E00", "F222220", "9999960", "9999620", "999FF90",
            "9966990", "9962220", "F12248F", "3222230", "8442100", "6222260", "2500000",
            "000000F", "4200000", "06999F0", "88E99E0", "0688860", "11799F0", "069F860",
            "3484440", "07971E0", "88E9990", "2022220", "1011960", "89ACA90", "6222230",
            "09F9990", "0E99990", "0699960", "0E99E80", "07991F0", "0AC8880", "078E1E0",
            "44E4430", "0999970", "0999620", "099FF90", "0966990", "099971E", "0F248F0",
            "1242210", "2222222", "4212240", "0050A00"
        };
        var g = new uint[95];
        for (int i = 0; i < data.Length && i < 95; i++)
        {
            uint v = 0;
            for (int j = 0; j < 7 && j < data[i].Length; j++)
            {
                int nibble = data[i][j] >= 'A' ? data[i][j] - 'A' + 10 : data[i][j] - '0';
                v |= (uint)nibble << (24 - j * 4);
            }
            g[i] = v;
        }
        return g;
    }

    public static void DrawChar(MidletHost.GraphicsContext g, char c, int x, int y, int cw, int ch, int argb)
    {
        if (c == ' ') return;
        int idx = c - 32;
        if (idx < 0 || idx >= _glyphs.Length) idx = '?' - 32;
        uint glyph = _glyphs[idx];

        float scaleX = cw / 4f, scaleY = ch / 7f;
        int ax = x + g.TransX, ay = y + g.TransY;
        for (int row = 0; row < 7; row++)
        {
            int nibble = (int)((glyph >> (24 - row * 4)) & 0xF);
            for (int col = 0; col < 4; col++)
            {
                if ((nibble & (8 >> col)) != 0)
                {
                    int px0 = ax + (int)(col * scaleX), px1 = ax + (int)((col + 1) * scaleX);
                    int py0 = ay + (int)(row * scaleY), py1 = ay + (int)((row + 1) * scaleY);
                    for (int py = py0; py < py1; py++)
                    {
                        if (py < g.ClipY || py >= g.ClipY + g.ClipH || py < 0 || py >= g.Height) continue;
                        int off = py * g.Width;
                        for (int px = px0; px < px1; px++)
                            if (px >= g.ClipX && px < g.ClipX + g.ClipW && px >= 0 && px < g.Width)
                                g.Buffer[off + px] = argb;
                    }
                }
            }
        }
    }
}
