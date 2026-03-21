global using System.Numerics;
global using Raylib_CsLo;
global using static Raylib_CsLo.Raylib;
global using static Raylib_CsLo.RlGl;
global using static Raylib_CsLo.KeyboardKey;
global using static Raylib_CsLo.MouseButton;
global using static Raylib_CsLo.ConfigFlags;

// ── PSX vertex shader ─────────────────────────────────────────────────────────
// Snaps clip-space vertices to a reduced-precision grid (PSX jitter) and uses
// noperspective UV interpolation for authentic affine texture warping.
const string PSX_VERT = @"#version 330
layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec2 vertexTexCoord;
layout(location = 2) in vec3 vertexNormal;
layout(location = 3) in vec4 vertexColor;
uniform mat4 mvp;
noperspective out vec2 fragTexCoord;
out vec4 fragColor;
out float fragDist;
void main() {
    vec4 clip = mvp * vec4(vertexPosition, 1.0);
    // Snap to low-precision grid → PSX vertex wobble
    float snap = 120.0;
    clip.xy = round(clip.xy / clip.w * snap) / snap * clip.w;
    fragTexCoord = vertexTexCoord;
    fragColor    = vertexColor;
    fragDist     = clip.w;
    gl_Position  = clip;
}";

// ── PSX fragment shader ───────────────────────────────────────────────────────
// 4×4 Bayer dithering + 5-bit colour quantisation (PSX 15-bit colour), with
// exponential distance fog matching Quake's dark dungeon atmosphere.
const string PSX_FRAG = @"#version 330
noperspective in vec2 fragTexCoord;
in vec4 fragColor;
in float fragDist;
uniform sampler2D texture0;
uniform vec4  colDiffuse;
uniform float uvScale;
uniform float fogNear;
uniform float fogFar;
uniform vec3  fogColor;
out vec4 finalColor;
void main() {
    vec4 tex = texture(texture0, fragTexCoord * uvScale) * colDiffuse * fragColor;
    // 4x4 Bayer ordered dither
    float bayer[16];
    bayer[0]  =  0.0; bayer[1]  =  8.0; bayer[2]  =  2.0; bayer[3]  = 10.0;
    bayer[4]  = 12.0; bayer[5]  =  4.0; bayer[6]  = 14.0; bayer[7]  =  6.0;
    bayer[8]  =  3.0; bayer[9]  = 11.0; bayer[10] =  1.0; bayer[11] =  9.0;
    bayer[12] = 15.0; bayer[13] =  7.0; bayer[14] = 13.0; bayer[15] =  5.0;
    int   bx     = int(mod(gl_FragCoord.x, 4.0));
    int   by     = int(mod(gl_FragCoord.y, 4.0));
    float dither = bayer[by * 4 + bx] / 16.0;
    // Quantise to 5-bit per channel (PSX 15-bit colour depth)
    float levels = 31.0;
    vec3 col = floor(tex.rgb * levels + dither * 0.55) / levels;
    // Distance fog
    float fogT = clamp((fragDist - fogNear) / (fogFar - fogNear), 0.0, 1.0);
    col = mix(col, fogColor, fogT * fogT);
    finalColor = vec4(col, tex.a);
}";

// ── Post-processing fragment shader ───────────────────────────────────────────
// Film grain, heavy vignette, and Quake warm colour grade.
const string POST_FRAG = @"#version 330
in  vec2 fragTexCoord;
in  vec4 fragColor;
uniform sampler2D texture0;
uniform float     time;
out vec4 finalColor;
float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}
void main() {
    vec2 uv  = fragTexCoord;
    vec3 col = texture(texture0, uv).rgb;
    // Film grain
    col += (rand(uv + fract(time * 0.07)) - 0.5) * 0.055;
    // Heavy vignette
    vec2  c   = uv - 0.5;
    float vig = 1.0 - dot(c, c) * 3.2;
    col *= clamp(vig, 0.0, 1.0);
    // Quake warm tone: desaturate 15%, push orange
    float lum = dot(col, vec3(0.299, 0.587, 0.114));
    col  = mix(col, vec3(lum), 0.15);
    col.r *= 1.08; col.g *= 0.96; col.b *= 0.82;
    finalColor = vec4(clamp(col, 0.0, 1.0), 1.0) * fragColor;
}";

