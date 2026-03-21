using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace PsxEmu;

static class Program
{
    // PSX button bit positions (active LOW in the 16-bit word)
    const int BTN_SELECT   = 0;
    const int BTN_START    = 3;
    const int BTN_UP       = 4;
    const int BTN_RIGHT    = 5;
    const int BTN_DOWN     = 6;
    const int BTN_LEFT     = 7;
    const int BTN_L2       = 8;
    const int BTN_R2       = 9;
    const int BTN_L1       = 10;
    const int BTN_R1       = 11;
    const int BTN_TRIANGLE = 12;
    const int BTN_CIRCLE   = 13;
    const int BTN_CROSS    = 14;
    const int BTN_SQUARE   = 15;

    static ushort PollJoypad()
    {
        int b = 0xFFFF;
        if (IsKeyDown(KeyboardKey.KEY_UP))           b &= ~(1 << BTN_UP);
        if (IsKeyDown(KeyboardKey.KEY_DOWN))         b &= ~(1 << BTN_DOWN);
        if (IsKeyDown(KeyboardKey.KEY_LEFT))         b &= ~(1 << BTN_LEFT);
        if (IsKeyDown(KeyboardKey.KEY_RIGHT))        b &= ~(1 << BTN_RIGHT);
        if (IsKeyDown(KeyboardKey.KEY_Z))            b &= ~(1 << BTN_CROSS);    // Z = Cross (X)
        if (IsKeyDown(KeyboardKey.KEY_X))            b &= ~(1 << BTN_CIRCLE);   // X = Circle
        if (IsKeyDown(KeyboardKey.KEY_A))            b &= ~(1 << BTN_SQUARE);   // A = Square
        if (IsKeyDown(KeyboardKey.KEY_S))            b &= ~(1 << BTN_TRIANGLE); // S = Triangle
        if (IsKeyDown(KeyboardKey.KEY_ENTER))        b &= ~(1 << BTN_START);
        if (IsKeyDown(KeyboardKey.KEY_RIGHT_SHIFT))  b &= ~(1 << BTN_SELECT);
        if (IsKeyDown(KeyboardKey.KEY_BACKSPACE))    b &= ~(1 << BTN_SELECT);
        if (IsKeyDown(KeyboardKey.KEY_Q))            b &= ~(1 << BTN_L1);
        if (IsKeyDown(KeyboardKey.KEY_W))            b &= ~(1 << BTN_L2);
        if (IsKeyDown(KeyboardKey.KEY_E))            b &= ~(1 << BTN_R1);
        if (IsKeyDown(KeyboardKey.KEY_R))            b &= ~(1 << BTN_R2);
        return (ushort)b;
    }

    static unsafe string? MarshalPath(sbyte* ptr) =>
        ptr == null ? null : Marshal.PtrToStringUTF8((IntPtr)ptr);

    const string DefaultBios = @"C:\Users\ashot\Downloads\SCPH9002(7502).BIN";
    const int TexW = 640;
    const int TexH = 480;

    static readonly string[] RegNames =
    {
        "zero","at","v0","v1","a0","a1","a2","a3",
        "t0","t1","t2","t3","t4","t5","t6","t7",
        "s0","s1","s2","s3","s4","s5","s6","s7",
        "t8","t9","k0","k1","gp","sp","fp","ra",
    };

    static readonly byte[] _biosPathBuf = new byte[1024];
    static bool _logScrollToBottom = true;
    static bool _logErrorsOnly;
    static readonly List<Log.Entry> _logCache = new(2048);
    static int _logCacheVersion = -1;

    static readonly string[] _fpsLabels = { "30", "60", "Unlimited" };
    static readonly int[] _fpsValues = { 30, 60, 0 };
    static int _fpsChoice = 1; // default 60

