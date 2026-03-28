using System.Net;
using System.Net.Sockets;
using System.Text;
using K4os.Compression.LZ4;

namespace SharpDesk;

sealed class NetworkPeer : IDisposable
{
    TcpListener? _listener;
    TcpClient?   _client;
    NetworkStream? _stream;
    CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);

    bool  _isLocal;
    float _lastNormX, _lastNormY;
    volatile bool _audioMuted = true;
    volatile bool _viewerMuted = true;
    volatile int  _frameDelayMs;

    public bool    IsListening => _listener != null;
    public bool    IsConnected => _client?.Connected == true && _stream != null;
    public bool    IsHost      { get; private set; }
    public string? RemoteEP    => _client?.Client?.RemoteEndPoint?.ToString();
    public string  CaptureMethod { get; private set; } = "";

    public bool ViewerMuted { get => _viewerMuted; set => _viewerMuted = value; }
    public float ViewerVolume { get; set; } = 0f;

    volatile byte[]? _latestFrame;
    volatile int _frameW, _frameH;
    public byte[]? LatestFrame => _latestFrame;
    public int FrameW => _frameW;
    public int FrameH => _frameH;

    // Remote cursor position (normalized, from host)

    public long   BytesSent     { get; private set; }
    public long   BytesRecv     { get; private set; }
    public double Fps           { get; private set; }
    public long   FramesSent    { get; private set; }
    public long   FramesSkipped { get; private set; }
    public long   KeyframesSent { get; private set; }

    // UDP
    UdpTransport? _udp;
    public bool UdpEnabled { get; set; }
    public long UdpDropped => _udp?.DroppedFrames ?? 0;

    // Clipboard echo guard
    volatile string? _lastClipRecv;

    public event Action?         OnConnected;
    public event Action?         OnDisconnected;
    public event Action<string>? OnError;
    public event Action<string>? OnLog;


    // ── Listen / Connect ──

    public void StartListening(int port)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                    if (_client != null) { c.Close(); continue; }
                    OnLog?.Invoke($"Incoming connection from {c.Client.RemoteEndPoint}");
                    Accept(c, isHost: true);
                    _ = Task.Run(() => RunHost(_cts.Token));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { OnError?.Invoke($"Listener: {ex.Message}"); }
        });
    }

    public async Task ConnectAsync(string host, int port)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(host, port);
        Accept(c, isHost: false);
        _ = Task.Run(() => RunViewer(_cts.Token));
    }

    TcpClient? _relayClient;
    NetworkStream? _relayStream;
    public string? RelayRoomId { get; private set; }
    public bool RelayRegistered => RelayRoomId != null && _relayClient?.Connected == true;

    public async Task<string> RegisterWithRelay(string relayHost, int relayPort, string preferredId)
    {
        _relayClient?.Close();
        _relayClient = new TcpClient { NoDelay = true };
        await _relayClient.ConnectAsync(relayHost, relayPort);
        _relayStream = _relayClient.GetStream();

        // Cmd 1 = REGISTER + [2-byte len][preferred id]
        var idBytes = Encoding.UTF8.GetBytes(preferredId);
        var pkt = new byte[3 + idBytes.Length];
        pkt[0] = 1;
        pkt[1] = (byte)(idBytes.Length & 0xFF);
        pkt[2] = (byte)((idBytes.Length >> 8) & 0xFF);
        Buffer.BlockCopy(idBytes, 0, pkt, 3, idBytes.Length);
        await _relayStream.WriteAsync(pkt);
        await _relayStream.FlushAsync();

        // Reply: [1-byte status][2-byte len][room id]
        var hdr = new byte[3];
        await ReadExactRelay(_relayStream, hdr, 3);
        if (hdr[0] == 255) throw new Exception("Relay registration failed");
        int len = hdr[1] | (hdr[2] << 8);
        var buf = new byte[len];
        await ReadExactRelay(_relayStream, buf, len);
        RelayRoomId = Encoding.UTF8.GetString(buf);
        OnLog?.Invoke($"Relay room: {RelayRoomId}");
        return RelayRoomId;
    }

    public async Task WaitForRelayPeer()
    {
        if (_relayStream == null || _relayClient == null) throw new Exception("Not registered");
        OnLog?.Invoke("Waiting for viewer via relay...");
        var status = new byte[1];
        await ReadExactRelay(_relayStream, status, 1);
        if (status[0] != 1) throw new Exception("Relay pairing failed");
        OnLog?.Invoke("Paired via relay!");
        Accept(_relayClient, isHost: true);
        _relayClient = null; _relayStream = null;
        _ = Task.Run(() => RunHost(_cts.Token));
    }

    public async Task ConnectToRoom(string relayHost, int relayPort, string roomId)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(relayHost, relayPort);
        var s = c.GetStream();

        // Cmd 2 = CONNECT + [2-byte len][room id]
        var roomBytes = Encoding.UTF8.GetBytes(roomId);
        var pkt = new byte[3 + roomBytes.Length];
        pkt[0] = 2;
        pkt[1] = (byte)(roomBytes.Length & 0xFF);
        pkt[2] = (byte)((roomBytes.Length >> 8) & 0xFF);
        Buffer.BlockCopy(roomBytes, 0, pkt, 3, roomBytes.Length);
        await s.WriteAsync(pkt);
        await s.FlushAsync();

        var status = new byte[1];
        await ReadExactRelay(s, status, 1);
        if (status[0] != 1) throw new Exception($"Room '{roomId}' not found");
        OnLog?.Invoke("Paired via relay!");
        Accept(c, isHost: false);
        _ = Task.Run(() => RunViewer(_cts.Token));
    }

    static async Task ReadExactRelay(NetworkStream s, byte[] buf, int count)
    {
        int pos = 0;
        while (pos < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(pos, count - pos));
            if (n == 0) throw new IOException("Relay disconnected");
            pos += n;
        }
    }

    void Accept(TcpClient c, bool isHost)
    {
        _client = c;
        _client.NoDelay = true;
        _client.SendBufferSize = _client.ReceiveBufferSize = 1 << 20;
        try { _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        _stream = _client.GetStream();
        IsHost  = isHost;
        _isLocal = c.Client.RemoteEndPoint is IPEndPoint ep && IPAddress.IsLoopback(ep.Address);
        _lastNormX = _lastNormY = 0f;
        _audioMuted = true;
        _viewerMuted = true;
        OnConnected?.Invoke();
        if (_isLocal) OnLog?.Invoke("Local connection \u2013 mouse-move injection skipped");
    }

    // ── HOST: sequential capture → encode → send ──

    async Task RunHost(CancellationToken ct)
    {
        ICapture capture;
        try { capture = new DxgiCapture(); CaptureMethod = "DXGI (GPU)"; }
        catch { capture = new ScreenCapture(); CaptureMethod = "GDI (CPU fallback)"; }
        OnLog?.Invoke($"Capture: {CaptureMethod} {capture.Width}x{capture.Height}");

        AudioCapture? audio = null;
        int audioSR = 0, audioCH = 0, audioBPS = 0;
        try
        {
            audio = new AudioCapture { Muted = true };
            audioSR = audio.SampleRate; audioCH = audio.Channels; audioBPS = audio.BitsPerSample;
            audio.OnData += (buf, len) =>
            {
                if (_audioMuted || !IsConnected) return;
                var copy = new byte[len];
                Buffer.BlockCopy(buf, 0, copy, 0, len);
                _ = Task.Run(async () =>
                {
                    try { await WriteLockedAsync(MsgType.Audio, copy, ct); BytesSent += len + 5; }
                    catch { }
                });
            };
            audio.Start();
            OnLog?.Invoke($"Audio: {audioSR}Hz {audioCH}ch PCM (muted by default)");
        }
        catch (Exception ex) { OnLog?.Invoke($"Audio unavailable: {ex.Message}"); }

        VideoEncoder? vidEnc = null;
        if (VideoEncoder.TryCreate(capture.Width, capture.Height, 60, out vidEnc, out var encLog))
            CaptureMethod += $" + {vidEnc!.Name}";
        OnLog?.Invoke(encLog);

        using (capture)
        using (audio)
        using (vidEnc)
        try
        {
            int frameSize = capture.FrameSize;
            await WriteLockedAsync(MsgType.Handshake,
                Protocol.EncodeHandshake(capture.Width, capture.Height, audioSR, audioCH, audioBPS), ct);

            UdpTransport? udp = null;
            if (!_isLocal)
            {
                try
                {
                    udp = new UdpTransport();
                    udp.OnLog += m => OnLog?.Invoke(m);
                    int udpPort = udp.StartHost();
                    await WriteLockedAsync(MsgType.UdpPort, Protocol.EncodeUdpPort(udpPort), ct);
                    OnLog?.Invoke($"UDP available on port {udpPort}");
                    _udp = udp;
                }
                catch (Exception ex) { OnLog?.Invoke($"UDP unavailable: {ex.Message}"); udp?.Dispose(); udp = null; }
            }

            var raw     = new byte[frameSize];
            var prev    = vidEnc == null ? new byte[frameSize] : null;
            var delta   = vidEnc == null ? new byte[frameSize] : null;
            var scratch = vidEnc == null ? new byte[frameSize] : null;
            var comp    = vidEnc == null ? new byte[LZ4Codec.MaximumOutputSize(frameSize) + 128] : null;
            int frameN = 0, fpsN = 0;
            var fpsClock = DateTime.UtcNow;
            int screenW = capture.Width, screenH = capture.Height;
            int idleFrames = 0;

            _ = Task.Run(async () =>
            {
                await ReceiveInput(audio, ct);
                if (!ct.IsCancellationRequested)
                {
                    OnLog?.Invoke("Input receiver died — disconnecting session");
                    Disconnect();
                }
            }, ct);
            _ = Task.Run(() => ClipboardLoop(ct), ct);

            var frameSw = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested && IsConnected)
            {
                frameSw.Restart();

                if (!capture.CaptureFrame(raw)) { await Task.Delay(1, ct); continue; }

                byte fType;
                byte[] sendBuf;
                int sendLen;

                if (vidEnc != null)
                {
                    var encoded = vidEnc.Encode(raw);
                    if (encoded == null) { frameN++; FramesSkipped++; continue; }
                    sendBuf = encoded; sendLen = encoded.Length; fType = vidEnc.FrameType;
                }
                else
                {
                    int cLen = FrameCodec.CompressTiles(raw, prev!, delta!, scratch!, comp!, screenW, screenH);
                    if (cLen == 0)
                    {
                        idleFrames++;
                        // After a few idle frames, send a full keyframe to clean any stale artifacts
                        if (idleFrames == 5)
                        {
                            int kLen = FrameCodec.CompressFull(raw, comp!);
                            sendBuf = comp!; sendLen = kLen; fType = 0;
                            KeyframesSent++;
                            goto send;
                        }
                        frameN++; FramesSkipped++; await Task.Delay(1, ct); continue;
                    }
                    idleFrames = 0;
                    // Periodic keyframe every 120 frames (~2s)
                    if (frameN % 120 == 0)
                    {
                        cLen = FrameCodec.CompressFull(raw, comp!);
                        fType = 0;
                        KeyframesSent++;
                    }
                    else fType = 2;
                    sendBuf = comp!; sendLen = cLen;
                }

                send:
                // For keyframes (fType 0), sync prev fully; for tile deltas, CompressTiles already synced dirty tiles
                if (vidEnc == null && fType == 0) Buffer.BlockCopy(raw, 0, prev!, 0, frameSize);

                if (udp != null && UdpEnabled)
                {
                    udp.SendFrame(fType, sendBuf, sendLen);
                    BytesSent += sendLen;
                }
                else
                {
                    await WriteFrameAsync(fType, sendBuf, sendLen, ct);
                    BytesSent += sendLen + 6;
                }
                FramesSent++;

                frameN++;
                fpsN++;
                double elapsed = (DateTime.UtcNow - fpsClock).TotalSeconds;
                if (elapsed >= 1.0) { Fps = fpsN / elapsed; fpsN = 0; fpsClock = DateTime.UtcNow; }

                int target = _frameDelayMs;
                if (target > 0)
                {
                    int remaining = target - (int)frameSw.ElapsedMilliseconds;
                    if (remaining > 1) await Task.Delay(remaining, ct);
                }
            }
            if (!ct.IsCancellationRequested) OnLog?.Invoke("Host loop ended (connection closed by peer)");
        }
        catch (OperationCanceledException) { OnLog?.Invoke("Host stopped (cancelled)"); }
        catch (IOException ex)   { OnLog?.Invoke($"Host IO error: {ex.Message}"); }
        catch (Exception ex)     { OnError?.Invoke($"Host: {ex.GetType().Name}: {ex.Message}"); OnLog?.Invoke($"  at {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        finally
        {
            _udp?.Dispose(); _udp = null;
            CaptureMethod = "";
            Disconnect();
        }
    }

    // ── VIEWER: receive → decompress → display ──

    async Task RunViewer(CancellationToken ct)
    {
        AudioPlayer? audioPlayer = null;
        VideoDecoder? videoDec = null;
        UdpTransport? udp = null;
        try
        {
            byte[]? prev = null, frame = null, deltaBuf = null;
            int rawSize = 0, fpsN = 0;
            var fpsClock = DateTime.UtcNow;

            _ = Task.Run(() => ClipboardLoop(ct), ct);

            while (!ct.IsCancellationRequested)
            {
                var s = _stream;
                if (s == null) { OnLog?.Invoke("Viewer: stream gone"); break; }
                var msg = await Protocol.ReadMessage(s, ct);
                if (msg == null) { OnLog?.Invoke("Viewer: connection closed by host"); break; }
                var (type, payload) = msg.Value;
                BytesRecv += payload.Length + 5;

                switch (type)
                {
                    case MsgType.Handshake:
                        var (w, h, sr, ch, bps, _) = Protocol.DecodeHandshake(payload);
                        _frameW = w; _frameH = h; rawSize = w * h * 4;
                        prev = new byte[rawSize]; frame = new byte[rawSize]; deltaBuf = new byte[rawSize];
                        OnLog?.Invoke($"Remote screen {w}x{h}");
                        if (sr > 0 && ch > 0 && bps > 0)
                        {
                            try
                            {
                                audioPlayer?.Dispose();
                                audioPlayer = new AudioPlayer(sr, ch, bps);
                                OnLog?.Invoke($"Audio: {sr}Hz {ch}ch PCM");
                            }
                            catch (Exception ex) { OnLog?.Invoke($"Audio playback failed: {ex.Message}"); }
                        }
                        break;

                    case MsgType.Frame when rawSize > 0:
                        ProcessFrame(payload, ref videoDec, prev!, frame!, deltaBuf!, rawSize, ref fpsN, ref fpsClock);
                        break;

                    case MsgType.Audio when audioPlayer != null && !_viewerMuted:
                        audioPlayer.Volume = ViewerVolume;
                        audioPlayer.Feed(payload, 0, payload.Length);
                        break;

                    case MsgType.CursorPos:
                        break;

                    case MsgType.Clipboard:
                        var text = Protocol.DecodeClipboard(payload);
                        _lastClipRecv = text;
                        try { ClipboardHelper.SetText(text); }
                        catch { }
                        break;

                    case MsgType.UdpPort when payload.Length >= 4:
                        int udpPort = Protocol.DecodeUdpPort(payload);
                        if (UdpEnabled && _client?.Client.RemoteEndPoint is IPEndPoint rep)
                        {
                            try
                            {
                                udp = new UdpTransport();
                                udp.OnLog += m => OnLog?.Invoke(m);
                                udp.OnFrame += (ft, data, len) =>
                                {
                                    if (rawSize <= 0 || frame == null || prev == null || deltaBuf == null) return;
                                    var framePayload = new byte[len + 1];
                                    framePayload[0] = ft;
                                    Buffer.BlockCopy(data, 0, framePayload, 1, len);
                                    ProcessFrame(framePayload, ref videoDec, prev, frame, deltaBuf, rawSize, ref fpsN, ref fpsClock);
                                };
                                udp.StartViewer(rep.Address.ToString(), udpPort);
                                _udp = udp;
                                OnLog?.Invoke($"UDP connected to port {udpPort}");
                            }
                            catch (Exception ex) { OnLog?.Invoke($"UDP connect failed: {ex.Message}"); }
                        }
                        else
                        {
                            OnLog?.Invoke($"UDP available (port {udpPort}) but not enabled");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { OnLog?.Invoke("Viewer stopped (cancelled)"); }
        catch (IOException ex)   { OnLog?.Invoke($"Viewer IO error: {ex.Message}"); }
        catch (Exception ex)     { OnError?.Invoke($"Viewer: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            videoDec?.Dispose();
            audioPlayer?.Dispose();
            udp?.Dispose(); _udp = null;
            Disconnect();
        }
    }

    void ProcessFrame(byte[] payload, ref VideoDecoder? videoDec, byte[] prev, byte[] frame, byte[] deltaBuf,
        int rawSize, ref int fpsN, ref DateTime fpsClock)
    {
        byte ft = payload[0];
        if (ft >= 3)
        {
            if (videoDec == null)
            {
                try { videoDec = new VideoDecoder(_frameW, _frameH, ft); OnLog?.Invoke($"Video decoder active (type {ft})"); }
                catch (Exception ex) { OnLog?.Invoke($"Video decoder failed: {ex.Message}"); return; }
            }
            var pktData = new byte[payload.Length - 1];
            Buffer.BlockCopy(payload, 1, pktData, 0, pktData.Length);
            if (!videoDec.Decode(pktData, frame)) return;
        }
        else
        {
            if (ft == 0) FrameCodec.DecompressFull(payload, 1, payload.Length - 1, frame);
            else if (ft == 2) FrameCodec.DecompressTiles(payload, 1, payload.Length - 1, prev, frame, deltaBuf, _frameW, _frameH);
            else FrameCodec.DecompressDelta(payload, 1, payload.Length - 1, prev, frame, deltaBuf);
            Buffer.BlockCopy(frame, 0, prev, 0, rawSize);
            FrameCodec.SwapBgraRgba(frame, rawSize);
        }
        _latestFrame = frame;
        fpsN++;
        double elapsed = (DateTime.UtcNow - fpsClock).TotalSeconds;
        if (elapsed >= 1.0) { Fps = fpsN / elapsed; fpsN = 0; fpsClock = DateTime.UtcNow; }
    }

    // ── Input handling (host receives from viewer) ──

    volatile bool _inputAlive;
    public bool InputAlive => _inputAlive;

    async Task ReceiveInput(AudioCapture? audio, CancellationToken ct)
    {
        _inputAlive = true;
        OnLog?.Invoke("Input receiver started");
        string exitReason = "unknown";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var s = _stream;
                if (s == null) { exitReason = "stream became null"; break; }
                var msg = await Protocol.ReadMessage(s, ct);
                if (msg == null) { exitReason = "stream closed"; break; }
                var (type, p) = msg.Value;

                try
                {
                    switch (type)
                    {
                        case MsgType.MouseMove:
                            var (nx, ny) = Protocol.DecodeMouseMove(p);
                            _lastNormX = nx; _lastNormY = ny;
                            if (!_isLocal) InputInjector.MoveMouse(nx, ny);
                            break;
                        case MsgType.MouseButton:
                            var (btn, down) = Protocol.DecodeMouseButton(p);
                            if (_isLocal) InputInjector.ClickAt(_lastNormX, _lastNormY, btn, down);
                            else          InputInjector.MouseButton(btn, down);
                            break;
                        case MsgType.KeyEvent:
                            var (vk, kd) = Protocol.DecodeKey(p);
                            InputInjector.Key(vk, kd);
                            break;
                        case MsgType.MouseWheel:
                            int d = Protocol.DecodeWheel(p);
                            if (_isLocal) InputInjector.WheelAt(_lastNormX, _lastNormY, d);
                            else          InputInjector.MouseWheel(d);
                            break;
                        case MsgType.AudioMute:
                            _audioMuted = p.Length > 0 && p[0] != 0;
                            if (audio != null) audio.Muted = _audioMuted;
                            OnLog?.Invoke(_audioMuted ? "Audio muted" : "Audio unmuted");
                            break;
                        case MsgType.FpsLimit:
                            if (p.Length < 1) break;
                            int fps = p[0];
                            _frameDelayMs = fps > 0 ? 1000 / fps : 0;
                            OnLog?.Invoke($"FPS limit: {(fps == 0 ? "Unlimited" : fps.ToString())}");
                            break;
                        case MsgType.Clipboard:
                            var text = Protocol.DecodeClipboard(p);
                            _lastClipRecv = text;
                            try { ClipboardHelper.SetText(text); }
                            catch { }
                            break;
                        default:
                            OnLog?.Invoke($"Input: unknown msg type {(byte)type}, len={p.Length}");
                            break;
                    }
                }
                catch (Exception ex) { OnLog?.Invoke($"Input dispatch error ({type}): {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { exitReason = "cancelled"; }
        catch (IOException ex) { exitReason = $"IO: {ex.Message}"; }
        catch (Exception ex) { exitReason = $"{ex.GetType().Name}: {ex.Message}"; }
        finally
        {
            _inputAlive = false;
            OnLog?.Invoke($"Input receiver stopped: {exitReason}");
        }
    }

    // ── Clipboard polling ──

    async Task ClipboardLoop(CancellationToken ct)
    {
        string? lastSent = null;
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                await Task.Delay(500, ct);
                var text = ClipboardHelper.GetText();
                if (text != null && text.Length < 1_000_000 && text != lastSent && text != _lastClipRecv)
                {
                    lastSent = text;
                    await WriteLockedAsync(MsgType.Clipboard, Protocol.EncodeClipboard(text), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ── Send helpers (viewer → host) ──

    public Task SendMouseMove(float nx, float ny)   => WriteSafe(MsgType.MouseMove,   Protocol.EncodeMouseMove(nx, ny));
    public Task SendMouseBtn(int btn, bool down)     => WriteSafe(MsgType.MouseButton, Protocol.EncodeMouseButton(btn, down));
    public Task SendKey(ushort vk, bool down)        => WriteSafe(MsgType.KeyEvent,    Protocol.EncodeKey(vk, down));
    public Task SendWheel(int delta)                 => WriteSafe(MsgType.MouseWheel,  Protocol.EncodeWheel(delta));
    public Task SendAudioMute(bool muted)            => WriteSafe(MsgType.AudioMute,   [(byte)(muted ? 1 : 0)]);
    public Task SendFpsLimit(int fps)                => WriteSafe(MsgType.FpsLimit,     [(byte)fps]);

    volatile bool _writeFailed;
    async Task WriteSafe(MsgType t, byte[] p)
    {
        if (!IsConnected || IsHost) return;
        try { await WriteLockedAsync(t, p); }
        catch (Exception ex)
        {
            if (!_writeFailed) { _writeFailed = true; OnLog?.Invoke($"Viewer write failed ({t}): {ex.Message}"); }
        }
    }

    async Task WriteLockedAsync(MsgType t, byte[] p, CancellationToken ct = default)
    {
        if (!await _writeLock.WaitAsync(2000, ct)) return;
        try
        {
            var s = _stream;
            if (s != null)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                await Protocol.WriteMessage(s, t, p, cts.Token);
            }
        }
        finally { _writeLock.Release(); }
    }

    async Task WriteFrameAsync(byte ft, byte[] buf, int len, CancellationToken ct)
    {
        if (!await _writeLock.WaitAsync(2000, ct)) return;
        try
        {
            var s = _stream;
            if (s != null)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);
                await Protocol.WriteFrame(s, ft, buf, len, cts.Token);
            }
        }
        finally { _writeLock.Release(); }
    }

    public void Disconnect()
    {
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null; _client = null; _latestFrame = null;
        IsHost = false; BytesSent = BytesRecv = 0; Fps = 0;
        FramesSent = FramesSkipped = KeyframesSent = 0;
        _writeFailed = false; _inputAlive = false;
        _lastClipRecv = null;
        OnDisconnected?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _udp?.Dispose(); _udp = null;
        Disconnect();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
