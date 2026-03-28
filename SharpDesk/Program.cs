using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

[assembly: SupportedOSPlatform("windows")]

namespace SharpDesk;

static class Program
{
    static readonly NetworkPeer _peer = new();
    static Discovery? _discovery;

    static string _connectAddr = "127.0.0.1";
    static int    _port = 9876;
    static string _portStr = "9876";

    static string _relayAddr = "64.176.178.246";
    static int    _relayPort = 9877;
    static string _relayPortStr = "9877";
    static string _connectRoomId = "";
    static string _myRoomId = "";
    static string _relayStatus = "Connecting...";
    static string _connectError = "";
    static bool   _relayOk;

    static readonly string IniPath = Path.Combine(AppContext.BaseDirectory, "sharpdesk.ini");

    static Texture _remoteTex;
    static int     _texW, _texH;
    static bool    _texReady;
    static float   _audioVolume;
    static int     _fpsChoice = 2;
    static bool    _useUdp;
    static readonly int[] FpsValues = [15, 30, 60, 0];
    static readonly string[] FpsLabels = ["15", "30", "60", "Max"];

    static readonly List<(DateTime t, string msg, bool err)> _log = new();
    static bool _logScroll = true;
    static bool _showLog;
    static bool _cursorHidden;

    // ── Keyboard forwarding ──

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    static bool _isBorderless;
    static RECT _savedWindowRect;
    static IntPtr _savedStyle;
    static IntPtr _hwnd;

    static void DoBorderlessToggle()
    {
        var handle = _hwnd;
        const int GWL_STYLE = -16;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_SHOWWINDOW = 0x0040;

        if (!_isBorderless)
        {
            GetWindowRect(handle, out _savedWindowRect);
            _savedStyle = GetWindowLongPtr(handle, GWL_STYLE);

            // Remove title bar and borders (WS_CAPTION | WS_THICKFRAME)
            long style = _savedStyle.ToInt64() & ~0x00C40000L & ~0x00040000L;
            SetWindowLongPtr(handle, GWL_STYLE, (IntPtr)style);

            int sw = GetSystemMetrics(0); // SM_CXSCREEN
            int sh = GetSystemMetrics(1); // SM_CYSCREEN
            SetWindowPos(handle, IntPtr.Zero, 0, 0, sw, sh, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            _isBorderless = true;
        }
        else
        {
            SetWindowLongPtr(handle, GWL_STYLE, _savedStyle);
            int x = _savedWindowRect.Left, y = _savedWindowRect.Top;
            int w = _savedWindowRect.Right - x, h = _savedWindowRect.Bottom - y;
            SetWindowPos(handle, IntPtr.Zero, x, y, w, h, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            _isBorderless = false;
        }
    }

    static bool _wasFocused, _xb1, _xb2;

    // ── Theme colors ──
    static readonly Vector4 ColBg       = new(0.067f, 0.071f, 0.106f, 1f);
    static readonly Vector4 ColPanel    = new(0.098f, 0.102f, 0.149f, 1f);
    static readonly Vector4 ColBorder   = new(0.180f, 0.190f, 0.260f, 1f);
    static readonly Vector4 ColAccent   = new(0.337f, 0.541f, 1.000f, 1f);
    static readonly Vector4 ColAccentH  = new(0.420f, 0.620f, 1.000f, 1f);
    static readonly Vector4 ColGreen    = new(0.306f, 0.855f, 0.475f, 1f);
    static readonly Vector4 ColYellow   = new(0.961f, 0.800f, 0.302f, 1f);
    static readonly Vector4 ColRed      = new(0.941f, 0.353f, 0.353f, 1f);
    static readonly Vector4 ColText     = new(0.847f, 0.871f, 0.949f, 1f);
    static readonly Vector4 ColDim      = new(0.502f, 0.533f, 0.639f, 1f);
    static readonly Vector4 ColToolbar  = new(0.090f, 0.094f, 0.137f, 0.96f);

    // ── Entry ──

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench") { RunBenchmark().GetAwaiter().GetResult(); return; }
        try { SetProcessDPIAware(); } catch { }
        LoadIni();
        if (!TryLoadNative()) { Console.ReadKey(true); return; }
        Run();
    }

    static void LoadIni()
    {
        try
        {
            if (File.Exists(IniPath))
            {
                foreach (var line in File.ReadAllLines(IniPath))
                {
                    var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2) continue;
                    switch (parts[0].ToLower())
                    {
                        case "relay_host": _relayAddr = parts[1]; break;
                        case "relay_port": if (int.TryParse(parts[1], out int rp)) { _relayPort = rp; _relayPortStr = parts[1]; } break;
                        case "listen_port": if (int.TryParse(parts[1], out int lp)) { _port = lp; _portStr = parts[1]; } break;
                        case "last_room": _connectRoomId = parts[1]; break;
                    }
                }
            }
            else
            {
                SaveIni();
            }
        }
        catch { }
    }

