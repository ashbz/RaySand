using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace SharpDesk;

static class Program
{
    static readonly NetworkPeer _peer = new();
    static Discovery? _discovery;

    static string _connectAddr = "127.0.0.1";
    static int    _port = 9876;
    static string _portStr = "9876";

    static string _relayAddr = "";
    static string _relayPort = "9877";
    static string _roomCode  = "";

    static Texture _remoteTex;
    static int     _texW, _texH;
    static bool    _texReady;
    static float   _audioVolume;
    static int     _fpsChoice = 2;
    static readonly int[] FpsValues = [15, 30, 60, 0];
    static readonly string[] FpsLabels = ["15", "30", "60", "\u221e"];

    static readonly List<(DateTime t, string msg, bool err)> _log = new();
    static bool _logScroll = true;
    static bool _showLog;

    // ── Keyboard forwarding ──

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static readonly bool[] _keyDown = new bool[400];
    static bool _wasFocused, _xb1, _xb2;

    static readonly (int rl, ushort vk)[] SpecialKeys =
    [
        (32,0x20), (39,0xDE), (44,0xBC), (45,0xBD), (46,0xBE), (47,0xBF),
        (59,0xBA), (61,0xBB), (91,0xDB), (92,0xDC), (93,0xDD), (96,0xC0),
        (256,0x1B),(257,0x0D),(258,0x09),(259,0x08),(260,0x2D),(261,0x2E),
        (262,0x27),(263,0x25),(264,0x28),(265,0x26),(266,0x21),(267,0x22),
        (268,0x24),(269,0x23),(280,0x14),(281,0x91),(282,0x90),(283,0x2C),(284,0x13),
        (290,0x70),(291,0x71),(292,0x72),(293,0x73),(294,0x74),(295,0x75),
        (296,0x76),(297,0x77),(298,0x78),(299,0x79),(300,0x7A),(301,0x7B),
        (340,0xA0),(341,0xA2),(342,0xA4),(343,0x5B),
        (344,0xA1),(345,0xA3),(346,0xA5),(347,0x5C),
        (320,0x60),(321,0x61),(322,0x62),(323,0x63),(324,0x64),
        (325,0x65),(326,0x66),(327,0x67),(328,0x68),(329,0x69),
        (330,0x6E),(331,0x6F),(332,0x6A),(333,0x6D),(334,0x6B),(335,0x0D),
    ];

    static readonly Dictionary<int, ushort> RlToVk = BuildMap();
    static Dictionary<int, ushort> BuildMap()
    {
        var m = new Dictionary<int, ushort>();
        for (int k = 48; k <= 57; k++) m[k] = (ushort)k;
        for (int k = 65; k <= 90; k++) m[k] = (ushort)k;
        foreach (var (rl, vk) in SpecialKeys) m[rl] = vk;
        return m;
    }

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
        if (!TryLoadNative()) { Console.ReadKey(true); return; }
        Run();
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
        SetTargetFPS(60);
        RlImGui.Setup();

        float dpiScale = GetWindowScaleDPI().X;
        ImGui.GetIO().FontGlobalScale = dpiScale > 1.1f ? dpiScale : 1f;

        ApplyTheme();

        _peer.OnConnected    += () => {
            Log("Connected");
            bool muted = _audioVolume < 0.01f;
            _peer.ViewerMuted = muted;
            _peer.ViewerVolume = _audioVolume;
            _ = _peer.SendAudioMute(muted);
            _ = _peer.SendFpsLimit(FpsValues[_fpsChoice]);
        };
        _peer.OnDisconnected += () => Log("Disconnected");
        _peer.OnError        += m  => Log(m, true);
        _peer.OnLog          += m  => Log(m);

        try { _peer.StartListening(_port); Log($"Listening on port {_port}"); }
        catch (Exception ex) { Log($"Listen failed: {ex.Message}", true); }

        try { _discovery = new Discovery(_port); _discovery.Start(); }
        catch (Exception ex) { Log($"Discovery: {ex.Message}", true); }

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
            foreach (var (t, msg, _) in _log)
                sb.Append('[').Append(t.ToString("HH:mm:ss")).Append("] ").AppendLine(msg);
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

    // ──────────────── HOME MODE (not viewing) ────────────────

