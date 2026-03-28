using System.Buffers.Binary;
using System.Text;

namespace SharpDesk;

enum MsgType : byte
{
    Handshake = 1, Frame, MouseMove, MouseButton, KeyEvent, MouseWheel,
    Audio, AudioMute, FpsLimit, CursorPos, Clipboard, UdpPort
}

static class Protocol
{
    const int MaxPayload = 50_000_000;

    public static async Task WriteMessage(Stream s, MsgType type, byte[] payload, CancellationToken ct = default)
    {
        var h = new byte[5];
        h[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(1), payload.Length);
        await s.WriteAsync(h, ct);
        if (payload.Length > 0) await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    public static async Task WriteFrame(Stream s, byte frameType, byte[] buf, int len, CancellationToken ct = default)
    {
        var h = new byte[6];
        h[0] = (byte)MsgType.Frame;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(1), 1 + len);
        h[5] = frameType;
        await s.WriteAsync(h, ct);
        if (len > 0) await s.WriteAsync(buf.AsMemory(0, len), ct);
        await s.FlushAsync(ct);
    }

    public static async Task<(MsgType type, byte[] payload)?> ReadMessage(Stream s, CancellationToken ct = default)
    {
        var h = new byte[5];
        if (!await ReadExact(s, h, 5, ct)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(1));
        if (len < 0 || len > MaxPayload)
            throw new IOException($"Invalid message: type={h[0]}, len={len} (max={MaxPayload})");
        var payload = new byte[len];
        if (len > 0 && !await ReadExact(s, payload, len, ct)) return null;
        return ((MsgType)h[0], payload);
    }

    static async Task<bool> ReadExact(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int pos = 0;
        while (pos < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(pos, count - pos), ct);
            if (n == 0) return false;
            pos += n;
        }
        return true;
    }

    // ── Handshake (13 bytes) ──

    public static byte[] EncodeHandshake(int w, int h, int sampleRate = 0, int channels = 0, int bitsPerSample = 0, bool opus = false)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)h);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)channels);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)bitsPerSample);
        b[12] = (byte)(opus ? 1 : 0);
        return b;
    }

    public static (int w, int h, int sampleRate, int channels, int bitsPerSample, bool opus) DecodeHandshake(byte[] p)
    {
        int w   = BinaryPrimitives.ReadUInt16LittleEndian(p);
        int h   = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));
        int sr  = p.Length >= 8  ? BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(4))  : 0;
        int ch  = p.Length >= 10 ? BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(8)) : 0;
        int bp  = p.Length >= 12 ? BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(10)): 0;
        bool op = p.Length >= 13 && p[12] != 0;
        return (w, h, sr, ch, bp, op);
    }

    // ── Mouse / Key ──

    public static byte[] EncodeMouseMove(float nx, float ny)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(b, nx);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), ny);
        return b;
    }
    public static (float nx, float ny) DecodeMouseMove(byte[] p) =>
        (BinaryPrimitives.ReadSingleLittleEndian(p), BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(4)));

    public static byte[] EncodeMouseButton(int btn, bool down) => [(byte)btn, (byte)(down ? 1 : 0)];
    public static (int btn, bool down) DecodeMouseButton(byte[] p) => (p[0], p[1] != 0);

    public static byte[] EncodeKey(ushort vk, bool down)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, vk);
        b[2] = (byte)(down ? 1 : 0);
        return b;
    }
    public static (ushort vk, bool down) DecodeKey(byte[] p) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(p), p[2] != 0);

    public static byte[] EncodeWheel(int delta)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, delta);
        return b;
    }
    public static int DecodeWheel(byte[] p) => BinaryPrimitives.ReadInt32LittleEndian(p);

    // ── Cursor position ──

    public static byte[] EncodeCursorPos(float nx, float ny) => EncodeMouseMove(nx, ny);
    public static (float nx, float ny) DecodeCursorPos(byte[] p) => DecodeMouseMove(p);

    // ── Clipboard ──

    public static byte[] EncodeClipboard(string text) => Encoding.UTF8.GetBytes(text);
    public static string DecodeClipboard(byte[] p) => Encoding.UTF8.GetString(p);

    // ── UDP port ──

    public static byte[] EncodeUdpPort(int port)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, port);
        return b;
    }
    public static int DecodeUdpPort(byte[] p) => BinaryPrimitives.ReadInt32LittleEndian(p);
}
