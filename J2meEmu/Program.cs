using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace J2meEmu;

static class Program
{
    static bool _logScrollToBottom = true;
    static bool _logErrorsOnly;
    static readonly List<Log.Entry> _logCache = new(2048);
    static int _logCacheVersion = -1;

    static readonly string[] _fpsLabels = { "15", "30", "60", "Unlimited" };
    static readonly int[] _fpsValues = { 15, 30, 60, 0 };
    static int _fpsChoice = 2;

    static JvmClassLoader? _loader;
    static MidletHost? _host;
    static JvmThread? _mainThread;
    static volatile bool _running;
    static volatile bool _appAlive = true;
    static string? _error;
    static string? _loadedJar;
    static volatile int _frameCount;
    static Thread? _emuThread;
    static readonly Queue<(int keyCode, bool pressed)> _keyQueue = new();
    static readonly object _keyLock = new();

    struct KeyMap { public KeyboardKey Key; public int J2meCode; }
    static readonly KeyMap[] _keyMap =
    {
        new() { Key = KeyboardKey.KEY_UP,    J2meCode = MidletHost.KEY_UP },
        new() { Key = KeyboardKey.KEY_DOWN,  J2meCode = MidletHost.KEY_DOWN },
        new() { Key = KeyboardKey.KEY_LEFT,  J2meCode = MidletHost.KEY_LEFT },
        new() { Key = KeyboardKey.KEY_RIGHT, J2meCode = MidletHost.KEY_RIGHT },
        new() { Key = KeyboardKey.KEY_ENTER, J2meCode = MidletHost.KEY_FIRE },
        new() { Key = KeyboardKey.KEY_SPACE, J2meCode = MidletHost.KEY_FIRE },
        new() { Key = KeyboardKey.KEY_Q,     J2meCode = MidletHost.KEY_SOFT_LEFT },
        new() { Key = KeyboardKey.KEY_E,     J2meCode = MidletHost.KEY_SOFT_RIGHT },
        new() { Key = KeyboardKey.KEY_KP_0,  J2meCode = MidletHost.KEY_NUM0 },
        new() { Key = KeyboardKey.KEY_KP_1,  J2meCode = MidletHost.KEY_NUM1 },
        new() { Key = KeyboardKey.KEY_KP_2,  J2meCode = MidletHost.KEY_NUM2 },
        new() { Key = KeyboardKey.KEY_KP_3,  J2meCode = MidletHost.KEY_NUM3 },
        new() { Key = KeyboardKey.KEY_KP_4,  J2meCode = MidletHost.KEY_NUM4 },
        new() { Key = KeyboardKey.KEY_KP_5,  J2meCode = MidletHost.KEY_NUM5 },
        new() { Key = KeyboardKey.KEY_KP_6,  J2meCode = MidletHost.KEY_NUM6 },
        new() { Key = KeyboardKey.KEY_KP_7,  J2meCode = MidletHost.KEY_NUM7 },
        new() { Key = KeyboardKey.KEY_KP_8,  J2meCode = MidletHost.KEY_NUM8 },
        new() { Key = KeyboardKey.KEY_KP_9,  J2meCode = MidletHost.KEY_NUM9 },
        new() { Key = KeyboardKey.KEY_ZERO,  J2meCode = MidletHost.KEY_NUM0 },
        new() { Key = KeyboardKey.KEY_ONE,   J2meCode = MidletHost.KEY_NUM1 },
        new() { Key = KeyboardKey.KEY_TWO,   J2meCode = MidletHost.KEY_NUM2 },
        new() { Key = KeyboardKey.KEY_THREE, J2meCode = MidletHost.KEY_NUM3 },
        new() { Key = KeyboardKey.KEY_FOUR,  J2meCode = MidletHost.KEY_NUM4 },
        new() { Key = KeyboardKey.KEY_FIVE,  J2meCode = MidletHost.KEY_NUM5 },
        new() { Key = KeyboardKey.KEY_SIX,   J2meCode = MidletHost.KEY_NUM6 },
        new() { Key = KeyboardKey.KEY_SEVEN, J2meCode = MidletHost.KEY_NUM7 },
        new() { Key = KeyboardKey.KEY_EIGHT, J2meCode = MidletHost.KEY_NUM8 },
        new() { Key = KeyboardKey.KEY_NINE,  J2meCode = MidletHost.KEY_NUM9 },
    };

