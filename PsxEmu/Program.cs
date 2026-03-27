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

    static ushort PollJoypad() => _inputChoice == 0 ? PollKeyboard() : PollGamepad();

    static ushort PollKeyboard()
    {
        int b = 0xFFFF;
        if (IsKeyDown(KeyboardKey.KEY_UP))           b &= ~(1 << BTN_UP);
        if (IsKeyDown(KeyboardKey.KEY_DOWN))         b &= ~(1 << BTN_DOWN);
        if (IsKeyDown(KeyboardKey.KEY_LEFT))         b &= ~(1 << BTN_LEFT);
        if (IsKeyDown(KeyboardKey.KEY_RIGHT))        b &= ~(1 << BTN_RIGHT);
        if (IsKeyDown(KeyboardKey.KEY_Z))            b &= ~(1 << BTN_CROSS);
        if (IsKeyDown(KeyboardKey.KEY_X))            b &= ~(1 << BTN_CIRCLE);
        if (IsKeyDown(KeyboardKey.KEY_A))            b &= ~(1 << BTN_SQUARE);
        if (IsKeyDown(KeyboardKey.KEY_S))            b &= ~(1 << BTN_TRIANGLE);
        if (IsKeyDown(KeyboardKey.KEY_ENTER))        b &= ~(1 << BTN_START);
        if (IsKeyDown(KeyboardKey.KEY_RIGHT_SHIFT))  b &= ~(1 << BTN_SELECT);
        if (IsKeyDown(KeyboardKey.KEY_BACKSPACE))    b &= ~(1 << BTN_SELECT);
        if (IsKeyDown(KeyboardKey.KEY_Q))            b &= ~(1 << BTN_L1);
        if (IsKeyDown(KeyboardKey.KEY_W))            b &= ~(1 << BTN_L2);
        if (IsKeyDown(KeyboardKey.KEY_E))            b &= ~(1 << BTN_R1);
        if (IsKeyDown(KeyboardKey.KEY_R))            b &= ~(1 << BTN_R2);
        return (ushort)b;
    }

    static ushort PollGamepad()
    {
        int b = 0xFFFF;
        if (!IsGamepadAvailable(0)) return (ushort)b;

        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_UP))    b &= ~(1 << BTN_UP);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_DOWN))  b &= ~(1 << BTN_DOWN);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_LEFT))  b &= ~(1 << BTN_LEFT);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_FACE_RIGHT)) b &= ~(1 << BTN_RIGHT);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_DOWN))  b &= ~(1 << BTN_CROSS);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_RIGHT)) b &= ~(1 << BTN_CIRCLE);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_LEFT))  b &= ~(1 << BTN_SQUARE);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_FACE_UP))    b &= ~(1 << BTN_TRIANGLE);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_MIDDLE_RIGHT)) b &= ~(1 << BTN_START);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_MIDDLE_LEFT))  b &= ~(1 << BTN_SELECT);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_TRIGGER_1))  b &= ~(1 << BTN_L1);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_LEFT_TRIGGER_2))  b &= ~(1 << BTN_L2);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_TRIGGER_1)) b &= ~(1 << BTN_R1);
        if (IsGamepadButtonDown(0, GamepadButton.GAMEPAD_BUTTON_RIGHT_TRIGGER_2)) b &= ~(1 << BTN_R2);

        float lx = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_X);
        float ly = GetGamepadAxisMovement(0, GamepadAxis.GAMEPAD_AXIS_LEFT_Y);
        if (lx < -0.5f) b &= ~(1 << BTN_LEFT);
        if (lx >  0.5f) b &= ~(1 << BTN_RIGHT);
        if (ly < -0.5f) b &= ~(1 << BTN_UP);
        if (ly >  0.5f) b &= ~(1 << BTN_DOWN);

        return (ushort)b;
    }

    static unsafe string? MarshalPath(sbyte* ptr) =>
        ptr == null ? null : Marshal.PtrToStringUTF8((IntPtr)ptr);

    const string DefaultBios = @"C:\Users\ashot\Downloads\SCPH9002(7502).BIN";

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
    static int _fpsChoice = 1;
    static bool _muted;
    static bool _upscale2x;

    static readonly string[] _rendererLabels = { "Software", "GPU" };
    static int _rendererChoice;

    static readonly string[] _inputLabels = { "Keyboard", "Controller" };
    static int _inputChoice;
    static bool _displayIsGpu;

    // VRAM viewer
    static bool _showVramViewer;
    static Color[] _vramViewPixels = new Color[1024 * 512];
    static Texture _vramViewTex;
    static bool _vramViewTexReady;

    // Primitive inspector
    static bool _showInspector;
    static int _selectedPrimId = -1;

    // Texture preview
    static Color[] _texPreviewPixels = new Color[256 * 256];
    static Texture _texPreviewTex;
    static bool _texPreviewTexReady;
    static int _texPreviewPrimId = -1;

    // Audio output
    const int AudioSampleRate = 44100;
    const int AudioBufFrames = 1024;
    static readonly short[] _audioBuf = new short[AudioBufFrames * 2];

    // Display texture dimensions (changes with upscaling)
    static int _texW = 640, _texH = 480;

    static unsafe void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--test")
        {
            RunHeadlessTest(args);
            return;
        }

        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(1600, 900, "PSX Emulator");
        SetTargetFPS(60);
        InitAudioDevice();
        SetAudioStreamBufferSizeDefault(AudioBufFrames);
        var audioStream = LoadAudioStream((uint)AudioSampleRate, 16, 2);
        PlayAudioStream(audioStream);

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

        var pixels = new Color[_texW * _texH];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color { a = 255 };

        Texture psxTex;
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
            psxTex = LoadTextureFromImage(img);
        }
        SetTextureFilter(psxTex, TextureFilter.TEXTURE_FILTER_BILINEAR);

        while (!WindowShouldClose())
        {
            machine.Bus.JoyButtons = PollJoypad();

            // Pump SPU audio to Raylib
            if (IsAudioStreamProcessed(audioStream))
            {
                if (_muted)
                {
                    Array.Clear(_audioBuf);
                    fixed (short* ap = _audioBuf) UpdateAudioStream(audioStream, ap, AudioBufFrames);
                }
                else
                {
                    int frames = machine.Bus.Spu.ReadSamples(_audioBuf, 0, AudioBufFrames);
                    if (frames < AudioBufFrames)
                        Array.Clear(_audioBuf, frames * 2, (AudioBufFrames - frames) * 2);
                    fixed (short* ap = _audioBuf) UpdateAudioStream(audioStream, ap, AudioBufFrames);
                }
            }

            // Handle upscaling toggle
            int wantScale = _upscale2x ? 2 : 1;
            if (machine.Gpu.Scale != wantScale)
            {
                machine.Gpu.Scale = wantScale;
                int newW = 640 * wantScale;
                int newH = 480 * wantScale;
                if (newW != _texW || newH != _texH)
                {
                    _texW = newW;
                    _texH = newH;
                    pixels = new Color[_texW * _texH];
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = new Color { a = 255 };
                    UnloadTexture(psxTex);
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
                        psxTex = LoadTextureFromImage(img);
                    }
                    SetTextureFilter(psxTex, TextureFilter.TEXTURE_FILTER_BILINEAR);
                }
            }

            if (machine.Gpu.Renderer is GpuRenderer { GpuReady: true } gpuR)
            {
                gpuR.FlushGpu();
                _displayIsGpu = true;
            }
            else
            {
                machine.Gpu.SnapshotDisplay(pixels, _texW, _texH);
                fixed (Color* p = pixels) UpdateTexture(psxTex, p);
                _displayIsGpu = false;
            }

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
                            machine.LoadGameFile(p);
                            break;
                        }
                    }
                    UnloadDroppedFiles(fpl);
                }
            }

            DrawScreenPanel(psxTex, machine);
            DrawCpuPanel(machine);
            DrawOptionsPanel(machine);
            DrawHacksPanel(machine);
            DrawBiosPanel(machine);
            DrawInputPanel(machine);
            DrawLogPanel();
            DrawProfilerPanel();
            DrawVramPanel(machine);
            DrawInspectorPanel(machine);

            RlImGui.End();
            EndDrawing();
        }

        appAlive = false;
        emuThread.Join(500);
        if (machine.Gpu.Renderer is GpuRenderer gpuCleanup) gpuCleanup.CleanupGpu();
        StopAudioStream(audioStream);
        UnloadAudioStream(audioStream);
        CloseAudioDevice();
        UnloadTexture(psxTex);
        RlImGui.Shutdown();
        CloseWindow();
    }

    static void DrawScreenPanel(Texture psxTex, PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(680, 530), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("PSX Screen")) { ImGui.End(); return; }

        int dw = Math.Max(16, machine.Gpu.DispWidth) * machine.Gpu.Scale;
        int dh = Math.Max(16, machine.Gpu.DispHeight) * machine.Gpu.Scale;

        var avail   = ImGui.GetContentRegionAvail();
        const float tvAspect = 4f / 3f;
        float w = avail.X, h = w / tvAspect;
        if (h > avail.Y) { h = avail.Y; w = h * tvAspect; }

        uint texId;
        Vector2 uv0, uv1;
        if (_displayIsGpu && machine.Gpu.Renderer is GpuRenderer { GpuReady: true } gpuR)
        {
            texId = gpuR.OutputTextureId;
            uv0 = new Vector2(0, 0);
            uv1 = new Vector2(1, 1);
        }
        else
        {
            texId = psxTex.id;
            uv0 = new Vector2(0, 0);
            uv1 = new Vector2((float)dw / _texW, (float)dh / _texH);
        }

        var topLeft = ImGui.GetCursorScreenPos();
        var imgPos = new Vector2(topLeft.X + (avail.X - w) * 0.5f, topLeft.Y);
        ImGui.SetCursorScreenPos(imgPos);
        ImGui.Image(new IntPtr(texId), new Vector2(w, h), uv0, uv1);

        if (machine.Gpu.Renderer.TrackPrimitives && ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var mousePos = ImGui.GetMousePos();
            float relX = (mousePos.X - imgPos.X) / w;
            float relY = (mousePos.Y - imgPos.Y) / h;
            int vramX = machine.Gpu.DispStartX + (int)(relX * machine.Gpu.DispWidth);
            int vramY = machine.Gpu.DispStartY + (int)(relY * machine.Gpu.DispHeight);
            if (vramX >= 0 && vramX < 1024 && vramY >= 0 && vramY < 512)
            {
                int primId = machine.Gpu.Renderer.SnapPrimIdBuf[vramY * 1024 + vramX];
                if (primId >= 0 && primId < machine.Gpu.Renderer.SnapPrims.Count)
                {
                    _selectedPrimId = primId;
                    _showInspector = true;
                    _texPreviewPrimId = -1;
                }
            }
        }

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

    static void DrawOptionsPanel(PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(330, 300), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Options")) { ImGui.End(); return; }

        var gpu = machine.Gpu;

        // FPS
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

        // Audio
        ImGui.Checkbox("Mute", ref _muted);

        ImGui.Separator();

        // Upscaling
        ImGui.Checkbox("2x Upscaling", ref _upscale2x);
        ImGui.SameLine();
        ImGui.TextDisabled("(internal rendering)");

        ImGui.Separator();

        // Input mode
        ImGui.SetNextItemWidth(120);
        ImGui.Combo("Input", ref _inputChoice, _inputLabels, _inputLabels.Length);
        if (_inputChoice == 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(IsGamepadAvailable(0) ? "connected" : "no gamepad");
        }

        ImGui.Separator();

        // Renderer backend
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("Renderer", ref _rendererChoice, _rendererLabels, _rendererLabels.Length))
        {
            var oldRenderer = gpu.Renderer;

            PsxRendererBase newRenderer = _rendererChoice == 0
                ? new SoftwareRenderer()
                : new GpuRenderer();

            Array.Copy(oldRenderer.Vram, newRenderer.Vram, oldRenderer.Vram.Length);
            newRenderer.DrawX1 = oldRenderer.DrawX1;
            newRenderer.DrawY1 = oldRenderer.DrawY1;
            newRenderer.DrawX2 = oldRenderer.DrawX2;
            newRenderer.DrawY2 = oldRenderer.DrawY2;
            newRenderer.OffX = oldRenderer.OffX;
            newRenderer.OffY = oldRenderer.OffY;
            newRenderer.TexPageX = oldRenderer.TexPageX;
            newRenderer.TexPageY = oldRenderer.TexPageY;
            newRenderer.TexDepth = oldRenderer.TexDepth;
            newRenderer.SemiTrans = oldRenderer.SemiTrans;
            newRenderer.Scale = oldRenderer.Scale;
            newRenderer.HackWireframe = oldRenderer.HackWireframe;
            newRenderer.HackVertexColorsOnly = oldRenderer.HackVertexColorsOnly;
            newRenderer.HackRandomColors = oldRenderer.HackRandomColors;
            newRenderer.TrackPrimitives = oldRenderer.TrackPrimitives;

            if (newRenderer is SoftwareRenderer swNew)
                swNew.RebuildUpscaleVram();

            if (oldRenderer is GpuRenderer oldGpu)
                oldGpu.CleanupGpu();

            gpu.SetRenderer(newRenderer);

            if (newRenderer is GpuRenderer newGpu)
                newGpu.InitGpu();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(gpu.Renderer is GpuRenderer ? "(hw)" : "(sw)");

        ImGui.Separator();

        // GPU stats
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.9f, 1f),
            $"Display    {gpu.DispWidth} × {gpu.DispHeight}  (×{gpu.Scale})");
        ImGui.Text($"VRAM start ({gpu.DispStartX}, {gpu.DispStartY})");
        ImGui.Text($"GPUSTAT    0x{gpu.ReadStat():X8}");
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
        if (_inputChoice == 0)
        {
            ImGui.TextDisabled("Arrows=D-Pad  Z=X  X=O  A=[]  S=^");
            ImGui.TextDisabled("Enter=Start  BkSp=Select  Q/E=L1/R1");
        }
        else
        {
            ImGui.TextDisabled("Using gamepad #0 (first connected)");
            ImGui.TextDisabled("Left stick also maps to D-Pad");
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

    static void DrawHacksPanel(PsxMachine machine)
    {
        ImGui.SetNextWindowSize(new Vector2(320, 380), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Rendering Hacks")) { ImGui.End(); return; }

        var R = machine.Gpu.Renderer;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Render Modes");
        ImGui.Separator();
        ImGui.Checkbox("Wireframe", ref R.HackWireframe);
        ImGui.SameLine(); ImGui.TextDisabled("(edges only)");
        ImGui.Checkbox("Vertex Colors Only", ref R.HackVertexColorsOnly);
        ImGui.SameLine(); ImGui.TextDisabled("(strip textures)");
        ImGui.Checkbox("Random Prim Colors", ref R.HackRandomColors);
        ImGui.SameLine(); ImGui.TextDisabled("(unique per draw)");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Debug Tools");
        ImGui.Separator();
        ImGui.Checkbox("VRAM Viewer", ref _showVramViewer);
        bool prevTrack = R.TrackPrimitives;
        ImGui.Checkbox("Primitive Picker", ref R.TrackPrimitives);
        if (R.TrackPrimitives)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({R.SnapPrims.Count} prims)");
        }
        if (!R.TrackPrimitives && prevTrack)
            _selectedPrimId = -1;
        ImGui.Checkbox("Inspector", ref _showInspector);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Click on the PSX Screen to pick a");
        ImGui.TextDisabled("primitive (requires Primitive Picker).");

        ImGui.End();
    }

    static unsafe void DrawVramPanel(PsxMachine machine)
    {
        if (!_showVramViewer) return;

        if (!_vramViewTexReady)
        {
            for (int i = 0; i < _vramViewPixels.Length; i++)
                _vramViewPixels[i] = new Color { a = 255 };
            fixed (Color* p = _vramViewPixels)
            {
                var img = new Image { data = p, width = 1024, height = 512, mipmaps = 1, format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8 };
                _vramViewTex = LoadTextureFromImage(img);
            }
            SetTextureFilter(_vramViewTex, TextureFilter.TEXTURE_FILTER_POINT);
            _vramViewTexReady = true;
        }

        machine.Gpu.Renderer.SnapshotFullVram(_vramViewPixels);
        fixed (Color* p = _vramViewPixels) UpdateTexture(_vramViewTex, p);

        ImGui.SetNextWindowSize(new Vector2(820, 460), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("VRAM Viewer", ref _showVramViewer)) { ImGui.End(); return; }

        var avail = ImGui.GetContentRegionAvail();
        const float aspect = 1024f / 512f;
        float iw = avail.X, ih = iw / aspect;
        if (ih > avail.Y) { ih = avail.Y; iw = ih * aspect; }

        var pos = ImGui.GetCursorScreenPos();
        ImGui.Image(new IntPtr(_vramViewTex.id), new Vector2(iw, ih));

        var drawList = ImGui.GetWindowDrawList();
        float sx = iw / 1024f, sy = ih / 512f;

        int dispX = machine.Gpu.DispStartX, dispY = machine.Gpu.DispStartY;
        int dispW = machine.Gpu.DispWidth, dispH = machine.Gpu.DispHeight;
        drawList.AddRect(
            new Vector2(pos.X + dispX * sx, pos.Y + dispY * sy),
            new Vector2(pos.X + (dispX + dispW) * sx, pos.Y + (dispY + dispH) * sy),
            0xFF00FF00, 0, ImDrawFlags.None, 2);

        if (_selectedPrimId >= 0 && _selectedPrimId < machine.Gpu.Renderer.SnapPrims.Count)
        {
            var prim = machine.Gpu.Renderer.SnapPrims[_selectedPrimId];

            drawList.AddTriangleFilled(
                new Vector2(pos.X + prim.X0 * sx, pos.Y + prim.Y0 * sy),
                new Vector2(pos.X + prim.X1 * sx, pos.Y + prim.Y1 * sy),
                new Vector2(pos.X + prim.X2 * sx, pos.Y + prim.Y2 * sy),
                0x4400FFFF);
            drawList.AddTriangle(
                new Vector2(pos.X + prim.X0 * sx, pos.Y + prim.Y0 * sy),
                new Vector2(pos.X + prim.X1 * sx, pos.Y + prim.Y1 * sy),
                new Vector2(pos.X + prim.X2 * sx, pos.Y + prim.Y2 * sy),
                0xFF00FFFF, 2);

            if (prim.Type is PrimInfo.Kind.TriTex or PrimInfo.Kind.TriTexBlend or PrimInfo.Kind.TriGouraudTex)
            {
                int tpW = prim.TpDepth switch { 0 => 64, 1 => 128, _ => 256 };
                drawList.AddRect(
                    new Vector2(pos.X + prim.TpX * sx, pos.Y + prim.TpY * sy),
                    new Vector2(pos.X + (prim.TpX + tpW) * sx, pos.Y + (prim.TpY + 256) * sy),
                    0xFFFF8800, 0, ImDrawFlags.None, 1);

                if (prim.TpDepth <= 1)
                {
                    int clutW = prim.TpDepth == 0 ? 16 : 256;
                    drawList.AddRect(
                        new Vector2(pos.X + prim.ClutX * sx, pos.Y + prim.ClutY * sy),
                        new Vector2(pos.X + (prim.ClutX + clutW) * sx, pos.Y + (prim.ClutY + 1) * sy),
                        0xFFFF00FF, 0, ImDrawFlags.None, 1);
                }
            }
        }

        ImGui.End();
    }

    static unsafe void DrawInspectorPanel(PsxMachine machine)
    {
        if (!_showInspector) return;
        ImGui.SetNextWindowSize(new Vector2(420, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Primitive Inspector", ref _showInspector)) { ImGui.End(); return; }

        if (!machine.Gpu.Renderer.TrackPrimitives)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Enable 'Primitive Picker' in Hacks panel.");
            ImGui.End();
            return;
        }

        if (_selectedPrimId < 0)
        {
            ImGui.TextDisabled("Click on the PSX Screen to select a primitive.");
            ImGui.End();
            return;
        }

        var prims = machine.Gpu.Renderer.SnapPrims;
        if (_selectedPrimId >= prims.Count)
        {
            ImGui.TextDisabled("Stale selection — click again.");
            ImGui.End();
            return;
        }

        var p = prims[_selectedPrimId];
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"Primitive #{_selectedPrimId}  ({p.Type})");
        ImGui.Separator();
        ImGui.Text($"V0: ({p.X0}, {p.Y0})");
        ImGui.SameLine(160); ImGui.Text($"V1: ({p.X1}, {p.Y1})");
        ImGui.SameLine(310); ImGui.Text($"V2: ({p.X2}, {p.Y2})");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.6f, 1f, 0.8f, 1f), "Colors");
        DrawColorSwatch("C0", p.C0); ImGui.SameLine();
        DrawColorSwatch("C1", p.C1); ImGui.SameLine();
        DrawColorSwatch("C2", p.C2);

        bool isTextured = p.Type is PrimInfo.Kind.TriTex or PrimInfo.Kind.TriTexBlend or PrimInfo.Kind.TriGouraudTex;
        if (isTextured)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 1f, 0.8f, 1f), "Texture");
            ImGui.Text($"UVs: ({p.U0},{p.V0})  ({p.U1},{p.V1})  ({p.U2},{p.V2})");
            string depthStr = p.TpDepth switch { 0 => "4bpp", 1 => "8bpp", _ => "15bpp" };
            ImGui.Text($"TexPage: ({p.TpX}, {p.TpY})  {depthStr}");
            if (p.TpDepth <= 1)
                ImGui.Text($"CLUT: ({p.ClutX}, {p.ClutY})");

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 1f, 0.8f, 1f), "Texture Preview");

            if (_texPreviewPrimId != _selectedPrimId)
            {
                BuildTexPreview(machine, p);
                _texPreviewPrimId = _selectedPrimId;
            }

            if (!_texPreviewTexReady)
            {
                for (int i = 0; i < _texPreviewPixels.Length; i++)
                    _texPreviewPixels[i] = new Color { a = 255 };
                fixed (Color* pp = _texPreviewPixels)
                {
                    var img = new Image { data = pp, width = 256, height = 256, mipmaps = 1, format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8 };
                    _texPreviewTex = LoadTextureFromImage(img);
                }
                SetTextureFilter(_texPreviewTex, TextureFilter.TEXTURE_FILTER_POINT);
                _texPreviewTexReady = true;
            }

            fixed (Color* pp = _texPreviewPixels) UpdateTexture(_texPreviewTex, pp);

            float previewSize = Math.Min(ImGui.GetContentRegionAvail().X, 256);
            ImGui.Image(new IntPtr(_texPreviewTex.id), new Vector2(previewSize, previewSize));
        }

        ImGui.Spacing();
        ImGui.Separator();
        int prev = _selectedPrimId - 1, next = _selectedPrimId + 1;
        if (prev >= 0 && ImGui.Button("< Prev"))
        {
            _selectedPrimId = prev;
            _texPreviewPrimId = -1;
        }
        ImGui.SameLine();
        if (next < prims.Count && ImGui.Button("Next >"))
        {
            _selectedPrimId = next;
            _texPreviewPrimId = -1;
        }

        ImGui.End();
    }

    static void DrawColorSwatch(string label, uint col)
    {
        float r = (col & 0xFF) / 255f;
        float g = ((col >> 8) & 0xFF) / 255f;
        float b = ((col >> 16) & 0xFF) / 255f;
        ImGui.ColorButton(label, new Vector4(r, g, b, 1f), ImGuiColorEditFlags.NoTooltip, new Vector2(24, 24));
        ImGui.SameLine();
        ImGui.Text($"0x{col:X6}");
    }

    static void BuildTexPreview(PsxMachine machine, PrimInfo prim)
    {
        var snapVram = machine.Gpu.Renderer.SnapVramData;
        if (snapVram == null) return;

        for (int v = 0; v < 256; v++)
            for (int u = 0; u < 256; u++)
            {
                ushort texel = PsxRendererBase.SampleTexelStatic(snapVram, u, v, prim.TpX, prim.TpY, prim.TpDepth, prim.ClutX, prim.ClutY);
                uint rgba = PsxRendererBase.Psx15ToRgba(texel);
                int idx = v * 256 + u;
                _texPreviewPixels[idx] = new Color
                {
                    r = (byte)(rgba & 0xFF),
                    g = (byte)((rgba >> 8) & 0xFF),
                    b = (byte)((rgba >> 16) & 0xFF),
                    a = (byte)(texel == 0 ? 0 : 255)
                };
            }
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