    static unsafe void Main(string[] args)
    {
        // Headless test mode: --test <exe_path> [frames]
        if (args.Length >= 2 && args[0] == "--test")
        {
            RunHeadlessTest(args);
            return;
        }

        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(1600, 900, "PSX Emulator");
        SetTargetFPS(60);

        RlImGui.Setup();

        var machine = new PsxMachine();

        string biosPath = args.Length > 0 ? args[0] : DefaultBios;
        if (File.Exists(biosPath))
        {
            machine.LoadBios(biosPath);
            FillPathBuf(biosPath);
        }
        else
        {
            string? found = machine.TryAutoLoad();
            if (found != null) FillPathBuf(found);
            else               FillPathBuf(biosPath);
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

        Texture psxTex;
        fixed (Color* p = pixels)
        {
            var img = new Image
            {
                data    = p,
                width   = TexW,
                height  = TexH,
                mipmaps = 1,
                format  = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
            };
            psxTex = LoadTextureFromImage(img);
        }
        SetTextureFilter(psxTex, TextureFilter.TEXTURE_FILTER_BILINEAR);

        while (!WindowShouldClose())
        {
            machine.Bus.JoyButtons = PollJoypad();

            machine.Gpu.SnapshotDisplay(pixels, TexW, TexH);
            fixed (Color* p = pixels) UpdateTexture(psxTex, p);

            BeginDrawing();
            ClearBackground(new Color { r = 12, g = 10, b = 18, a = 255 });

            RlImGui.Begin();
            ImGui.DockSpaceOverViewport();

            if (IsFileDropped())
            {
                unsafe
                {
                    var fpl = LoadDroppedFiles();
                    for (int fi = 0; fi < (int)fpl.count; fi++)
                    {
                        string? p = MarshalPath(fpl.paths[fi]);
                        if (p != null)
                        {
                            // Any dropped file is treated as a game (BIOS is locked to the hardcoded path)
                            machine.LoadGameFile(p);
                            break;
                        }
                    }
                    UnloadDroppedFiles(fpl);
                }
            }

            DrawScreenPanel(psxTex, machine);
            DrawCpuPanel(machine);
            DrawGpuPanel(machine);
            DrawBiosPanel(machine);
            DrawInputPanel(machine);
            DrawLogPanel();

            RlImGui.End();
            EndDrawing();
        }

        appAlive = false;
        emuThread.Join(500);
        UnloadTexture(psxTex);
        RlImGui.Shutdown();
        CloseWindow();
    }

    static void DrawScreenPanel(Texture psxTex, PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(680, 530), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("PSX Screen")) { ImGui.End(); return; }

        int dw = Math.Max(16, machine.Gpu.DispWidth);
        int dh = Math.Max(16, machine.Gpu.DispHeight);

        var avail   = ImGui.GetContentRegionAvail();
        const float tvAspect = 4f / 3f;
        float w = avail.X, h = w / tvAspect;
        if (h > avail.Y) { h = avail.Y; w = h * tvAspect; }

        var uv0 = new Vector2(0, 0);
        var uv1 = new Vector2((float)dw / TexW, (float)dh / TexH);

        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(topLeft.X + (avail.X - w) * 0.5f, topLeft.Y));
        ImGui.Image(new IntPtr(psxTex.id), new Vector2(w, h), uv0, uv1);