    static unsafe void Main(string[] args)
    {
        NativeRegistry.EnsureInit();

        if (args.Length >= 2 && args[0] == "--headless")
        {
            J2meAudio.Headless = true;
            RunHeadless(args[1], args.Length >= 3 && int.TryParse(args[2], out int f) ? f : 60);
            return;
        }

        if (args.Length >= 2 && args[0] == "--batch")
        {
            J2meAudio.Headless = true;
            RunBatch(args[1]);
            return;
        }

        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(1280, 900, "J2ME Emulator");
        SetTargetFPS(60);
        J2meAudio.Init();
        RlImGui.Setup();

        if (args.Length > 0 && File.Exists(args[0]))
            LoadJar(args[0]);

        int texW = 240, texH = 320;
        var pixels = new Color[texW * texH];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color { a = 255 };

        Texture j2meTex;
        fixed (Color* p = pixels)
        {
            var img = new Image { data = p, width = texW, height = texH, mipmaps = 1,
                format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8 };
            j2meTex = LoadTextureFromImage(img);
        }
        SetTextureFilter(j2meTex, TextureFilter.TEXTURE_FILTER_POINT);

        while (!WindowShouldClose())
        {
            if (_running && _host != null)
            {
                PollKeys();

                if (_host.ScreenWidth != texW || _host.ScreenHeight != texH)
                {
                    texW = _host.ScreenWidth; texH = _host.ScreenHeight;
                    pixels = new Color[texW * texH];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color { a = 255 };
                    UnloadTexture(j2meTex);
                    fixed (Color* p = pixels)
                    {
                        var img = new Image { data = p, width = texW, height = texH, mipmaps = 1,
                            format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8 };
                        j2meTex = LoadTextureFromImage(img);
                    }
                    SetTextureFilter(j2meTex, TextureFilter.TEXTURE_FILTER_POINT);
                }

                CopyFramebufferToPixels(_host, pixels, texW, texH);
                fixed (Color* p = pixels) UpdateTexture(j2meTex, p);
            }

            J2meAudio.Update();

            BeginDrawing();
            ClearBackground(new Color { r = 10, g = 10, b = 18, a = 255 });

            RlImGui.Begin();
            ImGui.DockSpaceOverViewport();

            if (IsFileDropped())
            {
                var fpl = LoadDroppedFiles();
                for (int fi = 0; fi < (int)fpl.count; fi++)
                {
                    string? path = fpl.paths[fi] == null ? null : Marshal.PtrToStringUTF8((IntPtr)fpl.paths[fi]);
                    if (path != null && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadJar(path);
                        break;
                    }
                }
                UnloadDroppedFiles(fpl);
            }

            DrawScreenPanel(j2meTex, texW, texH);
            DrawMidletPanel();
            DrawInputPanel();
            DrawLogPanel();

            RlImGui.End();
            EndDrawing();
        }

        _appAlive = false;
        _running = false;
        _emuThread?.Join(1000);
        UnloadTexture(j2meTex);
        J2meAudio.Shutdown();
        RlImGui.Shutdown();
        CloseWindow();
    }

    static void LoadJar(string path)
    {
        _running = false;
        _emuThread?.Join(1000);
        _emuThread = null;
        _error = null;
        _frameCount = 0;
        _loadedJar = Path.GetFileName(path);
        lock (_keyLock) _keyQueue.Clear();

        try
        {
            _loader = new JvmClassLoader();
            _host = new MidletHost();
            _loader.Host = _host;
            _loader.LoadJar(path);

            if (_loader.MidletClassName == null)
            {
                _error = "No MIDlet-1 in manifest";
                Log.Error(_error);
                return;
            }

            _running = true;
            _emuThread = new Thread(() => EmuThreadMain(path)) { IsBackground = true, Name = "J2meEmu" };
            _emuThread.Start();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Error($"Load failed: {ex}");
        }
    }

