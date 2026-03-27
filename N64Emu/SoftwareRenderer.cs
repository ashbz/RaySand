using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace N64Emu;

sealed class SoftwareRenderer
{
    readonly N64Bus _bus;
    readonly uint[] _rgba32Lut = new uint[0x10000]; // RGBA5551 -> RGBA8888

    public SoftwareRenderer(N64Bus bus)
    {
        _bus = bus;
        BuildLut();
    }

    void BuildLut()
    {
        for (int i = 0; i < 0x10000; i++)
        {
            uint r = (uint)((i >> 11) & 0x1F) * 255 / 31;
            uint g = (uint)((i >> 6) & 0x1F) * 255 / 31;
            uint b = (uint)((i >> 1) & 0x1F) * 255 / 31;
            uint a = (i & 1) != 0 ? 255u : 255u; // always opaque for display
            _rgba32Lut[i] = r | (g << 8) | (b << 16) | (a << 24); // ABGR for Raylib
        }
    }

    public unsafe void SnapshotDisplay(Raylib_CsLo.Color* pixels, int texW, int texH)
    {
        uint origin = _bus.Vi.Origin & 0x00FF_FFFF;
        int viWidth = _bus.Vi.FrameWidth;
        int viHeight = _bus.Vi.FrameHeight;
        int depth = _bus.Vi.ColorDepth;
        uint status = _bus.Vi.Status;

        if (depth == 0 || viWidth == 0 || viHeight == 0 || origin == 0)
        {
            for (int i = 0; i < texW * texH; i++)
                pixels[i] = new Raylib_CsLo.Color { r = 0, g = 0, b = 0, a = 255 };
            return;
        }

        int srcW = Math.Min(viWidth, texW);
        int srcH = Math.Min(viHeight, texH);

        if (depth == 16)
        {
            for (int y = 0; y < srcH; y++)
            {
                for (int x = 0; x < srcW; x++)
                {
                    uint addr = origin + (uint)(y * viWidth + x) * 2;
                    if (addr + 1 >= (uint)_bus.Rdram.Length)
                    {
                        pixels[y * texW + x] = default;
                        continue;
                    }

                    ushort px = (ushort)(_bus.Rdram[addr] << 8 | _bus.Rdram[addr + 1]);
                    uint rgba = _rgba32Lut[px];
                    pixels[y * texW + x] = new Raylib_CsLo.Color
                    {
                        r = (byte)(rgba),
                        g = (byte)(rgba >> 8),
                        b = (byte)(rgba >> 16),
                        a = (byte)(rgba >> 24),
                    };
                }

                for (int x = srcW; x < texW; x++)
                    pixels[y * texW + x] = default;
            }
        }
        else if (depth == 32)
        {
            for (int y = 0; y < srcH; y++)
            {
                for (int x = 0; x < srcW; x++)
                {
                    uint addr = origin + (uint)(y * viWidth + x) * 4;
                    if (addr + 3 >= (uint)_bus.Rdram.Length)
                    {
                        pixels[y * texW + x] = default;
                        continue;
                    }

                    pixels[y * texW + x] = new Raylib_CsLo.Color
                    {
                        r = _bus.Rdram[addr],
                        g = _bus.Rdram[addr + 1],
                        b = _bus.Rdram[addr + 2],
                        a = 255,
                    };
                }

                for (int x = srcW; x < texW; x++)
                    pixels[y * texW + x] = default;
            }
        }

        for (int y = srcH; y < texH; y++)
            for (int x = 0; x < texW; x++)
                pixels[y * texW + x] = default;
    }
}
