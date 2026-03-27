using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace N64Emu;

static class Program
{
    // N64 controller button bits (active-high in our representation)
    const int BTN_A       = 15;
    const int BTN_B       = 14;
    const int BTN_Z       = 13;
    const int BTN_START   = 12;
    const int BTN_DUP     = 11;
    const int BTN_DDOWN   = 10;
    const int BTN_DLEFT   = 9;
    const int BTN_DRIGHT  = 8;
    const int BTN_L       = 5;
    const int BTN_R       = 4;
    const int BTN_CUP     = 3;
    const int BTN_CDOWN   = 2;
    const int BTN_CLEFT   = 1;
    const int BTN_CRIGHT  = 0;

    static int _inputChoice;
    static readonly string[] _inputLabels = { "Keyboard", "Controller" };

    static (ushort buttons, sbyte stickX, sbyte stickY) PollInput() =>
        _inputChoice == 0 ? PollKeyboard() : PollGamepad();

    static (ushort, sbyte, sbyte) PollKeyboard()
    {
        ushort b = 0;
        if (IsKeyDown(KeyboardKey.KEY_Z))           b |= (1 << BTN_A);
        if (IsKeyDown(KeyboardKey.KEY_X))           b |= (1 << BTN_B);
        if (IsKeyDown(KeyboardKey.KEY_C))           b |= (1 << BTN_Z);
        if (IsKeyDown(KeyboardKey.KEY_ENTER))       b |= (1 << BTN_START);
        if (IsKeyDown(KeyboardKey.KEY_UP))          b |= (1 << BTN_DUP);
        if (IsKeyDown(KeyboardKey.KEY_DOWN))        b |= (1 << BTN_DDOWN);
        if (IsKeyDown(KeyboardKey.KEY_LEFT))        b |= (1 << BTN_DLEFT);
        if (IsKeyDown(KeyboardKey.KEY_RIGHT))       b |= (1 << BTN_DRIGHT);
        if (IsKeyDown(KeyboardKey.KEY_Q))           b |= (1 << BTN_L);
        if (IsKeyDown(KeyboardKey.KEY_E))           b |= (1 << BTN_R);
        if (IsKeyDown(KeyboardKey.KEY_I))           b |= (1 << BTN_CUP);
        if (IsKeyDown(KeyboardKey.KEY_K))           b |= (1 << BTN_CDOWN);
        if (IsKeyDown(KeyboardKey.KEY_J))           b |= (1 << BTN_CLEFT);
        if (IsKeyDown(KeyboardKey.KEY_L))           b |= (1 << BTN_CRIGHT);

        sbyte sx = 0, sy = 0;
        if (IsKeyDown(KeyboardKey.KEY_W)) sy = 80;
        if (IsKeyDown(KeyboardKey.KEY_S)) sy = -80;
        if (IsKeyDown(KeyboardKey.KEY_A)) sx = -80;
        if (IsKeyDown(KeyboardKey.KEY_D)) sx = 80;

        return (b, sx, sy);
    }

