using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace GcEmu;

class GpuRenderer
{
    SoftwareRenderer _sw = null!;
    GcBus _bus = null!;

    Texture _tex;
    Color[] _pixels = null!;
    int _texW, _texH;
    public bool GpuReady { get; private set; }
    public uint OutputTextureId => _tex.id;

    public void Init(GcBus bus, SoftwareRenderer sw)
    {
        _bus = bus;
        _sw = sw;
        _texW = 640;
        _texH = 480;
        _pixels = new Color[_texW * _texH];
        for (int i = 0; i < _pixels.Length; i++)
            _pixels[i] = new Color { a = 255 };

        unsafe
        {
            fixed (Color* p = _pixels)
            {
                var img = new Image
                {
                    data = p,
                    width = _texW,
                    height = _texH,
                    mipmaps = 1,
                    format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
                };
                _tex = LoadTextureFromImage(img);
            }
        }
        SetTextureFilter(_tex, TextureFilter.TEXTURE_FILTER_BILINEAR);
        GpuReady = true;
    }

    public unsafe void FlushGpu()
    {
        if (!GpuReady) return;

        int dispW = _bus.Vi.DispWidth;
        int dispH = _bus.Vi.DispHeight;

        if (dispW != _texW || dispH != _texH)
        {
            _texW = Math.Max(16, dispW);
            _texH = Math.Max(16, dispH);
            _pixels = new Color[_texW * _texH];
            for (int i = 0; i < _pixels.Length; i++)
                _pixels[i] = new Color { a = 255 };
            UnloadTexture(_tex);
            fixed (Color* p = _pixels)
            {
                var img = new Image
                {
                    data = p,
                    width = _texW,
                    height = _texH,
                    mipmaps = 1,
                    format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
                };
                _tex = LoadTextureFromImage(img);
            }
            SetTextureFilter(_tex, TextureFilter.TEXTURE_FILTER_BILINEAR);
        }

        _sw.SnapshotXfbDisplay(_pixels, _texW, _texH);
        fixed (Color* p = _pixels) UpdateTexture(_tex, p);
    }

    public void CleanupGpu()
    {
        if (GpuReady)
        {
            UnloadTexture(_tex);
            GpuReady = false;
        }
    }
}
