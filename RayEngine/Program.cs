using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

// ── --play <scenefile> mode ───────────────────────────────────────────────────
if (args.Length >= 2 && args[0] == "--play")
{
    var player = new RayEngine.GamePlayer(args[1]);
    player.Run();
    return;
}

// ── Editor mode ───────────────────────────────────────────────────────────────
SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_MSAA_4X_HINT);
InitWindow(1440, 900, "RayEngine");
SetTargetFPS(60);
SetExitKey(KeyboardKey.KEY_NULL);

int mon = GetCurrentMonitor();
int sw  = Math.Min(1440, GetMonitorWidth(mon));
int sh  = Math.Min(900,  GetMonitorHeight(mon));
if (sw != 1440 || sh != 900) SetWindowSize(sw, sh);

var editor = new RayEngine.Editor();
editor.Initialize(sw, sh);

while (!WindowShouldClose())
{
    editor.Update(GetFrameTime());

    BeginDrawing();
    ClearBackground(new Color { r = 22, g = 23, b = 28, a = 255 });
    editor.Render();
    EndDrawing();
}

editor.Shutdown();
CloseWindow();
