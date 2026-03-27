using System.Runtime.CompilerServices;
using RlColor = Raylib_CsLo.Color;

namespace PsxEmu;

struct PrimInfo
{
    public enum Kind : byte
    {
        TriFlat, TriGouraud, TriTex, TriTexBlend, TriGouraudTex,
        RectFlat, RectTex, Line, Fill
    }
    public Kind Type;
    public int X0, Y0, X1, Y1, X2, Y2;
    public uint C0, C1, C2;
    public byte U0, V0, U1, V1, U2, V2;
    public int TpX, TpY, TpDepth;
    public int ClutX, ClutY;
}

abstract class PsxRendererBase
{
    public abstract int Scale { get; set; }
    public abstract ushort[] Vram { get; }

    // Draw state — written by PsxGpu, read by renderers
    public int DrawX1, DrawY1;
    public int DrawX2 = 1023, DrawY2 = 511;
    public int OffX, OffY;
    public int TexPageX, TexPageY, TexDepth, SemiTrans;

    // Hack flags
    public bool HackWireframe;
    public bool HackVertexColorsOnly;
    public bool HackRandomColors;
    public bool TrackPrimitives;

    // Primitive tracking — snap arrays are read by UI thread
    public List<PrimInfo> SnapPrims = new();
    public int[] SnapPrimIdBuf = new int[1024 * 512];

    public virtual ushort[]? SnapVramData => null;

    public abstract void ClearVram();
    public abstract void WritePixel(int x, int y, ushort pix);
    public abstract ushort ReadPixel(int x, int y);

    public abstract void DrawTriFlat(ushort c, uint v0w, uint v1w, uint v2w);
    public abstract void DrawTriGouraud(uint c0, uint c1, uint c2, uint v0w, uint v1w, uint v2w);
    public abstract void DrawTriTex(uint v0w, uint uv0, uint v1w, uint uv1, uint v2w, uint uv2, int clutAttr);
    public abstract void DrawTriTexBlend(uint modColor, uint v0w, uint uv0, uint v1w, uint uv1, uint v2w, uint uv2, int clutAttr);
    public abstract void DrawTriGouraudTex(uint c0, uint c1, uint c2, uint v0w, uint uv0, uint v1w, uint uv1, uint v2w, uint uv2, int clutAttr);
    public abstract void DrawRectFlat(ushort c, uint pos, uint size);
    public abstract void DrawRectTex(uint pos, uint uvclut, uint size, int clutAttr);
    public abstract void DrawRectTexBlend(uint modColor, uint pos, uint uvclut, uint size, int clutAttr);
    public abstract void DrawLine(ushort c, int x0, int y0, int x1, int y1);
    public abstract void FillRect(int x, int y, int w, int h, ushort c);
    public abstract void CopyVram(int sx, int sy, int dx, int dy, int w, int h);

    public abstract void VBlankSnapshot(int dispX, int dispY, int dispW, int dispH);
    public abstract unsafe void SnapshotDisplay(RlColor[] pixels, int outW, int outH);
    public abstract unsafe void SnapshotFullVram(RlColor[] pixels);

    // ── Shared utilities ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort To555(uint rgb) =>
        (ushort)((((byte)rgb >> 3) & 0x1F) | ((((byte)(rgb >> 8) >> 3) & 0x1F) << 5) | ((((byte)(rgb >> 16) >> 3) & 0x1F) << 10));

    internal static uint AvgCol(uint a, uint b, uint c)
    {
        byte r = (byte)(((a & 0xFF) + (b & 0xFF) + (c & 0xFF)) / 3);
        byte g = (byte)((((a >> 8) & 0xFF) + ((b >> 8) & 0xFF) + ((c >> 8) & 0xFF)) / 3);
        byte bl = (byte)((((a >> 16) & 0xFF) + ((b >> 16) & 0xFF) + ((c >> 16) & 0xFF)) / 3);
        return (uint)(r | (g << 8) | (bl << 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short S11(uint n) => (short)(((int)n << 21) >> 21);

    internal static int Sext11(int v) => (v & 0x400) != 0 ? v | unchecked((int)0xFFFF_F800) : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort RandomColor(int id)
    {
        uint h = (uint)id * 2654435761u;
        return (ushort)(((h >> 1) & 0x1F) | (((h >> 7) & 0x1F) << 5) | (((h >> 13) & 0x1F) << 10));
    }

    internal static uint Psx15ToRgba(ushort c)
    {
        byte r = (byte)(((c & 0x1F) * 255 + 15) / 31);
        byte g = (byte)((((c >> 5) & 0x1F) * 255 + 15) / 31);
        byte b = (byte)((((c >> 10) & 0x1F) * 255 + 15) / 31);
        return (uint)(r | (g << 8) | (b << 16) | (0xFFu << 24));
    }

    internal static ushort SampleTexelStatic(ushort[] vram, int u, int v, int tpX, int tpY, int depth, int clutX, int clutY)
    {
        u &= 0xFF; v &= 0xFF;
        int row = (tpY + v) & 511;
        if (depth == 0)
        {
            int col = (tpX + u / 4) & 1023;
            ushort word = vram[row * 1024 + col];
            int nib = (word >> ((u & 3) * 4)) & 0xF;
            return vram[(clutY & 511) * 1024 + ((clutX + nib) & 1023)];
        }
        if (depth == 1)
        {
            int col = (tpX + u / 2) & 1023;
            ushort word = vram[row * 1024 + col];
            int idx = (u & 1) == 0 ? (word & 0xFF) : (word >> 8);
            return vram[(clutY & 511) * 1024 + ((clutX + idx) & 1023)];
        }
        return vram[row * 1024 + ((tpX + u) & 1023)];
    }
}
