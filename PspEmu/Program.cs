using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;
using static PspEmu.PspCtrl;

namespace PspEmu;

static class Program
{
    static PspMachine _machine = new();
    static bool _appAlive = true;

    // Display texture
    static Texture _screenTex;
    static readonly uint[] _screenBuf = new uint[PspDisplay.ScreenWidth * PspDisplay.ScreenHeight];

    // Audio
    static AudioStream _audioStream;
    static readonly short[] _audioBuf = new short[2048];

    // UI state
    static bool _showLog = true;
    static bool _showProfiler = true;
    static bool _showCpu = true;
    static bool _showThreads;
    static bool _showMemory;
    static bool _showOptions = true;
    static bool _showInput = true;
    static bool _showVram;
    static string _romPath = "";
    static float _screenScale = 1f;
    static bool _logAutoScroll = true;

    // FPS limit
    static readonly string[] _fpsLabels = { "30", "60", "Unlimited" };
    static readonly int[] _fpsValues = { 30, 60, 0 };
    static int _fpsChoice = 1;

    static readonly string[] RegNames = {
        "zero","at","v0","v1","a0","a1","a2","a3",
        "t0","t1","t2","t3","t4","t5","t6","t7",
        "s0","s1","s2","s3","s4","s5","s6","s7",
        "t8","t9","k0","k1","gp","sp","fp","ra"
    };