    static void DrawHomeMode()
    {
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

        if (_peer.IsConnected && _peer.IsHost)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.20f, 0.12f, 1f));
            ImGui.BeginChild("##hosting", new Vector2(-1, 60), ImGuiChildFlags.Borders);
            ImGui.TextColored(ColGreen, "Hosting \u2014 a remote user is viewing your screen");
            ImGui.TextDisabled($"Remote: {_peer.RemoteEP}");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 90);
            if (ImGui.SmallButton("Disconnect")) _peer.Disconnect();
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem(" LAN "))  { DrawLocalTab();   ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(" Relay ")) { DrawRemoteTab();  ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem(" Log "))   { DrawLogTab();     ImGui.EndTabItem(); }
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

    static void DrawRemoteTab()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("Connect through a relay server for access across the internet.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(200); ImGui.InputText("Server", ref _relayAddr, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80); ImGui.InputText("Port##r", ref _relayPort, 8);
        ImGui.SetNextItemWidth(200); ImGui.InputText("Room", ref _roomCode, 64);
        ImGui.Spacing();
        bool can = !_peer.IsConnected && _relayAddr.Length > 0 && _roomCode.Length > 0;
        if (!can) ImGui.BeginDisabled();
        if (ImGui.Button("Host", new Vector2(100, 0))) DoRelay(true);
        ImGui.SameLine();
        if (ImGui.Button("View", new Vector2(100, 0))) DoRelay(false);
        if (!can) ImGui.EndDisabled();
        ImGui.Spacing();
        ImGui.TextDisabled("Both peers use the same Server + Room.");
    }

    static string _logTextCache = "";
    static int _logCountCache;

    static void DrawLogTab()
    {
        if (ImGui.SmallButton("Clear")) { lock (_log) _log.Clear(); _logTextCache = ""; _logCountCache = 0; }
        ImGui.SameLine(); ImGui.Checkbox("Auto-scroll", ref _logScroll);
        ImGui.Separator();

        int count;
        lock (_log) count = _log.Count;
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

    static void DoRelay(bool asHost)
    {
        if (!int.TryParse(_relayPort, out int rp)) rp = 9877;
        var addr = _relayAddr; var room = _roomCode;
        _ = Task.Run(async () => {
            try { Log($"Relay {(asHost ? "hosting" : "viewing")} room '{room}'..."); await _peer.ConnectViaRelay(addr, rp, room, asHost); }
            catch (Exception ex) { Log($"Relay failed: {ex.Message}", true); }
        });
    }

    // ──────────────── VIEWER MODE (connected + viewing) ────────────────

    static bool _toolbarExpanded = true;
    static float _toolbarAnim = 1f;

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

        bool hovered = ImGui.IsItemHovered();
        bool wFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

        if (hovered) ForwardMouse(imgPos, w, h);
        if (wFocused && hovered) ForwardKeyboard();
        HandleFocus(wFocused && hovered);

        // Green circle cursor overlay
        if (hovered)
        {
            var mouse = ImGui.GetMousePos();
            var dl = ImGui.GetForegroundDrawList();
            uint green = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.85f, 0.35f, 0.8f));
            dl.AddCircle(mouse, 8, green, 24, 2f);
        }

        DrawToolbar(basePos, avail.X);
        if (_showLog) DrawLogOverlay(basePos, avail);
    }

    static void DrawToolbar(Vector2 basePos, float areaW)
    {
        float targetA = _toolbarExpanded ? 1f : 0f;
        _toolbarAnim += (targetA - _toolbarAnim) * 0.15f;
        float barH = 30 * _toolbarAnim;
        if (barH < 1) barH = 1;

        float barW = Math.Min(areaW, 900);
        var barPos = new Vector2(basePos.X + (areaW - barW) * 0.5f, basePos.Y);

        ImGui.SetCursorScreenPos(barPos);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 2));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColToolbar);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0, 0, 0, 0));
        ImGui.BeginChild("##tb", new Vector2(barW, barH), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);

        if (_toolbarAnim > 0.5f)
        {
            float btnH = 22;

            var fc = _peer.Fps >= 25 ? ColGreen : _peer.Fps >= 10 ? ColYellow : ColRed;
            ImGui.TextColored(fc, $"{_peer.Fps:F0} fps");
            ImGui.SameLine(); Sep();

            ImGui.TextColored(ColDim, Fmt(_peer.BytesSent + _peer.BytesRecv));
            ImGui.SameLine(); Sep();

            if (_peer.FrameW > 0)
            {
                ImGui.TextColored(ColDim, $"{_peer.FrameW}x{_peer.FrameH}");
                ImGui.SameLine();
            }
            if (_peer.CaptureMethod.Length > 0)
            {
                bool gpu = _peer.CaptureMethod.Contains("h264") || _peer.CaptureMethod.Contains("DXGI");
                string codec = _peer.CaptureMethod.Contains("+") ? _peer.CaptureMethod.Split('+').Last().Trim() : _peer.CaptureMethod;
                ImGui.TextColored(gpu ? ColAccent : ColYellow, codec);
                ImGui.SameLine(); Sep();
            }

            // Volume slider
            ImGui.SetNextItemWidth(70);
            if (ImGui.SliderFloat("##vol", ref _audioVolume, 0f, 1f, _audioVolume < 0.01f ? "Mute" : $"{(int)(_audioVolume * 100)}%%"))
            {
                bool muted = _audioVolume < 0.01f;
                _peer.ViewerMuted = muted;
                _peer.ViewerVolume = _audioVolume;
                _ = _peer.SendAudioMute(muted);
            }
            ImGui.SameLine(); Sep();

            // FPS limit buttons
            for (int i = 0; i < FpsLabels.Length; i++)
            {
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
            Sep();

            if (TbBtn("Log", _showLog, ColAccent, btnH)) _showLog = !_showLog;
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 1f));
            if (ImGui.Button("X", new Vector2(0, btnH))) _peer.Disconnect();
            ImGui.PopStyleColor(2);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);

        // Collapse/expand triangle
        var tPos = new Vector2(barPos.X + barW * 0.5f - 14, barPos.Y + barH);
        ImGui.SetCursorScreenPos(tPos);
        ImGui.InvisibleButton("##tbt", new Vector2(28, 10));
        if (ImGui.IsItemClicked()) _toolbarExpanded = !_toolbarExpanded;
        var dl = ImGui.GetWindowDrawList();
        uint tc = ImGui.ColorConvertFloat4ToU32(ColDim);
        float cx = tPos.X + 14, cy = tPos.Y + 5;
        if (_toolbarExpanded)
            dl.AddTriangleFilled(new Vector2(cx - 5, cy - 2), new Vector2(cx + 5, cy - 2), new Vector2(cx, cy + 3), tc);
        else
            dl.AddTriangleFilled(new Vector2(cx - 5, cy + 2), new Vector2(cx + 5, cy + 2), new Vector2(cx, cy - 3), tc);
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

        int count;
        lock (_log) count = _log.Count;
        if (count != _logCountCache) { _logTextCache = BuildLogText(); _logCountCache = count; }

        ImGui.InputTextMultiline("##logovtxt", ref _logTextCache, (uint)Math.Max(_logTextCache.Length + 1024, 65536),
            new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
        if (_logScroll) ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    static void ForwardMouse(Vector2 imgPos, float w, float h)
    {
        var mouse = ImGui.GetMousePos();
        float nx = Math.Clamp((mouse.X - imgPos.X) / w, 0f, 1f);
        float ny = Math.Clamp((mouse.Y - imgPos.Y) / h, 0f, 1f);
        _ = _peer.SendMouseMove(nx, ny);

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

    static void Sep()
    {
        ImGui.TextColored(new Vector4(0.3f, 0.32f, 0.4f, 0.6f), "\u2502");
        ImGui.SameLine();
    }

    static bool TbBtn(string label, bool active, Vector4 activeCol, float h)
    {
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, activeCol);
        bool clicked = ImGui.Button(label, new Vector2(0, h));
        if (active) ImGui.PopStyleColor();
        return clicked;
    }

    // ── Keyboard / XButton ──

    static void ForwardKeyboard() { foreach (var (rl, vk) in RlToVk) CheckKey(rl, vk); }

    static void CheckKey(int rl, ushort vk)
    {
        bool down = IsKeyDown((KeyboardKey)rl);
        if (down == _keyDown[rl]) return;
        _keyDown[rl] = down;
        _ = _peer.SendKey(vk, down);
    }

    static void HandleFocus(bool focused)
    {
        if (_wasFocused && !focused) ReleaseAll();
        _wasFocused = focused;
    }

    static void ReleaseAll()
    {
        for (int i = 0; i < _keyDown.Length; i++)
        {
            if (!_keyDown[i]) continue;
            _keyDown[i] = false;
            if (RlToVk.TryGetValue(i, out ushort vk)) _ = _peer.SendKey(vk, false);
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
        Console.WriteLine("SharpDesk Bandwidth Benchmark (60s, localhost)\n==============================================\nCtrl+C to stop early.\n");

        var host = new NetworkPeer();
        var viewer = new NetworkPeer();
        var setupLogs = new List<string>();
        host.OnLog += m => setupLogs.Add($"[H] {m}");
        viewer.OnLog += m => setupLogs.Add($"[V] {m}");
        host.OnError += m => Console.WriteLine($"  [H ERR] {m}");
        viewer.OnError += m => Console.WriteLine($"  [V ERR] {m}");

        host.StartListening(BenchPort);
        await Task.Delay(500);
        try { await viewer.ConnectAsync("127.0.0.1", BenchPort); }
        catch (Exception ex) { Console.WriteLine($"Connect failed: {ex.Message}"); host.Dispose(); return; }

        await Task.Delay(2000);
        foreach (var l in setupLogs) Console.WriteLine($"  {l}");
        host.OnLog += m => { }; viewer.OnLog += m => { };
        Console.WriteLine();

        Console.WriteLine($"{"Sec",-5} {"FPS",6} {"Sent/s",10} {"AvgFrame",10} {"Skipped",8} {"TotalSent",12} {"Mbps",7}");
        Console.WriteLine(new string('-', 64));

        long prevSent = host.BytesSent, prevFrames = host.FramesSent, prevSkipped = host.FramesSkipped, peakRate = 0;
        double fpsSum = 0; int samples = 0;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            for (int sec = 1; sec <= Seconds; sec++)
            {
                await Task.Delay(1000, cts.Token);
                long sent = host.BytesSent, frames = host.FramesSent, skipped = host.FramesSkipped;
                long sentRate = sent - prevSent, framesDelta = frames - prevFrames, skippedDelta = skipped - prevSkipped;
                prevSent = sent; prevFrames = frames; prevSkipped = skipped;
                if (sentRate > peakRate) peakRate = sentRate;
                double fps = host.Fps; fpsSum += fps; samples++;
                string avgFrame = framesDelta > 0 ? Fmt(sentRate / framesDelta) : "-";
                Console.WriteLine($"{sec,-5} {fps,6:F1} {Fmt(sentRate),10}/s {avgFrame,10} {skippedDelta,8} {Fmt(sent),12} {sentRate * 8.0 / 1_000_000,6:F2}");
                if (!host.IsConnected || !viewer.IsConnected) { Console.WriteLine("** Connection lost! **"); break; }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("\nStopped early."); }

        double totalSec = Math.Max(1, samples);
        long totalSent = host.BytesSent, rawFrame = (long)viewer.FrameW * viewer.FrameH * 4;
        double avgFps = samples > 0 ? fpsSum / samples : 0, avgBw = totalSent / totalSec;
        double avgFrameBytes = host.FramesSent > 0 ? (double)totalSent / host.FramesSent : 0;

        Console.WriteLine($"\n{"=== RESULTS ===",42}");
        Console.WriteLine($"  Capture: {host.CaptureMethod}  |  {viewer.FrameW}x{viewer.FrameH}  |  Raw: {Fmt(rawFrame)}");
        Console.WriteLine($"  Duration: {samples}s  |  Avg FPS: {avgFps:F1}  |  Sent: {host.FramesSent:N0}  Skip: {host.FramesSkipped:N0}  Key: {host.KeyframesSent:N0}");
        Console.WriteLine($"  Total: {Fmt(totalSent)}  |  Avg BW: {Fmt((long)avgBw)}/s ({avgBw * 8 / 1_000_000:F2} Mbps)  |  Peak: {Fmt(peakRate)}/s");
        Console.WriteLine($"  Avg frame: {Fmt((long)avgFrameBytes)}  |  Ratio: {(rawFrame > 0 ? $"{avgFrameBytes / rawFrame * 100:F3}%" : "?")}");

        viewer.Disconnect(); host.Disconnect(); await Task.Delay(500);
        viewer.Dispose(); host.Dispose();
    }
}
