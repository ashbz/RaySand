namespace PspEmu;

/// <summary>
/// PSP display controller: manages framebuffer address, format, and VBlank timing.
/// PSP native resolution: 480x272.
/// </summary>
sealed class PspDisplay
{
    public const int ScreenWidth = 480;
    public const int ScreenHeight = 272;

    public uint FrameBufAddr { get; private set; } = 0x0400_0000;
    public int BufWidth { get; private set; } = 512;
    public int PixelFormat { get; private set; } = 3; // PSP_DISPLAY_PIXEL_FORMAT_8888
    public int Mode { get; private set; }

    public uint VCount { get; private set; }
    public bool VBlankFlag { get; private set; }

    readonly PspBus _bus;
    readonly object _snapLock = new();
    uint[] _snapshot = new uint[ScreenWidth * ScreenHeight];
    public uint[] Snapshot => _snapshot;

    public PspDisplay(PspBus bus)
    {
        _bus = bus;
    }

    public void SetMode(int mode, int width, int height)
    {
        Mode = mode;
        Log.Write(LogCat.Display, $"SetMode mode={mode} {width}x{height}");
    }

    public void SetFrameBuf(uint addr, int bufWidth, int pixelFormat, int syncMode)
    {
        FrameBufAddr = addr;
        BufWidth = bufWidth > 0 ? bufWidth : 512;
        PixelFormat = pixelFormat;
    }

    public Allegrex? Cpu { get; set; }

    public void WaitVblankStart()
    {
        VBlankFlag = true;
        if (Cpu != null)
            Cpu.WaitingVblank = true;
    }

    public void VBlank()
    {
        VCount++;
        VBlankFlag = false;
        TakeSnapshot();
    }

    /// <summary>Copy the current framebuffer to the display snapshot (thread-safe, unsafe for speed).</summary>
    unsafe void TakeSnapshot()
    {
        uint fbAddr = FrameBufAddr;
        if (fbAddr == 0) return;

        bool isVram = (fbAddr >= 0x0400_0000 && fbAddr < 0x0400_0000 + PspBus.VramSize) ||
                      (fbAddr >= 0x4400_0000 && fbAddr < 0x4400_0000 + PspBus.VramSize);

        uint physBase = isVram ? (PspBus.VirtToPhys(fbAddr) - 0x0400_0000) : PspBus.VirtToPhys(fbAddr);
        byte[] source = isVram ? _bus.Vram : _bus.Ram;
        int stride = BufWidth;
        int fmt = PixelFormat;
        uint srcLen = (uint)source.Length;

        var snap = new uint[ScreenWidth * ScreenHeight];

        fixed (byte* srcPtr = source)
        fixed (uint* dstPtr = snap)
        {
            if (fmt == 3)
            {
                // 8888 fast path — bulk copy rows
                for (int y = 0; y < ScreenHeight; y++)
                {
                    uint rowOff = physBase + (uint)(y * stride) * 4;
                    if (rowOff + ScreenWidth * 4 > srcLen) continue;
                    uint* src = (uint*)(srcPtr + rowOff);
                    uint* dst = dstPtr + y * ScreenWidth;
                    for (int x = 0; x < ScreenWidth; x++)
                    {
                        uint abgr = src[x];
                        uint r = abgr & 0xFF;
                        uint g = (abgr >> 8) & 0xFF;
                        uint b = (abgr >> 16) & 0xFF;
                        dst[x] = 0xFF000000 | (b << 16) | (g << 8) | r;
                    }
                }
            }
            else
            {
                for (int y = 0; y < ScreenHeight; y++)
                {
                    for (int x = 0; x < ScreenWidth; x++)
                    {
                        uint pixel;
                        switch (fmt)
                        {
                            case 0:
                            {
                                uint off = physBase + (uint)(y * stride + x) * 2;
                                if (off + 1 >= srcLen) { pixel = 0; break; }
                                ushort c = *(ushort*)(srcPtr + off);
                                pixel = 0xFF000000 | (((uint)(c & 0x1F) * 255 / 31)) |
                                        (((uint)((c >> 5) & 0x3F) * 255 / 63) << 8) |
                                        (((uint)((c >> 11) & 0x1F) * 255 / 31) << 16);
                                break;
                            }
                            case 1:
                            {
                                uint off = physBase + (uint)(y * stride + x) * 2;
                                if (off + 1 >= srcLen) { pixel = 0; break; }
                                ushort c = *(ushort*)(srcPtr + off);
                                uint r = (uint)(c & 0x1F) * 255 / 31;
                                uint g = (uint)((c >> 5) & 0x1F) * 255 / 31;
                                uint b = (uint)((c >> 10) & 0x1F) * 255 / 31;
                                uint a = (c & 0x8000) != 0 ? 255u : 0u;
                                pixel = (a << 24) | (b << 16) | (g << 8) | r;
                                break;
                            }
                            case 2:
                            {
                                uint off = physBase + (uint)(y * stride + x) * 2;
                                if (off + 1 >= srcLen) { pixel = 0; break; }
                                ushort c = *(ushort*)(srcPtr + off);
                                uint r = (uint)(c & 0xF) * 255 / 15;
                                uint g = (uint)((c >> 4) & 0xF) * 255 / 15;
                                uint b = (uint)((c >> 8) & 0xF) * 255 / 15;
                                uint a2 = (uint)((c >> 12) & 0xF) * 255 / 15;
                                pixel = (a2 << 24) | (b << 16) | (g << 8) | r;
                                break;
                            }
                            default: pixel = 0; break;
                        }
                        dstPtr[y * ScreenWidth + x] = pixel;
                    }
                }
            }
        }

        lock (_snapLock)
            _snapshot = snap;
    }

    public uint[] GetSnapshot()
    {
        lock (_snapLock)
            return _snapshot;
    }
}
