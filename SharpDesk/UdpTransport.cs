using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SharpDesk;

sealed class UdpTransport : IDisposable
{
    const int ChunkHeader = 9;
    const int MaxChunkData = 1400 - ChunkHeader;

    UdpClient? _udp;
    IPEndPoint? _remoteEp;
    uint _frameSeq;
    readonly Dictionary<uint, FrameAssembly> _pending = new();
    readonly CancellationTokenSource _cts = new();

    public event Action<byte, byte[], int>? OnFrame;
    public event Action<string>? OnLog;
    public long DroppedFrames { get; private set; }

    class FrameAssembly
    {
        public byte[] Data;
        public bool[] Received;
        public int ReceivedCount;
        public int Total;
        public int LastChunkLen;
        public byte FrameType;
        public DateTime Started;

        public FrameAssembly(int totalChunks, byte ft)
        {
            Data = new byte[(totalChunks + 1) * MaxChunkData];
            Received = new bool[totalChunks];
            Total = totalChunks;
            FrameType = ft;
            Started = DateTime.UtcNow;
        }
    }

    public int StartHost()
    {
        _udp = new UdpClient(0);
        var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(ReceiveLoop);
        return port;
    }

    public void StartViewer(string host, int port)
    {
        var addr = IPAddress.Parse(host);
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        _remoteEp = new IPEndPoint(addr, port);
        _udp = new UdpClient(addr.AddressFamily);
        _udp.Connect(_remoteEp);
        _udp.Send([0xFF], 1);
        _ = Task.Run(ReceiveLoop);
    }

    public void SendFrame(byte frameType, byte[] data, int len)
    {
        if (_udp == null || _remoteEp == null) return;
        uint seq = _frameSeq++;
        int totalChunks = Math.Max(1, (len + MaxChunkData - 1) / MaxChunkData);

        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * MaxChunkData;
            int chunkLen = Math.Min(MaxChunkData, len - offset);
            var pkt = new byte[ChunkHeader + chunkLen];
            BinaryPrimitives.WriteUInt32LittleEndian(pkt, seq);
            BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(4), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(6), (ushort)totalChunks);
            pkt[8] = frameType;
            Buffer.BlockCopy(data, offset, pkt, ChunkHeader, chunkLen);
            try { _udp.Send(pkt, pkt.Length, _remoteEp); } catch { }
        }
    }

    async Task ReceiveLoop()
    {
        if (_udp == null) return;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                var data = result.Buffer;

                if (data.Length == 1 && data[0] == 0xFF)
                {
                    _remoteEp = result.RemoteEndPoint;
                    OnLog?.Invoke($"UDP peer: {_remoteEp}");
                    continue;
                }

                if (data.Length < ChunkHeader) continue;

                uint seq = BinaryPrimitives.ReadUInt32LittleEndian(data);
                int chunkIdx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
                int totalChunks = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
                byte ft = data[8];
                int chunkDataLen = data.Length - ChunkHeader;

                PruneStale();

                if (!_pending.TryGetValue(seq, out var asm))
                {
                    asm = new FrameAssembly(totalChunks, ft);
                    _pending[seq] = asm;
                }

                if (chunkIdx < asm.Total && !asm.Received[chunkIdx])
                {
                    Buffer.BlockCopy(data, ChunkHeader, asm.Data, chunkIdx * MaxChunkData, chunkDataLen);
                    asm.Received[chunkIdx] = true;
                    asm.ReceivedCount++;
                    if (chunkIdx == asm.Total - 1) asm.LastChunkLen = chunkDataLen;

                    if (asm.ReceivedCount == asm.Total)
                    {
                        _pending.Remove(seq);
                        int totalLen = asm.Total == 1 ? chunkDataLen : (asm.Total - 1) * MaxChunkData + asm.LastChunkLen;
                        OnFrame?.Invoke(asm.FrameType, asm.Data, totalLen);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { OnLog?.Invoke($"UDP error: {ex.Message}"); }
    }

    void PruneStale()
    {
        var now = DateTime.UtcNow;
        var stale = new List<uint>();
        foreach (var kv in _pending)
            if ((now - kv.Value.Started).TotalMilliseconds > 100) stale.Add(kv.Key);
        foreach (var key in stale) { _pending.Remove(key); DroppedFrames++; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}