        if (!machine.Running)
        {
            string msg = machine.BiosPath == null ? "Load a BIOS ROM in the BIOS panel" : "Emulator stopped";
            var ts = ImGui.CalcTextSize(msg);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(topLeft.X + (avail.X - ts.X) * 0.5f, topLeft.Y + (avail.Y - ts.Y) * 0.5f),
                0xBBFFFFFF, msg);
        }
        ImGui.End();
    }

    static unsafe void DrawBiosPanel(PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(500, 140), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("BIOS")) { ImGui.End(); return; }
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"BIOS: {Path.GetFileName(DefaultBios)}  (locked)");
        ImGui.TextDisabled("Drop a .bin/.img game file onto the window to load it");
        ImGui.Spacing();
        if (ImGui.Button("Reset", new Vector2(60, 0)) && machine.BiosPath != null) machine.Reset();
        ImGui.SameLine();
        if (machine.ExeName != null)
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.5f, 1f), $"Game: {machine.ExeName}");
        ImGui.Spacing();
        if (machine.Error != null)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Error: {machine.Error}");
        else if (machine.BiosPath != null)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.5f, 1f),
                $"Running   Frame={machine.FrameCount}   Cycles={machine.Cpu.TotalCycles:N0}");
        ImGui.End();
    }

    static void DrawCpuPanel(PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(310, 580), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("CPU  –  MIPS R3000A")) { ImGui.End(); return; }
        var cpu = machine.Cpu;
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"PC   0x{cpu.PC:X8}");
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"HI   0x{cpu.Hi:X8}");
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"LO   0x{cpu.Lo:X8}");
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), $"SR   0x{cpu.COP0[12]:X8}  IEc={(cpu.COP0[12] & 1) != 0}");
        ImGui.TextColored(new Vector4(0.8f, 0.7f, 1f, 1f), $"IStat 0x{machine.Bus.IStat:X4}  IMask 0x{machine.Bus.IMask:X4}");
        ImGui.Separator();
        if (ImGui.BeginTable("regs", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Reg", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn("Val", ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableHeadersRow();
            for (int i = 0; i < 32; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextDisabled($"${RegNames[i]}");
                ImGui.TableSetColumnIndex(1);
                uint v = cpu.GPR[i];
                ImGui.TextColored(v != 0 ? new Vector4(0.9f, 0.85f, 1f, 1f) : new Vector4(0.3f, 0.28f, 0.38f, 1f), $"0x{v:X8}");
            }
            ImGui.EndTable();
        }
        ImGui.End();
    }

    static void DrawGpuPanel(PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(310, 230), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("GPU  –  GTE / VRAM")) { ImGui.End(); return; }
        var gpu = machine.Gpu;
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
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f), $"Display    {gpu.DispWidth} × {gpu.DispHeight}");
        ImGui.Text($"VRAM start ({gpu.DispStartX}, {gpu.DispStartY})");
        ImGui.Text($"GPUSTAT    0x{gpu.ReadStat():X8}");
        ImGui.Separator();
        ImGui.Text($"GP0 words  {gpu.Gp0Count:N0}");
        ImGui.Text($"GP1 writes {gpu.Gp1Count:N0}");
        ImGui.TextColored(gpu.XferPixels > 0 ? new Vector4(0.4f, 1f, 0.5f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f),
            $"VRAM px    {gpu.XferPixels:N0}");
        ImGui.End();
    }

    static void DrawInputPanel(PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(260, 200), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Input")) { ImGui.End(); return; }

        ushort b = machine.Bus.JoyButtons;
        var on  = new Vector4(0.2f, 1f, 0.4f, 1f);
        var off = new Vector4(0.3f, 0.3f, 0.3f, 1f);

        void Btn(string label, int bit)
        {
            bool pressed = (b & (1 << bit)) == 0;
            ImGui.TextColored(pressed ? on : off, pressed ? $"[{label}]" : $" {label} ");
        }

        ImGui.Text("D-Pad:");
        ImGui.SameLine(80); Btn("UP", BTN_UP);
        Btn("LEFT", BTN_LEFT); ImGui.SameLine(); Btn("DOWN", BTN_DOWN); ImGui.SameLine(); Btn("RIGHT", BTN_RIGHT);
        ImGui.Separator();
        ImGui.Text("Buttons:");
        ImGui.SameLine(80);
        Btn("^", BTN_TRIANGLE); ImGui.SameLine();
        Btn("O", BTN_CIRCLE); ImGui.SameLine();
        Btn("X", BTN_CROSS); ImGui.SameLine();
        Btn("[]", BTN_SQUARE);
        ImGui.Separator();
        Btn("L1", BTN_L1); ImGui.SameLine(); Btn("R1", BTN_R1); ImGui.SameLine();
        Btn("L2", BTN_L2); ImGui.SameLine(); Btn("R2", BTN_R2);
        ImGui.Separator();
        Btn("START", BTN_START); ImGui.SameLine(); Btn("SELECT", BTN_SELECT);
        ImGui.Separator();
        ImGui.TextDisabled("Arrows=D-Pad  Z=X  X=O  A=[]  S=^");
        ImGui.TextDisabled("Enter=Start  BkSp=Select  Q/E=L1/R1");
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

        // Only refresh the cache when the log has changed
        int ver = Log.Version;
        if (ver != _logCacheVersion)
        {
            Log.SnapshotInto(_logCache);
            _logCacheVersion = ver;
        }

        for (int i = 0; i < _logCache.Count; i++)
        {
            ref var e = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_logCache)[i];
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

    static void FillPathBuf(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path);
        Array.Clear(_biosPathBuf);
        Array.Copy(bytes, _biosPathBuf, Math.Min(bytes.Length, _biosPathBuf.Length - 1));
    }

    static void RunHeadlessTest(string[] args)
    {
        string filePath = args[1];
        int maxFrames = args.Length >= 3 && int.TryParse(args[2], out int f) ? f : 600;

        Log.FileLogging = true;
        var machine = new PsxMachine();
        if (!machine.LoadBios(DefaultBios))
        {
            Console.Error.WriteLine($"BIOS load failed: {machine.Error}");
            Environment.Exit(1);
            return;
        }

        if (filePath != "bios-only" && File.Exists(filePath))
        {
            if (!machine.LoadGameFile(filePath))
            {
                Console.Error.WriteLine($"Game load failed: {machine.Error}");
                Environment.Exit(1);
                return;
            }
        }

        for (int frame = 0; frame < maxFrames; frame++)
        {
            machine.Tick();
            if (!machine.Running)
            {
                Console.Error.WriteLine($"Emulator stopped at frame {frame}: {machine.Error}");
                break;
            }

            if (frame % 50 == 0)
            {
                var gpu = machine.Gpu;
                Console.Error.WriteLine($"[F{frame}] PC=0x{machine.Cpu.PC:X8} GP0={gpu.Gp0Count} GP1={gpu.Gp1Count} xfer={gpu.XferPixels} IStat=0x{machine.Bus.IStat:X} IMask=0x{machine.Bus.IMask:X} SR=0x{machine.Cpu.COP0[12]:X8}");
            }
        }

        Console.Write(machine.Cpu.TtyOutput);
    }
}
