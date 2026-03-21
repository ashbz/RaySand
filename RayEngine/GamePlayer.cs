using System.Numerics;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace RayEngine;

/// <summary>
/// Standalone game runner. Loaded from a temp JSON scene file when the
/// editor's Play button launches a new process with --play &lt;file&gt;.
///
/// Hot-reload: the editor writes an updated scene to the same temp file
/// whenever Ctrl+S is pressed. This class watches the file and reloads
/// the scene + scripts transparently without closing the window.
/// </summary>
public class GamePlayer
{
    readonly string         _scenePath;
    Scene                   _scene;
    ScriptRuntime           _runtime  = new();
    readonly ParticleSystem _particles = new();

    // ── Hot-reload via FileSystemWatcher ─────────────────────────────────────
    FileSystemWatcher? _watcher;
    volatile bool      _pendingReload;
    DateTime           _reloadAt = DateTime.MinValue;

    // Flash "HOT RELOAD" on screen for a moment
    float _reloadFlash;

    public GamePlayer(string scenePath)
    {
        _scenePath = scenePath;
        _scene     = Scene.LoadFrom(scenePath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    public void Run()
    {
        SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
        InitWindow(800, 600, $"{_scene.Name}  —  RayEngine Play");
        SetTargetFPS(60);
        SetExitKey(KeyboardKey.KEY_NULL);

        _runtime.Initialize(_scene.Root);
        _runtime.Start();

        StartWatcher();

        while (!WindowShouldClose() && !IsKeyPressed(KeyboardKey.KEY_ESCAPE))
        {
            float dt = GetFrameTime();

            // Apply pending hot-reload (debounced)
            if (_pendingReload && DateTime.UtcNow >= _reloadAt)
            {
                _pendingReload = false;
                DoHotReload();
            }

            _runtime.Update(dt);
            _particles.Update(_scene.Root, dt);

            if (_reloadFlash > 0f) _reloadFlash -= dt;

            BeginDrawing();
            ClearBackground(new Color { r = 28, g = 30, b = 36, a = 255 });
            DrawScene();
            DrawHUD();
            EndDrawing();
        }

        _watcher?.Dispose();
        CloseWindow();
    }

    // ─────────────────────────────────────────────────────────────────────────
    void StartWatcher()
    {
        string dir  = Path.GetDirectoryName(_scenePath) ?? ".";
        string file = Path.GetFileName(_scenePath);

        try
        {
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
            {
                // Debounce: wait 200 ms after the last event before reloading
                // (editors may write the file in multiple chunks)
                _reloadAt      = DateTime.UtcNow.AddMilliseconds(200);
                _pendingReload = true;
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Watch] Could not watch scene file: {ex.Message}");
        }
    }

    void DoHotReload()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var newScene = Scene.LoadFrom(_scenePath);
                _scene = newScene;

                _runtime = new ScriptRuntime();
                _runtime.Initialize(_scene.Root);
                _runtime.Start();

                _particles.Reset();
                _reloadFlash = 1.2f;
                Console.WriteLine("[HotReload] Scene reloaded OK");
                return;
            }
            catch (IOException)
            {
                // File still locked by the writer — retry after a short wait
                System.Threading.Thread.Sleep(80);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HotReload] Failed: {ex.Message}");
                return;
            }
        }
        Console.WriteLine("[HotReload] File remained locked — skipped");
    }

    // ─────────────────────────────────────────────────────────────────────────
    void DrawScene()
    {
        foreach (var node in _scene.Root.Descendants().Skip(1))
        {
            if (!node.Visible) continue;
            DrawNode(node);
        }

        _particles.Draw(_scene.Root);

        // Script errors overlay
        int ey = 30;
        foreach (var err in _runtime.Errors)
        {
            DrawText($"LUA ERR: {err}", 8, ey, 10, new Color { r = 255, g = 80, b = 80, a = 220 });
            ey += 14;
        }
    }

    void DrawHUD()
    {
        DrawText("ESC — exit play", 8, 8, 10, new Color { r = 120, g = 120, b = 120, a = 180 });

        // Hot-reload flash
        if (_reloadFlash > 0f)
        {
            byte alpha = (byte)Math.Clamp((int)(_reloadFlash * 255f), 0, 255);
            DrawText("HOT RELOAD", GetScreenWidth() / 2 - 50, 12, 18,
                new Color { r = 100, g = 255, b = 120, a = alpha });
        }
    }

    void DrawNode(SceneNode node)
    {
        switch (node.Type)
        {
            case NodeType.Sprite2D:
            {
                var c   = node.Color;
                var col = new Color
                {
                    r = (byte)(c.X * 255),
                    g = (byte)(c.Y * 255),
                    b = (byte)(c.Z * 255),
                    a = (byte)(c.W * 255),
                };
                DrawRectanglePro(
                    new Rectangle(node.Position.X, node.Position.Y, node.Size.X, node.Size.Y),
                    new Vector2(node.Size.X / 2f, node.Size.Y / 2f),
                    node.Rotation.Z,
                    col);
                break;
            }

            case NodeType.Label2D:
            {
                var c   = node.Color;
                var col = new Color
                {
                    r = (byte)(c.X * 255),
                    g = (byte)(c.Y * 255),
                    b = (byte)(c.Z * 255),
                    a = (byte)(c.W * 255),
                };
                int fs = Math.Clamp((int)node.FontSize, 6, 120);
                DrawText(node.LabelText, (int)node.Position.X, (int)node.Position.Y, fs, col);
                break;
            }

            case NodeType.PointLight:
            {
                var c   = node.LightColor;
                var col = new Color
                {
                    r = (byte)(c.X * 255),
                    g = (byte)(c.Y * 255),
                    b = (byte)(c.Z * 100),
                    a = 60,
                };
                DrawCircleLines((int)node.Position.X, (int)node.Position.Y,
                    node.LightRange * 4f, col);
                break;
            }

            // ParticleEmitter: drawn by _particles.Draw, nothing else to do here
        }
    }
}
