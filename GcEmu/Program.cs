using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace GcEmu;

static class Program
{
    static readonly string[] _fpsLabels = { "30", "60", "Unlimited" };
    static readonly int[] _fpsValues = { 30, 60, 0 };
    static int _fpsChoice = 1;

    static readonly string[] _inputLabels = { "Keyboard", "Controller" };
    static int _inputChoice;

    static bool _logScrollToBottom = true;
    static bool _logErrorsOnly;
    static readonly List<Log.Entry> _logCache = new(2048);
    static int _logCacheVersion = -1;

    static readonly string[] RegNames =
    {
        "r0","r1/sp","r2","r3","r4","r5","r6","r7",
        "r8","r9","r10","r11","r12","r13","r14","r15",
        "r16","r17","r18","r19","r20","r21","r22","r23",
        "r24","r25","r26","r27","r28","r29","r30","r31",
    };

    const int TexW = 640, TexH = 480;

    static unsafe string? MarshalPath(sbyte* ptr) =>
        ptr == null ? null : Marshal.PtrToStringUTF8((IntPtr)ptr);

    static void PollInput(GcSi si)
    {
        if (_inputChoice == 0) PollKeyboard(si);
        else PollGamepad(si);
    }

    static void PollKeyboard(GcSi si)
    {
        ushort b = 0;
        if (IsKeyDown(KeyboardKey.KEY_ENTER))        b |= 0x1000;
        if (IsKeyDown(KeyboardKey.KEY_S))             b |= 0x0800;
        if (IsKeyDown(KeyboardKey.KEY_A))             b |= 0x0400;
        if (IsKeyDown(KeyboardKey.KEY_X))             b |= 0x0200;
        if (IsKeyDown(KeyboardKey.KEY_Z))             b |= 0x0100;
        if (IsKeyDown(KeyboardKey.KEY_Q))             b |= 0x0040;
        if (IsKeyDown(KeyboardKey.KEY_E))             b |= 0x0020;
        if (IsKeyDown(KeyboardKey.KEY_C))             b |= 0x0010;
        if (IsKeyDown(KeyboardKey.KEY_UP))            b |= 0x0008;
        if (IsKeyDown(KeyboardKey.KEY_DOWN))          b |= 0x0004;
        if (IsKeyDown(KeyboardKey.KEY_LEFT))          b |= 0x0002;
        if (IsKeyDown(KeyboardKey.KEY_RIGHT))         b |= 0x0001;

        si.Buttons = b;

        byte sx = 128, sy = 128;
        if (IsKeyDown(KeyboardKey.KEY_I)) sy = 255;
        if (IsKeyDown(KeyboardKey.KEY_K)) sy = 0;
        if (IsKeyDown(KeyboardKey.KEY_J)) sx = 0;
        if (IsKeyDown(KeyboardKey.KEY_L)) sx = 255;
        si.StickX = sx;
        si.StickY = sy;
        si.CStickX = 128;
        si.CStickY = 128;

        si.TriggerL = IsKeyDown(KeyboardKey.KEY_Q) ? (byte)255 : (byte)0;
        si.TriggerR = IsKeyDown(KeyboardKey.KEY_E) ? (byte)255 : (byte)0;
    }

