using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Raylib_CsLo;

namespace GcEmu;

class SoftwareRenderer
{
    GcBus _bus = null!;

    public const int EfbWidth = 640;
    public const int EfbHeight = 528;

    public uint[] EfbColor = new uint[EfbWidth * EfbHeight];
    public uint[] EfbDepth = new uint[EfbWidth * EfbHeight];

    readonly object _snapLock = new();
    uint[] _snapEfb = new uint[EfbWidth * EfbHeight];
    bool _snapDirty;

    public int DispWidth = 640;
    public int DispHeight = 480;

    public void Init(GcBus bus) => _bus = bus;

    public void ClearEfb(byte r, byte g, byte b, byte a, uint z)
    {
        uint color = (uint)(a << 24 | r << 16 | g << 8 | b);
        Array.Fill(EfbColor, color);
        Array.Fill(EfbDepth, z);
    }

    public void CopyEfbToXfb(uint xfbAddr, int width, int height, int stride)
    {
        if (_bus == null) return;
        uint phys = xfbAddr & 0x01FFFFFF;
        int dstStride = stride > 0 ? stride * 2 : width * 2;

        int maxH = Math.Min(height, EfbHeight);
        int maxW = Math.Min(width, EfbWidth);

        for (int y = 0; y < maxH; y++)
        {
            uint rowDst = phys + (uint)(y * dstStride);
            for (int x = 0; x < maxW; x += 2)
            {
                uint c0 = EfbColor[y * EfbWidth + x];
                uint c1 = x + 1 < maxW ? EfbColor[y * EfbWidth + x + 1] : c0;

                byte r0 = (byte)((c0 >> 16) & 0xFF);
                byte g0 = (byte)((c0 >> 8) & 0xFF);
                byte b0 = (byte)(c0 & 0xFF);

                byte r1 = (byte)((c1 >> 16) & 0xFF);
                byte g1 = (byte)((c1 >> 8) & 0xFF);
                byte b1 = (byte)(c1 & 0xFF);

                byte y0 = RgbToY(r0, g0, b0);
                byte y1 = RgbToY(r1, g1, b1);
                int avgR = (r0 + r1) >> 1;
                int avgG = (g0 + g1) >> 1;
                int avgB = (b0 + b1) >> 1;
                byte cb = RgbToCb((byte)avgR, (byte)avgG, (byte)avgB);
                byte cr = RgbToCr((byte)avgR, (byte)avgG, (byte)avgB);

                uint dst = rowDst + (uint)(x * 2);
                if (dst + 3 < _bus.Ram.Length)
                {
                    _bus.Ram[dst]     = y0;
                    _bus.Ram[dst + 1] = cb;
                    _bus.Ram[dst + 2] = y1;
                    _bus.Ram[dst + 3] = cr;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte RgbToY(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte RgbToCb(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte RgbToCr(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);

    public void BeginPrimitive(int type, int count, int vat, uint[] cpRegs, uint[] xfRegs)
    {
    }

    public void VBlankSnapshot()
    {
        lock (_snapLock)
        {
            Array.Copy(EfbColor, _snapEfb, EfbColor.Length);
            _snapDirty = true;
        }
    }

    public void SnapshotDisplay(Color[] pixels, int texW, int texH)
    {
        uint[] efb;
        lock (_snapLock)
        {
            efb = _snapEfb;
        }

        int maxY = Math.Min(texH, EfbHeight);
        int maxX = Math.Min(texW, EfbWidth);

        for (int y = 0; y < maxY; y++)
        {
            for (int x = 0; x < maxX; x++)
            {
                uint c = efb[y * EfbWidth + x];
                pixels[y * texW + x] = new Color
                {
                    r = (byte)((c >> 16) & 0xFF),
                    g = (byte)((c >> 8) & 0xFF),
                    b = (byte)(c & 0xFF),
                    a = 255
                };
            }
        }
    }

    public void SnapshotXfbDisplay(Color[] pixels, int texW, int texH)
    {
        if (_bus == null) return;

        uint xfbAddr = _bus.Vi.XfbAddr & 0x01FFFFFF;
        if (xfbAddr == 0) return;

        int maxY = Math.Min(texH, _bus.Vi.DispHeight);
        int maxX = Math.Min(texW, _bus.Vi.DispWidth);

        for (int y = 0; y < maxY; y++)
        {
            uint rowAddr = xfbAddr + (uint)(y * maxX * 2);
            for (int x = 0; x < maxX; x += 2)
            {
                uint addr = rowAddr + (uint)(x * 2);
                if (addr + 3 >= _bus.Ram.Length) break;

                byte y0 = _bus.Ram[addr];
                byte cb = _bus.Ram[addr + 1];
                byte y1 = _bus.Ram[addr + 2];
                byte cr = _bus.Ram[addr + 3];

                YCbCrToRgb(y0, cb, cr, out byte r0, out byte g0, out byte b0);
                YCbCrToRgb(y1, cb, cr, out byte r1, out byte g1, out byte b1);

                int pi0 = y * texW + x;
                if (pi0 < pixels.Length)
                    pixels[pi0] = new Color { r = r0, g = g0, b = b0, a = 255 };

                int pi1 = y * texW + x + 1;
                if (pi1 < pixels.Length)
                    pixels[pi1] = new Color { r = r1, g = g1, b = b1, a = 255 };
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void YCbCrToRgb(byte yy, byte cb, byte cr, out byte r, out byte g, out byte b)
    {
        int c = yy - 16;
        int d = cb - 128;
        int e = cr - 128;
        r = (byte)Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
        g = (byte)Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
        b = (byte)Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);
    }

    public void Reset()
    {
        Array.Clear(EfbColor);
        Array.Clear(EfbDepth);
        lock (_snapLock)
        {
            Array.Clear(_snapEfb);
            _snapDirty = false;
        }
    }
}
