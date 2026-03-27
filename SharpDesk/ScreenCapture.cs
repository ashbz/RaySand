using System.Runtime.InteropServices;

namespace SharpDesk;

interface ICapture : IDisposable
{
    int Width { get; }
    int Height { get; }
    int FrameSize { get; }
    bool CaptureFrame(byte[] buffer);
}

/// <summary>GDI BitBlt capture. Slower than DXGI but works everywhere (RDP, older Windows).</summary>
sealed unsafe class ScreenCapture : ICapture
{
    const int SRCCOPY = 0x00CC0020, DI_NORMAL = 3, CURSOR_SHOWING = 1;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] static extern int    GetSystemMetrics(int n);
    [DllImport("user32.dll")] static extern bool   GetCursorInfo(ref CursorInfo ci);
    [DllImport("user32.dll")] static extern bool   DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon,
                                                               int cxW, int cyW, uint step, IntPtr hbr, int flags);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")]  static extern bool   BitBlt(IntPtr dst, int x, int y, int w, int h,
                                                           IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")]  static extern int    GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                                                              IntPtr bits, ref BITMAPINFO bi, uint usage);
    [DllImport("gdi32.dll")]  static extern bool   DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")]  static extern bool   DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPPM, biYPPM, biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER h; }

    [StructLayout(LayoutKind.Sequential)]
    struct CursorInfo { public int cbSize, flags; public IntPtr hCursor; public int ptX, ptY; }

    readonly IntPtr _deskDc, _memDc, _bmp, _oldBmp;
    BITMAPINFO _bmi;

    public int Width  { get; }
    public int Height { get; }
    public int FrameSize => Width * Height * 4;

    public ScreenCapture()
    {
        Width  = GetSystemMetrics(0);
        Height = GetSystemMetrics(1);
        _deskDc = GetDC(IntPtr.Zero);
        _memDc  = CreateCompatibleDC(_deskDc);
        _bmp    = CreateCompatibleBitmap(_deskDc, Width, Height);
        _oldBmp = SelectObject(_memDc, _bmp);
        _bmi = new BITMAPINFO { h = new BITMAPINFOHEADER
        {
            biSize = sizeof(BITMAPINFOHEADER), biWidth = Width, biHeight = -Height,
            biPlanes = 1, biBitCount = 32
        }};
    }

    public bool CaptureFrame(byte[] buffer)
    {
        BitBlt(_memDc, 0, 0, Width, Height, _deskDc, 0, 0, SRCCOPY);

        var ci = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0)
            DrawIconEx(_memDc, ci.ptX, ci.ptY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);

        fixed (byte* p = buffer)
        {
            if (GetDIBits(_memDc, _bmp, 0, (uint)Height, (IntPtr)p, ref _bmi, 0) <= 0)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        SelectObject(_memDc, _oldBmp);
        DeleteObject(_bmp);
        DeleteDC(_memDc);
        ReleaseDC(IntPtr.Zero, _deskDc);
    }
}
