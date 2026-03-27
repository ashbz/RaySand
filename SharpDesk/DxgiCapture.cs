using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SharpDesk;

/// <summary>
/// GPU-accelerated screen capture via DXGI Desktop Duplication.
/// Much faster than GDI: zero-copy from GPU, only captures when screen changes.
/// </summary>
sealed class DxgiCapture : ICapture
{
    static readonly FeatureLevel[] Levels =
        [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _ctx;
    readonly IDXGIOutputDuplication _dup;
    readonly ID3D11Texture2D _staging;

    public int Width  { get; }
    public int Height { get; }
    public int FrameSize => Width * Height * 4;

    public DxgiCapture()
    {
        _device = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            FeatureLevel.Level_11_0);
        _ctx = _device.ImmediateContext;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        dxgiDevice.GetAdapter(out var adapter);
        using (adapter)
        {
            adapter.EnumOutputs(0, out var output);
            using (output)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                var bounds = output1.Description.DesktopCoordinates;
                Width  = bounds.Right  - bounds.Left;
                Height = bounds.Bottom - bounds.Top;
                _dup = output1.DuplicateOutput(_device);
            }
        }

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width              = (uint)Width,
            Height             = (uint)Height,
            MipLevels          = 1,
            ArraySize          = 1,
            Format             = Format.B8G8R8A8_UNorm,
            SampleDescription  = { Count = 1, Quality = 0 },
            Usage              = ResourceUsage.Staging,
            BindFlags          = BindFlags.None,
            CPUAccessFlags     = CpuAccessFlags.Read,
            MiscFlags          = ResourceOptionFlags.None,
        });
    }

    public unsafe bool CaptureFrame(byte[] buffer)
    {
        IDXGIResource? resource = null;
        try { _dup.AcquireNextFrame(100, out _, out resource); }
        catch { return false; }
        if (resource == null) { try { _dup.ReleaseFrame(); } catch { } return false; }

        try
        {
            using var tex = resource.QueryInterface<ID3D11Texture2D>();
            _ctx.CopyResource(_staging, tex);
        }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }

        var mapped = _ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = Width * 4;
            fixed (byte* dst = buffer)
            {
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy(
                        (byte*)mapped.DataPointer + y * mapped.RowPitch,
                        dst + y * rowBytes, rowBytes, rowBytes);
            }
        }
        finally { _ctx.Unmap(_staging, 0); }

        return true;
    }

    public void Dispose()
    {
        _staging.Dispose();
        _dup.Dispose();
        _ctx.Dispose();
        _device.Dispose();
    }
}