    static (ushort, sbyte, sbyte) PollGamepad()
    {
        ushort b = 0;
        if (!IsGamepadAvailable(0)) return (b, 0, 0);

        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_DOWN))  b |= (1 << BTN_A);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_LEFT))  b |= (1 << BTN_B);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_TRIGGER_2))   b |= (1 << BTN_Z);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_MIDDLE_RIGHT))     b |= (1 << BTN_START);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_UP))     b |= (1 << BTN_DUP);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_DOWN))   b |= (1 << BTN_DDOWN);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_LEFT))   b |= (1 << BTN_DLEFT);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_RIGHT))  b |= (1 << BTN_DRIGHT);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_TRIGGER_1))   b |= (1 << BTN_L);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_TRIGGER_1))  b |= (1 << BTN_R);

        float rx = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_RIGHT_X);
        float ry = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_RIGHT_Y);
        if (ry < -0.5f) b |= (1 << BTN_CUP);
        if (ry >  0.5f) b |= (1 << BTN_CDOWN);
        if (rx < -0.5f) b |= (1 << BTN_CLEFT);
        if (rx >  0.5f) b |= (1 << BTN_CRIGHT);

        float lx = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_X);
        float ly = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_Y);
        sbyte sx = (sbyte)Math.Clamp((int)(lx * 80), -80, 80);
        sbyte sy = (sbyte)Math.Clamp((int)(-ly * 80), -80, 80);

        return (b, sx, sy);
    }

    static readonly string[] RegNames =
    {
        "zero","at","v0","v1","a0","a1","a2","a3",
        "t0","t1","t2","t3","t4","t5","t6","t7",
        "s0","s1","s2","s3","s4","s5","s6","s7",
        "t8","t9","k0","k1","gp","sp","fp","ra",
    };

    static bool _logScrollToBottom = true;
    static bool _logErrorsOnly;
    static readonly List<Log.Entry> _logCache = new(2048);
    static int _logCacheVersion = -1;

    static readonly string[] _fpsLabels = { "30", "60", "Unlimited" };
    static readonly int[] _fpsValues = { 30, 60, 0 };
    static int _fpsChoice = 1;
    static bool _muted;

    static int _texW = 320, _texH = 240;

    static AudioStream _audioStream;
    static int _audioSampleRate;
    static readonly byte[] _audioFeedBuf = new byte[0x10000];

    static unsafe void Main(string[] args)
    {
        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(1600, 900, "N64 Emulator");
        SetTargetFPS(60);
        InitAudioDevice();
        RlImGui.Setup();

        _audioSampleRate = 44100;
        _audioStream = LoadAudioStream((uint)_audioSampleRate, 16, 2);
        PlayAudioStream(_audioStream);

        var machine = new N64Machine();

        // Try to load PIF ROM from common locations
        string[] pifPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "pifdata.bin"),
            Path.Combine(AppContext.BaseDirectory, "pif.bin"),
            @"C:\Users\ashot\Downloads\pifdata.bin",
        };
        foreach (var p in pifPaths)
            if (File.Exists(p)) { machine.LoadPifRom(p); break; }

        if (args.Length > 0 && File.Exists(args[0]))
            machine.LoadRom(args[0]);

        var renderer = new SoftwareRenderer(machine.Bus);

        bool appAlive = true;
        var emuThread = new Thread(() =>
        {
            while (appAlive)
            {
                if (machine.Running) machine.Tick();
                else Thread.Sleep(4);
            }
        }) { IsBackground = true, Name = "EmuThread" };
        emuThread.Start();

        var pixels = new Color[_texW * _texH];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color { a = 255 };

        Texture n64Tex;
        fixed (Color* p = pixels)
        {
            var img = new Image
            {
                data    = p,
                width   = _texW,
                height  = _texH,
                mipmaps = 1,
                format  = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
            };
            n64Tex = LoadTextureFromImage(img);
        }
        SetTextureFilter(n64Tex, TextureFilter.TEXTURE_FILTER_BILINEAR);

        while (!WindowShouldClose())
        {
            var (buttons, stickX, stickY) = PollInput();
            machine.Bus.Pif.Buttons = buttons;
            machine.Bus.Pif.StickX = stickX;
            machine.Bus.Pif.StickY = stickY;

            // Resize display texture if VI size changed
            int viW = machine.Bus.Vi.FrameWidth;
            int viH = machine.Bus.Vi.FrameHeight;
            if (viW > 0 && viH > 0 && (viW != _texW || viH != _texH))
            {
                _texW = viW;
                _texH = viH;
                pixels = new Color[_texW * _texH];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color { a = 255 };
                UnloadTexture(n64Tex);
                fixed (Color* p = pixels)
                {
                    var img = new Image
                    {
                        data    = p,
                        width   = _texW,
                        height  = _texH,
                        mipmaps = 1,
                        format  = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
                    };
                    n64Tex = LoadTextureFromImage(img);
                }
                SetTextureFilter(n64Tex, TextureFilter.TEXTURE_FILTER_BILINEAR);
            }

            fixed (Color* p = pixels)
            {
                renderer.SnapshotDisplay(p, _texW, _texH);
                UpdateTexture(n64Tex, p);
            }

            FeedAudio(machine);

            BeginDrawing();
            ClearBackground(new Color { r = 8, g = 14, b = 8, a = 255 });

            RlImGui.Begin();
            ImGui.DockSpaceOverViewport();

            if (IsFileDropped())
            {
                unsafe
                {
                    var fpl = LoadDroppedFiles();
                    for (int fi = 0; fi < (int)fpl.count; fi++)
                    {
                        string? path = fpl.paths[fi] == null ? null
                            : Marshal.PtrToStringUTF8((IntPtr)fpl.paths[fi]);
                        if (path != null)
                        {
                            machine.LoadRom(path);
                            break;
                        }
                    }
                    UnloadDroppedFiles(fpl);
                }
            }

            DrawScreenPanel(n64Tex, machine);
            DrawCpuPanel(machine);
            DrawOptionsPanel(machine);
            DrawRomPanel(machine);
            DrawInputPanel(machine);
            DrawLogPanel();
            DrawProfilerPanel();

            RlImGui.End();
            EndDrawing();
        }

        appAlive = false;
        emuThread.Join(500);
        StopAudioStream(_audioStream);
        UnloadAudioStream(_audioStream);
        UnloadTexture(n64Tex);
        CloseAudioDevice();
        RlImGui.Shutdown();
        CloseWindow();
    }

    static void DrawScreenPanel(Texture n64Tex, N64Machine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(680, 530), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("N64 Screen")) { ImGui.End(); return; }

        var avail = ImGui.GetContentRegionAvail();
        const float aspect = 4f / 3f;
        float w = avail.X, h = w / aspect;
        if (h > avail.Y) { h = avail.Y; w = h * aspect; }

        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(topLeft.X + (avail.X - w) * 0.5f, topLeft.Y));
        ImGui.Image(new IntPtr(n64Tex.id), new Vector2(w, h));

        if (!machine.Running)
        {
            string msg = machine.Bus.Cart.Loaded ? "Emulator stopped"
                : "Drop an N64 ROM (.z64 / .v64 / .n64) onto the window";
            var ts = ImGui.CalcTextSize(msg);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(topLeft.X + (avail.X - ts.X) * 0.5f, topLeft.Y + (avail.Y - ts.Y) * 0.5f),
                0xBBFFFFFF, msg);
        }
        ImGui.End();
    }

    static void DrawRomPanel(N64Machine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(500, 140), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("ROM")) { ImGui.End(); return; }

        if (machine.Bus.Cart.Loaded)
        {
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.6f, 1f), $"ROM: {machine.Bus.Cart.Title}");
            ImGui.TextDisabled($"CIC: {machine.Bus.Cart.CicType}  |  {machine.Bus.Cart.Rom.Length / 1024}KB  |  Country: {machine.Bus.Cart.CountryCode}");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "No ROM loaded");
            ImGui.TextDisabled("Drop a .z64 / .v64 / .n64 file onto the window");
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset", new Vector2(60, 0)) && machine.Bus.Cart.Loaded) machine.Reset();
        ImGui.SameLine();
        bool direct = machine.UseDirectBoot;
        if (ImGui.Checkbox("Direct Boot", ref direct)) machine.UseDirectBoot = direct;
        ImGui.SameLine();
        ImGui.TextDisabled(machine.Bus.Pif.BootRomLoaded ? "PIF: loaded" : "PIF: HLE boot");

        ImGui.Spacing();
        if (machine.Error != null)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Error: {machine.Error}");
        else if (machine.Running)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.5f, 1f),
                $"Running   Frame={machine.FrameCount}   Cycles={machine.Cpu.TotalCycles:N0}");

        ImGui.End();
    }

    static void DrawCpuPanel(N64Machine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(340, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("CPU  –  VR4300i")) { ImGui.End(); return; }

        var cpu = machine.Cpu;
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.8f, 1f), $"PC   0x{cpu.PC:X16}");
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.8f, 1f), $"HI   0x{cpu.Hi:X16}");
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.8f, 1f), $"LO   0x{cpu.Lo:X16}");
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), $"SR   0x{cpu.COP0[12]:X8}");
        ImGui.TextColored(new Vector4(0.8f, 0.7f, 1f, 1f),
            $"MI Intr 0x{machine.Bus.Mi.MiIntr:X2}  Mask 0x{machine.Bus.Mi.MiIntrMask:X2}");
        ImGui.Separator();

        if (ImGui.BeginTable("regs", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Reg", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Val", ImGuiTableColumnFlags.WidthFixed, 155);
            ImGui.TableHeadersRow();
            for (int i = 0; i < 32; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled($"${RegNames[i]}");
                ImGui.TableSetColumnIndex(1);
                long v = cpu.GPR[i];
                ImGui.TextColored(v != 0 ? new Vector4(0.9f, 0.95f, 0.88f, 1f) : new Vector4(0.25f, 0.3f, 0.22f, 1f),
                    $"0x{(ulong)v:X16}");
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    static void DrawOptionsPanel(N64Machine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(330, 250), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Options")) { ImGui.End(); return; }

        float target = _fpsValues[_fpsChoice] > 0 ? _fpsValues[_fpsChoice] : 999f;
        float frac = (float)(machine.EmuFps / target);
        var col = frac >= 0.9f ? new Vector4(0.4f, 1f, 0.5f, 1f)
                : frac >= 0.5f ? new Vector4(1f, 0.9f, 0.4f, 1f)
                               : new Vector4(1f, 0.4f, 0.4f, 1f);
        string targetStr = _fpsValues[_fpsChoice] > 0 ? $"{_fpsValues[_fpsChoice]}" : "∞";
        ImGui.TextColored(col, $"Emu speed  {machine.EmuFps:F1} / {targetStr} fps");
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("FPS Limit", ref _fpsChoice, _fpsLabels, _fpsLabels.Length))
        {
            int v = _fpsValues[_fpsChoice];
            SetTargetFPS(v > 0 ? v : 10000);
            machine.TargetFps = v;
        }

        ImGui.Separator();
        ImGui.Checkbox("Mute", ref _muted);

        ImGui.Separator();
        ImGui.SetNextItemWidth(120);
        ImGui.Combo("Input", ref _inputChoice, _inputLabels, _inputLabels.Length);
        if (_inputChoice == 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(IsGamepadAvailable(0) ? "connected" : "no gamepad");
        }

        ImGui.Separator();
        bool diagDump = RspHle.DiagDump;
        if (ImGui.Checkbox("RSP Diag Dump", ref diagDump)) RspHle.DiagDump = diagDump;
        ImGui.SameLine();
        ImGui.TextDisabled("(writes to n64_diag.txt)");

        ImGui.Separator();
        var vi = machine.Bus.Vi;
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.8f, 1f),
            $"Display  {vi.FrameWidth} × {vi.FrameHeight}  ({vi.ColorDepth}bpp)");
        ImGui.Text($"VI Origin  0x{vi.Origin:X8}");
        ImGui.Text($"SP Status  0x{machine.Bus.Rsp.SpStatus:X8}");
        ImGui.Text($"DP Status  0x{machine.Bus.Rdp.DpStatus:X8}");
        ImGui.End();
    }

    static void DrawInputPanel(N64Machine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(280, 220), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Controller")) { ImGui.End(); return; }

        ushort b = machine.Bus.Pif.Buttons;
        sbyte sx = machine.Bus.Pif.StickX;
        sbyte sy = machine.Bus.Pif.StickY;

        var on  = new Vector4(0.2f, 1f, 0.4f, 1f);
        var off = new Vector4(0.3f, 0.3f, 0.3f, 1f);

        void Btn(string label, int bit)
        {
            bool pressed = (b & (1 << bit)) != 0;
            ImGui.TextColored(pressed ? on : off, pressed ? $"[{label}]" : $" {label} ");
        }

        ImGui.Text("D-Pad:");
        ImGui.SameLine(80); Btn("UP", BTN_DUP);
        Btn("LEFT", BTN_DLEFT); ImGui.SameLine(); Btn("DOWN", BTN_DDOWN); ImGui.SameLine(); Btn("RIGHT", BTN_DRIGHT);
        ImGui.Separator();
        ImGui.Text("Buttons:");
        ImGui.SameLine(80);
        Btn("A", BTN_A); ImGui.SameLine();
        Btn("B", BTN_B); ImGui.SameLine();
        Btn("Z", BTN_Z); ImGui.SameLine();
        Btn("START", BTN_START);
        ImGui.Separator();
        Btn("L", BTN_L); ImGui.SameLine(); Btn("R", BTN_R);
        ImGui.Separator();
        ImGui.Text("C-Buttons:");
        ImGui.SameLine(80);
        Btn("C^", BTN_CUP); ImGui.SameLine();
        Btn("Cv", BTN_CDOWN); ImGui.SameLine();
        Btn("C<", BTN_CLEFT); ImGui.SameLine();
        Btn("C>", BTN_CRIGHT);
        ImGui.Separator();
        ImGui.Text($"Stick: ({sx}, {sy})");
        ImGui.Separator();
        if (_inputChoice == 0)
        {
            ImGui.TextDisabled("WASD=Stick  Z=A  X=B  C=Z  Enter=Start");
            ImGui.TextDisabled("Arrows=D-Pad  IJKL=C-Buttons  Q/E=L/R");
        }
        else
        {
            ImGui.TextDisabled("Left stick=Analog  Right stick=C-Buttons");
        }
        ImGui.End();
    }

    static void DrawLogPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(900, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Log")) { ImGui.End(); return; }
        ImGui.Checkbox("Scroll##log", ref _logScrollToBottom);
        ImGui.SameLine();
        ImGui.Checkbox("Errors Only", ref _logErrorsOnly);
        ImGui.SameLine();
        if (ImGui.Button("Clear")) Log.Clear();
        ImGui.Separator();
        ImGui.BeginChild("logscroll", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        int ver = Log.Version;
        if (ver != _logCacheVersion)
        {
            Log.SnapshotInto(_logCache);
            _logCacheVersion = ver;
        }

        for (int i = 0; i < _logCache.Count; i++)
        {
            ref var e = ref CollectionsMarshal.AsSpan(_logCache)[i];
            if (_logErrorsOnly && e.Category != Log.Cat.Error) continue;
            var c = e.Category switch
            {
                Log.Cat.GPU   => new Vector4(0.5f, 0.9f, 1.0f, 1f),
                Log.Cat.CPU   => new Vector4(0.7f, 1.0f, 0.7f, 1f),
                Log.Cat.RSP   => new Vector4(0.9f, 0.8f, 1.0f, 1f),
                Log.Cat.RDP   => new Vector4(0.5f, 0.9f, 0.9f, 1f),
                Log.Cat.DMA   => new Vector4(1.0f, 0.9f, 0.5f, 1f),
                Log.Cat.IRQ   => new Vector4(1.0f, 0.7f, 1.0f, 1f),
                Log.Cat.Error => new Vector4(1.0f, 0.3f, 0.3f, 1f),
                _             => new Vector4(0.85f, 0.85f, 0.85f, 1f),
            };
            ImGui.TextColored(c, $"[{e.When:HH:mm:ss.fff}][{e.Category,-5}] {e.Text}");
        }
        if (_logScrollToBottom) ImGui.SetScrollHereY(1.0f);
        ImGui.EndChild();
        ImGui.End();
    }

    static void DrawProfilerPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(540, 360), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Profiler")) { ImGui.End(); return; }

        var stats = Profiler.Snapshot;
        if (stats.Length == 0)
        {
            ImGui.TextDisabled("Waiting for data...");
            ImGui.End();
            return;
        }

        if (ImGui.BeginTable("prof", 6,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Method",     ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Total (ms)", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Calls/f",    ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Avg/call",   ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Peak (ms)",  ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("% Frame",    ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableHeadersRow();

            for (int i = 0; i < stats.Length; i++)
            {
                ref var s = ref stats[i];
                if (s.Name == null) continue;
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.Text(s.Name);

                ImGui.TableSetColumnIndex(1);
                var colMs = s.TotalMs < 2 ? new Vector4(0.4f, 1f, 0.5f, 1f)
                          : s.TotalMs < 8 ? new Vector4(1f, 0.9f, 0.4f, 1f)
                                           : new Vector4(1f, 0.4f, 0.4f, 1f);
                ImGui.TextColored(colMs, $"{s.TotalMs:F2}");

                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{s.Calls}");

                ImGui.TableSetColumnIndex(3);
                ImGui.Text($"{s.AvgCallMs:F3}");

                ImGui.TableSetColumnIndex(4);
                ImGui.Text($"{s.PeakMs:F2}");

                ImGui.TableSetColumnIndex(5);
                ImGui.Text($"{s.PctFrame:F1}%");
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    static unsafe void FeedAudio(N64Machine machine)
    {
        SetAudioStreamVolume(_audioStream, _muted ? 0f : 1f);

        var ai = machine.Bus.Ai;
        int rate = ai.SampleRate;
        if (rate != _audioSampleRate && rate > 0 && rate < 100000)
        {
            StopAudioStream(_audioStream);
            UnloadAudioStream(_audioStream);
            _audioSampleRate = rate;
            _audioStream = LoadAudioStream((uint)_audioSampleRate, 16, 2);
            PlayAudioStream(_audioStream);
            ai.SampleRateChanged = false;
        }

        if (!IsAudioStreamProcessed(_audioStream)) return;

        int avail = ai.AvailableBytes;
        if (avail <= 0) return;

        int toRead = Math.Min(avail, _audioFeedBuf.Length);
        toRead &= ~3; // align to 4 bytes (one stereo sample = 4 bytes at 16-bit)
        int read = ai.ReadSamples(_audioFeedBuf, 0, toRead);
        if (read <= 0) return;

        // N64 audio is big-endian 16-bit; host is little-endian -- byte-swap
        for (int i = 0; i < read - 1; i += 2)
        {
            byte tmp = _audioFeedBuf[i];
            _audioFeedBuf[i] = _audioFeedBuf[i + 1];
            _audioFeedBuf[i + 1] = tmp;
        }

        int frameCount = read / 4; // 4 bytes per stereo frame (16-bit * 2 channels)
        if (frameCount > 0)
        {
            fixed (byte* ptr = _audioFeedBuf)
                UpdateAudioStream(_audioStream, ptr, frameCount);
        }
    }
}
