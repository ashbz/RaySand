using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

// ── SharpDesk Relay Server ──
// Protocol:
//   Client sends: [1-byte cmd][2-byte len][payload]
//   Cmd 1 = REGISTER: no payload → server replies [1-byte status=0][4-byte roomId]
//     Client stays connected waiting for a viewer to pair.
//   Cmd 2 = CONNECT:  payload = UTF-8 room id → server replies [1-byte status]
//     Status: 1 = paired, 255 = room not found / error.
//   Legacy (cmd=0): [2-byte len][room code] for backward compat.
//
// Once paired, the server bridges TCP traffic between host and viewer.

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 9877;
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"SharpDesk Relay listening on port {port}");

var rooms = new ConcurrentDictionary<string, WaitingHost>();

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

        var first = new byte[1];
        if (!await ReadExact(stream, first, 1)) { client.Close(); return; }

        if (first[0] == 1)
        {
            // REGISTER: client sends preferred room ID
            var hdrR = new byte[2];
            if (!await ReadExact(stream, hdrR, 2)) { client.Close(); return; }
            int idLen = hdrR[0] | (hdrR[1] << 8);
            if (idLen is < 1 or > 256) { SendByte(stream, 255); client.Close(); return; }
            var idBuf = new byte[idLen];
            if (!await ReadExact(stream, idBuf, idLen)) { client.Close(); return; }
            room = Encoding.UTF8.GetString(idBuf);

            var tcs = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wh = new WaitingHost(client, tcs);

            // If room already registered (stale connection), evict the old one
            if (rooms.TryRemove(room, out var old))
            {
                old.Tcs.TrySetCanceled();
                try { old.Client.Close(); } catch { }
                Console.WriteLine($"Room {room} evicted stale registration");
            }
            rooms[room] = wh;

            Console.WriteLine($"[{client.Client.RemoteEndPoint}] registered room {room}");

            // Reply: status=0 (waiting) + 4 bytes room id length + room id string
            var roomBytes = Encoding.UTF8.GetBytes(room);
            var reply = new byte[1 + 2 + roomBytes.Length];
            reply[0] = 0; // waiting
            reply[1] = (byte)(roomBytes.Length & 0xFF);
            reply[2] = (byte)((roomBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(roomBytes, 0, reply, 3, roomBytes.Length);
            await stream.WriteAsync(reply);
            await stream.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            cts.Token.Register(() => tcs.TrySetCanceled());

            TcpClient peer;
            try { peer = await tcs.Task; }
            catch (TaskCanceledException)
            {
                rooms.TryRemove(room, out _);
                SendByte(stream, 255);
                client.Close();
                Console.WriteLine($"Room {room} timed out");
                return;
            }

            rooms.TryRemove(room, out _);
            Console.WriteLine($"Room {room} paired!");
            SendByte(stream, 1); // paired
            SendByte(peer.GetStream(), 1); // paired

            await Bridge(client, peer);
            Console.WriteLine($"Room {room} ended");
        }
        else if (first[0] == 2)
        {
            // CONNECT: read room id, pair with waiting host
            var hdr = new byte[2];
            if (!await ReadExact(stream, hdr, 2)) { client.Close(); return; }
            int len = BitConverter.ToUInt16(hdr);
            if (len is < 1 or > 256) { SendByte(stream, 255); client.Close(); return; }
            var buf = new byte[len];
            if (!await ReadExact(stream, buf, len)) { client.Close(); return; }
            room = Encoding.UTF8.GetString(buf);

            Console.WriteLine($"[{client.Client.RemoteEndPoint}] connecting to room {room}");

            if (rooms.TryRemove(room, out var wh))
            {
                wh.Tcs.SetResult(client);
            }
            else
            {
                SendByte(stream, 255);
                client.Close();
                Console.WriteLine($"Room {room} not found");
            }
        }
        else
        {
            // Legacy protocol: first byte is high byte of 2-byte length
            var hdr2 = new byte[1];
            if (!await ReadExact(stream, hdr2, 1)) { client.Close(); return; }
            int len = first[0] | (hdr2[0] << 8);
            if (len is < 1 or > 256) { client.Close(); return; }
            var buf = new byte[len];
            if (!await ReadExact(stream, buf, len)) { client.Close(); return; }
            room = Encoding.UTF8.GetString(buf);

            Console.WriteLine($"[{client.Client.RemoteEndPoint}] legacy room '{room}'");

            var tcs = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (rooms.TryAdd(room, new WaitingHost(client, tcs)))
            {
                SendByte(stream, 0);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                cts.Token.Register(() => tcs.TrySetCanceled());

                TcpClient peer;
                try { peer = await tcs.Task; }
                catch (TaskCanceledException)
                {
                    rooms.TryRemove(room, out _);
                    SendByte(stream, 255);
                    client.Close();
                    Console.WriteLine($"Room '{room}' timed out");
                    return;
                }

                rooms.TryRemove(room, out _);
                Console.WriteLine($"Room '{room}' paired!");
                SendByte(stream, 1);
                SendByte(peer.GetStream(), 1);
                await Bridge(client, peer);
                Console.WriteLine($"Room '{room}' ended");
            }
            else if (rooms.TryRemove(room, out var waiting))
            {
                waiting.Tcs.SetResult(client);
            }
            else
            {
                SendByte(stream, 255);
                client.Close();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Room '{room}' error: {ex.Message}");
        try { client.Close(); } catch { }
    }
}

void SendByte(NetworkStream s, byte b) { try { s.WriteByte(b); s.Flush(); } catch { } }

async Task Bridge(TcpClient a, TcpClient b)
{
    try
    {
        var sa = a.GetStream();
        var sb = b.GetStream();
        await Task.WhenAny(sa.CopyToAsync(sb), sb.CopyToAsync(sa));
    }
    catch { }
    finally
    {
        try { a.Close(); } catch { }
        try { b.Close(); } catch { }
    }
}

async Task<bool> ReadExact(NetworkStream s, byte[] buf, int count)
{
    int pos = 0;
    while (pos < count)
    {
        int n = await s.ReadAsync(buf.AsMemory(pos, count - pos));
        if (n == 0) return false;
        pos += n;
    }
    return true;
}

record WaitingHost(TcpClient Client, TaskCompletionSource<TcpClient> Tcs);
