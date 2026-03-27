using System.Runtime.InteropServices;

namespace SharpDesk;

static class InputInjector
{
    const int INPUT_MOUSE = 0, INPUT_KB = 1;
    const int MF_MOVE = 0x0001, MF_ABSOLUTE = 0x8000, MF_WHEEL = 0x0800;
    const int MF_LDOWN = 0x0002, MF_LUP = 0x0004, MF_RDOWN = 0x0008, MF_RUP = 0x0010;
    const int MF_MDOWN = 0x0020, MF_MUP = 0x0040, MF_XDOWN = 0x0080, MF_XUP = 0x0100;
    const int XBTN1 = 1, XBTN2 = 2, KF_UP = 0x0002;

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inp, int size);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int  GetSystemMetrics(int n);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public U u; }
    [StructLayout(LayoutKind.Explicit)]   struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
    [StructLayout(LayoutKind.Sequential)] struct MI { public int dx, dy, mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KI { public ushort wVk, wScan; public int dwFlags, time; public IntPtr extra; }

    static readonly int SZ = Marshal.SizeOf<INPUT>();
    static readonly int SW = GetSystemMetrics(0), SH = GetSystemMetrics(1);

    static (int flags, int data) BtnFlags(int btn, bool down) => btn switch
    {
        0 => (down ? MF_LDOWN : MF_LUP,   0),
        1 => (down ? MF_RDOWN : MF_RUP,   0),
        2 => (down ? MF_MDOWN : MF_MUP,   0),
        3 => (down ? MF_XDOWN : MF_XUP,   XBTN1),
        4 => (down ? MF_XDOWN : MF_XUP,   XBTN2),
        _ => (0, 0),
    };

    public static void MoveMouse(float nx, float ny) =>
        Send(new INPUT { type = INPUT_MOUSE, u = { mi = new MI {
            dx = (int)(Math.Clamp(nx, 0f, 1f) * 65535),
            dy = (int)(Math.Clamp(ny, 0f, 1f) * 65535),
            dwFlags = MF_MOVE | MF_ABSOLUTE }}});

    public static void MouseButton(int btn, bool down)
    {
        var (f, d) = BtnFlags(btn, down);
        if (f != 0) Send(new INPUT { type = INPUT_MOUSE, u = { mi = new MI { dwFlags = f, mouseData = d } } });
    }

    public static void MouseWheel(int delta) =>
        Send(new INPUT { type = INPUT_MOUSE, u = { mi = new MI { dwFlags = MF_WHEEL, mouseData = delta } } });

    public static void Key(ushort vk, bool down) =>
        Send(new INPUT { type = INPUT_KB, u = { ki = new KI { wVk = vk, dwFlags = down ? 0 : KF_UP } } });

    /// <summary>Local-safe click: teleport → act → restore cursor.</summary>
    public static void ClickAt(float nx, float ny, int btn, bool down)
    {
        var (f, d) = BtnFlags(btn, down);
        if (f == 0) return;
        GetCursorPos(out var saved);
        SetCursorPos((int)(Math.Clamp(nx, 0f, 1f) * (SW - 1)), (int)(Math.Clamp(ny, 0f, 1f) * (SH - 1)));
        Send(new INPUT { type = INPUT_MOUSE, u = { mi = new MI { dwFlags = f, mouseData = d } } });
        SetCursorPos(saved.X, saved.Y);
    }

    /// <summary>Local-safe wheel: teleport → scroll → restore cursor.</summary>
    public static void WheelAt(float nx, float ny, int delta)
    {
        GetCursorPos(out var saved);
        SetCursorPos((int)(Math.Clamp(nx, 0f, 1f) * (SW - 1)), (int)(Math.Clamp(ny, 0f, 1f) * (SH - 1)));
        Send(new INPUT { type = INPUT_MOUSE, u = { mi = new MI { dwFlags = MF_WHEEL, mouseData = delta } } });
        SetCursorPos(saved.X, saved.Y);
    }

    static void Send(INPUT inp) => SendInput(1, [inp], SZ);
}
