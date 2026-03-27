using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SharpDesk;

/// <summary>
/// UDP broadcast LAN discovery. Each instance beacons every 2 s on a fixed UDP port.
/// Peers that stop beaconing are pruned after 8 s.
/// </summary>
sealed class Discovery : IDisposable
{
    const int    UdpPort         = 9877;
    const int    BeaconMs        = 2000;
    const double PeerTimeoutSec  = 8.0;
    static readonly byte[] Magic = [(byte)'S', (byte)'D', (byte)'S', (byte)'K'];

    readonly Guid   _id = Guid.NewGuid();
    readonly int    _tcpPort;
    readonly string _machineName = Environment.MachineName;
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<Guid, PeerInfo> _peers = new();

    public record PeerInfo(string Name, string Address, int TcpPort, DateTime LastSeen);

    public Discovery(int tcpPort) => _tcpPort = tcpPort;

    public void Start()
    {
        _ = Task.Run(BroadcastLoop);
        _ = Task.Run(ListenLoop);
    }

    async Task BroadcastLoop()
    {
        using var client = new UdpClient { EnableBroadcast = true };
        var target = new IPEndPoint(IPAddress.Broadcast, UdpPort);
        var beacon = BuildBeacon();

        while (!_cts.IsCancellationRequested)
        {
            try { await client.SendAsync(beacon, beacon.Length, target); }
            catch { }
            try { await Task.Delay(BeaconMs, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task ListenLoop()
    {
        using var client = new UdpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.ExclusiveAddressUse = false;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(_cts.Token);
                ParseBeacon(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    byte[] BuildBeacon()
    {
        var nameBytes = Encoding.UTF8.GetBytes(_machineName);
        var buf = new byte[4 + 16 + 2 + nameBytes.Length];
        Magic.CopyTo(buf, 0);
        _id.ToByteArray().CopyTo(buf, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20), (ushort)_tcpPort);
        nameBytes.CopyTo(buf, 22);
        return buf;
    }

    void ParseBeacon(byte[] data, IPEndPoint sender)
    {
        if (data.Length < 22) return;
        if (data[0] != Magic[0] || data[1] != Magic[1] ||
            data[2] != Magic[2] || data[3] != Magic[3]) return;

        var id = new Guid(data.AsSpan(4, 16));
        if (id == _id) return;

        int port  = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20));
        string name = Encoding.UTF8.GetString(data, 22, data.Length - 22);

        _peers[id] = new PeerInfo(name, sender.Address.ToString(), port, DateTime.UtcNow);
    }

    public PeerInfo[] GetPeers()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _peers)
            if ((now - kv.Value.LastSeen).TotalSeconds > PeerTimeoutSec)
                _peers.TryRemove(kv.Key, out _);
        return _peers.Values.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
