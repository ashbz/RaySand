using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace PsxEmu;

/// <summary>
/// GPU-accelerated renderer. Inherits software rasterization for VRAM correctness,
/// and uses the same LUT-based 15-bit→RGBA conversion as the software path to
/// guarantee identical color output. Maintains its own display texture so the
/// renderers can be swapped without affecting the main display texture.
/// </summary>
unsafe class GpuRenderer : SoftwareRenderer
{
    Texture _displayTex;
    int _texW, _texH;
    bool _gpuReady;
    Color[] _gpuPixels = Array.Empty<Color>();

    public uint OutputTextureId => _gpuReady ? _displayTex.id : 0;
    public bool GpuReady => _gpuReady;

    public void InitGpu()
    {
        if (_gpuReady) return;

        _texW = 640;
        _texH = 480;
        _gpuPixels = new Color[_texW * _texH];
        for (int i = 0; i < _gpuPixels.Length; i++)
            _gpuPixels[i] = new Color { a = 255 };

        fixed (Color* p = _gpuPixels)
        {
            var img = new Image
            {
                data    = p,
                width   = _texW,
                height  = _texH,
                mipmaps = 1,
                format  = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
            };
            _displayTex = LoadTextureFromImage(img);
        }
        SetTextureFilter(_displayTex, TextureFilter.TEXTURE_FILTER_BILINEAR);
        _gpuReady = true;
    }

    public void FlushGpu()
    {
        if (!_gpuReady) return;

        int s, dw, dh;
        lock (_snapLock)
        {
            s = Math.Max(1, _snapScale);
            dw = Math.Max(1, _snapDw);
            dh = Math.Max(1, _snapDh);
        }

        int outW = dw * s;
        int outH = dh * s;

        if (outW != _texW || outH != _texH)
        {
            _texW = outW;
            _texH = outH;
            _gpuPixels = new Color[_texW * _texH];
            for (int i = 0; i < _gpuPixels.Length; i++)
                _gpuPixels[i] = new Color { a = 255 };

            UnloadTexture(_displayTex);
            fixed (Color* p = _gpuPixels)
            {
                var img = new Image
                {
                    data    = p,
                    width   = _texW,
                    height  = _texH,
                    mipmaps = 1,
                    format  = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
                };
                _displayTex = LoadTextureFromImage(img);
            }
            SetTextureFilter(_displayTex, TextureFilter.TEXTURE_FILTER_BILINEAR);
        }

        SnapshotDisplay(_gpuPixels, _texW, _texH);
        fixed (Color* p = _gpuPixels) UpdateTexture(_displayTex, p);
    }

    public void CleanupGpu()
    {
        if (!_gpuReady) return;
        UnloadTexture(_displayTex);
        _gpuReady = false;
    }
}
