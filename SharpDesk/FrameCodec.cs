using System.Numerics;
using K4os.Compression.LZ4;

namespace SharpDesk;

static class FrameCodec
{
    public const int TileSize = 64;

    // ── Keyframe (full) ──

    public static int CompressFull(byte[] raw, byte[] output)
        => LZ4Codec.Encode(raw, 0, raw.Length, output, 0, output.Length, LZ4Level.L00_FAST);

    public static void DecompressFull(byte[] src, int off, int len, byte[] dst)
        => LZ4Codec.Decode(src, off, len, dst, 0, dst.Length);

    // ── Tile-based delta (frame type 2) ──
    // Wire: [cols:1][rows:1][bitmask:N][lz4(dirty tile XOR data)]

    public static int CompressTiles(byte[] cur, byte[] prev, byte[] delta, byte[] scratch, byte[] output, int width, int height)
    {
        XorBlocks(cur, prev, delta);

        int cols = (width + TileSize - 1) / TileSize;
        int rows = (height + TileSize - 1) / TileSize;
        int totalTiles = cols * rows;
        int bitmaskLen = (totalTiles + 7) / 8;
        int headerLen = 2 + bitmaskLen;

        output[0] = (byte)cols;
        output[1] = (byte)rows;
        Array.Clear(output, 2, bitmaskLen);

        int sOff = 0, dirtyCount = 0;
        for (int ty = 0; ty < rows; ty++)
            for (int tx = 0; tx < cols; tx++)
            {
                int x0 = tx * TileSize, y0 = ty * TileSize;
                int tw = Math.Min(TileSize, width - x0);
                int th = Math.Min(TileSize, height - y0);

                if (!IsTileDirty(delta, width, x0, y0, tw, th)) continue;

                dirtyCount++;
                int idx = ty * cols + tx;
                output[2 + idx / 8] |= (byte)(1 << (idx % 8));

                for (int y = y0; y < y0 + th; y++)
                {
                    Buffer.BlockCopy(delta, (y * width + x0) * 4, scratch, sOff, tw * 4);
                    sOff += tw * 4;
                }
            }

        if (sOff == 0) return 0;

        var level = dirtyCount > totalTiles / 3 ? LZ4Level.L00_FAST : LZ4Level.L03_HC;
        int compLen = LZ4Codec.Encode(scratch, 0, sOff, output, headerLen, output.Length - headerLen, level);
        return headerLen + compLen;
    }

    public static void DecompressTiles(byte[] payload, int off, int len, byte[] prev, byte[] frame, byte[] scratch, int width, int height)
    {
        Buffer.BlockCopy(prev, 0, frame, 0, width * height * 4);

        int cols = payload[off];
        int rows = payload[off + 1];
        int bitmaskOff = off + 2;
        int bitmaskLen = (cols * rows + 7) / 8;

        int dirtyBytes = 0;
        for (int ty = 0; ty < rows; ty++)
            for (int tx = 0; tx < cols; tx++)
            {
                int idx = ty * cols + tx;
                if ((payload[bitmaskOff + idx / 8] & (1 << (idx % 8))) == 0) continue;
                dirtyBytes += Math.Min(TileSize, width - tx * TileSize) * Math.Min(TileSize, height - ty * TileSize) * 4;
            }

        int compOff = off + 2 + bitmaskLen;
        LZ4Codec.Decode(payload, compOff, len - 2 - bitmaskLen, scratch, 0, dirtyBytes);

        int sOff = 0;
        for (int ty = 0; ty < rows; ty++)
            for (int tx = 0; tx < cols; tx++)
            {
                int idx = ty * cols + tx;
                if ((payload[bitmaskOff + idx / 8] & (1 << (idx % 8))) == 0) continue;

                int x0 = tx * TileSize, y0 = ty * TileSize;
                int tw = Math.Min(TileSize, width - x0);
                int th = Math.Min(TileSize, height - y0);

                for (int y = y0; y < y0 + th; y++)
                {
                    int fOff = (y * width + x0) * 4;
                    XorRow(prev, fOff, scratch, sOff, frame, fOff, tw * 4);
                    sOff += tw * 4;
                }
            }
    }

    // ── Legacy full-frame delta (frame type 1, kept for compat) ──

    public static int CompressDelta(byte[] cur, byte[] prev, byte[] delta, byte[] output)
    {
        XorBlocks(cur, prev, delta);
        return LZ4Codec.Encode(delta, 0, delta.Length, output, 0, output.Length, LZ4Level.L03_HC);
    }

    public static void DecompressDelta(byte[] src, int off, int len, byte[] prev, byte[] dst, byte[] delta)
    {
        LZ4Codec.Decode(src, off, len, delta, 0, delta.Length);
        XorBlocks(delta, prev, dst);
    }

    // ── Pixel format ──

    public static unsafe void SwapBgraRgba(byte[] data, int len)
    {
        fixed (byte* p = data)
            for (int i = 0; i < len; i += 4)
                (p[i], p[i + 2]) = (p[i + 2], p[i]);
    }

    // ── Helpers ──

    static void XorBlocks(byte[] a, byte[] b, byte[] result)
    {
        int vecLen = Vector<byte>.Count;
        int i = 0;
        for (; i + vecLen <= a.Length; i += vecLen)
            (new Vector<byte>(a, i) ^ new Vector<byte>(b, i)).CopyTo(result, i);
        for (; i < a.Length; i++)
            result[i] = (byte)(a[i] ^ b[i]);
    }

    static void XorRow(byte[] a, int aOff, byte[] b, int bOff, byte[] dst, int dstOff, int len)
    {
        int vecLen = Vector<byte>.Count;
        int i = 0;
        for (; i + vecLen <= len; i += vecLen)
            (new Vector<byte>(a, aOff + i) ^ new Vector<byte>(b, bOff + i)).CopyTo(dst, dstOff + i);
        for (; i < len; i++)
            dst[dstOff + i] = (byte)(a[aOff + i] ^ b[bOff + i]);
    }

    static bool IsTileDirty(byte[] delta, int width, int x0, int y0, int tw, int th)
    {
        int vecLen = Vector<byte>.Count;
        for (int y = y0; y < y0 + th; y++)
        {
            int rowStart = (y * width + x0) * 4;
            int rowEnd = rowStart + tw * 4;
            int i = rowStart;
            for (; i + vecLen <= rowEnd; i += vecLen)
                if (new Vector<byte>(delta, i) != Vector<byte>.Zero) return true;
            for (; i < rowEnd; i++)
                if (delta[i] != 0) return true;
        }
        return false;
    }
}
