using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

// ── SharpDesk Relay Server ──
// Pairs two peers by room code and forwards all TCP traffic between them.
// Protocol: client sends [2-byte length][UTF-8 room code], server replies [1-byte status].
// Status: 0 = waiting for peer, 1 = paired, 255 = error.

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 9877;
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"SharpDesk Relay listening on port {port}");

var rooms = new ConcurrentDictionary<string, TaskCompletionSource<TcpClient>>();

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleClient(client);
}

async Task HandleClient(TcpClient client)
{
    string room = "?";
    try
    {
        client.NoDelay = true;
        var stream = client.GetStream();

        // Read room code
        var hdr = new byte[2];
        await ReadExact(stream, hdr, 2);
        int len = BitConverter.ToUInt16(hdr);
        if (len is < 1 or > 256) { client.Close(); return; }
        var buf = new byte[len];
        await ReadExact(stream, buf, len);
        room = Encoding.UTF8.GetString(buf);

        Console.WriteLine($"[{client.Client.RemoteEndPoint}] room '{room}'");

        var tcs = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (rooms.TryAdd(room, tcs))
        {
            // First peer — wait for a partner (5 min timeout)
            stream.WriteByte(0);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            cts.Token.Register(() => tcs.TrySetCanceled());

            TcpClient peer;
            try { peer = await tcs.Task; }
            catch (TaskCanceledException)
            {
                rooms.TryRemove(room, out _);
                stream.WriteByte(255);
                client.Close();
                Console.WriteLine($"Room '{room}' timed out");
                return;
            }

            rooms.TryRemove(room, out _);
            Console.WriteLine($"Room '{room}' paired!");
            stream.WriteByte(1);
            peer.GetStream().WriteByte(1);

            // Bridge all traffic between the two peers
            await Bridge(client, peer);
            Console.WriteLine($"Room '{room}' ended");
        }
        else if (rooms.TryRemove(room, out var waiting))
        {
            // Second peer — complete the first peer's wait
            waiting.SetResult(client);
        }
        else
        {
            stream.WriteByte(255);
            client.Close();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Room '{room}' error: {ex.Message}");
        try { client.Close(); } catch { }
    }
}

async Task Bridge(TcpClient a, TcpClient b)
{
    try
    {
        var sa = a.GetStream();
        var sb = b.GetStream();
        var t1 = sa.CopyToAsync(sb);
        var t2 = sb.CopyToAsync(sa);
        await Task.WhenAny(t1, t2);
    }
    catch { }
    finally
    {
        try { a.Close(); } catch { }
        try { b.Close(); } catch { }
    }
}

async Task ReadExact(NetworkStream s, byte[] buf, int count)
{
    int pos = 0;
    while (pos < count)
    {
        int n = await s.ReadAsync(buf.AsMemory(pos, count - pos));
        if (n == 0) throw new IOException("Disconnected");
        pos += n;
    }
}