    static unsafe void Main(string[] args)
    {
        // Headless test mode: --test <file> [frames]
        if (args.Length >= 2 && args[0] == "--test")
        {
            int frames = args.Length >= 3 && int.TryParse(args[2], out int f) ? f : 120;
            RunHeadlessTest(args[1], frames);
            return;
        }

        int winW = 1280, winH = 720;
        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(winW, winH, "PspEmu - PSP Emulator");
        SetTargetFPS(60);
        InitAudioDevice();

        // Create screen texture
        var img = GenImageColor(PspDisplay.ScreenWidth, PspDisplay.ScreenHeight, BLACK);
        _screenTex = LoadTextureFromImage(img);
        UnloadImage(img);

        // Audio stream
        _audioStream = LoadAudioStream(44100, 16, 2);
        PlayAudioStream(_audioStream);

        RlImGui.Setup();

        // CLI argument: load ROM
        if (args.Length > 0 && (File.Exists(args[0]) || Directory.Exists(args[0])))
        {
            _romPath = args[0];
            _machine.LoadAndStart(_romPath);
        }

        // Emulation thread
        var emuThread = new Thread(() =>
        {
            while (_appAlive)
            {
                if (_machine.Running)
                    _machine.Tick();
                else
                    Thread.Sleep(4);
            }
        })
        { IsBackground = true, Name = "EmuThread" };
        emuThread.Start();

        while (!WindowShouldClose())
        {
            // Drag-and-drop file loading
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
                            _romPath = path;
                            _machine.LoadAndStart(path);
                            break;
                        }
                    }
                    UnloadDroppedFiles(fpl);
                }
            }

            PollInput();
            UpdateAudio();
            UpdateScreenTexture();

            BeginDrawing();
            ClearBackground(new Color(18, 18, 24, 255));

            RlImGui.Begin();
            DrawUI();
            RlImGui.End();

            EndDrawing();
        }

        _appAlive = false;
        emuThread.Join(500);

        UnloadTexture(_screenTex);
        UnloadAudioStream(_audioStream);
        CloseAudioDevice();
        RlImGui.Shutdown();
        CloseWindow();
    }

    // ── Input ──

    static void PollInput()
    {
        var ctrl = _machine.Ctrl;

        // Keyboard mapping
        ctrl.SetButton(PspButton.Up,       IsKeyDown(KeyboardKey.KEY_UP)    || IsKeyDown(KeyboardKey.KEY_W));
        ctrl.SetButton(PspButton.Down,     IsKeyDown(KeyboardKey.KEY_DOWN)  || IsKeyDown(KeyboardKey.KEY_S));
        ctrl.SetButton(PspButton.Left,     IsKeyDown(KeyboardKey.KEY_LEFT)  || IsKeyDown(KeyboardKey.KEY_A));
        ctrl.SetButton(PspButton.Right,    IsKeyDown(KeyboardKey.KEY_RIGHT) || IsKeyDown(KeyboardKey.KEY_D));
        ctrl.SetButton(PspButton.Cross,    IsKeyDown(KeyboardKey.KEY_Z)     || IsKeyDown(KeyboardKey.KEY_X));
        ctrl.SetButton(PspButton.Circle,   IsKeyDown(KeyboardKey.KEY_X)     || IsKeyDown(KeyboardKey.KEY_C));
        ctrl.SetButton(PspButton.Square,   IsKeyDown(KeyboardKey.KEY_Q));
        ctrl.SetButton(PspButton.Triangle, IsKeyDown(KeyboardKey.KEY_E));
        ctrl.SetButton(PspButton.Start,    IsKeyDown(KeyboardKey.KEY_ENTER));
        ctrl.SetButton(PspButton.Select,   IsKeyDown(KeyboardKey.KEY_BACKSPACE));
        ctrl.SetButton(PspButton.LTrigger, IsKeyDown(KeyboardKey.KEY_LEFT_SHIFT));
        ctrl.SetButton(PspButton.RTrigger, IsKeyDown(KeyboardKey.KEY_RIGHT_SHIFT) || IsKeyDown(KeyboardKey.KEY_LEFT_CONTROL));

        // Analog stick via IJKL
        float ax = 0, ay = 0;
        if (IsKeyDown(KeyboardKey.KEY_J)) ax -= 1f;
        if (IsKeyDown(KeyboardKey.KEY_L)) ax += 1f;
        if (IsKeyDown(KeyboardKey.KEY_I)) ay -= 1f;
        if (IsKeyDown(KeyboardKey.KEY_K)) ay += 1f;

        // Gamepad
        if (IsGamepadAvailable(0))
        {
            float gx = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_X);
            float gy = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_Y);
            if (MathF.Abs(gx) > 0.15f) ax = gx;
            if (MathF.Abs(gy) > 0.15f) ay = gy;

            ctrl.SetButton(PspButton.Cross,    IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_DOWN));
            ctrl.SetButton(PspButton.Circle,   IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_RIGHT));
            ctrl.SetButton(PspButton.Square,   IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_LEFT));
            ctrl.SetButton(PspButton.Triangle, IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_UP));
            ctrl.SetButton(PspButton.Up,       IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_UP));
            ctrl.SetButton(PspButton.Down,     IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_DOWN));
            ctrl.SetButton(PspButton.Left,     IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_LEFT));
            ctrl.SetButton(PspButton.Right,    IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_RIGHT));
            ctrl.SetButton(PspButton.Start,    IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_MIDDLE_RIGHT));
            ctrl.SetButton(PspButton.Select,   IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_MIDDLE_LEFT));
            ctrl.SetButton(PspButton.LTrigger, IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_TRIGGER_1));
            ctrl.SetButton(PspButton.RTrigger, IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_TRIGGER_1));
        }

        ctrl.SetAnalog(ax, ay);
    }

    // ── Audio ──

    static unsafe void UpdateAudio()
    {
        if (!IsAudioStreamProcessed(_audioStream)) return;

        _machine.Audio.ReadSamples(_audioBuf);
        fixed (short* p = _audioBuf)
            UpdateAudioStream(_audioStream, p, _audioBuf.Length / 2);
    }

    // ── Screen ──

    static unsafe void UpdateScreenTexture()
    {
        var snap = _machine.Display.GetSnapshot();
        if (snap.Length != _screenBuf.Length) return;
        Array.Copy(snap, _screenBuf, snap.Length);

        fixed (uint* p = _screenBuf)
            UpdateTexture(_screenTex, p);
    }

    // ── UI ──

    static void DrawUI()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Reset")) _machine.Reset();
                ImGui.Separator();
                if (ImGui.MenuItem("Exit")) _appAlive = false;
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Debug"))
            {
                ImGui.MenuItem("CPU", null, ref _showCpu);
                ImGui.MenuItem("Input", null, ref _showInput);
                ImGui.MenuItem("Log", null, ref _showLog);
                ImGui.MenuItem("Profiler", null, ref _showProfiler);
                ImGui.MenuItem("Threads", null, ref _showThreads);
                ImGui.MenuItem("Memory", null, ref _showMemory);
                ImGui.MenuItem("VRAM Viewer", null, ref _showVram);
                ImGui.EndMenu();
            }

            float fps = _machine.Running ? (float)(1000.0 / Math.Max(_machine.LastFrameMs, 0.1)) : 0;
            var fpsCol = fps >= 55 ? new Vector4(0.4f, 1f, 0.5f, 1f)
                       : fps >= 25 ? new Vector4(1f, 0.9f, 0.4f, 1f)
                                   : new Vector4(1f, 0.4f, 0.4f, 1f);
            string status = _machine.Running
                ? $"{fps:F1} FPS | Frame {_machine.TotalFrames} | {_machine.Cpu.CyclesExecuted:N0} cycles"
                : "Stopped";
            ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(status).X - 16);
            if (_machine.Running)
                ImGui.TextColored(fpsCol, status);
            else
                ImGui.TextDisabled(status);

            ImGui.EndMainMenuBar();
        }

        ImGui.DockSpaceOverViewport(0, ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode);

        DrawScreenPanel();
        DrawOptionsPanel();
        if (_showCpu) DrawCpuPanel();
        if (_showInput) DrawInputPanel();
        if (_showLog) DrawLogPanel();
        if (_showProfiler) DrawProfilerPanel();
        if (_showThreads) DrawThreadsPanel();
        if (_showMemory) DrawMemoryPanel();
        if (_showVram) DrawVramPanel();
    }

    static void DrawScreenPanel()
    {
        float w = PspDisplay.ScreenWidth * _screenScale;
        float h = PspDisplay.ScreenHeight * _screenScale;
        float padW = 16, padH = 56;
        ImGui.SetNextWindowSize(new Vector2(w + padW, h + padH), ImGuiCond.Always);
        if (!ImGui.Begin("PSP Screen")) { ImGui.End(); return; }

        var avail = ImGui.GetContentRegionAvail();
        var topLeft = ImGui.GetCursorScreenPos();
        float imgX = topLeft.X + (avail.X - w) * 0.5f;
        float imgY = topLeft.Y + (avail.Y - h) * 0.5f;
        if (imgX < topLeft.X) imgX = topLeft.X;
        if (imgY < topLeft.Y) imgY = topLeft.Y;
        ImGui.SetCursorScreenPos(new Vector2(imgX, imgY));
        ImGui.Image(new IntPtr(_screenTex.id), new Vector2(w, h));

        if (!_machine.Running)
        {
            string msg = "Drop a .elf / .pbp / .prx / .iso file to load";
            var ts = ImGui.CalcTextSize(msg);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(topLeft.X + (avail.X - ts.X) * 0.5f, topLeft.Y + (avail.Y - ts.Y) * 0.5f),
                0xBBFFFFFF, msg);
        }
        ImGui.End();
    }

    static void DrawOptionsPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(340, 340), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Options", ref _showOptions)) { ImGui.End(); return; }

        // FPS
        float fps = _machine.Running ? (float)(1000.0 / Math.Max(_machine.LastFrameMs, 0.1)) : 0;
        float target = _fpsValues[_fpsChoice] > 0 ? _fpsValues[_fpsChoice] : 999f;
        float frac = _machine.Running ? fps / target : 0;
        var col = frac >= 0.9f ? new Vector4(0.4f, 1f, 0.5f, 1f)
                : frac >= 0.5f ? new Vector4(1f, 0.9f, 0.4f, 1f)
                               : new Vector4(1f, 0.4f, 0.4f, 1f);
        string targetStr = _fpsValues[_fpsChoice] > 0 ? $"{_fpsValues[_fpsChoice]}" : "inf";
        if (_machine.Running)
            ImGui.TextColored(col, $"Speed  {fps:F1} / {targetStr} fps");
        else
            ImGui.TextDisabled("Emulator stopped");

        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("FPS Limit", ref _fpsChoice, _fpsLabels, _fpsLabels.Length))
        {
            int v = _fpsValues[_fpsChoice];
            _machine.TargetFps = v > 0 ? v : 10000;
        }

        ImGui.Separator();

        // ROM loading
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 50);
        ImGui.InputText("##path", ref _romPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Load") && _romPath.Length > 0)
            _machine.LoadAndStart(_romPath);

        // Control buttons
        if (ImGui.Button("Reset", new Vector2(70, 0)))
            _machine.Reset();
        ImGui.SameLine();
        if (ImGui.Button(_machine.Running ? "Pause" : "Resume", new Vector2(70, 0)))
            _machine.Running = !_machine.Running;

        if (_machine.Loader.ModuleName.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.5f, 1f), _machine.Loader.ModuleName);
        }

        ImGui.Separator();

        // Display scale
        ImGui.SetNextItemWidth(160);
        ImGui.SliderFloat("Scale", ref _screenScale, 1f, 4f, "%.1fx");
        ImGui.SameLine();
        if (ImGui.SmallButton("1x")) _screenScale = 1f;
        ImGui.SameLine();
        if (ImGui.SmallButton("2x")) _screenScale = 2f;
        ImGui.SameLine();
        if (ImGui.SmallButton("3x")) _screenScale = 3f;

        ImGui.Separator();

        // GE stats
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f),
            $"Display  {PspDisplay.ScreenWidth} x {PspDisplay.ScreenHeight}");
        ImGui.Text($"FB Addr  0x{_machine.Display.FrameBufAddr:X8}  Fmt={_machine.Display.PixelFormat}");
        ImGui.Text($"VCount   {_machine.Display.VCount}");

        ImGui.End();
    }

    static void DrawInputPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(260, 240), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Input", ref _showInput)) { ImGui.End(); return; }

        uint b = _machine.Ctrl.Buttons;
        var on  = new Vector4(0.2f, 1f, 0.4f, 1f);
        var off = new Vector4(0.3f, 0.3f, 0.3f, 1f);

        void Btn(string label, PspCtrl.PspButton btn)
        {
            bool pressed = (b & (uint)btn) != 0;
            ImGui.TextColored(pressed ? on : off, pressed ? $"[{label}]" : $" {label} ");
        }

        ImGui.Text("D-Pad:");
        ImGui.SameLine(80);
        Btn("UP", PspCtrl.PspButton.Up);
        Btn("LEFT", PspCtrl.PspButton.Left); ImGui.SameLine();
        Btn("DOWN", PspCtrl.PspButton.Down); ImGui.SameLine();
        Btn("RIGHT", PspCtrl.PspButton.Right);

        ImGui.Separator();
        ImGui.Text("Buttons:");
        ImGui.SameLine(80);
        Btn("^", PspCtrl.PspButton.Triangle); ImGui.SameLine();
        Btn("O", PspCtrl.PspButton.Circle); ImGui.SameLine();
        Btn("X", PspCtrl.PspButton.Cross); ImGui.SameLine();
        Btn("[]", PspCtrl.PspButton.Square);

        ImGui.Separator();
        Btn("L", PspCtrl.PspButton.LTrigger); ImGui.SameLine();
        Btn("R", PspCtrl.PspButton.RTrigger);

        ImGui.Separator();
        Btn("START", PspCtrl.PspButton.Start); ImGui.SameLine();
        Btn("SELECT", PspCtrl.PspButton.Select);

        ImGui.Separator();
        ImGui.Text($"Analog: ({_machine.Ctrl.AnalogX}, {_machine.Ctrl.AnalogY})");

        ImGui.Separator();
        ImGui.TextDisabled("Arrows/WASD=D-Pad  Z=X  X/C=O");
        ImGui.TextDisabled("Q=[]  E=^  Enter=Start  Bksp=Select");
        ImGui.TextDisabled("LShift=L  RShift=R  IJKL=Analog");

        ImGui.End();
    }

    static void DrawCpuPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(310, 580), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("CPU  -  Allegrex", ref _showCpu)) { ImGui.End(); return; }

        var cpu = _machine.Cpu;
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"PC   0x{cpu.Pc:X8}");
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"HI   0x{cpu.Hi:X8}");
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"LO   0x{cpu.Lo:X8}");
        ImGui.TextColored(new Vector4(0.8f, 0.7f, 1f, 1f), $"Cycles  {cpu.CyclesExecuted:N0}");
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), $"Halted  {cpu.Halted}");

        ImGui.Separator();
        if (ImGui.BeginTable("regs", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Reg", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn("Val", ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableHeadersRow();
            for (int i = 0; i < 32; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled($"${RegNames[i]}");
                ImGui.TableSetColumnIndex(1);
                uint v = cpu.Gpr[i];
                ImGui.TextColored(v != 0 ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.3f, 0.28f, 0.38f, 1f),
                    $"0x{v:X8}");
            }
            ImGui.EndTable();
        }

        if (ImGui.CollapsingHeader("FPU"))
        {
            if (ImGui.BeginTable("fpu", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Reg", ImGuiTableColumnFlags.WidthFixed, 48);
                ImGui.TableSetupColumn("Val", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableHeadersRow();
                for (int i = 0; i < 32; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0); ImGui.TextDisabled($"f{i}");
                    ImGui.TableSetColumnIndex(1); ImGui.Text($"{cpu.Fpr[i]:F4}");
                }
                ImGui.EndTable();
            }
        }
        ImGui.End();
    }

    static void DrawLogPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(900, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Log", ref _showLog)) { ImGui.End(); return; }

        ImGui.Checkbox("Scroll", ref _logAutoScroll);
        ImGui.SameLine();
        if (ImGui.Button("Clear")) Log.Clear();
        ImGui.SameLine();
        for (int i = 0; i < Log.Enabled.Length; i++)
        {
            ImGui.SameLine();
            bool en = Log.Enabled[i];
            if (ImGui.Checkbox(((LogCat)i).ToString(), ref en)) Log.Enabled[i] = en;
        }

        ImGui.Separator();
        ImGui.BeginChild("logscroll", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        var (lines, count) = Log.Snapshot();
        for (int i = 0; i < count; i++)
        {
            string line = lines[i];
            var c = line.Contains("ERROR") ? new Vector4(1f, 0.3f, 0.3f, 1f)
                  : line.Contains("WARN") ? new Vector4(1f, 0.9f, 0.4f, 1f)
                  : new Vector4(0.85f, 0.85f, 0.85f, 1f);
            ImGui.TextColored(c, line);
        }
        if (_logAutoScroll && count > 0) ImGui.SetScrollHereY(1.0f);
        ImGui.EndChild();
        ImGui.End();
    }

    static void DrawProfilerPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(540, 280), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Profiler", ref _showProfiler)) { ImGui.End(); return; }

        var stats = Profiler.Snapshot;
        if (stats.Length == 0) { ImGui.TextDisabled("Waiting..."); ImGui.End(); return; }

        if (ImGui.BeginTable("prof", 5,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Section",    ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Total (ms)", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Calls",      ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Peak (ms)",  ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("% Frame",    ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (var s in stats)
            {
                if (s.Name == null) continue;
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text(s.Name);
                ImGui.TableSetColumnIndex(1);
                var colMs = s.TotalMs < 2 ? new Vector4(0.4f, 1f, 0.5f, 1f)
                          : s.TotalMs < 8 ? new Vector4(1f, 0.9f, 0.4f, 1f)
                                           : new Vector4(1f, 0.4f, 0.4f, 1f);
                ImGui.TextColored(colMs, $"{s.TotalMs:F2}");
                ImGui.TableSetColumnIndex(2); ImGui.Text($"{s.Calls}");
                ImGui.TableSetColumnIndex(3); ImGui.Text($"{s.PeakMs:F2}");
                ImGui.TableSetColumnIndex(4); ImGui.Text($"{s.PctFrame:F1}%");
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    static void DrawThreadsPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(500, 300), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Threads", ref _showThreads)) { ImGui.End(); return; }

        var threads = _machine.Kernel.GetThreads();
        if (ImGui.BeginTable("threads", 5,
            ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("UID",      ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Status",   ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("PC",       ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            foreach (var t in threads)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Text($"{t.Uid}");
                ImGui.TableSetColumnIndex(1); ImGui.Text(t.Name);
                ImGui.TableSetColumnIndex(2);
                var sc = t.Status == HleKernel.ThreadStatus.Running ? new Vector4(0.4f, 1f, 0.5f, 1f)
                       : t.Status == HleKernel.ThreadStatus.Ready  ? new Vector4(1f, 0.9f, 0.4f, 1f)
                                                                   : new Vector4(0.6f, 0.6f, 0.6f, 1f);
                ImGui.TextColored(sc, t.Status.ToString());
                ImGui.TableSetColumnIndex(3); ImGui.Text($"{t.Priority}");
                ImGui.TableSetColumnIndex(4); ImGui.Text($"0x{t.Pc:X8}");
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.Text("Semaphores:");
        foreach (var s in _machine.Kernel.GetSemaphores())
            ImGui.Text($"  [{s.Uid}] {s.Name} count={s.Count}/{s.MaxCount}");
        ImGui.Text("Event Flags:");
        foreach (var e in _machine.Kernel.GetEventFlags())
            ImGui.Text($"  [{e.Uid}] {e.Name} pattern=0x{e.Pattern:X8}");

        ImGui.End();
    }

    static void DrawMemoryPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(400, 300), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Memory", ref _showMemory)) { ImGui.End(); return; }

        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"RAM: {PspBus.RamSize / 1024 / 1024} MB");
        ImGui.Text($"VRAM: {PspBus.VramSize / 1024} KB");
        ImGui.Text($"Scratchpad: {PspBus.ScratchpadSize / 1024} KB");

        ImGui.Separator();
        ImGui.Text("Allocated blocks:");
        foreach (var b in _machine.Kernel.GetMemBlocks())
            ImGui.Text($"  [{b.Uid}] {b.Name} addr=0x{b.Address:X8} size=0x{b.Size:X}");

        ImGui.Separator();
        ImGui.Text($"Free user memory: {_machine.Kernel.MaxFreeMemSize() / 1024} KB");
        ImGui.End();
    }

    static unsafe void DrawVramPanel()
    {
        if (!_showVram) return;
        ImGui.SetNextWindowSize(new Vector2(520, 340), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("VRAM Viewer", ref _showVram)) { ImGui.End(); return; }

        ImGui.Text($"FB Addr: 0x{_machine.Display.FrameBufAddr:X8}  Width: {_machine.Display.BufWidth}  Format: {_machine.Display.PixelFormat}");
        ImGui.Text($"VCount: {_machine.Display.VCount}");

        var avail = ImGui.GetContentRegionAvail();
        const float aspect = 480f / 272f;
        float w = avail.X, h = w / aspect;
        if (h > avail.Y) { h = avail.Y; w = h * aspect; }
        ImGui.Image(new IntPtr(_screenTex.id), new Vector2(w, h));
        ImGui.End();
    }

    // ── Headless test mode ──

    static void RunHeadlessTest(string path, int numFrames)
    {
        var machine = new PspMachine();
        machine.TargetFps = 100000;

        string name = Path.GetFileName(path);

        if (!File.Exists(path))
        {
            Console.WriteLine($"MISSING|{name}|File not found");
            Environment.Exit(1);
            return;
        }

        bool loaded;
        try
        {
            loaded = machine.LoadAndStart(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LOAD_CRASH|{name}|{ex.GetType().Name}: {ex.Message}");
            Environment.Exit(2);
            return;
        }

        if (!loaded)
        {
            var (ll, lc) = Log.Snapshot();
            string lastErr = "";
            for (int i = lc - 1; i >= 0; i--)
                if (ll[i].Contains("ERROR")) { lastErr = ll[i]; break; }
            Console.WriteLine($"LOAD_FAIL|{name}|{lastErr}");
            Environment.Exit(1);
            return;
        }

        int crashFrame = -1;
        string crashMsg = "";
        try
        {
            for (int f = 0; f < numFrames && machine.Running; f++)
            {
                machine.Tick();
                if (machine.Cpu.Halted && !machine.Cpu.WaitingVblank)
                {
                    crashFrame = f;
                    crashMsg = $"CPU halted at PC=0x{machine.Cpu.Pc:X8}";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            crashFrame = (int)machine.TotalFrames;
            crashMsg = $"{ex.GetType().Name}: {ex.Message}";
        }

        var (lines, count) = Log.Snapshot();
        int errors = 0, warnings = 0;
        var errorLines = new List<string>();
        var warnLines = new List<string>();
        var nidLines = new List<string>();
        var seenSyscalls = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            if (lines[i].Contains("ERROR")) { errors++; if (errorLines.Count < 20) errorLines.Add(lines[i]); }
            else if (lines[i].Contains("Unresolved NID")) { nidLines.Add(lines[i]); }
            else if (lines[i].Contains("Unhandled syscall"))
            {
                if (seenSyscalls.Add(lines[i])) warnLines.Add(lines[i]);
                warnings++;
            }
            else if (lines[i].Contains("WARN"))
            {
                warnings++;
                if (warnLines.Count < 20) warnLines.Add(lines[i]);
            }
        }

        var unresolvedModules = new HashSet<string>();
        foreach (var nl in nidLines)
        {
            int inIdx = nl.IndexOf(" in ");
            if (inIdx > 0) unresolvedModules.Add(nl[(inIdx + 4)..]);
        }

        string status;
        if (crashMsg.Length > 0)
            status = $"CRASH|{name}|frame={crashFrame} {crashMsg}";
        else if (errors > 0)
            status = $"ERRORS|{name}|frames={machine.TotalFrames} errors={errors}";
        else
            status = $"OK|{name}|frames={machine.TotalFrames} warnings={warnings}";

        if (nidLines.Count > 0)
            status += $" unresolved_nids={nidLines.Count}({string.Join(",", unresolvedModules)})";

        Console.WriteLine(status);

        foreach (var e in errorLines)
            Console.WriteLine($"  ERR: {e}");
        foreach (var nl in nidLines)
            Console.WriteLine($"  NID: {nl}");
        foreach (var w in warnLines)
            Console.WriteLine($"  WARN: {w}");

        Environment.Exit(errors > 0 || crashMsg.Length > 0 ? 1 : 0);
    }
}