// ── Map ───────────────────────────────────────────────────────────────────────
// map[row, col]  =  map[world-Z, world-X]
int[,] map =
{
    { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 2, 2, 2, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 2, 0, 2, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 3, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 5, 5, 5, 5, 5, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 5, 0, 0, 0, 5, 0, 0, 0, 0, 0, 1 },
    { 1, 0, 0, 0, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 1 },
    { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
};
const int   MROWS = 16, MCOLS = 16;
const float WALL_H = 4f;   // room height in world units


// ── 3D Camera ─────────────────────────────────────────────────────────────────
float    camYaw   = 0f;          // radians — horizontal look angle
float    camPitch = 0f;          // radians — vertical look angle
Vector3  camPos   = new Vector3(2.5f, 0.5f, 2.5f);
Camera3D camera   = new Camera3D
{
    up         = new Vector3(0, 1, 0),
    fovy       = 75f,
    projection = (int)CameraProjection.CAMERA_PERSPECTIVE
};

// ── Screen — set to 1920×1080, clamped to actual monitor after InitWindow ─────
int SW = 1920, SH = 1080;

// ── Player ────────────────────────────────────────────────────────────────────
const float MOVE_SPEED = 7.0f;
const float LOOK_SENS  = 0.0028f;
float hpF         = 100f;
float shootCd     = 0f;
float hitFlash    = 0f;
float damageFlash = 0f;

// ── Game state ────────────────────────────────────────────────────────────────
bool inMenu       = false;
bool soundEnabled = false;
bool gameOver     = false;
bool gameWin      = false;

// ── Lighting ──────────────────────────────────────────────────────────────────
float torchFlicker = 1f;
float totalTime    = 0f;

// ── Particle system ───────────────────────────────────────────────────────────
// Each particle: (world position, velocity, remaining life, max life, colour)
var ptcls = new List<(Vector3 pos, Vector3 vel, float life, float maxLife, Color col)>();

// ── 3D Snow particles (world-space, rendered inside BeginMode3D) ─────────────
const int NUM_SNOW = 150;
float[] snowX   = new float[NUM_SNOW];
float[] snowY   = new float[NUM_SNOW];
float[] snowZ   = new float[NUM_SNOW];
float[] snowSpd = new float[NUM_SNOW];
var     snowRng = new Random(404);
for (int i = 0; i < NUM_SNOW; i++) ResetSnow(i, true);

// ── Init ──────────────────────────────────────────────────────────────────────
SetConfigFlags(FLAG_VSYNC_HINT | FLAG_WINDOW_RESIZABLE);
InitWindow(SW, SH, "Dungeon3D — PSX");

// Clamp window to actual monitor — prevents GLFW_NOT_INITIALIZED on
// systems where 1920×1080 exceeds the usable display (DPI scaling, laptops…)
{
    int mon = GetCurrentMonitor();
    SW = Math.Min(SW, GetMonitorWidth(mon));
    SH = Math.Min(SH, GetMonitorHeight(mon));
    if (SW != GetScreenWidth() || SH != GetScreenHeight())
        SetWindowSize(SW, SH);
}

SetExitKey(KEY_NULL);   // ESC opens menu only; window X button still exits
SetTargetFPS(60);
InitAudioDevice();
DisableCursor();

// ── Generate sounds ───────────────────────────────────────────────────────────
const int ETYPES = 5;
var eSprPx    = Enumerable.Range(0, ETYPES).Select(i => ProceduralGen.GenerateEnemySprite(i)).ToArray();
var eGrowl    = Enumerable.Range(0, ETYPES).Select(i => ProceduralGen.MakeEnemyGrowl(i)).ToArray();
var steps     = Enumerable.Range(0, 4).Select(i => ProceduralGen.MakeFootstep(i)).ToArray();
var ambient   = ProceduralGen.MakeAmbient();
var shotSnd   = ProceduralGen.MakeShot();
var hitSnd    = ProceduralGen.MakeHit();

// ── Wall / floor / ceiling textures (load from textures/ or generate) ─────────
var wallModel = new Model[6]; // index 0 unused
for (int t = 1; t <= 5; t++)
    wallModel[t] = MakeCubeModel(
        LoadOrGen($"textures/wall{t}.png", () => ProceduralGen.MakeQuakeTex(t, ProceduralGen.TEX)));

var floorModel = MakePlaneModel(
    LoadOrGen("textures/floor.png", () => ProceduralGen.MakeQuakeFloor(ProceduralGen.TEX)),
    MCOLS, MROWS);
var ceilModel  = MakePlaneModel(
    LoadOrGen("textures/ceil.png",  () => ProceduralGen.MakeQuakeCeil(ProceduralGen.TEX)),
    MCOLS, MROWS);

// ── Enemy GPU textures + cube models ─────────────────────────────────────────
var enemyTex   = new Texture[ETYPES];
var enemyModel = new Model[ETYPES];
for (int i = 0; i < ETYPES; i++)
{
    enemyTex[i]   = ProceduralGen.ToTexture(eSprPx[i], ProceduralGen.TEX);
    enemyModel[i] = MakeCubeModel(enemyTex[i]);
}

// ── Spawn enemies ─────────────────────────────────────────────────────────────
var enemies  = new List<Enemy>();
var spawnRng = new Random(31337);
for (int i = 0; i < 2; i++)
{
    double ex, ey; int tries = 0;
    do
    {
        ex = 1.5 + spawnRng.NextDouble() * (MCOLS - 3);
        ey = 1.5 + spawnRng.NextDouble() * (MROWS - 3);
        tries++;
    }
    while (tries < 200 &&
           (map[(int)ey, (int)ex] != 0 ||
            Math.Sqrt((ex - camPos.X) * (ex - camPos.X) + (ey - camPos.Z) * (ey - camPos.Z)) < 3.0));

    if (tries < 200)
        enemies.Add(new Enemy(ex, ey, eSprPx[i % ETYPES], eGrowl[i % ETYPES], i % ETYPES));
}

// ── Render texture + shaders ──────────────────────────────────────────────────
var renderTex  = LoadRenderTexture(SW, SH);
var postShader = LoadShaderFromMemory(null, POST_FRAG);
int timeLoc    = GetShaderLocation(postShader, "time");

var psxShader  = LoadShaderFromMemory(PSX_VERT, PSX_FRAG);
int uvScaleLoc = GetShaderLocation(psxShader, "uvScale");
int fogNearLoc = GetShaderLocation(psxShader, "fogNear");
int fogFarLoc  = GetShaderLocation(psxShader, "fogFar");
int fogColLoc  = GetShaderLocation(psxShader, "fogColor");
unsafe
{
    float near = 4.0f, far = 20.0f;
    var   fog  = new Vector3(0.055f, 0.042f, 0.028f);
    SetShaderValue<float>(psxShader, fogNearLoc, &near, ShaderUniformDataType.SHADER_UNIFORM_FLOAT);
    SetShaderValue<float>(psxShader, fogFarLoc,  &far,  ShaderUniformDataType.SHADER_UNIFORM_FLOAT);
    SetShaderValue<Vector3>(psxShader, fogColLoc, &fog, ShaderUniformDataType.SHADER_UNIFORM_VEC3);
}

// ── Timers ────────────────────────────────────────────────────────────────────
float stepTimer = 0f;
int   stepIdx   = 0;

// ══════════════════════════════════════════════════════════════════════════════
// Game loop
// ══════════════════════════════════════════════════════════════════════════════
while (!WindowShouldClose())
{
    double dt  = GetFrameTime();
    totalTime += (float)dt;

    // ── Ambient sound loop ─────────────────────────────────────────────────
    if (soundEnabled && !IsSoundPlaying(ambient)) PlaySound(ambient);

    // ── Menu toggle ────────────────────────────────────────────────────────
    if (IsKeyPressed(KEY_ESCAPE) || IsKeyPressed(KEY_M))
    {
        inMenu = !inMenu;
        if (inMenu) EnableCursor(); else DisableCursor();
    }

    if (!inMenu && !gameOver && !gameWin)
    {
        // ── Mouse look ────────────────────────────────────────────────────
        var md = GetMouseDelta();
        camYaw   += md.X * LOOK_SENS;
        camPitch -= md.Y * LOOK_SENS;
        camPitch  = Math.Clamp(camPitch, -1.3f, 1.3f);

        // ── Movement ──────────────────────────────────────────────────────
        float cy = MathF.Cos(camYaw), sy = MathF.Sin(camYaw);
        var fwd   = new Vector3(cy, 0, sy);
        var right = new Vector3(-sy, 0, cy);

        bool moving = false;
        if (IsKeyDown(KEY_W)) { MovePlayer(fwd   * (float)(MOVE_SPEED * dt)); moving = true; }
        if (IsKeyDown(KEY_S)) { MovePlayer(-fwd  * (float)(MOVE_SPEED * dt)); moving = true; }
        if (IsKeyDown(KEY_A)) { MovePlayer(-right * (float)(MOVE_SPEED * dt)); moving = true; }
        if (IsKeyDown(KEY_D)) { MovePlayer(right  * (float)(MOVE_SPEED * dt)); moving = true; }

        if (moving)
        {
            stepTimer -= (float)dt;
            if (stepTimer <= 0f)
            {
                if (soundEnabled) PlaySound(steps[stepIdx++ % steps.Length]);
                stepTimer = 0.38f;
            }
        }

        // ── Shoot ─────────────────────────────────────────────────────────
        shootCd -= (float)dt;
        if ((IsKeyPressed(KEY_SPACE) || IsMouseButtonPressed(MOUSE_BUTTON_LEFT)) && shootCd <= 0f)
        {
            if (soundEnabled) PlaySound(shotSnd);
            shootCd = 0.4f;
            TryShoot3D();
        }

        // ── Enemy update + damage ──────────────────────────────────────────
        foreach (var e in enemies)
        {
            bool growl = e.Update(camPos.X, camPos.Z, dt, map);
            if (growl && soundEnabled) PlaySound(e.Growl);
            if (e.Alive && e.DistSq(camPos.X, camPos.Z) < 0.55 * 0.55)
            {
                hpF -= 28f * (float)dt;
                damageFlash = 0.6f;
            }
        }
        hpF = Math.Clamp(hpF, 0f, 100f);
        hitFlash    = Math.Max(0f, hitFlash    - (float)dt * 3f);
        damageFlash = Math.Max(0f, damageFlash - (float)dt * 3f);

        if (hpF <= 0f) gameOver = true;
        if (enemies.Count > 0 && enemies.All(e => !e.Alive)) gameWin = true;
    }

    // ── Torch flicker ─────────────────────────────────────────────────────
    torchFlicker = 0.82f
        + 0.11f * MathF.Sin(totalTime * 7.4f)
        + 0.07f * MathF.Sin(totalTime * 19.3f + 1.2f);

    // ── Rebuild camera from yaw/pitch ─────────────────────────────────────
    float cosPitch = MathF.Cos(camPitch);
    float camEyeY  = WALL_H * 0.35f + MathF.Sin(totalTime * 1.1f) * 0.04f; // ~1.4 units eye height
    camPos.Y = camEyeY;

    camera.position = camPos;
    camera.target   = camPos + new Vector3(
        MathF.Cos(camYaw) * cosPitch,
        MathF.Sin(camPitch),
        MathF.Sin(camYaw) * cosPitch);
    camera.fovy = 90f;

    // ── Particle update ───────────────────────────────────────────────────
    for (int i = ptcls.Count - 1; i >= 0; i--)
    {
        var (pos, vel, life, maxLife, col) = ptcls[i];
        life -= (float)dt;
        if (life <= 0f) { ptcls.RemoveAt(i); continue; }
        vel.Y -= 5.5f * (float)dt;          // gravity
        pos   += vel  * (float)dt;
        ptcls[i] = (pos, vel, life, maxLife, col);
    }

    // ── 3D Snow update ────────────────────────────────────────────────────
    float windOfs = MathF.Sin(totalTime * 0.3f) * 0.4f;
    for (int i = 0; i < NUM_SNOW; i++)
    {
        snowY[i] -= snowSpd[i] * (float)dt;
        snowX[i] += windOfs * (float)dt;
        float dsx = snowX[i] - camPos.X, dsz = snowZ[i] - camPos.Z;
        if (snowY[i] < 0f || dsx * dsx + dsz * dsz > 80f)
            ResetSnow(i, false);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Render 3D scene → render texture
    // ════════════════════════════════════════════════════════════════════════
    BeginTextureMode(renderTex);
    ClearBackground(new Color { r = 14, g = 10, b = 7, a = 255 }); // near-black Quake sky
    BeginMode3D(camera);
    BeginShaderMode(psxShader);

    // ── Floor: textured plane, UV tiles 1× per world unit ─────────────────
    unsafe { float us = (float)MCOLS; SetShaderValue<float>(psxShader, uvScaleLoc, &us, ShaderUniformDataType.SHADER_UNIFORM_FLOAT); }
    byte fb = (byte)(185 * torchFlicker);
    DrawModel(floorModel, new Vector3(MCOLS / 2f, 0f, MROWS / 2f), 1f,
              new Color { r = fb, g = (byte)(fb * 0.94f), b = (byte)(fb * 0.80f), a = 255 });

    // ── Ceiling ────────────────────────────────────────────────────────────
    rlDisableBackfaceCulling();
    byte cb = (byte)(120 * torchFlicker);
    DrawModel(ceilModel, new Vector3(MCOLS / 2f, WALL_H, MROWS / 2f), 1f,
              new Color { r = cb, g = (byte)(cb * 0.90f), b = (byte)(cb * 0.78f), a = 255 });
    rlEnableBackfaceCulling();

    // ── Walls: tall textured models with distance torch attenuation ───────
    unsafe { float us = 1f; SetShaderValue<float>(psxShader, uvScaleLoc, &us, ShaderUniformDataType.SHADER_UNIFORM_FLOAT); }
    for (int row = 0; row < MROWS; row++)
    for (int col = 0; col < MCOLS; col++)
    {
        int wtype = map[row, col];
        if (wtype == 0) continue;
        float wdx    = col + 0.5f - camPos.X, wdz = row + 0.5f - camPos.Z;
        float bright = Math.Clamp((14f - MathF.Sqrt(wdx * wdx + wdz * wdz)) / 14f, 0f, 1f) * torchFlicker;
        bright = 0.08f + bright * 0.92f;
        var wtint = new Color
        {
            r = (byte)(255 * bright),
            g = (byte)(240 * bright),
            b = (byte)(195 * bright),
            a = 255
        };
        DrawModelEx(wallModel[wtype],
                    new Vector3(col + 0.5f, WALL_H / 2f, row + 0.5f),
                    Vector3.UnitY, 0f,
                    new Vector3(1f, WALL_H, 1f), wtint);
    }

    // ── Enemies ───────────────────────────────────────────────────────────
    foreach (var e in enemies.Where(e => e.Alive))
    {
        float bob  = MathF.Sin(totalTime * 1.4f + e.BobPhase) * 0.12f;
        float edx  = (float)e.X - camPos.X, edz = (float)e.Y - camPos.Z;
        float ebr  = Math.Clamp((14f - MathF.Sqrt(edx * edx + edz * edz)) / 14f, 0f, 1f) * torchFlicker;
        ebr = 0.08f + ebr * 0.92f;
        var etint = new Color { r = (byte)(255 * ebr), g = (byte)(225 * ebr), b = (byte)(175 * ebr), a = 255 };
        DrawModelEx(enemyModel[e.TypeIndex],
                    new Vector3((float)e.X, 0.60f + bob, (float)e.Y),
                    Vector3.UnitY, e.RotAngle,
                    new Vector3(1.2f, 1.2f, 1.2f), etint);
    }

    // ── Particle explosions (rendered inside PSX shader → authentic dither) ─
    foreach (var (pos, _, life, maxLife, col) in ptcls)
    {
        float t  = life / maxLife;
        float sz = 0.14f * t + 0.02f;
        DrawCube(pos, sz, sz, sz,
                 new Color { r = col.r, g = col.g, b = col.b, a = (byte)(t * 230) });
    }

    EndShaderMode();

    // ── Floating dust motes (no PSX shader — raw tiny cubes) ─────────────
    for (int i = 0; i < NUM_SNOW; i++)
        DrawCube(new Vector3(snowX[i], snowY[i], snowZ[i]), 0.022f, 0.022f, 0.022f,
                 new Color { r = 148, g = 132, b = 108, a = 70 });

    EndMode3D();
    EndTextureMode();

    // ════════════════════════════════════════════════════════════════════════
    // Blit render texture through post-processing shader + 2D HUD
    // ════════════════════════════════════════════════════════════════════════
    BeginDrawing();
    ClearBackground(BLACK);

    unsafe
    {
        float t = totalTime;
        SetShaderValue<float>(postShader, timeLoc, &t, ShaderUniformDataType.SHADER_UNIFORM_FLOAT);
    }
    BeginShaderMode(postShader);
    // Negative height flips render-texture Y (Raylib convention)
    DrawTextureRec(renderTex.texture,
                   new Rectangle(0, 0, SW, -SH),
                   Vector2.Zero, WHITE);
    EndShaderMode();

    // ── Screen-space flash ────────────────────────────────────────────────
    if (damageFlash > 0f)
        DrawRectangle(0, 0, SW, SH, new Color { r = 210, g = 0, b = 0, a = (byte)(damageFlash * 85) });
    if (hitFlash > 0f)
        DrawRectangle(0, 0, SW, SH, new Color { r = 0, g = 180, b = 50, a = (byte)(hitFlash * 75) });

    // ── HUD ───────────────────────────────────────────────────────────────
    const int MM = 10, MMX = 16, MMY = 16;
    for (int r = 0; r < MROWS; r++)
    for (int c = 0; c < MCOLS; c++)
    {
        var mc = map[r, c] > 0
            ? new Color { r = 130, g = 130, b = 130, a = 190 }
            : new Color { r = 10,  g = 10,  b = 16,  a = 160 };
        DrawRectangle(MMX + c * MM, MMY + r * MM, MM - 1, MM - 1, mc);
    }
    foreach (var e in enemies.Where(en => en.Alive))
        DrawRectangle(MMX + (int)(e.X * MM) - 1, MMY + (int)(e.Y * MM) - 1, 3, 3,
                      new Color { r = 220, g = 50, b = 50, a = 220 });
    {
        int ppx = MMX + (int)(camPos.X * MM), ppz = MMY + (int)(camPos.Z * MM);
        DrawRectangle(ppx - 2, ppz - 2, 4, 4, YELLOW);
        // Player arrow based on yaw
        DrawLine(ppx, ppz,
                 ppx + (int)(MathF.Cos(camYaw) * 2 * MM),
                 ppz + (int)(MathF.Sin(camYaw) * 2 * MM), YELLOW);
    }

    // HP bar
    int hpPx = (int)(hpF * 3.5f);
    DrawRectangle(16, SH - 62, 372, 34, new Color { r = 50, g = 0,  b = 0,  a = 210 });
    DrawRectangle(18, SH - 59, hpPx, 28, new Color { r = 200, g = 30, b = 30, a = 230 });
    DrawText($"HP  {(int)hpF}", 396, SH - 62, 30, new Color { r = 255, g = 80, b = 80, a = 255 });
    DrawText($"Enemies: {enemies.Count(e => e.Alive)}", SW - 290, SH - 62, 30,
             new Color { r = 180, g = 180, b = 180, a = 190 });

    // Crosshair (turns red while cooling down)
    var xhairCol = shootCd > 0.2f
        ? new Color { r = 255, g = 60, b = 60, a = 210 }
        : new Color { r = 200, g = 200, b = 200, a = 160 };
    DrawLine(SW / 2 - 22, SH / 2,      SW / 2 + 22, SH / 2,      xhairCol);
    DrawLine(SW / 2,      SH / 2 - 22, SW / 2,      SH / 2 + 22, xhairCol);
    DrawCircleLines(SW / 2, SH / 2, 16, new Color { r = xhairCol.r, g = xhairCol.g, b = xhairCol.b, a = 60 });

    if (gameOver) DrawCenteredText("GAME OVER", SH / 2 - 50, 80, new Color { r = 230, g = 30, b = 30, a = 255 });
    if (gameWin)  DrawCenteredText("YOU WIN!",  SH / 2 - 50, 80, new Color { r = 60,  g = 230, b = 80, a = 255 });

    DrawText("WASD move  MOUSE look  SPACE/LMB shoot  M/ESC menu", 16, SH - 80, 20,
             new Color { r = 120, g = 120, b = 120, a = 150 });
    DrawFPS(SW - 140, 16);

    // ── Menu overlay ──────────────────────────────────────────────────────
    if (inMenu) DrawMenu();

    EndDrawing();
}

CloseAudioDevice();
CloseWindow();

// ══════════════════════════════════════════════════════════════════════════════
// Local helpers
// ══════════════════════════════════════════════════════════════════════════════

void MovePlayer(Vector3 delta)
{
    // Slide collision on X and Z independently
    if (map[(int)camPos.Z, Math.Clamp((int)(camPos.X + delta.X), 0, MCOLS - 1)] == 0)
        camPos.X = Math.Clamp(camPos.X + delta.X, 0.3f, MCOLS - 1.3f);
    if (map[Math.Clamp((int)(camPos.Z + delta.Z), 0, MROWS - 1), (int)camPos.X] == 0)
        camPos.Z = Math.Clamp(camPos.Z + delta.Z, 0.3f, MROWS - 1.3f);
}

void TryShoot3D()
{
    // Forward direction from yaw/pitch
    float cp  = MathF.Cos(camPitch);
    var   fwd = Vector3.Normalize(new Vector3(
        MathF.Cos(camYaw) * cp,
        MathF.Sin(camPitch),
        MathF.Sin(camYaw) * cp));

    Enemy? closest = null;
    float  bestT   = float.MaxValue;
    foreach (var e in enemies.Where(e => e.Alive))
    {
        float? t = RaySphere(camPos, fwd,
                             new Vector3((float)e.X, 0.6f, (float)e.Y), 0.85f);
        if (t.HasValue && t.Value > 0f && t.Value < bestT)
        {
            closest = e; bestT = t.Value;
        }
    }
    if (closest != null)
    {
        bool wasAlive = closest.Alive;
        closest.Hit();
        if (wasAlive && !closest.Alive)
            SpawnExplosion(new Vector3((float)closest.X, 0.60f, (float)closest.Y));
        if (soundEnabled) PlaySound(hitSnd);
        hitFlash = 1f;
    }
}

float? RaySphere(Vector3 origin, Vector3 dir, Vector3 centre, float radius)
{
    var   oc   = origin - centre;
    float a    = Vector3.Dot(dir, dir);
    float b    = 2f * Vector3.Dot(oc, dir);
    float c    = Vector3.Dot(oc, oc) - radius * radius;
    float disc = b * b - 4f * a * c;
    if (disc < 0f) return null;
    return (-b - MathF.Sqrt(disc)) / (2f * a);
}

unsafe Model MakeCubeModel(Texture tex)
{
    var mesh  = GenMeshCube(1f, 1f, 1f);
    var model = LoadModelFromMesh(mesh);
    model.materials[0].maps[0].texture = tex;
    return model;
}

unsafe Model MakePlaneModel(Texture tex, int w, int h)
{
    // Dense subdivisions so PSX vertex snap is visible on flat surfaces
    var mesh  = GenMeshPlane(w, h, w * 2, h * 2);
    var model = LoadModelFromMesh(mesh);
    model.materials[0].maps[0].texture = tex;
    return model;
}

Texture LoadOrGen(string relPath, Func<Texture> gen)
{
    if (File.Exists(relPath)) return LoadTexture(relPath);
    string full = Path.Combine(AppContext.BaseDirectory, relPath);
    if (File.Exists(full)) return LoadTexture(full);
    return gen();
}

void ResetSnow(int i, bool randomY)
{
    const float R = 7f;
    snowX[i]   = camPos.X + snowRng.NextSingle() * 2 * R - R;
    snowZ[i]   = camPos.Z + snowRng.NextSingle() * 2 * R - R;
    snowY[i]   = randomY ? snowRng.NextSingle() * (WALL_H - 0.1f) : WALL_H - 0.05f;
    snowSpd[i] = 0.25f + snowRng.NextSingle() * 0.55f;
}

void DrawCenteredText(string text, int y, int fontSize, Color col)
{
    int w = MeasureText(text, fontSize);
    DrawText(text, (SW - w) / 2 + 2, y + 2, fontSize, new Color { r = 0, g = 0, b = 0, a = 110 });
    DrawText(text, (SW - w) / 2,     y,     fontSize, col);
}

void DrawMenu()
{
    DrawRectangle(0, 0, SW, SH, new Color { r = 0, g = 0, b = 0, a = 155 });
    const int PW = 500, PH = 390;
    int PX = (SW - PW) / 2, PY = (SH - PH) / 2;
    DrawRectangle(PX, PY, PW, PH, new Color { r = 10, g = 6, b = 22, a = 245 });
    DrawRectangleLines(PX,     PY,     PW,     PH,     new Color { r = 80,  g = 60, b = 140, a = 255 });
    DrawRectangleLines(PX + 1, PY + 1, PW - 2, PH - 2, new Color { r = 40,  g = 28, b = 70,  a = 200 });
    DrawCenteredText("PAUSED", PY + 30, 48, new Color { r = 200, g = 180, b = 255, a = 255 });

    var  mouse = GetMousePosition();
    bool click = IsMouseButtonPressed(MOUSE_BUTTON_LEFT);

    if (MenuButton("RESUME",                     PX + 60, PY + 116, PW - 120, 62, mouse, click)) { inMenu = false; DisableCursor(); }
    if (MenuButton($"SOUND:  {(soundEnabled?"ON":"OFF")}", PX + 60, PY + 196, PW - 120, 62, mouse, click))
    {
        soundEnabled = !soundEnabled;
        if (!soundEnabled) StopSound(ambient);
    }
    if (MenuButton("QUIT",                        PX + 60, PY + 276, PW - 120, 62, mouse, click))
        CloseWindow();
}

bool MenuButton(string label, int x, int y, int w, int h, Vector2 mouse, bool click)
{
    bool hover = mouse.X >= x && mouse.X <= x + w && mouse.Y >= y && mouse.Y <= y + h;
    DrawRectangle(x, y, w, h,
        hover ? new Color { r = 70, g = 52, b = 120, a = 255 }
              : new Color { r = 22, g = 15, b = 48,  a = 255 });
    DrawRectangleLines(x, y, w, h, new Color { r = 90, g = 68, b = 155, a = 200 });
    int fw = MeasureText(label, 28);
    DrawText(label, x + (w - fw) / 2, y + (h - 28) / 2, 28,
             hover ? WHITE : new Color { r = 175, g = 165, b = 220, a = 255 });
    return hover && click;
}

void SpawnExplosion(Vector3 centre)
{
    var rng = Random.Shared;
    for (int i = 0; i < 90; i++)
    {
        float theta = rng.NextSingle() * MathF.PI * 2f;
        float phi   = MathF.Acos(2f * rng.NextSingle() - 1f);
        float spd   = 2.5f + rng.NextSingle() * 7.0f;

        // Bias slightly upward so the burst looks like an impact
        var dir = Vector3.Normalize(new Vector3(
            MathF.Sin(phi) * MathF.Cos(theta),
            MathF.Abs(MathF.Sin(phi) * MathF.Sin(theta)) + 0.4f,
            MathF.Cos(phi)));

        float r2  = rng.NextSingle();
        Color col = r2 < 0.22f
            ? new Color { r = 255, g = 248, b = 200, a = 255 }   // hot white
            : r2 < 0.52f
                ? new Color { r = 255, g = 128, b = 12,  a = 255 }  // orange
                : r2 < 0.80f
                    ? new Color { r = 218, g = 38,  b = 8,   a = 255 }  // red
                    : new Color { r = 82,  g = 72,  b = 55,  a = 255 }; // debris

        float life = 0.35f + rng.NextSingle() * 1.1f;
        ptcls.Add((centre, dir * spd, life, life, col));
    }
}