    static void PollGamepad(GcSi si)
    {
        if (!IsGamepadAvailable(0)) { si.Buttons = 0; return; }

        ushort b = 0;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_MIDDLE_RIGHT)) b |= 0x1000;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_UP)) b |= 0x0800;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_LEFT)) b |= 0x0400;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_RIGHT)) b |= 0x0200;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_DOWN)) b |= 0x0100;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_TRIGGER_1)) b |= 0x0040;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_TRIGGER_1)) b |= 0x0020;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_MIDDLE_LEFT)) b |= 0x0010;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_UP)) b |= 0x0008;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_DOWN)) b |= 0x0004;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_LEFT)) b |= 0x0002;
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_RIGHT)) b |= 0x0001;
        si.Buttons = b;

        float lx = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_X);
        float ly = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_Y);
        si.StickX = (byte)Math.Clamp((int)(lx * 127 + 128), 0, 255);
        si.StickY = (byte)Math.Clamp((int)(-ly * 127 + 128), 0, 255);

        float rx = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_RIGHT_X);
        float ry = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_RIGHT_Y);
        si.CStickX = (byte)Math.Clamp((int)(rx * 127 + 128), 0, 255);
        si.CStickY = (byte)Math.Clamp((int)(-ry * 127 + 128), 0, 255);

        float lt = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_TRIGGER);
        float rt = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_RIGHT_TRIGGER);
        si.TriggerL = (byte)Math.Clamp((int)((lt + 1f) * 127.5f), 0, 255);
        si.TriggerR = (byte)Math.Clamp((int)((rt + 1f) * 127.5f), 0, 255);
    }

    static unsafe void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--test")
        {
            RunHeadlessTest(args);
            return;
        }

        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(1600, 900, "GameCube Emulator");
        SetTargetFPS(60);

        RlImGui.Setup();

        var machine = new GcMachine();

        if (args.Length > 0 && File.Exists(args[0]))
        {
            string ext = Path.GetExtension(args[0]).ToLower();
            if (ext is ".iso" or ".gcm" or ".ciso" or ".gcz")
                machine.LoadDisc(args[0]);
        }

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

        var pixels = new Color[TexW * TexH];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color { a = 255 };

        Texture gcTex;
        fixed (Color* p = pixels)
        {
            var img = new Image
            {
                data = p, width = TexW, height = TexH, mipmaps = 1,
                format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
            };
            gcTex = LoadTextureFromImage(img);
        }
        SetTextureFilter(gcTex, TextureFilter.TEXTURE_FILTER_BILINEAR);

        GpuRenderer? gpuRenderer = null;

        while (!WindowShouldClose())
        {
            PollInput(machine.Bus.Si);

            if (gpuRenderer == null && machine.Running)
            {
                gpuRenderer = new GpuRenderer();
                gpuRenderer.Init(machine.Bus, machine.Bus.Gpu.Renderer);
            }

            if (gpuRenderer is { GpuReady: true })
            {
                gpuRenderer.FlushGpu();
            }
            else
            {
                machine.Bus.Gpu.Renderer.SnapshotXfbDisplay(pixels, TexW, TexH);
                fixed (Color* p = pixels) UpdateTexture(gcTex, p);
            }

            BeginDrawing();
            ClearBackground(new Color { r = 12, g = 10, b = 18, a = 255 });

            RlImGui.Begin();
            ImGui.DockSpaceOverViewport();

            if (IsFileDropped())
            {
                var fpl = LoadDroppedFiles();
                for (int fi = 0; fi < (int)fpl.count; fi++)
                {
                    string? p = MarshalPath(fpl.paths[fi]);
                    if (p != null)
                    {
                        string ext = Path.GetExtension(p).ToLower();
                        if (ext is ".iso" or ".gcm" or ".ciso" or ".gcz")
                            machine.LoadDisc(p);
                        break;
                    }
                }
                UnloadDroppedFiles(fpl);
            }

            DrawScreenPanel(gcTex, gpuRenderer, machine);
            DrawCpuPanel(machine);
            DrawOptionsPanel(machine);
            DrawInputPanel(machine);
            DrawLogPanel();
            DrawProfilerPanel();

            RlImGui.End();
            EndDrawing();
        }

        appAlive = false;
        emuThread.Join(500);
        gpuRenderer?.CleanupGpu();
        UnloadTexture(gcTex);
        RlImGui.Shutdown();
        CloseWindow();
    }

    static void DrawScreenPanel(Texture gcTex, GpuRenderer? gpuR, GcMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(680, 530), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("GC Screen")) { ImGui.End(); return; }

        var avail = ImGui.GetContentRegionAvail();
        const float tvAspect = 4f / 3f;
        float w = avail.X, h = w / tvAspect;
        if (h > avail.Y) { h = avail.Y; w = h * tvAspect; }

        uint texId;
        Vector2 uv0, uv1;
        if (gpuR is { GpuReady: true })
        {
            texId = gpuR.OutputTextureId;
            uv0 = new Vector2(0, 0);
            uv1 = new Vector2(1, 1);
        }
        else
        {
            texId = gcTex.id;
            uv0 = new Vector2(0, 0);
            uv1 = new Vector2(1, 1);
        }

        var topLeft = ImGui.GetCursorScreenPos();
        var imgPos = new Vector2(topLeft.X + (avail.X - w) * 0.5f, topLeft.Y);
        ImGui.SetCursorScreenPos(imgPos);
        ImGui.Image(new IntPtr(texId), new Vector2(w, h), uv0, uv1);

        if (!machine.Running)
        {
            string msg = "Drop a .iso/.gcm/.ciso GameCube disc image to start";
            var ts = ImGui.CalcTextSize(msg);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(topLeft.X + (avail.X - ts.X) * 0.5f, topLeft.Y + (avail.Y - ts.Y) * 0.5f),
                0xBBFFFFFF, msg);
        }
        ImGui.End();
    }

    static void DrawCpuPanel(GcMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(330, 640), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("CPU  –  Gekko PPC750CL")) { ImGui.End(); return; }
        var cpu = machine.Cpu;
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"PC   0x{cpu.PC:X8}");
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"LR   0x{cpu.LR:X8}");
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"CTR  0x{cpu.CTR:X8}");
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), $"MSR  0x{cpu.MSR:X8}  EE={(cpu.MSR & 0x8000) != 0}");
        ImGui.TextColored(new Vector4(0.8f, 0.7f, 1f, 1f), $"CR   0x{cpu.CR:X8}  XER  0x{cpu.XER:X8}");
        ImGui.TextColored(new Vector4(0.8f, 0.7f, 1f, 1f), $"PI INTSR 0x{machine.Bus.Pi.IntSr:X8}  INTMR 0x{machine.Bus.Pi.IntMr:X8}");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 1f, 1f), $"HID0 0x{cpu.HID0:X8}  HID2 0x{cpu.HID2:X8}");
        ImGui.Separator();
        if (ImGui.BeginTable("regs", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Reg", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn("Val", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            for (int i = 0; i < 32; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextDisabled(RegNames[i]);
                ImGui.TableSetColumnIndex(1);
                uint v = cpu.GPR[i];
                ImGui.TextColored(v != 0 ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.3f, 0.28f, 0.38f, 1f), $"0x{v:X8}");
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    static void DrawOptionsPanel(GcMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(380, 350), ImGuiCond.FirstUseEver);
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

        ImGui.SetNextItemWidth(120);
        ImGui.Combo("Input", ref _inputChoice, _inputLabels, _inputLabels.Length);
        if (_inputChoice == 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(IsGamepadAvailable(0) ? "connected" : "no gamepad");
        }

        ImGui.Separator();

        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f),
            $"Display    {machine.Bus.Vi.DispWidth} × {machine.Bus.Vi.DispHeight}");
        ImGui.Text($"XFB Addr   0x{machine.Bus.Vi.XfbAddr:X8}");
        ImGui.Text($"VI Fields  {machine.Bus.Vi.TotalFields}");
        ImGui.Text($"FIFO Writes {machine.Bus.Gpu.FifoWrites:N0}");
        ImGui.Text($"GP Cmds    {machine.Bus.Gpu.Gp0Count:N0}");

        ImGui.Separator();

        if (machine.Running)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.5f, 1f),
                $"Running   Frame={machine.FrameCount}   Cycles={machine.Cpu.TotalCycles:N0}");
            if (machine.GameName != null)
                ImGui.TextColored(new Vector4(1f, 0.9f, 0.5f, 1f), $"Game: {machine.GameName}");
        }
        else if (machine.Error != null)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Error: {machine.Error}");
        else
            ImGui.TextDisabled("No disc loaded");

        ImGui.Separator();
        if (ImGui.Button("Reset", new Vector2(60, 0)) && machine.Running) machine.Reset();

        ImGui.End();
    }

    static void DrawInputPanel(GcMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(300, 240), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Input")) { ImGui.End(); return; }

        var si = machine.Bus.Si;
        ushort b = si.Buttons;
        var on  = new Vector4(0.2f, 1f, 0.4f, 1f);
        var off = new Vector4(0.3f, 0.3f, 0.3f, 1f);

        void Btn(string label, int bit)
        {
            bool pressed = (b & (1 << bit)) != 0;
            ImGui.TextColored(pressed ? on : off, pressed ? $"[{label}]" : $" {label} ");
        }

        ImGui.Text("D-Pad:");
        ImGui.SameLine(80); Btn("UP", 3);
        Btn("LEFT", 1); ImGui.SameLine(); Btn("DOWN", 2); ImGui.SameLine(); Btn("RIGHT", 0);
        ImGui.Separator();
        ImGui.Text("Buttons:");
        ImGui.SameLine(80);
        Btn("A", 8); ImGui.SameLine(); Btn("B", 9); ImGui.SameLine(); Btn("X", 10); ImGui.SameLine(); Btn("Y", 11);
        ImGui.Separator();
        Btn("L", 6); ImGui.SameLine(); Btn("R", 5); ImGui.SameLine(); Btn("Z", 4);
        ImGui.Separator();
        Btn("START", 12);
        ImGui.Separator();
        ImGui.Text($"Stick: ({si.StickX}, {si.StickY})");
        ImGui.Text($"C-Stick: ({si.CStickX}, {si.CStickY})");
        ImGui.Text($"Triggers: L={si.TriggerL} R={si.TriggerR}");
        ImGui.Separator();
        if (_inputChoice == 0)
        {
            ImGui.TextDisabled("Arrows=D-Pad  IJKL=Stick  Z=A  X=B");
            ImGui.TextDisabled("A=X  S=Y  Q=L  E=R  C=Z  Enter=Start");
        }
        else
        {
            ImGui.TextDisabled("Using gamepad #0 (first connected)");
        }
        ImGui.End();
    }

    static void DrawLogPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(900, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Log")) { ImGui.End(); return; }
        ImGui.Checkbox("Scroll##log", ref _logScrollToBottom);
        ImGui.SameLine();
        ImGui.Checkbox("Show Only Errors", ref _logErrorsOnly);
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

    static void RunHeadlessTest(string[] args)
    {
        string filePath = args[1];
        int maxFrames = args.Length >= 3 && int.TryParse(args[2], out int f) ? f : 600;

        Log.FileLogging = true;
        var machine = new GcMachine();

        if (!machine.LoadDisc(filePath))
        {
            Console.Error.WriteLine($"Disc load failed: {machine.Error}");
            Environment.Exit(1);
            return;
        }

        Console.Error.WriteLine($"Boot PC=0x{machine.Cpu.PC:X8} MSR=0x{machine.Cpu.MSR:X8}");

        Console.Error.WriteLine("Exception vectors:");
        uint[] vectors = { 0x100, 0x300, 0x400, 0x500, 0x900, 0xC00 };
        foreach (var v in vectors)
            Console.Error.WriteLine($"  [{v:X4}] = 0x{machine.Bus.Read32(v):X8} 0x{machine.Bus.Read32(v+4):X8}");

        Console.Error.WriteLine("Low memory BEFORE execution:");
        Console.Error.WriteLine($"  [0x28]={machine.Bus.Read32(0x80000028):X8} [0x30]={machine.Bus.Read32(0x80000030):X8} [0x34]={machine.Bus.Read32(0x80000034):X8}");
        Console.Error.WriteLine($"  [0x38]={machine.Bus.Read32(0x80000038):X8} [0x3C]={machine.Bus.Read32(0x8000003C):X8}");
        Console.Error.WriteLine($"  [0xF4]={machine.Bus.Read32(0x800000F4):X8}");

        Console.Error.WriteLine("Pre-running 50 frames to get past cache loops...");
        for (int ff = 0; ff < 50; ff++) machine.Tick();

        Console.Error.WriteLine("Code at 0x8000C350 AFTER 50 frames:");
        for (uint a = 0x8000C350; a <= 0x8000C370; a += 4)
            Console.Error.WriteLine($"  0x{a:X8}: 0x{machine.Bus.Read32(a):X8}");

        if (machine.Cpu.HaltDetected)
        {
            var hg = machine.Cpu.HaltGPR;
            Console.Error.WriteLine($"  HALT at 0x8000C354 detected! LR=0x{machine.Cpu.HaltLR:X8} SP=0x{machine.Cpu.HaltSP:X8}");
            Console.Error.WriteLine($"  r3=0x{hg[3]:X8} r4=0x{hg[4]:X8} r5=0x{hg[5]:X8}");
            Console.Error.WriteLine($"  r29=0x{hg[29]:X8} r30=0x{hg[30]:X8} r31=0x{hg[31]:X8}");
            Console.Error.WriteLine($"  [r29+0x19C]=0x{machine.Bus.Read32(hg[29]+0x19C):X8}");

            Console.Error.WriteLine("  String at r31+0x60:");
            try
            {
                var sb = new System.Text.StringBuilder();
                for (int ci = 0; ci < 128; ci++)
                {
                    byte ch = (byte)machine.Bus.Read8(hg[31] + 0x60u + (uint)ci);
                    if (ch == 0) break;
                    sb.Append((char)ch);
                }
                Console.Error.WriteLine($"    \"{sb}\"");
            }
            catch { Console.Error.WriteLine("    (unreadable)"); }

            Console.Error.WriteLine("  Code around check (0x8000E200-0x8000E280):");
            for (uint a = 0x8000E200; a < 0x8000E280; a += 4)
                Console.Error.WriteLine($"    0x{a:X8}: 0x{machine.Bus.Read32(a):X8}");
        }
        Console.Error.WriteLine($"After 50 frames: PC=0x{machine.Cpu.PC:X8} MSR=0x{machine.Cpu.MSR:X8}");
        Console.Error.WriteLine($"  ViIrq={machine.Cpu.ViInterruptCount} DecIrq={machine.Cpu.DecInterruptCount} AramDma={machine.Bus.Dsp.AramDmaCount} DspMbox={machine.Bus.Dsp.DspMailboxCount}");
        Console.Error.WriteLine($"  OSCurrentCtx=0x{machine.Bus.Read32(0x800000C0):X8} OSInterruptCtx=0x{machine.Bus.Read32(0x800000D4):X8}");
        Console.Error.WriteLine($"  OSDefaultThread=0x{machine.Bus.Read32(0x800000E4):X8}");
        Console.Error.WriteLine($"  ThreadSwitches={machine.Bus.TotalThreadSwitches}");
        Console.Error.WriteLine($"  DVD callback ptr at 0x8016B98C = 0x{machine.Bus.Read32(0x8016B98C):X8}");
        Console.Error.WriteLine($"  DVD state at 0x8016B97C = 0x{machine.Bus.Read32(0x8016B97C):X8}");
        Console.Error.WriteLine($"  DVD more: 0x{machine.Bus.Read32(0x8016B980):X8} 0x{machine.Bus.Read32(0x8016B984):X8} 0x{machine.Bus.Read32(0x8016B988):X8}");
        Console.Error.WriteLine("Context switch function at 0x8000E640-0x8000E6C0:");
        for (uint a = 0x8000E640; a < 0x8000E6C0; a += 4)
            Console.Error.WriteLine($"  0x{a:X8}: 0x{machine.Bus.Read32(a):X8}");
        Console.Error.WriteLine("OSSaveContext at 0x8000E580-0x8000E640:");
        for (uint a = 0x8000E580; a < 0x8000E640; a += 4)
            Console.Error.WriteLine($"  0x{a:X8}: 0x{machine.Bus.Read32(a):X8}");
        Console.Error.WriteLine($"Worker context dump (0x8017C5E0):");
        uint wctx = 0x8017C5E0;
        for (uint off = 0; off < 0x1A8; off += 16)
        {
            uint v0 = machine.Bus.Read32(wctx + off);
            uint v1 = machine.Bus.Read32(wctx + off + 4);
            uint v2 = machine.Bus.Read32(wctx + off + 8);
            uint v3 = machine.Bus.Read32(wctx + off + 12);
            Console.Error.WriteLine($"  +0x{off:X3}: {v0:X8} {v1:X8} {v2:X8} {v3:X8}");
        }

        Console.Error.WriteLine("Code at stuck PC area:");
        uint stuckPc = machine.Cpu.PC & 0x01FFFFFF;
        for (uint a = stuckPc - 16; a <= stuckPc + 32; a += 4)
        {
            uint instr = machine.Bus.Read32(a);
            string mark = a == stuckPc ? " <-- PC" : "";
            Console.Error.WriteLine($"  0x{(a | 0x80000000):X8}: 0x{instr:X8}{mark}");
        }
        Console.Error.WriteLine($"  DI DiSr=0x{machine.Bus.Di.DiSr:X8} DiCr=0x{machine.Bus.Di.DiCr:X8}");
        Console.Error.WriteLine($"  DI CmdBuf: 0x{machine.Bus.Di.CmdBuf0:X8} 0x{machine.Bus.Di.CmdBuf1:X8} 0x{machine.Bus.Di.CmdBuf2:X8}");
        Console.Error.WriteLine($"  DI DiMar=0x{machine.Bus.Di.DiMar:X8} DiLength=0x{machine.Bus.Di.DiLength:X8} ImmBuf=0x{machine.Bus.Di.ImmBuf:X8}");
        Console.Error.WriteLine($"  Flag [0x8016B870]=0x{machine.Bus.Read32(0x8016B870):X8}");
        Console.Error.WriteLine($"  r3=0x{machine.Cpu.GPR[3]:X8} r4=0x{machine.Cpu.GPR[4]:X8} r5=0x{machine.Cpu.GPR[5]:X8}");
        Console.Error.WriteLine($"  r6=0x{machine.Cpu.GPR[6]:X8} r7=0x{machine.Cpu.GPR[7]:X8} r8=0x{machine.Cpu.GPR[8]:X8}");
        Console.Error.WriteLine($"  r31=0x{machine.Cpu.GPR[31]:X8} r30=0x{machine.Cpu.GPR[30]:X8} r29=0x{machine.Cpu.GPR[29]:X8}");
        Console.Error.WriteLine("Caller code around LR:");
        uint lr = machine.Cpu.LR;
        for (uint a = lr - 24; a <= lr + 24; a += 4)
            Console.Error.WriteLine($"  0x{a:X8}: 0x{machine.Bus.Read32(a):X8}{(a == lr ? " <-- LR" : "")}");
        Console.Error.WriteLine("Flag-setting function at 0x8002C500-0x8002C5A0:");
        for (uint a = 0x8002C500; a <= 0x8002C5A0; a += 4)
            Console.Error.WriteLine($"  0x{a:X8}: 0x{machine.Bus.Read32(a):X8}");
        Console.Error.WriteLine("Caller context at 0x8002C230-0x8002C280:");
        for (uint a = 0x8002C230; a <= 0x8002C280; a += 4)
            Console.Error.WriteLine($"  0x{a:X8}: 0x{machine.Bus.Read32(a):X8}");
        Console.Error.WriteLine($"Flag structure full dump (0x801393C0-0x80139400):");
        for (uint a = 0x801393C0; a < 0x80139400; a += 16)
            Console.Error.WriteLine($"  0x{a:X8}: {machine.Bus.Read32(a):X8} {machine.Bus.Read32(a+4):X8} {machine.Bus.Read32(a+8):X8} {machine.Bus.Read32(a+12):X8}");

        Console.Error.WriteLine("Exception vectors (game-installed):");
        uint[] evecs2 = { 0x100, 0x300, 0x400, 0x500, 0x900, 0xC00 };
        foreach (var v in evecs2)
            Console.Error.WriteLine($"  [{v:X4}] = 0x{machine.Bus.Read32(v):X8} 0x{machine.Bus.Read32(v+4):X8}");

        Console.Error.WriteLine($"  PI INTSR=0x{machine.Bus.Pi.IntSr:X8} INTMR=0x{machine.Bus.Pi.IntMr:X8}");
        Console.Error.WriteLine($"  VI DI0=0x{machine.Bus.Vi.Di[0]:X8} DI1=0x{machine.Bus.Vi.Di[1]:X8} Dcr=0x{machine.Bus.Vi.Dcr:X4}");
        Console.Error.WriteLine($"  XFB=0x{machine.Bus.Vi.XfbAddr:X8} Tfbl=0x{machine.Bus.Vi.Tfbl:X8}");
        Console.Error.WriteLine($"  FIFO Base=0x{machine.Bus.Pi.FifoBase:X8} End=0x{machine.Bus.Pi.FifoEnd:X8} Wp=0x{machine.Bus.Pi.FifoWp:X8}");
        Console.Error.WriteLine($"  DEC=0x{machine.Cpu.DEC:X8}");

        Console.Error.WriteLine($"MSR EE={((machine.Cpu.MSR & 0x8000) != 0 ? "ON" : "OFF")}");
        Console.Error.WriteLine($"  PI INTSR=0x{machine.Bus.Pi.IntSr:X8} INTMR=0x{machine.Bus.Pi.IntMr:X8}");
        Console.Error.WriteLine($"  DEC=0x{machine.Cpu.DEC:X8}");
        Console.Error.WriteLine($"  DI reads={machine.Bus.Di.ReadCount} VI writes={machine.Bus.Vi.WriteCount}");
        Console.Error.WriteLine($"  PeIntSr=0x{machine.Bus.Gpu.PeIntSr:X4} PeFinishCount={machine.Bus.Gpu.PeFinishCount}");
        Console.Error.WriteLine($"  CTR=0x{machine.Cpu.CTR:X8} DEC=0x{machine.Cpu.DEC:X8}");
        Console.Error.WriteLine($"  DI TotalCmds={machine.Bus.Di.TotalCmds}");
        Console.Error.WriteLine($"  PI INTSR=0x{machine.Bus.Pi.IntSr:X8} INTMR=0x{machine.Bus.Pi.IntMr:X8}");
        Console.Error.WriteLine($"  MSR EE={((machine.Cpu.MSR & 0x8000) != 0 ? "ON" : "OFF")} MSR=0x{machine.Cpu.MSR:X8}");

        Console.Error.WriteLine("Low memory setup:");
        Console.Error.WriteLine($"  [0x28] = 0x{machine.Bus.Read32(0x80000028):X8}  (phys mem size)");
        Console.Error.WriteLine($"  [0x30] = 0x{machine.Bus.Read32(0x80000030):X8}  (arena lo)");
        Console.Error.WriteLine($"  [0x34] = 0x{machine.Bus.Read32(0x80000034):X8}  (arena hi)");
        Console.Error.WriteLine($"  [0x38] = 0x{machine.Bus.Read32(0x80000038):X8}  (FST addr)");
        Console.Error.WriteLine($"  [0x3C] = 0x{machine.Bus.Read32(0x8000003C):X8}  (FST size)");
        Console.Error.WriteLine($"  [0xF0] = 0x{machine.Bus.Read32(0x800000F0):X8}  (sim mem size)");
        Console.Error.WriteLine($"  [0xF4] = 0x{machine.Bus.Read32(0x800000F4):X8}  (BI2 addr)");
        Console.Error.WriteLine("BI2 data:");
        uint bi2VAddr = machine.Bus.Read32(0x800000F4);
        if (bi2VAddr >= 0x80000000)
        {
            Console.Error.WriteLine($"  BI2[0x00] = 0x{machine.Bus.Read32(bi2VAddr):X8}  (debug mon size)");
            Console.Error.WriteLine($"  BI2[0x04] = 0x{machine.Bus.Read32(bi2VAddr+4):X8}  (sim mem size)");
            Console.Error.WriteLine($"  BI2[0x18] = 0x{machine.Bus.Read32(bi2VAddr+0x18):X8}  (country code)");
        }

        uint prevPc = 0;
        int stuckCount = 0;
        for (int frame = 0; frame < maxFrames; frame++)
        {
            machine.Tick();
            if (!machine.Running)
            {
                Console.Error.WriteLine($"Emulator stopped at frame {frame}: {machine.Error}");
                Console.Error.WriteLine($"  PC=0x{machine.Cpu.PC:X8} LR=0x{machine.Cpu.LR:X8} MSR=0x{machine.Cpu.MSR:X8}");
                Console.Error.WriteLine($"  r0=0x{machine.Cpu.GPR[0]:X8} r1=0x{machine.Cpu.GPR[1]:X8} r3=0x{machine.Cpu.GPR[3]:X8} r4=0x{machine.Cpu.GPR[4]:X8}");
                Console.Error.WriteLine($"  CR=0x{machine.Cpu.CR:X8} XER=0x{machine.Cpu.XER:X8} CTR=0x{machine.Cpu.CTR:X8}");
                Console.Error.WriteLine($"  PI INTSR=0x{machine.Bus.Pi.IntSr:X8} INTMR=0x{machine.Bus.Pi.IntMr:X8}");
                Console.Error.WriteLine($"  Instr @ PC: 0x{machine.Bus.Read32(machine.Cpu.PC):X8}");
                break;
            }

            if (machine.Cpu.PC == prevPc)
            {
                stuckCount++;
                if (stuckCount == 3)
                {
                    Console.Error.WriteLine($"[F{frame}] STUCK at PC=0x{machine.Cpu.PC:X8}");
                    Console.Error.WriteLine($"  LR=0x{machine.Cpu.LR:X8} MSR=0x{machine.Cpu.MSR:X8} DEC=0x{machine.Cpu.DEC:X8}");
                    Console.Error.WriteLine($"  r3=0x{machine.Cpu.GPR[3]:X8} r4=0x{machine.Cpu.GPR[4]:X8} r5=0x{machine.Cpu.GPR[5]:X8}");
                    Console.Error.WriteLine($"  PI INTSR=0x{machine.Bus.Pi.IntSr:X8} INTMR=0x{machine.Bus.Pi.IntMr:X8}");
                    Console.Error.WriteLine($"  VI DI0=0x{machine.Bus.Vi.Di[0]:X8} DI1=0x{machine.Bus.Vi.Di[1]:X8}");
                    Console.Error.WriteLine($"  DSP CSR=0x{machine.Bus.Dsp.Csr:X4} CPU_MBX_H=0x{machine.Bus.Dsp.CpuMboxH:X4}");
                    Console.Error.WriteLine("  Instructions around stuck PC:");
                    for (int off = -16; off <= 16; off += 4)
                    {
                        uint a = (uint)(machine.Cpu.PC + off);
                        uint instr = machine.Bus.Read32(a);
                        string marker = off == 0 ? " <--" : "";
                        Console.Error.WriteLine($"    0x{a:X8}: 0x{instr:X8}{marker}");
                    }
                }
            }
            else
            {
                stuckCount = 0;
            }
            prevPc = machine.Cpu.PC;

            if (frame % 50 == 0)
            {
                uint flagAddr = 0x801393C0;
                Console.Error.WriteLine($"[F{frame}] PC=0x{machine.Cpu.PC:X8} FIFO={machine.Bus.Gpu.FifoWrites} VI={machine.Bus.Vi.TotalFields} ViIrq={machine.Cpu.ViInterruptCount} DecIrq={machine.Cpu.DecInterruptCount}");
                Console.Error.WriteLine($"  flags: +0=0x{machine.Bus.Read32(flagAddr):X8} +4=0x{machine.Bus.Read32(flagAddr+4):X8} +8=0x{machine.Bus.Read32(flagAddr+8):X8} +C=0x{machine.Bus.Read32(flagAddr+0xC):X8} +10=0x{machine.Bus.Read32(flagAddr+0x10):X8} +14=0x{machine.Bus.Read32(flagAddr+0x14):X8}");
                Console.Error.WriteLine($"  DSP CSR=0x{machine.Bus.Dsp.Csr:X4} AiDmaCtrl=0x{machine.Bus.Dsp.AiDmaCtrl:X4} DI={machine.Bus.Di.TotalCmds} AramDma={machine.Bus.Dsp.AramDmaCount} DspMbox={machine.Bus.Dsp.DspMailboxCount}");
                Console.Error.WriteLine($"  DiSr=0x{machine.Bus.Di.DiSr:X8} DiCr=0x{machine.Bus.Di.DiCr:X8} DiFinish={machine.Bus.Di.DiInterruptCount} TcintClr={machine.Bus.Di.TcintClearCount}");
                Console.Error.WriteLine($"  DEC=0x{machine.Cpu.DEC:X8} PI_INTSR=0x{machine.Bus.Pi.IntSr:X8} PI_INTMR=0x{machine.Bus.Pi.IntMr:X8}");
                uint dvdCb = machine.Bus.Read32(0x8016B98C);
                Console.Error.WriteLine($"  OSCtx=0x{machine.Bus.Read32(0x800000C0):X8} r1/SP=0x{machine.Cpu.GPR[1]:X8} ThreadSw={machine.Bus.TotalThreadSwitches} DvdCb=0x{dvdCb:X8}");
            }
        }
    }
}