    static void EmuThreadMain(string jarPath)
    {
        try
        {
            var loader = _loader!;
            var host = _host!;

            _mainThread = new JvmThread(loader, "main");
            var thread = _mainThread;

            var midletClass = loader.LoadClass(loader.MidletClassName!);
            loader.InitializeClass(midletClass, thread);

            var midlet = new JavaObject(midletClass);
            host.MidletObject = midlet;

            var init = midletClass.FindMethod("<init>", "()V");
            if (init != null) thread.Invoke(init, new[] { JValue.OfRef(midlet) });

            var startApp = midletClass.FindMethod("startApp", "()V");
            if (startApp != null) thread.Invoke(startApp, new[] { JValue.OfRef(midlet) });

            Log.Info($"MIDlet started: {loader.MidletClassName}");

            host.RepaintRequested = true;

            while (_running && _appAlive && !host.Destroyed)
            {
                long frameStart = Environment.TickCount64;

                lock (_keyLock)
                {
                    while (_keyQueue.Count > 0)
                    {
                        var (keyCode, pressed) = _keyQueue.Dequeue();
                        try { host.SendKeyEvent(thread, keyCode, pressed); }
                        catch (Exception ex) { Log.Error($"key error: {ex.Message}"); }
                    }
                }

                bool hasCanvas = host.CanvasObject != null || host.CurrentDisplayable != null;
                if (hasCanvas)
                {
                    try
                    {
                        host.DoPaint(thread);
                        _frameCount++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"paint error: {ex.Message}");
                    }
                }

                long elapsed = Environment.TickCount64 - frameStart;
                int sleepMs = Math.Max(1, 33 - (int)elapsed);
                Thread.Sleep(sleepMs);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Error($"Emu thread error: {ex.Message}");
        }
    }

    static void PollKeys()
    {
        lock (_keyLock)
        {
            foreach (var km in _keyMap)
            {
                if (IsKeyPressed(km.Key)) _keyQueue.Enqueue((km.J2meCode, true));
                if (IsKeyReleased(km.Key)) _keyQueue.Enqueue((km.J2meCode, false));
            }
        }
    }

    static void CopyFramebufferToPixels(MidletHost host, Color[] pixels, int w, int h)
    {
        int len = Math.Min(host.Framebuffer.Length, pixels.Length);
        for (int i = 0; i < len; i++)
        {
            int argb = host.Framebuffer[i];
            pixels[i] = new Color
            {
                r = (byte)((argb >> 16) & 0xFF),
                g = (byte)((argb >> 8) & 0xFF),
                b = (byte)(argb & 0xFF),
                a = (byte)((argb >> 24) & 0xFF)
            };
        }
    }

    static void DrawScreenPanel(Texture tex, int texW, int texH)
    {
        ImGui.SetNextWindowSize(new Vector2(300, 440), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("J2ME Screen")) { ImGui.End(); return; }

        var avail = ImGui.GetContentRegionAvail();
        float aspect = (float)texW / texH;
        float w = avail.X, h = w / aspect;
        if (h > avail.Y) { h = avail.Y; w = h * aspect; }

        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(topLeft.X + (avail.X - w) * 0.5f, topLeft.Y));
        ImGui.Image(new IntPtr(tex.id), new Vector2(w, h));

