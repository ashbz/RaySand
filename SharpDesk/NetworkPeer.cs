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

    /// <summary>Viewer-side mute: instantly stops audio playback without network round-trip.</summary>
    public bool ViewerMuted { get => _viewerMuted; set => _viewerMuted = value; }
    public float ViewerVolume { get; set; } = 0f;

    volatile byte[]? _latestFrame;
    volatile int _frameW, _frameH;
    public byte[]? LatestFrame => _latestFrame;
    public int FrameW => _frameW;
    public int FrameH => _frameH;

    public long   BytesSent     { get; private set; }
    public long   BytesRecv     { get; private set; }
    public double Fps           { get; private set; }
    public long   FramesSent    { get; private set; }
    public long   FramesSkipped { get; private set; }
    public long   KeyframesSent { get; private set; }

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

    public async Task ConnectViaRelay(string relayHost, int relayPort, string roomCode, bool asHost)
    {
        var c = new TcpClient { NoDelay = true };
        await c.ConnectAsync(relayHost, relayPort);
        var s = c.GetStream();

        var code = Encoding.UTF8.GetBytes(roomCode);
        await s.WriteAsync(BitConverter.GetBytes((ushort)code.Length));
        await s.WriteAsync(code);
        await s.FlushAsync();

        int status = s.ReadByte();
        if (status == 0)
        {
            OnLog?.Invoke("Waiting for peer to join room...");
            status = s.ReadByte();
        }
        if (status != 1) throw new Exception("Relay pairing failed");
        OnLog?.Invoke("Paired via relay!");

        Accept(c, asHost);
        if (asHost)
            _ = Task.Run(() => RunHost(_cts.Token));
        else
            _ = Task.Run(() => RunViewer(_cts.Token));
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
        if (_isLocal) OnLog?.Invoke("Local connection – mouse-move injection skipped");
    }

    // ── HOST: capture → compress → send ──

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
            audioSR  = audio.SampleRate;
            audioCH  = audio.Channels;
            audioBPS = audio.BitsPerSample;
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
            OnLog?.Invoke($"Audio: {audioSR}Hz {audioCH}ch {audioBPS}bit (muted by default)");
        }
        catch (Exception ex) { OnLog?.Invoke($"Audio unavailable: {ex.Message}"); }

        VideoEncoder? h264 = null;
        if (VideoEncoder.TryCreate(capture.Width, capture.Height, 60, out h264, out var encLog))
            CaptureMethod += $" + {h264!.Name}";
        OnLog?.Invoke(encLog);

        using (capture)
        using (audio)
        using (h264)
        try
        {
            int frameSize = capture.FrameSize;
            await WriteLockedAsync(MsgType.Handshake,
                Protocol.EncodeHandshake(capture.Width, capture.Height, audioSR, audioCH, audioBPS), ct);

            var raw     = new byte[frameSize];
            var prev    = h264 == null ? new byte[frameSize] : null;
            var delta   = h264 == null ? new byte[frameSize] : null;
            var scratch = h264 == null ? new byte[frameSize] : null;
            var comp    = h264 == null ? new byte[LZ4Codec.MaximumOutputSize(frameSize) + 128] : null;
            int frameN = 0, fpsN = 0;
            var fpsClock = DateTime.UtcNow;

            _ = Task.Run(() => ReceiveInput(audio, ct), ct);

            while (!ct.IsCancellationRequested && IsConnected)
            {
                if (!capture.CaptureFrame(raw)) { await Task.Delay(1, ct); continue; }

                byte fType;
                byte[] sendBuf;
                int sendLen;

                if (h264 != null)
                {
                    var encoded = h264.Encode(raw);
                    if (encoded == null) { frameN++; FramesSkipped++; continue; }
                    sendBuf = encoded;
                    sendLen = encoded.Length;
                    fType = 3;
                }
                else if (frameN % 300 == 0)
                {
                    int cLen = FrameCodec.CompressFull(raw, comp!);
                    sendBuf = comp!; sendLen = cLen; fType = 0;
                    KeyframesSent++;
                }
                else
                {
                    int cLen = FrameCodec.CompressTiles(raw, prev!, delta!, scratch!, comp!, capture.Width, capture.Height);
                    if (cLen == 0) { frameN++; FramesSkipped++; await Task.Delay(1, ct); continue; }
                    sendBuf = comp!; sendLen = cLen; fType = 2;
                }

                if (h264 == null) Buffer.BlockCopy(raw, 0, prev!, 0, frameSize);
                await WriteFrameAsync(fType, sendBuf, sendLen, ct);
                BytesSent += sendLen + 6;
                FramesSent++;

                frameN++;
                fpsN++;
                double elapsed = (DateTime.UtcNow - fpsClock).TotalSeconds;
                if (elapsed >= 1.0) { Fps = fpsN / elapsed; fpsN = 0; fpsClock = DateTime.UtcNow; }

                int delay = _frameDelayMs;
                if (delay > 0) await Task.Delay(delay, ct);
            }
            if (!ct.IsCancellationRequested) OnLog?.Invoke("Host loop ended (connection closed by peer)");
        }
        catch (OperationCanceledException) { OnLog?.Invoke("Host stopped (cancelled)"); }
        catch (IOException ex)   { OnLog?.Invoke($"Host IO error: {ex.Message}"); }
        catch (Exception ex)     { OnError?.Invoke($"Host: {ex.GetType().Name}: {ex.Message}"); OnLog?.Invoke($"  at {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        finally
        {
            CaptureMethod = "";
            Disconnect();
        }
    }

    // ── VIEWER: receive → decompress → display ──

    async Task RunViewer(CancellationToken ct)
    {
        AudioPlayer? audioPlayer = null;
        VideoDecoder? h264dec = null;
        try
        {
            byte[]? prev = null, frame = null, deltaBuf = null;
            int rawSize = 0, fpsN = 0;
            var fpsClock = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                var s = _stream; if (s == null) { OnLog?.Invoke("Viewer: stream gone"); break; }
                var msg = await Protocol.ReadMessage(s, ct);
                if (msg == null) { OnLog?.Invoke("Viewer: connection closed by host"); break; }
                var (type, payload) = msg.Value;
                BytesRecv += payload.Length + 5;

                switch (type)
                {
                    case MsgType.Handshake:
                        var (w, h, sr, ch, bps) = Protocol.DecodeHandshake(payload);
                        _frameW = w; _frameH = h; rawSize = w * h * 4;
                        prev = new byte[rawSize]; frame = new byte[rawSize]; deltaBuf = new byte[rawSize];
                        OnLog?.Invoke($"Remote screen {w}x{h}");
                        if (sr > 0 && ch > 0 && bps > 0)
                        {
                            try
                            {
                                audioPlayer?.Dispose();
                                audioPlayer = new AudioPlayer(sr, ch, bps);
                                OnLog?.Invoke($"Audio playback: {sr}Hz {ch}ch {bps}bit");
                            }
                            catch (Exception ex) { OnLog?.Invoke($"Audio playback failed: {ex.Message}"); }
                        }
                        break;

                    case MsgType.Frame when rawSize > 0:
                        byte ft = payload[0];
                        if (ft == 3)
                        {
                            if (h264dec == null)
                            {
                                try { h264dec = new VideoDecoder(_frameW, _frameH); OnLog?.Invoke("H.264 decoder active"); }
                                catch (Exception ex) { OnLog?.Invoke($"H.264 decoder failed: {ex.Message}"); break; }
                            }
                            var pktData = new byte[payload.Length - 1];
                            Buffer.BlockCopy(payload, 1, pktData, 0, pktData.Length);
                            if (!h264dec.Decode(pktData, frame!)) break;
                        }
                        else
                        {
                            if (ft == 0) FrameCodec.DecompressFull(payload, 1, payload.Length - 1, frame!);
                            else if (ft == 2) FrameCodec.DecompressTiles(payload, 1, payload.Length - 1, prev!, frame!, deltaBuf!, _frameW, _frameH);
                            else FrameCodec.DecompressDelta(payload, 1, payload.Length - 1, prev!, frame!, deltaBuf!);
                            Buffer.BlockCopy(frame!, 0, prev!, 0, rawSize);
                            FrameCodec.SwapBgraRgba(frame!, rawSize);
                        }
                        _latestFrame = frame;
                        fpsN++;
                        double elapsed = (DateTime.UtcNow - fpsClock).TotalSeconds;
                        if (elapsed >= 1.0) { Fps = fpsN / elapsed; fpsN = 0; fpsClock = DateTime.UtcNow; }
                        break;

                    case MsgType.Audio when audioPlayer != null && !_viewerMuted:
                        audioPlayer.Volume = ViewerVolume;
                        audioPlayer.Feed(payload, 0, payload.Length);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { OnLog?.Invoke("Viewer stopped (cancelled)"); }
        catch (IOException ex)   { OnLog?.Invoke($"Viewer IO error: {ex.Message}"); }
        catch (Exception ex)     { OnError?.Invoke($"Viewer: {ex.GetType().Name}: {ex.Message}"); OnLog?.Invoke($"  at {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        finally
        {
            h264dec?.Dispose();
            audioPlayer?.Dispose();
            Disconnect();
        }
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
                if (msg == null) { exitReason = "stream closed (ReadMessage returned null)"; break; }
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
                            OnLog?.Invoke(_audioMuted ? "Audio muted by viewer" : "Audio unmuted by viewer");
                            break;
                        case MsgType.FpsLimit:
                            if (p.Length < 1) break;
                            int fps = p[0];
                            _frameDelayMs = fps > 0 ? 1000 / fps : 0;
                            OnLog?.Invoke($"FPS limit: {(fps == 0 ? "Unlimited" : fps.ToString())}");
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
        catch (IOException ex) { exitReason = $"IO error: {ex.Message}"; }
        catch (Exception ex) { exitReason = $"{ex.GetType().Name}: {ex.Message}"; }
        finally
        {
            _inputAlive = false;
            OnLog?.Invoke($"Input receiver stopped: {exitReason}");
        }
    }

    // ── Send helpers (viewer → host) ──

    public Task SendMouseMove(float nx, float ny)   => WriteSafe(MsgType.MouseMove,   Protocol.EncodeMouseMove(nx, ny));
    public Task SendMouseBtn(int btn, bool down)     => WriteSafe(MsgType.MouseButton, Protocol.EncodeMouseButton(btn, down));
    public Task SendKey(ushort vk, bool down)        => WriteSafe(MsgType.KeyEvent,    Protocol.EncodeKey(vk, down));
    public Task SendWheel(int delta)                 => WriteSafe(MsgType.MouseWheel,  Protocol.EncodeWheel(delta));
    public Task SendAudioMute(bool muted)            => WriteSafe(MsgType.AudioMute,   [(byte)(muted ? 1 : 0)]);
    public Task SendFpsLimit(int fps)                 => WriteSafe(MsgType.FpsLimit,    [(byte)fps]);

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
        await _writeLock.WaitAsync(ct);
        try { var s = _stream; if (s != null) await Protocol.WriteMessage(s, t, p, ct); }
        finally { _writeLock.Release(); }
    }

    async Task WriteFrameAsync(byte ft, byte[] buf, int len, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { var s = _stream; if (s != null) await Protocol.WriteFrame(s, ft, buf, len, ct); }
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
        OnDisconnected?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        Disconnect();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