    static void SaveIni()
    {
        try { File.WriteAllText(IniPath, $"relay_host={_relayAddr}\nrelay_port={_relayPort}\nlisten_port={_port}\nlast_room={_connectRoomId}\n"); }
        catch { }
    }

    static bool TryLoadNative()
    {
        try { NativeLibrary.Load("raylib", typeof(Program).Assembly, null); return true; }
        catch
        {
            Console.WriteLine("Failed to load raylib.dll.");
            Console.WriteLine("Install VC++ 2015-2022 Redistributable (x64):");
            Console.WriteLine("https://aka.ms/vs/17/release/vc_redist.x64.exe");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe void Run()
    {
        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_WINDOW_HIGHDPI | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(1280, 800, "SharpDesk");
        _hwnd = (IntPtr)GetWindowHandle();
        SetTargetFPS(60);
        RlImGui.Setup();

        float dpiScale = GetWindowScaleDPI().X;
        ImGui.GetIO().FontGlobalScale = dpiScale > 1.1f ? dpiScale : 1f;

        ApplyTheme();

        _peer.OnConnected += () => {
            Log("Connected");
            bool muted = _audioVolume < 0.01f;
            _peer.ViewerMuted = muted;
            _peer.ViewerVolume = _audioVolume;
            _peer.UdpEnabled = _useUdp;
            _ = _peer.SendAudioMute(muted);
            _ = _peer.SendFpsLimit(FpsValues[_fpsChoice]);
        };
        _peer.OnDisconnected += () => { Log("Disconnected"); RegisterWithRelay(); };
        _peer.OnError        += m  => Log(m, true);
        _peer.OnLog          += m  => Log(m);

        try { _peer.StartListening(_port); Log($"Listening on port {_port}"); }
        catch (Exception ex) { Log($"Listen failed: {ex.Message}", true); }

        try { _discovery = new Discovery(_port); _discovery.Start(); }
        catch (Exception ex) { Log($"Discovery: {ex.Message}", true); }

        RegisterWithRelay();

        while (!WindowShouldClose())
        {
            UploadFrame();
            BeginDrawing();
            ClearBackground(new Color { r = 17, g = 18, b = 27, a = 255 });
            RlImGui.Begin();

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.Pos);
            ImGui.SetNextWindowSize(vp.Size);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
            ImGui.Begin("##root", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus);
            ImGui.PopStyleVar(2);

            bool viewing = _peer.IsConnected && !_peer.IsHost && _texReady;
            if (viewing) DrawViewerMode();
            else         DrawHomeMode();

            ImGui.End();
            RlImGui.End();
            EndDrawing();
        }

        _discovery?.Dispose();
        _peer.Dispose();
        if (_texReady) UnloadTexture(_remoteTex);
        RlImGui.Shutdown();
        CloseWindow();
    }

    static void ApplyTheme()
    {
        var s = ImGui.GetStyle();
        s.WindowRounding    = 8f;
        s.ChildRounding     = 6f;
        s.FrameRounding     = 5f;
        s.GrabRounding      = 4f;
        s.PopupRounding     = 6f;
        s.TabRounding       = 5f;
        s.ScrollbarRounding = 6f;
        s.WindowPadding     = new Vector2(14, 14);
        s.FramePadding      = new Vector2(10, 6);
        s.ItemSpacing       = new Vector2(10, 8);
        s.ScrollbarSize     = 12f;
        s.GrabMinSize       = 10f;
        s.WindowBorderSize  = 1f;
        s.ChildBorderSize   = 1f;
        s.TabBorderSize     = 0f;

        var c = s.Colors;
        c[(int)ImGuiCol.WindowBg]            = ColPanel;
        c[(int)ImGuiCol.ChildBg]             = ColBg;
        c[(int)ImGuiCol.PopupBg]             = new Vector4(0.10f, 0.11f, 0.16f, 0.96f);
        c[(int)ImGuiCol.Border]              = ColBorder;
        c[(int)ImGuiCol.FrameBg]             = new Vector4(0.14f, 0.15f, 0.21f, 1f);
        c[(int)ImGuiCol.FrameBgHovered]      = new Vector4(0.18f, 0.19f, 0.27f, 1f);
        c[(int)ImGuiCol.FrameBgActive]       = new Vector4(0.22f, 0.24f, 0.33f, 1f);
        c[(int)ImGuiCol.TitleBg]             = ColBg;
        c[(int)ImGuiCol.TitleBgActive]       = ColBg;
        c[(int)ImGuiCol.MenuBarBg]           = ColPanel;
        c[(int)ImGuiCol.ScrollbarBg]         = ColBg;
        c[(int)ImGuiCol.ScrollbarGrab]       = ColBorder;
        c[(int)ImGuiCol.ScrollbarGrabHovered]= new Vector4(0.28f, 0.30f, 0.40f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabActive] = ColAccent;
        c[(int)ImGuiCol.CheckMark]           = ColAccent;
        c[(int)ImGuiCol.SliderGrab]          = ColAccent;
        c[(int)ImGuiCol.SliderGrabActive]    = ColAccentH;
        c[(int)ImGuiCol.Button]              = new Vector4(0.16f, 0.17f, 0.24f, 1f);
        c[(int)ImGuiCol.ButtonHovered]       = new Vector4(0.22f, 0.24f, 0.34f, 1f);
        c[(int)ImGuiCol.ButtonActive]        = ColAccent;
        c[(int)ImGuiCol.Header]              = new Vector4(0.16f, 0.17f, 0.24f, 1f);
        c[(int)ImGuiCol.HeaderHovered]       = new Vector4(0.22f, 0.24f, 0.34f, 1f);
        c[(int)ImGuiCol.HeaderActive]        = ColAccent;
        c[(int)ImGuiCol.Separator]           = ColBorder;
        c[(int)ImGuiCol.Tab]                 = new Vector4(0.12f, 0.13f, 0.19f, 1f);
        c[(int)ImGuiCol.TabHovered]          = new Vector4(0.22f, 0.24f, 0.34f, 1f);
        c[(int)ImGuiCol.TabSelected]         = new Vector4(0.20f, 0.22f, 0.32f, 1f);
        c[(int)ImGuiCol.TableHeaderBg]       = new Vector4(0.12f, 0.13f, 0.19f, 1f);
        c[(int)ImGuiCol.TableBorderStrong]   = ColBorder;
        c[(int)ImGuiCol.TableBorderLight]    = new Vector4(0.14f, 0.15f, 0.20f, 1f);
        c[(int)ImGuiCol.TableRowBg]          = Vector4.Zero;
        c[(int)ImGuiCol.TableRowBgAlt]       = new Vector4(1, 1, 1, 0.02f);
        c[(int)ImGuiCol.Text]                = ColText;
        c[(int)ImGuiCol.TextDisabled]        = ColDim;
        c[(int)ImGuiCol.ResizeGrip]          = Vector4.Zero;
    }

    static void Log(string msg, bool err = false) { lock (_log) _log.Add((DateTime.Now, msg, err)); }

    static string BuildLogText()
    {
        lock (_log)
        {
            var sb = new StringBuilder(_log.Count * 60);
            foreach (var (t, msg, _) in _log) sb.Append('[').Append(t.ToString("HH:mm:ss")).Append("] ").AppendLine(msg);
            return sb.ToString();
        }
    }

    // ── Texture upload ──

    static unsafe void UploadFrame()
    {
        var f = _peer.LatestFrame;
        if (f == null) return;
        int w = _peer.FrameW, h = _peer.FrameH;
        if (w <= 0 || h <= 0) return;

        if (w != _texW || h != _texH || !_texReady)
        {
            if (_texReady) UnloadTexture(_remoteTex);
            var blank = new Color[w * h];
            for (int i = 0; i < blank.Length; i++) blank[i] = new Color { a = 255 };
            fixed (Color* p = blank)
            {
                var img = new Image { data = p, width = w, height = h, mipmaps = 1,
                    format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8 };
                _remoteTex = LoadTextureFromImage(img);
            }
            SetTextureFilter(_remoteTex, TextureFilter.TEXTURE_FILTER_BILINEAR);
            _texW = w; _texH = h; _texReady = true;
        }
        fixed (byte* p = f) UpdateTexture(_remoteTex, p);
    }

    // ──────────────── HOME MODE ────────────────

    static void DrawHomeMode()
    {
        // Restore cursor if it was hidden in viewer mode
        if (_cursorHidden) { ShowCursor(); _cursorHidden = false; }

        var avail = ImGui.GetContentRegionAvail();
        float panelW = Math.Min(520, avail.X - 40);
        float panelH = avail.Y - 20;
        ImGui.SetCursorPos(new Vector2((avail.X - panelW) * 0.5f, 10));

        ImGui.BeginChild("##home", new Vector2(panelW, panelH), ImGuiChildFlags.Borders);

        ImGui.PushStyleColor(ImGuiCol.Text, ColAccent);
        ImGui.SetWindowFontScale(1.3f);
        ImGui.Text("SharpDesk");
        ImGui.SetWindowFontScale(1f);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled("Remote Desktop");

        // Your ID
        ImGui.Spacing();
        ImGui.TextDisabled("Your ID");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 70);
        ImGui.TextColored(_relayOk ? ColGreen : ColYellow, _relayStatus);
        if (!string.IsNullOrEmpty(_myRoomId))
        {
            ImGui.SetWindowFontScale(2f);
            ImGui.PushStyleColor(ImGuiCol.Text, ColAccent);
            ImGui.Text(_myRoomId);
            ImGui.PopStyleColor();
            ImGui.SetWindowFontScale(1f);
        }
        else
        {
            ImGui.TextDisabled("...");
        }

        ImGui.Spacing();

        // Room ID input
        ImGui.TextDisabled("Room Id");
        ImGui.SetNextItemWidth(-110);
        bool enter = ImGui.InputText("##roomid", ref _connectRoomId, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        bool canConnect = !_peer.IsConnected && _connectRoomId.Trim().Length > 0 && _relayOk;
        if (!canConnect) ImGui.BeginDisabled();
        if (ImGui.Button("Connect", new Vector2(-1, 0)) || (enter && canConnect)) DoConnectToRoom();
        if (!canConnect) ImGui.EndDisabled();
        if (_connectError.Length > 0) ImGui.TextColored(ColRed, _connectError);

        if (_peer.IsConnected && _peer.IsHost)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.20f, 0.12f, 1f));
            ImGui.BeginChild("##hosting", new Vector2(-1, 60), ImGuiChildFlags.Borders);
            ImGui.TextColored(ColGreen, "Hosting - a remote user is viewing your screen");
            ImGui.TextDisabled($"Remote: {_peer.RemoteEP}");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 90);
            if (ImGui.SmallButton("Disconnect")) _peer.Disconnect();
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem(" LAN "))      { DrawLocalTab();    ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(" Settings "))  { DrawSettingsTab(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(" Log "))       { DrawLogTab();      ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        ImGui.EndChild();
    }

    static void DrawLocalTab()
    {
        ImGui.Spacing();
        var peers = _discovery?.GetPeers() ?? [];
        if (peers.Length == 0)
        {
            ImGui.TextDisabled("Scanning for LAN peers...");
        }
        else if (ImGui.BeginTable("##peers", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Machine", ImGuiTableColumnFlags.None, 2f);
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.None, 1.5f);
            ImGui.TableSetupColumn("##a", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();
            bool ok = !_peer.IsConnected;
            for (int i = 0; i < peers.Length; i++)
            {
                var p = peers[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text(p.Name);
                ImGui.TableSetColumnIndex(1); ImGui.TextDisabled($"{p.Address}:{p.TcpPort}");
                ImGui.TableSetColumnIndex(2);
                if (!ok) ImGui.BeginDisabled();
                if (ImGui.SmallButton($"Connect##{i}")) DoConnect(p.Address, p.TcpPort, p.Name);
                if (!ok) ImGui.EndDisabled();
            }
            ImGui.EndTable();
        }

        ImGui.Spacing(); ImGui.Spacing();
        ImGui.TextColored(ColDim, "Direct Connect");
        ImGui.SetNextItemWidth(180); ImGui.InputText("IP", ref _connectAddr, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80); if (ImGui.InputText("Port", ref _portStr, 8)) int.TryParse(_portStr, out _port);
        ImGui.SameLine();
        bool can = !_peer.IsConnected && _connectAddr.Length > 0 && _port > 0;
        if (!can) ImGui.BeginDisabled();
        if (ImGui.Button("Go")) DoConnect(_connectAddr, _port);
        if (!can) ImGui.EndDisabled();


        ImGui.Spacing();
        ImGui.TextColored(ColDim, "Your Addresses");
        foreach (var ip in GetLocalIps()) ImGui.BulletText($"{ip}:{_port}");
    }

    static void DrawSettingsTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColDim, "Relay Server");
        ImGui.SetNextItemWidth(-1); ImGui.InputText("##relayhost", ref _relayAddr, 256);
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputText("Port##relay", ref _relayPortStr, 8))
            if (int.TryParse(_relayPortStr, out int rp)) _relayPort = rp;
        ImGui.SameLine();
        if (ImGui.Button("Save & Reconnect", new Vector2(-1, 0)))
        {
            if (int.TryParse(_relayPortStr, out int rp)) _relayPort = rp;
            SaveIni();
            RegisterWithRelay();
        }

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        ImGui.TextColored(ColDim, "Local P2P");
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputText("Listen Port", ref _portStr, 8)) int.TryParse(_portStr, out _port);
        ImGui.Checkbox("UDP frames (experimental)", ref _useUdp);
    }

    static string _logTextCache = "";
    static int _logCountCache;

    static void DrawLogTab()
    {
        if (ImGui.SmallButton("Clear")) { lock (_log) _log.Clear(); _logTextCache = ""; _logCountCache = 0; }
        ImGui.SameLine(); ImGui.Checkbox("Auto-scroll", ref _logScroll);
        ImGui.Separator();

        int count; lock (_log) count = _log.Count;
        if (count != _logCountCache) { _logTextCache = BuildLogText(); _logCountCache = count; }

        ImGui.InputTextMultiline("##logtxt", ref _logTextCache, (uint)Math.Max(_logTextCache.Length + 1024, 65536),
            new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
        if (_logScroll) ImGui.SetScrollHereY(1f);
    }

    static void DoConnect(string addr, int port, string? label = null)
    {
        _ = Task.Run(async () => {
            try { Log($"Connecting to {label ?? addr}:{port}..."); await _peer.ConnectAsync(addr, port); }
            catch (Exception ex) { Log($"Connect failed: {ex.Message}", true); }
        });
    }

    static string _machineId = "";

    static string GetMachineId()
    {
        if (_machineId.Length > 0) return _machineId;
        try
        {
            // Windows MachineGuid is stable per OS install
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string ?? "";
            if (guid.Length > 0)
            {
                ulong hash = 14695981039346656037;
                foreach (char c in guid) { hash ^= c; hash *= 1099511628211; }
                _machineId = (hash % 10_000_000_000).ToString("D10"); // 10-digit stable ID
                return _machineId;
            }
        }
        catch { }
        // Fallback: hash machine name
        ulong h2 = 14695981039346656037;
        foreach (char c in Environment.MachineName) { h2 ^= c; h2 *= 1099511628211; }
        _machineId = (h2 % 10_000_000_000).ToString("D10");
        return _machineId;
    }

    static CancellationTokenSource? _relayCts;

    static void RegisterWithRelay()
    {
        if (string.IsNullOrWhiteSpace(_relayAddr)) { _relayStatus = "No relay configured"; return; }
        _relayCts?.Cancel();
        _relayCts = new CancellationTokenSource();
        var ct = _relayCts.Token;
        _relayStatus = "Connecting...";
        _relayOk = false;
        _myRoomId = "";
        var addr = _relayAddr;
        var port = _relayPort;
        var machineId = GetMachineId();
        _ = Task.Run(async () =>
        {
            int backoff = 2;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var roomId = await _peer.RegisterWithRelay(addr, port, machineId);
                    _myRoomId = roomId;
                    _relayOk = true;
                    _relayStatus = "Online";
                    backoff = 2;
                    Log($"Relay: registered as room {roomId}");

                    await _peer.WaitForRelayPeer();
                    // Peer connected and session started — stop the loop.
                    // OnDisconnected will call RegisterWithRelay() again.
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _relayOk = false;
                    _relayStatus = $"Reconnecting...";
                    Log($"Relay lost: {ex.Message} (retry in {backoff}s)", true);
                    try { await Task.Delay(backoff * 1000, ct); }
                    catch (OperationCanceledException) { return; }
                    backoff = Math.Min(backoff * 2, 30);
                }
            }
        });
    }

    static void DoConnectToRoom()
    {
        var addr = _relayAddr;
        var port = _relayPort;
        var room = _connectRoomId.Trim();
        if (string.IsNullOrEmpty(room)) return;
        _connectError = "";
        _ = Task.Run(async () =>
        {
            try
            {
                Log($"Connecting to room {room}...");
                await _peer.ConnectToRoom(addr, port, room);
                _connectError = "";
                SaveIni();
            }
            catch (Exception ex)
            {
                _connectError = $"Could not connect: {ex.Message}";
                Log($"Connect failed: {ex.Message}", true);
            }
        });
    }

    // ──────────────── VIEWER MODE ────────────────

    static bool _toolbarExpanded = true;
    static float _toolbarAnim = 1f;
    static float _toolbarContentW;

    static void DrawViewerMode()
    {
        var avail = ImGui.GetContentRegionAvail();
        var basePos = ImGui.GetCursorScreenPos();

        float aspect = (float)_texW / _texH;
        float w = avail.X, h = w / aspect;
        if (h > avail.Y) { h = avail.Y; w = h * aspect; }
        var imgPos = new Vector2(basePos.X + (avail.X - w) * .5f, basePos.Y + (avail.Y - h) * .5f);
        ImGui.SetCursorScreenPos(imgPos);
        ImGui.Image(new IntPtr(_remoteTex.id), new Vector2(w, h));

        bool imgHovered = ImGui.IsItemHovered();
        bool wFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

        bool ourWindowActive = GetForegroundWindow() == _hwnd;
        bool active = imgHovered && wFocused && ourWindowActive;
        if (active) ForwardMouse(imgPos, w, h);
        if (active) ForwardKeyboard();
        HandleFocus(active);

        // Hide OS cursor over image, show elsewhere
        if (imgHovered && !_cursorHidden) { HideCursor(); _cursorHidden = true; }
        else if (!imgHovered && _cursorHidden) { ShowCursor(); _cursorHidden = false; }

        // Green cursor overlay at viewer's local mouse position (no network round-trip)
        if (imgHovered)
        {
            var mouse = ImGui.GetMousePos();
            float dotX = Math.Clamp(mouse.X, imgPos.X, imgPos.X + w);
            float dotY = Math.Clamp(mouse.Y, imgPos.Y, imgPos.Y + h);
            var dl = ImGui.GetForegroundDrawList();
            uint greenFill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.85f, 0.35f, 0.25f));
            uint greenRing = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.85f, 0.35f, 0.65f));
            uint greenDot  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.85f, 0.35f, 0.9f));
            dl.AddCircleFilled(new Vector2(dotX, dotY), 10, greenFill, 24);
            dl.AddCircle(new Vector2(dotX, dotY), 10, greenRing, 24, 1.5f);
            dl.AddCircleFilled(new Vector2(dotX, dotY), 2.5f, greenDot, 12);
        }

        DrawToolbar(basePos, avail.X);
        if (_showLog) DrawLogOverlay(basePos, avail);
    }

    static void DrawToolbar(Vector2 basePos, float areaW)
    {
        float targetA = _toolbarExpanded ? 1f : 0f;
        _toolbarAnim += (targetA - _toolbarAnim) * 0.15f;
        if (_toolbarAnim < 0.01f) _toolbarAnim = 0f;
        if (_toolbarAnim > 0.99f) _toolbarAnim = 1f;

        float fullBarH = 36;
        float barW = Math.Min(areaW, 920);
        float slideY = -fullBarH * (1f - _toolbarAnim);
        var barPos = new Vector2(basePos.X + (areaW - barW) * 0.5f, basePos.Y + slideY);

        ImGui.SetCursorScreenPos(barPos);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 0));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColToolbar);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(ColBorder.X, ColBorder.Y, ColBorder.Z, 0.4f));
        ImGui.BeginChild("##tb", new Vector2(barW, fullBarH), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar);

        float btnH = 24;
        float textY = (fullBarH - ImGui.GetTextLineHeight()) * 0.5f;
        float btnY = (fullBarH - btnH) * 0.5f;

        float startX = Math.Max(12, (barW - _toolbarContentW) * 0.5f);
        ImGui.SetCursorPosY(textY);
        ImGui.SetCursorPosX(startX);

        var fc = _peer.Fps >= 25 ? ColGreen : _peer.Fps >= 10 ? ColYellow : ColRed;
        ImGui.TextColored(fc, $"{_peer.Fps:F0} fps");
        ImGui.SameLine(); Sep(fullBarH);

        ImGui.SetCursorPosY(textY);
        ImGui.TextColored(ColDim, Fmt(_peer.BytesSent + _peer.BytesRecv));
        ImGui.SameLine(); Sep(fullBarH);

        if (_peer.FrameW > 0)
        {
            ImGui.SetCursorPosY(textY);
            ImGui.TextColored(ColDim, $"{_peer.FrameW}x{_peer.FrameH}");
            ImGui.SameLine();
        }
        if (_peer.CaptureMethod.Length > 0)
        {
            ImGui.SetCursorPosY(textY);
            bool gpu = _peer.CaptureMethod.Contains("h264") || _peer.CaptureMethod.Contains("h265")
                    || _peer.CaptureMethod.Contains("hevc") || _peer.CaptureMethod.Contains("av1")
                    || _peer.CaptureMethod.Contains("DXGI") || _peer.CaptureMethod.Contains("nvenc")
                    || _peer.CaptureMethod.Contains("amf")  || _peer.CaptureMethod.Contains("qsv");
            string codec = _peer.CaptureMethod.Contains("+") ? _peer.CaptureMethod.Split('+').Last().Trim() : _peer.CaptureMethod;
            ImGui.TextColored(gpu ? ColAccent : ColYellow, codec);
            ImGui.SameLine(); Sep(fullBarH);
        }

        // Volume slider
        ImGui.SetCursorPosY(btnY);
        ImGui.SetNextItemWidth(70);
        if (ImGui.SliderFloat("##vol", ref _audioVolume, 0f, 1f, _audioVolume < 0.01f ? "Mute" : $"{(int)(_audioVolume * 100)}%%"))
        {
            bool muted = _audioVolume < 0.01f;
            _peer.ViewerMuted = muted;
            _peer.ViewerVolume = _audioVolume;
            _ = _peer.SendAudioMute(muted);
        }
        ImGui.SameLine(); Sep(fullBarH);

        // FPS buttons
        for (int i = 0; i < FpsLabels.Length; i++)
        {
            ImGui.SetCursorPosY(btnY);
            bool active = _fpsChoice == i;
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, ColAccent);
            if (ImGui.Button(FpsLabels[i], new Vector2(0, btnH)))
            {
                _fpsChoice = i;
                _ = _peer.SendFpsLimit(FpsValues[i]);
            }
            if (active) ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        Sep(fullBarH);

        // Fullscreen
        ImGui.SetCursorPosY(btnY);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (ImGui.Button("[ ]", new Vector2(btnH + 8, btnH))) DoBorderlessToggle();
        ImGui.PopStyleVar();
        ImGui.SameLine();

        // Log toggle
        ImGui.SetCursorPosY(btnY);
        if (TbBtn("Log", _showLog, ColAccent, btnH)) _showLog = !_showLog;
        ImGui.SameLine();

        // Disconnect
        ImGui.SetCursorPosY(btnY);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f));
        if (ImGui.Button("X", new Vector2(btnH, btnH))) _peer.Disconnect();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        ImGui.SameLine();
        _toolbarContentW = ImGui.GetCursorPosX() - startX;

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);

        // Expand/collapse pill
        float pillW = 60, pillH = 18;
        float pillY = barPos.Y + fullBarH;
        if (pillY < basePos.Y) pillY = basePos.Y;
        var pillPos = new Vector2(barPos.X + barW * 0.5f - pillW * 0.5f, pillY);
        ImGui.SetCursorScreenPos(pillPos);

        var dl = ImGui.GetWindowDrawList();
        uint pillBg = ImGui.ColorConvertFloat4ToU32(new Vector4(ColToolbar.X, ColToolbar.Y, ColToolbar.Z, 0.85f));
        uint pillFg = ImGui.ColorConvertFloat4ToU32(ColDim);
        dl.AddRectFilled(pillPos, new Vector2(pillPos.X + pillW, pillPos.Y + pillH), pillBg, pillH * 0.5f);

        ImGui.InvisibleButton("##tbt", new Vector2(pillW, pillH));
        if (ImGui.IsItemHovered())
        {
            uint hoverBg = ImGui.ColorConvertFloat4ToU32(new Vector4(ColBorder.X, ColBorder.Y, ColBorder.Z, 0.9f));
            dl.AddRectFilled(pillPos, new Vector2(pillPos.X + pillW, pillPos.Y + pillH), hoverBg, pillH * 0.5f);
        }
        if (ImGui.IsItemClicked()) _toolbarExpanded = !_toolbarExpanded;

        float cx = pillPos.X + pillW * 0.5f, cy = pillPos.Y + pillH * 0.5f;
        if (_toolbarExpanded)
            dl.AddTriangleFilled(new Vector2(cx - 7, cy + 3), new Vector2(cx + 7, cy + 3), new Vector2(cx, cy - 3), pillFg);
        else
            dl.AddTriangleFilled(new Vector2(cx - 7, cy - 3), new Vector2(cx + 7, cy - 3), new Vector2(cx, cy + 3), pillFg);
    }

    static void DrawLogOverlay(Vector2 basePos, Vector2 avail)
    {
        float logH = Math.Min(200, avail.Y * 0.35f);
        var logPos = new Vector2(basePos.X + 20, basePos.Y + avail.Y - logH - 10);
        float logW = avail.X - 40;

        ImGui.SetCursorScreenPos(logPos);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.05f, 0.06f, 0.09f, 0.88f));
        ImGui.BeginChild("##logov", new Vector2(logW, logH), ImGuiChildFlags.Borders);

        ImGui.Checkbox("Auto-scroll", ref _logScroll);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) { lock (_log) _log.Clear(); _logTextCache = ""; _logCountCache = 0; }
        ImGui.Separator();

        int count; lock (_log) count = _log.Count;
        if (count != _logCountCache) { _logTextCache = BuildLogText(); _logCountCache = count; }

        ImGui.InputTextMultiline("##logovtxt", ref _logTextCache, (uint)Math.Max(_logTextCache.Length + 1024, 65536),
            new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
        if (_logScroll) ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    static float _prevSendNx = -1, _prevSendNy = -1;

    static void ForwardMouse(Vector2 imgPos, float w, float h)
    {
        var mouse = ImGui.GetMousePos();
        float nx = Math.Clamp((mouse.X - imgPos.X) / w, 0f, 1f);
        float ny = Math.Clamp((mouse.Y - imgPos.Y) / h, 0f, 1f);
        if (MathF.Abs(nx - _prevSendNx) > 0.0002f || MathF.Abs(ny - _prevSendNy) > 0.0002f)
        {
            _prevSendNx = nx; _prevSendNy = ny;
            _ = _peer.SendMouseMove(nx, ny);
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))    _ = _peer.SendMouseBtn(0, true);
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))   _ = _peer.SendMouseBtn(0, false);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))   _ = _peer.SendMouseBtn(1, true);
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))  _ = _peer.SendMouseBtn(1, false);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle))  _ = _peer.SendMouseBtn(2, true);
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle)) _ = _peer.SendMouseBtn(2, false);

        float wheel = ImGui.GetIO().MouseWheel;
        if (MathF.Abs(wheel) > 0.01f) _ = _peer.SendWheel((int)(wheel * 120));
        ForwardXButtons();
    }

    // ── UI helpers ──

    static void Sep(float barH)
    {
        var pos = ImGui.GetCursorScreenPos();
        float x = pos.X + 2;
        float y1 = pos.Y - barH * 0.5f + 4;
        float y2 = y1 + barH - 8;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, y1), new Vector2(x, y2),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.32f, 0.4f, 0.4f)), 1f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6);
    }

    static bool TbBtn(string label, bool active, Vector4 activeCol, float h)
    {
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, activeCol);
        bool clicked = ImGui.Button(label, new Vector2(0, h));
        if (active) ImGui.PopStyleColor();
        return clicked;
    }

    // ── Keyboard / XButton ──
    // Use GetAsyncKeyState for ALL keys so F-keys, PrintScreen, Win+S etc.
    // are captured before the OS processes them.

    static readonly ushort[] AllVKeys = BuildVKeyList();
    static readonly bool[] _vkDown = new bool[256];

    static ushort[] BuildVKeyList()
    {
        var keys = new HashSet<ushort>();
        // Letters, digits
        for (int k = 0x08; k <= 0xDF; k++) keys.Add((ushort)k);
        return [.. keys];
    }

    static void ForwardKeyboard()
    {
        foreach (var vk in AllVKeys)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (down == _vkDown[vk]) continue;
            _vkDown[vk] = down;
            _ = _peer.SendKey(vk, down);
        }
    }

    static void HandleFocus(bool focused)
    {
        if (_wasFocused && !focused) ReleaseAll();
        _wasFocused = focused;
    }

    static void ReleaseAll()
    {
        for (int vk = 0; vk < _vkDown.Length; vk++)
        {
            if (!_vkDown[vk]) continue;
            _vkDown[vk] = false;
            _ = _peer.SendKey((ushort)vk, false);
        }
        if (_xb1) { _xb1 = false; _ = _peer.SendMouseBtn(3, false); }
        if (_xb2) { _xb2 = false; _ = _peer.SendMouseBtn(4, false); }
    }

    static void ForwardXButtons()
    {
        bool b1 = (GetAsyncKeyState(0x05) & 0x8000) != 0;
        if (b1 != _xb1) { _xb1 = b1; _ = _peer.SendMouseBtn(3, b1); }
        bool b2 = (GetAsyncKeyState(0x06) & 0x8000) != 0;
        if (b2 != _xb2) { _xb2 = b2; _ = _peer.SendMouseBtn(4, b2); }
    }

    // ── Helpers ──

    static string Fmt(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:F1} KB" : $"{b / (1024.0 * 1024):F1} MB";

    static List<string> GetLocalIps()
    {
        var ips = new List<string>();
        try { foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList) if (ip.AddressFamily == AddressFamily.InterNetwork) ips.Add(ip.ToString()); } catch { }
        if (ips.Count == 0) ips.Add("127.0.0.1");
        return ips;
    }

    // ── Benchmark ──

    static async Task RunBenchmark()
    {
        const int BenchPort = 19876, Seconds = 60;
        Console.WriteLine("SharpDesk Benchmark (60s, localhost)\n====================================\n");

        var host = new NetworkPeer();
        var viewer = new NetworkPeer();
        var logs = new List<string>();
        host.OnLog += m => logs.Add($"[H] {m}"); viewer.OnLog += m => logs.Add($"[V] {m}");
        host.OnError += m => Console.WriteLine($"  [H ERR] {m}"); viewer.OnError += m => Console.WriteLine($"  [V ERR] {m}");

        host.StartListening(BenchPort); await Task.Delay(500);
        try { await viewer.ConnectAsync("127.0.0.1", BenchPort); }
        catch (Exception ex) { Console.WriteLine($"Connect failed: {ex.Message}"); host.Dispose(); return; }

        await Task.Delay(2000);
        foreach (var l in logs) Console.WriteLine($"  {l}");
        host.OnLog += m => { }; viewer.OnLog += m => { };

        Console.WriteLine($"\n{"Sec",-5} {"FPS",6} {"Sent/s",10} {"AvgFr",10} {"Skip",8} {"Total",12} {"Mbps",7}");
        Console.WriteLine(new string('-', 64));

        long ps = host.BytesSent, pf = host.FramesSent, pk = host.FramesSkipped, peak = 0;
        double fSum = 0; int n = 0;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            for (int s = 1; s <= Seconds; s++)
            {
                await Task.Delay(1000, cts.Token);
                long sent = host.BytesSent, fr = host.FramesSent, sk = host.FramesSkipped;
                long sr = sent - ps, fd = fr - pf, sd = sk - pk;
                ps = sent; pf = fr; pk = sk;
                if (sr > peak) peak = sr;
                double fps = host.Fps; fSum += fps; n++;
                Console.WriteLine($"{s,-5} {fps,6:F1} {Fmt(sr),10}/s {(fd > 0 ? Fmt(sr / fd) : "-"),10} {sd,8} {Fmt(sent),12} {sr * 8.0 / 1_000_000,6:F2}");
                if (!host.IsConnected || !viewer.IsConnected) { Console.WriteLine("** Disconnected **"); break; }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("\nStopped."); }

        double sec = Math.Max(1, n); long tot = host.BytesSent; long raw = (long)viewer.FrameW * viewer.FrameH * 4;
        double af = n > 0 ? fSum / n : 0, ab = tot / sec, afb = host.FramesSent > 0 ? (double)tot / host.FramesSent : 0;
        Console.WriteLine($"\n  {host.CaptureMethod}  |  {viewer.FrameW}x{viewer.FrameH}  |  {af:F1} fps  |  {Fmt((long)ab)}/s  |  Ratio: {(raw > 0 ? $"{afb / raw * 100:F3}%" : "?")}");

        viewer.Disconnect(); host.Disconnect(); await Task.Delay(500);
        viewer.Dispose(); host.Dispose();
    }
}
