using System.Runtime.InteropServices;
using System.Text;

namespace SharpDesk;

static class ClipboardHelper
{
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr hMem);

    const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 0x0002;

    public static string? GetText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var hData = GetClipboardData(CF_UNICODETEXT);
            if (hData == IntPtr.Zero) return null;
            var ptr = GlobalLock(hData);
            if (ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(hData); }
        }
        finally { CloseClipboard(); }
    }

    public static void SetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (hMem == IntPtr.Zero) return;
            var ptr = GlobalLock(hMem);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);
            SetClipboardData(CF_UNICODETEXT, hMem);
        }
        finally { CloseClipboard(); }
    }
}