        if (!_running)
        {
            string msg = _loadedJar == null ? "Drop a .jar MIDlet onto the window" : "Emulator stopped";
            var ts = ImGui.CalcTextSize(msg);
            ImGui.GetWindowDrawList().AddText(
                new Vector2(topLeft.X + (avail.X - ts.X) * 0.5f, topLeft.Y + (avail.Y - ts.Y) * 0.5f),
                0xBBFFFFFF, msg);
        }
        ImGui.End();
    }

    static void DrawMidletPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(480, 400), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("MIDlet")) { ImGui.End(); return; }

        if (_loadedJar != null)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), $"JAR: {_loadedJar}");
            if (_loader != null)
            {
                ImGui.TextDisabled($"MIDlet: {_loader.MidletClassName ?? "none"}");
                string? name = _loader.ManifestProps.GetValueOrDefault("MIDlet-Name");
                string? vendor = _loader.ManifestProps.GetValueOrDefault("MIDlet-Vendor");
                if (name != null) ImGui.TextDisabled($"Name: {name}");
                if (vendor != null) ImGui.TextDisabled($"Vendor: {vendor}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "No JAR loaded");
            ImGui.TextDisabled("Drop a .jar file onto the window");
        }

        ImGui.Spacing();
        if (_error != null)
            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Error: {_error}");
        else if (_running)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.5f, 1f), $"Running   Frame={_frameCount}");

        if (_loader != null)
        {
            ImGui.TextDisabled($"Classes: {_loader.ClassesLoaded}  Calls: {_loader.MethodsInvoked}");
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f),
                $"Screen: {_host?.ScreenWidth ?? 240} x {_host?.ScreenHeight ?? 320}");
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("FPS Limit", ref _fpsChoice, _fpsLabels, _fpsLabels.Length))
        {
            int v = _fpsValues[_fpsChoice];
            SetTargetFPS(v > 0 ? v : 10000);
        }

        if (_loader != null)
        {
            int mc = _loader.MissingClasses.Count, mm = _loader.MissingMethods.Count;
            if (mc + mm > 0)
            {
                ImGui.Separator();
                if (ImGui.CollapsingHeader($"Missing APIs ({mc} classes, {mm} methods)"))
                {
                    ImGui.BeginChild("missapis", new Vector2(0, 150), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
                    if (mc > 0)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Classes:");
                        foreach (var c in _loader.MissingClasses)
                            ImGui.TextColored(new Vector4(1f, 0.7f, 0.5f, 1f), $"  {c}");
                    }
                    if (mm > 0)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), "Methods:");
                        foreach (var m in _loader.MissingMethods)
                            ImGui.TextColored(new Vector4(1f, 0.7f, 0.5f, 1f), $"  {m}");
                    }
                    ImGui.EndChild();
                }
            }
            else
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.5f, 1f), "No missing APIs");
            }
        }
        ImGui.End();
    }

    static void DrawInputPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(280, 250), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Input")) { ImGui.End(); return; }

        int pk = _host?.PressedKeys ?? 0;
        var on  = new Vector4(0.2f, 1f, 0.4f, 1f);
        var off = new Vector4(0.3f, 0.3f, 0.3f, 1f);

        void Btn(string label, int bit)
        {
            bool pressed = (pk & bit) != 0;
            ImGui.TextColored(pressed ? on : off, pressed ? $"[{label}]" : $" {label} ");
        }

        ImGui.Text("D-Pad:");
        ImGui.SameLine(70); Btn("UP", MidletHost.BIT_UP);
        Btn("LEFT", MidletHost.BIT_LEFT); ImGui.SameLine();
        Btn("DOWN", MidletHost.BIT_DOWN); ImGui.SameLine();
        Btn("RIGHT", MidletHost.BIT_RIGHT);
        ImGui.Separator();
        ImGui.Text("Action:");
        ImGui.SameLine(70);
        Btn("FIRE", MidletHost.BIT_FIRE); ImGui.SameLine();
        Btn("SOFT-L", MidletHost.BIT_SOFT_L); ImGui.SameLine();
        Btn("SOFT-R", MidletHost.BIT_SOFT_R);
        ImGui.Separator();
        ImGui.Text("Numpad:");
        Btn("1", MidletHost.BIT_NUM1); ImGui.SameLine();
        Btn("2", MidletHost.BIT_NUM2); ImGui.SameLine();
        Btn("3", MidletHost.BIT_NUM3);
        Btn("4", MidletHost.BIT_NUM4); ImGui.SameLine();
        Btn("5", MidletHost.BIT_NUM5); ImGui.SameLine();
        Btn("6", MidletHost.BIT_NUM6);
        Btn("7", MidletHost.BIT_NUM7); ImGui.SameLine();
        Btn("8", MidletHost.BIT_NUM8); ImGui.SameLine();
        Btn("9", MidletHost.BIT_NUM9);
        ImGui.SameLine(); Btn("0", MidletHost.BIT_NUM0);
        ImGui.Separator();
        ImGui.TextDisabled("Arrows=D-Pad  Enter/Space=FIRE");
        ImGui.TextDisabled("Q=SoftL  E=SoftR  0-9=Numpad");
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
                Log.Cat.JVM   => new Vector4(0.7f, 1.0f, 0.7f, 1f),
                Log.Cat.GFX   => new Vector4(0.5f, 0.9f, 1.0f, 1f),
                Log.Cat.Class => new Vector4(1.0f, 0.9f, 0.5f, 1f),
                Log.Cat.MIDP  => new Vector4(0.9f, 0.7f, 1.0f, 1f),
                Log.Cat.Error => new Vector4(1.0f, 0.3f, 0.3f, 1f),
                _             => new Vector4(0.85f, 0.85f, 0.85f, 1f),
            };
            ImGui.TextColored(c, $"[{e.When:HH:mm:ss.fff}][{e.Category,-5}] {e.Text}");
        }
        if (_logScrollToBottom) ImGui.SetScrollHereY(1.0f);
        ImGui.EndChild();
        ImGui.End();
    }


    // === Headless mode ===

    static void RunHeadless(string jarPath, int maxFrames)
    {
        Log.FileLogging = true;
        Console.Error.WriteLine($"[headless] Loading: {jarPath}");

        var loader = new JvmClassLoader();
        var host = new MidletHost();
        loader.Host = host;

        try
        {
            loader.LoadJar(jarPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {Path.GetFileName(jarPath)} - JAR load error: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        if (loader.MidletClassName == null)
        {
            Console.Error.WriteLine($"FAIL: {Path.GetFileName(jarPath)} - No MIDlet-1 in manifest");
            Environment.Exit(1);
            return;
        }

        var thread = new JvmThread(loader, "main");
        thread.MaxInstructions = 5_000_000;
        try
        {
            var cls = loader.LoadClass(loader.MidletClassName);
            loader.InitializeClass(cls, thread);
            var midlet = new JavaObject(cls);
            host.MidletObject = midlet;

            var init = cls.FindMethod("<init>", "()V");
            if (init != null) thread.Invoke(init, new[] { JValue.OfRef(midlet) });

            var startApp = cls.FindMethod("startApp", "()V");
            if (startApp != null) thread.Invoke(startApp, new[] { JValue.OfRef(midlet) });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {Path.GetFileName(jarPath)} - startApp error: {ex}");
            PrintReport(loader, jarPath);
            Environment.Exit(1);
            return;
        }

        long startMs = Environment.TickCount64;
        long lastKeyMs = 0;
        int keyPresses = 0;
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (host.Destroyed) break;

            long now = Environment.TickCount64;
            if (now - lastKeyMs >= 1000 && keyPresses < 15)
            {
                lastKeyMs = now;
                keyPresses++;
                try
                {
                    host.SendKeyEvent(thread, MidletHost.KEY_FIRE, true);
                    host.SendKeyEvent(thread, MidletHost.KEY_FIRE, false);
                }
                catch { }
            }

            try { host.DoPaint(thread); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL: {Path.GetFileName(jarPath)} - paint error at frame {frame}: {ex}");
                PrintReport(loader, jarPath);
                Environment.Exit(1);
                return;
            }
            Thread.Sleep(33);
        }

        int nonZero = 0;
        foreach (int px in host.Framebuffer) if (px != 0) nonZero++;
        Console.Error.WriteLine($"OK: {Path.GetFileName(jarPath)} - {maxFrames} frames, {loader.ClassesLoaded} classes, {loader.MethodsInvoked} calls, {nonZero}/{host.Framebuffer.Length} pixels drawn");
        PrintReport(loader, jarPath);
    }

    // === Batch mode ===

    static void RunBatch(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Console.Error.WriteLine($"Directory not found: {folderPath}");
            Environment.Exit(1);
            return;
        }

        var jars = Directory.GetFiles(folderPath, "*.jar", SearchOption.AllDirectories);
        Console.WriteLine($"Found {jars.Length} JAR files in {folderPath}");
        Console.WriteLine(new string('=', 80));

        int pass = 0, fail = 0;
        var allMissingClasses = new Dictionary<string, int>();
        var allMissingMethods = new Dictionary<string, int>();
        var failures = new List<(string name, string err)>();

        foreach (var jar in jars)
        {
            Console.Write($"  {Path.GetFileName(jar)} ... ");
            Console.Out.Flush();
            NativeRegistry.EnsureInit();

            var loader = new JvmClassLoader();
            var host = new MidletHost();
            loader.Host = host;
            bool ok = false;
            string errMsg = "";

            try
            {
                loader.LoadJar(jar);
                if (loader.MidletClassName == null)
                {
                    errMsg = "No MIDlet-1";
                }
                else
                {
                    var task = Task.Run(() =>
                    {
                        var thread = new JvmThread(loader, "main");
                        thread.MaxInstructions = 50_000_000;
                        var cls = loader.LoadClass(loader.MidletClassName);
                        loader.InitializeClass(cls, thread);
                        var midlet = new JavaObject(cls);
                        host.MidletObject = midlet;

                        var init = cls.FindMethod("<init>", "()V");
                        if (init != null) thread.Invoke(init, new[] { JValue.OfRef(midlet) });

                        var startApp = cls.FindMethod("startApp", "()V");
                        if (startApp != null) thread.Invoke(startApp, new[] { JValue.OfRef(midlet) });

                        int[] keySequence = {
                            MidletHost.KEY_FIRE, MidletHost.KEY_FIRE, MidletHost.KEY_FIRE,
                            MidletHost.KEY_LEFT, MidletHost.KEY_FIRE, MidletHost.KEY_RIGHT,
                            MidletHost.KEY_FIRE, MidletHost.KEY_DOWN, MidletHost.KEY_FIRE,
                            MidletHost.KEY_UP, MidletHost.KEY_FIRE, MidletHost.KEY_LEFT,
                            MidletHost.KEY_FIRE, MidletHost.KEY_RIGHT, MidletHost.KEY_FIRE,
                            MidletHost.KEY_FIRE, MidletHost.KEY_NUM5, MidletHost.KEY_FIRE,
                            MidletHost.KEY_SOFT_LEFT, MidletHost.KEY_FIRE,
                        };

                        for (int f = 0; f < 600; f++)
                        {
                            if (host.Destroyed) break;
                            if (thread.InstructionCount > thread.MaxInstructions) break;

                            if (f % 15 == 0 && f / 15 < keySequence.Length)
                            {
                                int kc = keySequence[f / 15];
                                try
                                {
                                    host.SendKeyEvent(thread, kc, true);
                                    host.DoPaint(thread);
                                    host.SendKeyEvent(thread, kc, false);
                                }
                                catch (Exception ex) { throw new Exception($"key/paint frame {f}: {ex.Message}"); }
                            }

                            try { host.DoPaint(thread); }
                            catch (Exception ex) { throw new Exception($"paint frame {f}: {ex.Message}"); }

                            Thread.Sleep(33);
                        }
                    });

                    if (task.Wait(TimeSpan.FromSeconds(30)))
                    {
                        if (task.IsFaulted) throw task.Exception!.InnerException!;
                        ok = true;
                    }
                    else
                    {
                        errMsg = "Timeout (30s)";
                    }
                }
            }
            catch (Exception ex)
            {
                errMsg = ex.InnerException?.Message ?? ex.Message;
            }

            foreach (var c in loader.MissingClasses)
                allMissingClasses[c] = allMissingClasses.GetValueOrDefault(c) + 1;
            foreach (var m in loader.MissingMethods)
                allMissingMethods[m] = allMissingMethods.GetValueOrDefault(m) + 1;

            if (ok)
            {
                pass++;
                int mc = loader.MissingClasses.Count, mm = loader.MissingMethods.Count;
                Console.WriteLine(mc + mm > 0
                    ? $"OK (missing: {mc} classes, {mm} methods)"
                    : "OK");
            }
            else
            {
                fail++;
                failures.Add((Path.GetFileName(jar), errMsg));
                Console.WriteLine($"FAIL: {errMsg}");
            }
        }

        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"Results: {pass} passed, {fail} failed, {jars.Length} total");

        if (failures.Count > 0)
        {
            Console.WriteLine($"\nFailures:");
            foreach (var (name, err) in failures)
                Console.WriteLine($"  {name}: {err}");
        }

        if (allMissingClasses.Count > 0)
        {
            Console.WriteLine($"\nTop missing classes (across all JARs):");
            foreach (var (cls, count) in allMissingClasses.OrderByDescending(x => x.Value).Take(20))
                Console.WriteLine($"  [{count,3}x] {cls}");
        }
        if (allMissingMethods.Count > 0)
        {
            Console.WriteLine($"\nTop missing methods (across all JARs):");
            foreach (var (method, count) in allMissingMethods.OrderByDescending(x => x.Value).Take(30))
                Console.WriteLine($"  [{count,3}x] {method}");
        }
    }

    static void PrintReport(JvmClassLoader loader, string jarPath)
    {
        if (loader.MissingClasses.Count > 0)
        {
            Console.Error.WriteLine($"  Missing classes:");
            foreach (var c in loader.MissingClasses) Console.Error.WriteLine($"    {c}");
        }
        if (loader.MissingMethods.Count > 0)
        {
            Console.Error.WriteLine($"  Missing methods:");
            foreach (var m in loader.MissingMethods) Console.Error.WriteLine($"    {m}");
        }
    }
}
