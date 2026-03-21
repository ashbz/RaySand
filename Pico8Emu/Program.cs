using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Raylib_CsLo;
using Pico8Emu;
using static Raylib_CsLo.Raylib;

// ══════════════════════════════════════════════════════════════════════════════
// ── Helpers
// ══════════════════════════════════════════════════════════════════════════════
static unsafe string? MarshalPath(sbyte* ptr) =>
    ptr == null ? null : Marshal.PtrToStringUTF8((IntPtr)ptr);

// ══════════════════════════════════════════════════════════════════════════════
// ── ROM + machine
// ══════════════════════════════════════════════════════════════════════════════
const string FallbackRom = "roms/sample.p8";
string romPath  = args.Length > 0 && File.Exists(args[0]) ? args[0] : FallbackRom;
var cart    = CartLoader.Load(romPath);
var machine = new Pico8Machine();
machine.LoadCart(cart);
string cartName = Path.GetFileNameWithoutExtension(romPath);

// Suppress Raylib INFO spam (TIMER: Target time per frame, etc.)
SetTraceLogLevel((int)TraceLogLevel.LOG_WARNING);

SetConfigFlags(ConfigFlags.FLAG_WINDOW_RESIZABLE | ConfigFlags.FLAG_VSYNC_HINT);
InitWindow(1480, 900, $"PICO-8 Studio — {cartName}");
InitAudioDevice();
SetTargetFPS(60);
SetExitKey(KeyboardKey.KEY_NULL);

RlImGui.Setup();


// ══════════════════════════════════════════════════════════════════════════════
// ── Screen texture (128 × 128) + ping-pong game RTs
// ══════════════════════════════════════════════════════════════════════════════
var cpuImg    = GenImageColor(128, 128, BLACK);
var screenTex = LoadTextureFromImage(cpuImg);
SetTextureFilter(screenTex, TextureFilter.TEXTURE_FILTER_POINT);
UnloadImage(cpuImg);
var pixels = new Color[128 * 128];

int   gameRtW = 512, gameRtH = 512;
var   gameRtA = LoadRenderTexture(gameRtW, gameRtH);
var   gameRtB = LoadRenderTexture(gameRtW, gameRtH);

// ── Sprite sheet texture (128×128, updated when sprite data changes) ──────
var sprSheetPixels = new Color[128 * 128];
var sprSheetTex    = LoadTextureFromImage(GenImageColor(128, 128, BLACK));
SetTextureFilter(sprSheetTex, TextureFilter.TEXTURE_FILTER_POINT);

// ── Map texture (1024×512, 128 tiles × 64 tiles each 8 px, updated on dirty) ─
var mapTexels   = new Color[1024 * 512];
var mapTex      = LoadTextureFromImage(GenImageColor(1024, 512, BLACK));
SetTextureFilter(mapTex, TextureFilter.TEXTURE_FILTER_POINT);
bool mapDirty   = true;
int  mapSelSpr  = 1;   // sprite index to paint on the map
float mapZoom   = 2f;  // zoom factor for map view

// ══════════════════════════════════════════════════════════════════════════════
// ── SHADERS  (all stored in a named list so they can be toggled & stacked)
// ══════════════════════════════════════════════════════════════════════════════

// ── 1. Smooth Upscale (Catmull-Rom bicubic) ───────────────────────────────
const string UpscaleFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; out vec4 finalColor;
vec4 crW(float t){float t2=t*t,t3=t2*t;return vec4(-0.5*t3+t2-0.5*t,1.5*t3-2.5*t2+1.,-1.5*t3+2.*t2+0.5*t,0.5*t3-0.5*t2);}
vec4 cr(vec2 uv){vec2 sz=vec2(textureSize(texture0,0)),pos=uv*sz-.5;vec2 i=floor(pos),f=fract(pos);vec4 wx=crW(f.x),wy=crW(f.y),res=vec4(0);
  for(int r=-1;r<=2;r++){float wr=wy[r+1];for(int c=-1;c<=2;c++)res+=texture(texture0,clamp((i+vec2(c+.5,r+.5))/sz,0.,1.))*wx[c+1]*wr;}return max(res,0.);}
void main(){vec4 col=cr(fragTexCoord)*fragColor;float l=dot(col.rgb,vec3(.299,.587,.114));col.rgb=mix(vec3(l),col.rgb,1.12);finalColor=vec4(clamp(col.rgb,0.,1.),col.a);}
";

// ── 2. CRT + Bloom ────────────────────────────────────────────────────────
const string AAFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; out vec4 finalColor;
vec4 ss(vec2 uv){vec2 sz=vec2(textureSize(texture0,0)),p=uv*sz,i=floor(p),f=fract(p),e=fwidth(p);
  vec2 aa=clamp(f/e,0.,.5)+clamp((f-1.)/e+.5,0.,.5);return texture(texture0,(i+aa)/sz);}
vec4 blur(vec2 uv,float r){vec2 d=r/vec2(textureSize(texture0,0));vec4 c=ss(uv)*4.;
  c+=ss(uv+vec2(d.x,0))*2.+ss(uv-vec2(d.x,0))*2.+ss(uv+vec2(0,d.y))*2.+ss(uv-vec2(0,d.y))*2.;
  c+=ss(uv+d)+ss(uv-d)+ss(uv+vec2(d.x,-d.y))+ss(uv+vec2(-d.x,d.y));return c/16.;}
void main(){vec4 col=ss(fragTexCoord)*fragColor;vec4 b=blur(fragTexCoord,1.2);float l=dot(b.rgb,vec3(.299,.587,.114));
  col.rgb+=b.rgb*(l*l)*.55;vec2 hp=.45/vec2(textureSize(texture0,0));
  vec3 spr=(ss(fragTexCoord+vec2(hp.x,0)).rgb+ss(fragTexCoord-vec2(hp.x,0)).rgb+ss(fragTexCoord+vec2(0,hp.y)).rgb+ss(fragTexCoord-vec2(0,hp.y)).rgb)*.25;
  col.rgb=mix(col.rgb,spr,.10);vec2 cc=fragTexCoord-.5;float vig=1.-dot(cc,cc)*1.35;col.rgb*=clamp(vig,.45,1.);
  float th=float(textureSize(texture0,0).y);col.rgb*=.92+.08*smoothstep(.3,.7,fract(fragTexCoord.y*th));
  col.rgb=pow(max(col.rgb,0.),vec3(.88))*1.04;finalColor=vec4(clamp(col.rgb,0.,1.),col.a);}
";

// ── 3. Ocean ──────────────────────────────────────────────────────────────
const string OceanFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; uniform float iTime; out vec4 finalColor;
float h21(vec2 p){p=fract(p*vec2(127.1,311.7));p+=dot(p,p+19.19);return fract(p.x*p.y);}
float ns(vec2 p){vec2 i=floor(p),f=fract(p);f=f*f*(3.-2.*f);return mix(mix(h21(i),h21(i+vec2(1,0)),f.x),mix(h21(i+vec2(0,1)),h21(i+vec2(1,1)),f.x),f.y);}
float fbm(vec2 p){float v=0.,a=.5;for(int i=0;i<5;i++){v+=a*ns(p);p=p*2.1+vec2(.31,.71);a*=.5;}return v;}
float ca(vec2 uv,float t){float c=fbm(uv*2.8+vec2(t*.14,t*.09));c=1.-abs(c-.5)*2.;return pow(max(c,0.),4.);}
void main(){vec2 uv=vec2(fragTexCoord.x,1.-fragTexCoord.y);float t=iTime,S=.0055,br=.85+.15*sin(t*.35);
  vec2 w;w.x=(sin(uv.y*22.+t*1.2)+sin(uv.y*9.-t*.75+1.4)*.65+sin((uv.x+uv.y)*14.+t)*.4)*S*br;
  w.y=(cos(uv.x*19.-t*1.05)+cos(uv.x*8.+t*.6+2.2)*.65+cos((uv.x-uv.y)*11.+t*.85)*.4)*S*br;
  vec2 d=clamp(uv+w,.001,.999),cd=normalize(w+vec2(.0001));float ca2=.0035+.001*sin(t*.45);
  vec3 col=vec3(texture(texture0,clamp(d+cd*ca2,0.,1.)).r,texture(texture0,d).g,texture(texture0,clamp(d-cd*ca2*.7,0.,1.)).b)*fragColor.rgb;
  float c2=(ca(uv+vec2(t*.06,t*.04),t)*.55+ca(uv+vec2(-t*.05,t*.045),t*.75)*.35)*.18;
  float lm=dot(col,vec3(.299,.587,.114));col=mix(col,mix(vec3(0.,.12,.30),vec3(.05,.35,.45),lm),.22)+c2*vec3(.55,.80,.95);
  vec2 cv=uv-.5;float vg=smoothstep(0.,1.,clamp(1.-dot(cv,cv)*2.2,0.,1.));col*=mix(.45,1.08,vg);
  finalColor=vec4(clamp(col,0.,1.),fragColor.a);}
";

// ── 4. Chromatic Aberration ───────────────────────────────────────────────
const string ChromaFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; out vec4 finalColor;
void main(){
  vec2 uv=fragTexCoord,dir=uv-.5;
  float dist=length(dir)*0.022;
  vec2 off=normalize(dir+vec2(.0001))*dist;
  float r=texture(texture0,clamp(uv+off,0.,1.)).r;
  float g=texture(texture0,uv).g;
  float b=texture(texture0,clamp(uv-off,0.,1.)).b;
  finalColor=vec4(r,g,b,1.)*fragColor;}
";

// ── 5. Neon Glow ──────────────────────────────────────────────────────────
const string NeonFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; out vec4 finalColor;
void main(){
  vec2 uv=fragTexCoord;
  vec2 sz=1./vec2(textureSize(texture0,0));
  vec4 orig=texture(texture0,uv);
  vec4 glow=vec4(0);float tw=0.;
  for(int x=-3;x<=3;x++){for(int y=-3;y<=3;y++){
    float w=exp(-float(x*x+y*y)/6.);
    glow+=texture(texture0,uv+vec2(x,y)*sz*1.8)*w; tw+=w;}}
  glow/=tw;
  float l=dot(glow.rgb,vec3(.299,.587,.114));
  glow.rgb=mix(vec3(l),glow.rgb,3.2)*1.6;
  finalColor=clamp(orig+glow*.85,0.,1.)*fragColor;}
";

// ── 6. VHS ────────────────────────────────────────────────────────────────
const string VHSFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; uniform float iTime; out vec4 finalColor;
float rnd(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
void main(){
  vec2 uv=fragTexCoord;float t=iTime;
  float wb=sin(uv.y*80.+t*4.)*.002+rnd(vec2(floor(uv.y*120.),t))*.003;
  uv.x=clamp(uv.x+wb,0.,1.);
  vec2 sz=1./vec2(textureSize(texture0,0));
  vec3 col=vec3(0);float tw=0.;
  for(int i=-2;i<=2;i++){float w=exp(-float(i*i)/2.);col+=texture(texture0,uv+vec2(sz.x*i,0)).rgb*w;tw+=w;}
  col/=tw;
  col+=(rnd(uv+t)-.5)*.07;
  float l=dot(col,vec3(.299,.587,.114));
  col=mix(col,vec3(l*.88,l*.94,l*.82),.18);
  float scan=.90+.10*sin(uv.y*float(textureSize(texture0,0).y)*3.14159);
  finalColor=vec4(clamp(col*scan,0.,1.),1.)*fragColor;}
";

// ── 7. Emboss 3D (surface relief lighting) ────────────────────────────────
const string EmbossFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; out vec4 finalColor;
void main(){
  vec2 uv=fragTexCoord;vec2 sz=1./vec2(textureSize(texture0,0));
  vec4 orig=texture(texture0,uv);
  float lf=dot(texture(texture0,uv+vec2(-sz.x,0)).rgb,vec3(.333));
  float rf=dot(texture(texture0,uv+vec2( sz.x,0)).rgb,vec3(.333));
  float uf=dot(texture(texture0,uv+vec2(0, sz.y)).rgb,vec3(.333));
  float df=dot(texture(texture0,uv+vec2(0,-sz.y)).rgb,vec3(.333));
  vec3 n=normalize(vec3(lf-rf,df-uf,.45));
  vec3 ld=normalize(vec3(.55,.75,1.));
  float diff=max(dot(n,ld),0.);
  float spec=pow(max(dot(reflect(-ld,n),vec3(0,0,1)),0.),28.)*.35;
  vec3 col=orig.rgb*(.30+diff*.85)+spec;
  finalColor=vec4(clamp(col,0.,1.),orig.a)*fragColor;}
";

// ── 8. Parallax 3D (animated depth wobble) ────────────────────────────────
const string ParallaxFragSrc = @"#version 330
in vec2 fragTexCoord; in vec4 fragColor;
uniform sampler2D texture0; uniform float iTime; out vec4 finalColor;
void main(){
  vec2 uv=fragTexCoord;float t=iTime;
  float depth=dot(texture(texture0,uv).rgb,vec3(.299,.587,.114));
  float cx=sin(t*.35)*.018, cy=cos(t*.28)*.012;
  vec2 par=vec2(cx,cy)*(depth-.5)*2.2;
  vec4 col=texture(texture0,clamp(uv+par,.001,.999));
  finalColor=col*fragColor;}
";

// ── Load all shaders ──────────────────────────────────────────────────────
// (Name, Shader, NeedsTime)
var shaders = new (string Name, Shader Sh, bool NeedsTime)[]
{
    ("Smooth Upscale",   LoadShaderFromMemory(null, UpscaleFragSrc),  false),
    ("CRT + Bloom",      LoadShaderFromMemory(null, AAFragSrc),       false),
    ("Ocean",            LoadShaderFromMemory(null, OceanFragSrc),    true),
    ("Chromatic Aberr.", LoadShaderFromMemory(null, ChromaFragSrc),   false),
    ("Neon Glow",        LoadShaderFromMemory(null, NeonFragSrc),     false),
    ("VHS",              LoadShaderFromMemory(null, VHSFragSrc),      true),
    ("Emboss 3D",        LoadShaderFromMemory(null, EmbossFragSrc),   false),
    ("Parallax 3D",      LoadShaderFromMemory(null, ParallaxFragSrc), true),
};
bool[] shaderEnabled = new bool[shaders.Length];

// Pre-locate the iTime uniform for shaders that use it
var shaderTimeLocs = new int[shaders.Length];
for (int si = 0; si < shaders.Length; si++)
    shaderTimeLocs[si] = shaders[si].NeedsTime
        ? GetShaderLocation(shaders[si].Sh, "iTime") : -1;

// ══════════════════════════════════════════════════════════════════════════════
// ── State
// ══════════════════════════════════════════════════════════════════════════════
bool  muted      = true;   // muted by default
bool  unlockFps  = false;
bool  prevUnlock = false;
float totalTime  = 0f;
string edStatus  = "Ready";

// ── Panel visibility flags ────────────────────────────────────────────────
bool showSpriteEditor = false;
bool showMapEditor    = false;
bool showConsole      = false;
bool showPalEditor    = false;
bool showProfiler     = false;

// ── Profiler ──────────────────────────────────────────────────────────────
float[] frameTimes  = new float[120];
int     frameTimeIdx = 0;
float   updateMs    = 0f;
float   drawMs      = 0f;

// ── Palette editor ────────────────────────────────────────────────────────
int selPalColor = 0;

// ── Lua console / REPL ────────────────────────────────────────────────────
string replInput     = "";
bool   consoleScroll = true;

// ══════════════════════════════════════════════════════════════════════════════
// ── Sprite editor state
// ══════════════════════════════════════════════════════════════════════════════
int    selSprite       = 0;        // 0..255
byte   paintColor      = 7;        // 0..15  (PICO-8 white)
bool   eyedropMode     = false;
bool   sprSheetDirty   = true;
byte[] spriteBuf       = new byte[64]; // copy/cut buffer
bool   spriteBufValid  = false;
float  zoomCellSz      = 24f;      // pixels per sprite pixel in zoom view

// ── External Lua editor (FileSystemWatcher) ───────────────────────────────
FileSystemWatcher? watcher = null;
string?            watchPath = null;
string?            pendingCode = null;     // written from watcher thread, read on main
object             pendingLock = new();

void StartExternalEdit()
{
    string tmpDir  = Path.GetTempPath();
    string tmpFile = Path.Combine(tmpDir, $"pico8_{cartName}.lua");
    File.WriteAllText(tmpFile, machine.PreprocessedLua);

    watcher?.Dispose();
    watcher = new FileSystemWatcher(tmpDir, Path.GetFileName(tmpFile))
    {
        NotifyFilter         = NotifyFilters.LastWrite,
        EnableRaisingEvents  = true,
    };
    watcher.Changed += (_, _) =>
    {
        try
        {
            Thread.Sleep(80); // brief debounce
            string code = File.ReadAllText(tmpFile);
            lock (pendingLock) pendingCode = code;
        }
        catch { /* file still locked by editor */ }
    };

    watchPath = tmpFile;
    edStatus  = $"Watching {tmpFile}";
    Process.Start(new ProcessStartInfo(tmpFile) { UseShellExecute = true });
}

void StopExternalEdit()
{
    watcher?.Dispose();
    watcher    = null;
    watchPath  = null;
    edStatus   = "Stopped watching";
}

// ── Drag-and-drop ROM loader ──────────────────────────────────────────────
void LoadRom(string path)
{
    try
    {
        StopExternalEdit();
        var newCart = CartLoader.Load(path);
        machine.LoadCart(newCart);
        cartName = Path.GetFileNameWithoutExtension(path);
        SetWindowTitle($"PICO-8 Studio — {cartName}");
        sprSheetDirty = true;
        mapDirty = true;
        edStatus = $"Loaded {cartName}";
    }
    catch (Exception ex)
    {
        edStatus = $"Load error: {ex.Message}";
    }
}

// ── Audio ─────────────────────────────────────────────────────────────────
const int AudioBuf   = Pico8Audio.BufferFrames;
var audioStream  = LoadAudioStream((uint)Pico8Audio.SampleRate, 16, 1);
var audioSamples = new short[AudioBuf];
SetAudioStreamBufferSizeDefault(AudioBuf);
PlayAudioStream(audioStream);

// ── Sprite sheet texture upload ───────────────────────────────────────────
void UploadSpriteSheet()
{
    var sht = machine.SpriteSheet;
    for (int i = 0; i < 128 * 128; i++)
    {
        var (r, g, b) = Pico8Machine.Palette[sht[i] & 0xF];
        sprSheetPixels[i] = new Color(r, g, b, (byte)255);
    }
    unsafe { fixed (Color* p = sprSheetPixels) UpdateTexture(sprSheetTex, p); }
    sprSheetDirty = false;
}

// ── Map texture upload ────────────────────────────────────────────────────
void UploadMapTex()
{
    var sht = machine.SpriteSheet;
    var map = machine.MapData;
    for (int ty = 0; ty < 64; ty++)
    {
        for (int tx = 0; tx < 128; tx++)
        {
            byte si = map[ty * 128 + tx];
            int sx2 = (si % 16) * 8, sy2 = (si / 16) * 8;
            for (int py = 0; py < 8; py++)
            {
                for (int px = 0; px < 8; px++)
                {
                    byte ci = sht[(sy2 + py) * 128 + (sx2 + px)];
                    var (r, g, b) = Pico8Machine.Palette[ci & 0xF];
                    mapTexels[(ty * 8 + py) * 1024 + (tx * 8 + px)] = new Color(r, g, b, (byte)255);
                }
            }
        }
    }
    unsafe { fixed (Color* p = mapTexels) UpdateTexture(mapTex, p); }
    mapDirty = false;
}

// ── Shader pass helpers ───────────────────────────────────────────────────
void ShaderPass(Shader sh, Texture src, RenderTexture dst, bool flipY = false)
{
    BeginTextureMode(dst);
    ClearBackground(BLACK);
    BeginShaderMode(sh);
    DrawTexturePro(src,
        new Rectangle(0, 0, src.width, flipY ? -src.height : src.height),
        new Rectangle(0, 0, dst.texture.width, dst.texture.height),
        Vector2.Zero, 0f, WHITE);
    EndShaderMode();
    EndTextureMode();
}
void BlitPass(Texture src, RenderTexture dst, bool flipY = false)
{
    BeginTextureMode(dst);
    ClearBackground(BLACK);
    DrawTexturePro(src,
        new Rectangle(0, 0, src.width, flipY ? -src.height : src.height),
        new Rectangle(0, 0, dst.texture.width, dst.texture.height),
        Vector2.Zero, 0f, WHITE);
    EndTextureMode();
}

// ── PICO-8 button reader ──────────────────────────────────────────────────
static bool[] ReadButtons() => new[]
{
    IsKeyDown(KeyboardKey.KEY_LEFT)  || IsKeyDown(KeyboardKey.KEY_A),
    IsKeyDown(KeyboardKey.KEY_RIGHT) || IsKeyDown(KeyboardKey.KEY_D),
    IsKeyDown(KeyboardKey.KEY_UP)    || IsKeyDown(KeyboardKey.KEY_W),
    IsKeyDown(KeyboardKey.KEY_DOWN)  || IsKeyDown(KeyboardKey.KEY_S),
    IsKeyDown(KeyboardKey.KEY_Z)     || IsKeyDown(KeyboardKey.KEY_N),
    IsKeyDown(KeyboardKey.KEY_X)     || IsKeyDown(KeyboardKey.KEY_M),
    false, false,
};

// ══════════════════════════════════════════════════════════════════════════════
// ── Main loop
// ══════════════════════════════════════════════════════════════════════════════
while (!WindowShouldClose())
{
    float dt = GetFrameTime();
    totalTime += dt;

    // FPS cap — only call SetTargetFPS when the setting changes to avoid log spam
    if (unlockFps != prevUnlock)
    {
        prevUnlock = unlockFps;
        SetTargetFPS(unlockFps ? 0 : 60);
    }

    // ── Handle dropped ROM files ──────────────────────────────────────────
    if (IsFileDropped())
    {
        unsafe
        {
            var fpl = LoadDroppedFiles();
            for (int fi = 0; fi < (int)fpl.count; fi++)
            {
                string? p = MarshalPath(fpl.paths[fi]);
                if (p != null &&
                    (p.EndsWith(".p8", StringComparison.OrdinalIgnoreCase) ||
                     p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                {
                    LoadRom(p);
                    break;
                }
            }
            UnloadDroppedFiles(fpl);
        }
    }

    // ── Apply any pending Lua reload from the file watcher ────────────────
    string? codeToReload = null;
    lock (pendingLock) { codeToReload = pendingCode; pendingCode = null; }
    if (codeToReload != null)
    {
        machine.ReloadLua(codeToReload);
        edStatus = $"Reloaded from file at {DateTime.Now:HH:mm:ss}";
    }

    // ── PICO-8 update & draw ──────────────────────────────────────────────
    // Always pass input — NavEnableKeyboard is off so ImGui won't steal arrows.
    var swUpdate = Stopwatch.StartNew();
    machine.Update(ReadButtons());
    updateMs = (float)swUpdate.Elapsed.TotalMilliseconds;

    var swDraw = Stopwatch.StartNew();
    machine.Draw();
    drawMs = (float)swDraw.Elapsed.TotalMilliseconds;

    frameTimes[frameTimeIdx % frameTimes.Length] = dt * 1000f;
    frameTimeIdx++;

    // ── Audio ─────────────────────────────────────────────────────────────
    if (!muted && IsAudioStreamProcessed(audioStream) && machine.Audio != null)
    {
        machine.Audio.FillBuffer(audioSamples, 0, AudioBuf);
        unsafe { fixed (short* p = audioSamples) UpdateAudioStream(audioStream, p, AudioBuf); }
    }

    // ── Upload PICO-8 pixels ──────────────────────────────────────────────
    for (int i = 0; i < pixels.Length; i++)
    {
        byte di = machine.DisplayPalette[machine.Screen[i] & 0xF];
        var (r, g, b) = Pico8Machine.Palette[di & 0xF];
        pixels[i] = new Color(r, g, b, (byte)255);
    }
    unsafe { fixed (Color* p = pixels) UpdateTexture(screenTex, p); }

    // ── Draw ──────────────────────────────────────────────────────────────
    BeginDrawing();
    ClearBackground(new Color { r = 22, g = 23, b = 28, a = 255 });

    RlImGui.Begin();

    // ── Fullscreen dockspace host ─────────────────────────────────────────
    {
        var vp    = ImGui.GetMainViewport();
        var flags = ImGuiWindowFlags.NoTitleBar    | ImGuiWindowFlags.NoCollapse
                  | ImGuiWindowFlags.NoResize      | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.MenuBar;
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);
        ImGui.SetNextWindowViewport(vp.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    Vector2.Zero);
        bool open = true;
        ImGui.Begin("##Host", ref open, flags);
        ImGui.PopStyleVar(3);

        if (ImGui.BeginMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                ImGui.TextDisabled("Drag a .p8 / .p8.png onto the window");
                if (ImGui.MenuItem("Exit")) Environment.Exit(0);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Windows"))
            {
                ImGui.MenuItem("Sprite Editor",  null, ref showSpriteEditor);
                ImGui.MenuItem("Map Editor",      null, ref showMapEditor);
                ImGui.MenuItem("Lua Console",     null, ref showConsole);
                ImGui.MenuItem("Palette Editor",  null, ref showPalEditor);
                ImGui.MenuItem("Profiler",        null, ref showProfiler);
                ImGui.EndMenu();
            }
            // FPS shown in menu bar top-right
            string fpsStr = $"{GetFPS()} fps  |  {cartName}";
            ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(fpsStr).X + 10);
            ImGui.TextDisabled(fpsStr);
            ImGui.EndMenuBar();
        }

        ImGui.DockSpace(ImGui.GetID("MainDock"), Vector2.Zero, ImGuiDockNodeFlags.None);
        ImGui.End();
    }

    // ── Options panel (left dock) ─────────────────────────────────────────
    ImGui.SetNextWindowPos(new Vector2(0, 20), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSize(new Vector2(220, GetScreenHeight() - 20), ImGuiCond.FirstUseEver);
    if (ImGui.Begin("Options"))
    {
        ImGui.SeparatorText("Playback");
        ImGui.Checkbox("Mute audio",  ref muted);
        ImGui.Checkbox("Unlock FPS",  ref unlockFps);
        ImGui.Spacing();

        ImGui.SeparatorText("Lua Code");
        if (watcher == null)
        {
            if (ImGui.Button("Edit Lua Code (external)"))
                StartExternalEdit();
            ImGui.SetItemTooltip("Saves preprocessed code to a temp .lua file,\nopens it in your default editor,\nand hot-reloads on every save.");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            if (ImGui.Button("Stop Watching")) StopExternalEdit();
            ImGui.PopStyleColor();
            ImGui.TextDisabled(watchPath ?? "");
        }
        ImGui.Spacing();

        ImGui.SeparatorText("Export");
        if (ImGui.Button("Export PNG (128×128)"))
        {
            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"{cartName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var img = LoadImageFromTexture(screenTex);
            ExportImage(img, outPath);
            UnloadImage(img);
            edStatus = $"Exported: {outPath}";
        }
        if (ImGui.Button("Export PNG (with shaders)") && gameRtA.texture.width > 0)
        {
            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"{cartName}_shaded_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var img = LoadImageFromTexture(gameRtA.texture);
            unsafe { var tmp = img; ImageFlipVertical(&tmp); img = tmp; }
            ExportImage(img, outPath);
            UnloadImage(img);
            edStatus = $"Exported: {outPath}";
        }
        ImGui.Spacing();

        ImGui.SeparatorText("Shaders  (stackable)");
        ImGui.TextDisabled("Applied in listed order");
        ImGui.Spacing();
        for (int si = 0; si < shaders.Length; si++)
            ImGui.Checkbox(shaders[si].Name, ref shaderEnabled[si]);

        ImGui.Spacing();
        ImGui.SeparatorText("Status");
        ImGui.TextWrapped(edStatus);
    }
    ImGui.End();

    // ── Game view (center dock) ───────────────────────────────────────────
    ImGui.SetNextWindowPos(new Vector2(224, 20), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSize(new Vector2(GetScreenWidth() - 224, GetScreenHeight() - 20), ImGuiCond.FirstUseEver);
    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    if (ImGui.Begin("Game View"))
    {
        ImGui.PopStyleVar();

        var avail     = ImGui.GetContentRegionAvail();
        float sc      = MathF.Max(MathF.Min(avail.X / 128f, avail.Y / 128f), 1f);
        var displaySz = new Vector2(128f * sc, 128f * sc);
        var offset    = Vector2.Max((avail - displaySz) * 0.5f, Vector2.Zero);
        ImGui.SetCursorPos(ImGui.GetCursorPos() + offset);

        // Resize RTs
        int nw = Math.Max((int)displaySz.X, 1), nh = Math.Max((int)displaySz.Y, 1);
        if (nw != gameRtW || nh != gameRtH)
        {
            UnloadRenderTexture(gameRtA); UnloadRenderTexture(gameRtB);
            gameRtA = LoadRenderTexture(nw, nh);
            gameRtB = LoadRenderTexture(nw, nh);
            gameRtW = nw; gameRtH = nh;
        }

        // Shader pipeline
        var activeSh = new System.Collections.Generic.List<int>();
        for (int si = 0; si < shaders.Length; si++)
            if (shaderEnabled[si]) activeSh.Add(si);

        // Feed iTime into time-based shaders
        foreach (int si in activeSh)
            if (shaderTimeLocs[si] >= 0)
                SetShaderValue(shaders[si].Sh, shaderTimeLocs[si],
                    totalTime, ShaderUniformDataType.SHADER_UNIFORM_FLOAT);

        Texture displayTex;
        if (activeSh.Count == 0)
        {
            BlitPass(screenTex, gameRtA, false);
            displayTex = gameRtA.texture;
        }
        else
        {
            ShaderPass(shaders[activeSh[0]].Sh, screenTex, gameRtA, false);
            var src = gameRtA; var dst = gameRtB;
            for (int pi = 1; pi < activeSh.Count; pi++)
            {
                ShaderPass(shaders[activeSh[pi]].Sh, src.texture, dst, true);
                (src, dst) = (dst, src);
            }
            displayTex = src.texture;
        }

        // Render textures are flipped in Y — use UV (0,1)→(1,0)
        ImGui.Image((nint)displayTex.id, displaySz, new Vector2(0, 1), new Vector2(1, 0));
    }
    else ImGui.PopStyleVar();
    ImGui.End();

    // ── Sprite Editor ─────────────────────────────────────────────────────
    if (showSpriteEditor)
    {
    ImGui.SetNextWindowPos(new Vector2(0, GetScreenHeight() - 310), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSize(new Vector2(GetScreenWidth(), 310), ImGuiCond.FirstUseEver);
    if (ImGui.Begin("Sprite Editor", ref showSpriteEditor))
    {
        if (sprSheetDirty) UploadSpriteSheet();

        var sht = machine.SpriteSheet;
        float avH = ImGui.GetContentRegionAvail().Y - 4;

        // ── Column 1: full sprite sheet ───────────────────────────────────
        float shSz = Math.Min(avH, 256f);
        ImGui.BeginChild("##ss", new Vector2(shSz + 4, avH), ImGuiChildFlags.None);
        ImGui.TextDisabled("Sheet  (click = select)");

        var shPos = ImGui.GetCursorScreenPos();
        ImGui.Image((nint)sprSheetTex.id, new Vector2(shSz, shSz));

        // Handle click to select sprite
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var mp = ImGui.GetMousePos() - shPos;
            int tx = Math.Clamp((int)(mp.X / shSz * 16), 0, 15);
            int ty = Math.Clamp((int)(mp.Y / shSz * 16), 0, 15);
            selSprite = ty * 16 + tx;
        }

        // Highlight selected sprite + grid
        {
            var dl = ImGui.GetWindowDrawList();
            float cs = shSz / 16f;
            // Light grid every sprite
            for (int gi = 1; gi < 16; gi++)
            {
                dl.AddLine(shPos + new Vector2(gi * cs, 0), shPos + new Vector2(gi * cs, shSz), 0x30_FF_FF_FFu);
                dl.AddLine(shPos + new Vector2(0, gi * cs), shPos + new Vector2(shSz, gi * cs), 0x30_FF_FF_FFu);
            }
            // Selection box
            int sx = (selSprite % 16); int sy2 = (selSprite / 16);
            var tl = shPos + new Vector2(sx * cs, sy2 * cs);
            var br = tl + new Vector2(cs, cs);
            dl.AddRect(tl, br, 0xFF_FF_FF_FFu, 0f, ImDrawFlags.None, 2f);
            dl.AddRect(tl - Vector2.One, br + Vector2.One, 0xFF_00_00_00u, 0f, ImDrawFlags.None, 1f);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // ── Column 2: zoomed sprite view ──────────────────────────────────
        float zSz = 8 * zoomCellSz;
        ImGui.BeginChild("##zoom", new Vector2(zSz + 20, avH), ImGuiChildFlags.None);
        int bx = (selSprite % 16) * 8;
        int by2 = (selSprite / 16) * 8;
        ImGui.TextDisabled($"Spr #{selSprite}  pos ({bx},{by2})");

        // Reserve click region first, then draw using DrawList
        ImGui.InvisibleButton("##zpaint", new Vector2(zSz, zSz));
        bool zpHov  = ImGui.IsItemHovered();
        var  zOrig  = ImGui.GetItemRectMin();
        var  dlZ    = ImGui.GetWindowDrawList();

        // Paint / eyedrop on click
        if (zpHov)
        {
            var mp = ImGui.GetMousePos() - zOrig;
            int px2 = Math.Clamp((int)(mp.X / zoomCellSz), 0, 7);
            int py2 = Math.Clamp((int)(mp.Y / zoomCellSz), 0, 7);
            int idx = (by2 + py2) * 128 + (bx + px2);

            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (eyedropMode)
                { paintColor = sht[idx]; eyedropMode = false; }
                else
                { sht[idx] = paintColor; sprSheetDirty = true; }
            }
            if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
            { paintColor = sht[idx]; }
        }

        // Draw 8×8 pixels
        for (int py2 = 0; py2 < 8; py2++)
        {
            for (int px2 = 0; px2 < 8; px2++)
            {
                byte ci = sht[(by2 + py2) * 128 + (bx + px2)];
                var (r, g, b) = Pico8Machine.Palette[ci & 0xF];
                uint fc = (uint)((255 << 24) | (b << 16) | (g << 8) | r);
                var tl = zOrig + new Vector2(px2 * zoomCellSz, py2 * zoomCellSz);
                var br = tl + new Vector2(zoomCellSz, zoomCellSz);
                dlZ.AddRectFilled(tl, br, fc);
                dlZ.AddRect(tl, br, 0x40_00_00_00u);
            }
        }

        // Hover preview pixel highlight
        if (zpHov)
        {
            var mp = ImGui.GetMousePos() - zOrig;
            int px2 = Math.Clamp((int)(mp.X / zoomCellSz), 0, 7);
            int py2 = Math.Clamp((int)(mp.Y / zoomCellSz), 0, 7);
            var htl = zOrig + new Vector2(px2 * zoomCellSz, py2 * zoomCellSz);
            dlZ.AddRect(htl, htl + new Vector2(zoomCellSz, zoomCellSz), 0xFF_FF_FF_FFu, 0f, ImDrawFlags.None, 2f);
        }

        // Zoom slider
        ImGui.SetCursorScreenPos(zOrig + new Vector2(0, zSz + 4));
        ImGui.SetNextItemWidth(zSz);
        ImGui.SliderFloat("Zoom##zsl", ref zoomCellSz, 8f, 40f);

        ImGui.EndChild();
        ImGui.SameLine();

        // ── Column 3: palette + tools ─────────────────────────────────────
        ImGui.BeginChild("##tools", new Vector2(0, avH), ImGuiChildFlags.None);

        ImGui.TextDisabled("Palette");
        ImGui.Spacing();
        for (int ci = 0; ci < 16; ci++)
        {
            var (r, g, b) = Pico8Machine.Palette[ci];
            var c4 = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
            bool sel = (ci == paintColor);
            if (sel) { ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2.5f); ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1,1,1,1)); }
            if (ImGui.ColorButton($"##p{ci}", c4, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoBorder, new Vector2(22, 22)))
                paintColor = (byte)ci;
            if (sel) { ImGui.PopStyleColor(); ImGui.PopStyleVar(); }
            if (ci % 4 != 3) ImGui.SameLine(0, 2);
        }

        ImGui.Spacing();
        ImGui.SeparatorText("Tools");

        if (ImGui.Button(eyedropMode ? "● Pick" : "Pick Color", new Vector2(-1, 0)))
            eyedropMode = !eyedropMode;
        ImGui.SetItemTooltip("Left-click a pixel to pick its color.\nRight-click in zoom also picks.");

        ImGui.Spacing();
        ImGui.SeparatorText("Sprite Ops");

        if (ImGui.Button("Copy",   new Vector2(-1, 0)))
        {
            for (int py2 = 0; py2 < 8; py2++)
                for (int px2 = 0; px2 < 8; px2++)
                    spriteBuf[py2 * 8 + px2] = sht[(by2 + py2) * 128 + (bx + px2)];
            spriteBufValid = true;
        }
        if (ImGui.Button("Cut",    new Vector2(-1, 0)))
        {
            for (int py2 = 0; py2 < 8; py2++)
                for (int px2 = 0; px2 < 8; px2++)
                { spriteBuf[py2 * 8 + px2] = sht[(by2 + py2) * 128 + (bx + px2)]; sht[(by2 + py2) * 128 + (bx + px2)] = 0; }
            spriteBufValid = true; sprSheetDirty = true;
        }
        ImGui.BeginDisabled(!spriteBufValid);
        if (ImGui.Button("Paste",  new Vector2(-1, 0)))
        {
            for (int py2 = 0; py2 < 8; py2++)
                for (int px2 = 0; px2 < 8; px2++)
                    sht[(by2 + py2) * 128 + (bx + px2)] = spriteBuf[py2 * 8 + px2];
            sprSheetDirty = true;
        }
        ImGui.EndDisabled();
        if (ImGui.Button("Clear",  new Vector2(-1, 0)))
        { for (int i2 = 0; i2 < 8; i2++) for (int j2 = 0; j2 < 8; j2++) sht[(by2+i2)*128+(bx+j2)]=0; sprSheetDirty=true; }

        ImGui.Spacing();
        ImGui.SeparatorText("Transform");

        if (ImGui.Button("Flip H",    new Vector2(-1, 0)))
        {
            for (int py2 = 0; py2 < 8; py2++)
                for (int px2 = 0; px2 < 4; px2++)
                { byte t = sht[(by2+py2)*128+(bx+px2)]; sht[(by2+py2)*128+(bx+px2)]=sht[(by2+py2)*128+(bx+7-px2)]; sht[(by2+py2)*128+(bx+7-px2)]=t; }
            sprSheetDirty = true;
        }
        if (ImGui.Button("Flip V",    new Vector2(-1, 0)))
        {
            for (int py2 = 0; py2 < 4; py2++)
                for (int px2 = 0; px2 < 8; px2++)
                { byte t = sht[(by2+py2)*128+(bx+px2)]; sht[(by2+py2)*128+(bx+px2)]=sht[(by2+7-py2)*128+(bx+px2)]; sht[(by2+7-py2)*128+(bx+px2)]=t; }
            sprSheetDirty = true;
        }
        if (ImGui.Button("Rotate CW", new Vector2(-1, 0)))
        {
            byte[] tmp = new byte[64];
            for (int py2 = 0; py2 < 8; py2++) for (int px2 = 0; px2 < 8; px2++) tmp[px2*8+(7-py2)] = sht[(by2+py2)*128+(bx+px2)];
            for (int py2 = 0; py2 < 8; py2++) for (int px2 = 0; px2 < 8; px2++) sht[(by2+py2)*128+(bx+px2)] = tmp[py2*8+px2];
            sprSheetDirty = true;
        }

        ImGui.EndChild();
    }
    ImGui.End();
    } // end if (showSpriteEditor)

    // ── Map Editor ────────────────────────────────────────────────────────
    if (showMapEditor)
    {
        ImGui.SetNextWindowPos(new Vector2(0, GetScreenHeight() - 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(900, 340), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Map Editor", ref showMapEditor))
        {
            if (mapDirty || sprSheetDirty) { if (sprSheetDirty) UploadSpriteSheet(); UploadMapTex(); }

            float avH2 = ImGui.GetContentRegionAvail().Y - 4;

            // ── Map view ──────────────────────────────────────────────────
            ImGui.BeginChild("##mapview", new Vector2(avH2 * 2 + 8, avH2), ImGuiChildFlags.None);
            ImGui.TextDisabled("Map  (click = paint tile)");
            float mapDispW = 1024f * mapZoom;
            float mapDispH = 512f  * mapZoom;
            var mapCursor = ImGui.GetCursorScreenPos();
            ImGui.BeginChild("##mapscroll", new Vector2(avH2 * 2 + 4, avH2 - 20), ImGuiChildFlags.None);
            ImGui.Image((nint)mapTex.id, new Vector2(mapDispW, mapDispH));
            if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var mp = ImGui.GetMousePos() - ImGui.GetItemRectMin();
                int tx2 = Math.Clamp((int)(mp.X / mapZoom / 8), 0, 127);
                int ty2 = Math.Clamp((int)(mp.Y / mapZoom / 8), 0, 63);
                if (machine.MapData[ty2 * 128 + tx2] != (byte)mapSelSpr)
                {
                    machine.MapData[ty2 * 128 + tx2] = (byte)mapSelSpr;
                    mapDirty = true;
                }
            }
            // Draw tile-grid overlay (every 8 tiles = 1 screen width)
            {
                var dl2 = ImGui.GetWindowDrawList();
                var orig = ImGui.GetItemRectMin();
                float ts = 8f * mapZoom;
                uint gridCol = 0x18_FF_FF_FFu;
                for (int gi = 0; gi <= 128; gi += 8)
                    dl2.AddLine(orig + new Vector2(gi * ts, 0), orig + new Vector2(gi * ts, mapDispH), gridCol);
                for (int gi = 0; gi <= 64; gi += 8)
                    dl2.AddLine(orig + new Vector2(0, gi * ts), orig + new Vector2(mapDispW, gi * ts), gridCol);
            }
            ImGui.EndChild();
            ImGui.EndChild();

            ImGui.SameLine();

            // ── Sprite picker for map painting ────────────────────────────
            float picSz = Math.Min(avH2 - 22, 200f);
            ImGui.BeginChild("##mappick", new Vector2(picSz + 60, avH2), ImGuiChildFlags.None);
            ImGui.TextDisabled("Pick tile");
            ImGui.SliderFloat("Zoom##mz", ref mapZoom, 0.5f, 4f);
            var pickPos = ImGui.GetCursorScreenPos();
            ImGui.Image((nint)sprSheetTex.id, new Vector2(picSz, picSz));
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var mp = ImGui.GetMousePos() - pickPos;
                int tx2 = Math.Clamp((int)(mp.X / picSz * 16), 0, 15);
                int ty2 = Math.Clamp((int)(mp.Y / picSz * 16), 0, 15);
                mapSelSpr = ty2 * 16 + tx2;
            }
            // Selection highlight on picker
            {
                var dl3 = ImGui.GetWindowDrawList();
                float cs3 = picSz / 16f;
                int msx = mapSelSpr % 16, msy = mapSelSpr / 16;
                var tl3 = pickPos + new Vector2(msx * cs3, msy * cs3);
                dl3.AddRect(tl3, tl3 + new Vector2(cs3, cs3), 0xFF_FF_FF_FFu, 0f, ImDrawFlags.None, 2f);
            }
            ImGui.TextDisabled($"Spr #{mapSelSpr}");
            ImGui.EndChild();
        }
        ImGui.End();
    }

    // ── Lua Console ───────────────────────────────────────────────────────
    if (showConsole)
    {
        ImGui.SetNextWindowPos(new Vector2(GetScreenWidth() - 500, 20), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(500, 360), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Lua Console", ref showConsole))
        {
            if (ImGui.Button("Clear")) machine.ClearConsole();
            ImGui.SameLine();
            ImGui.Checkbox("Auto-scroll", ref consoleScroll);

            float logH = ImGui.GetContentRegionAvail().Y - 32;
            ImGui.BeginChild("##conlog", new Vector2(-1, logH), ImGuiChildFlags.Borders);
            foreach (var line in machine.ConsoleLines)
            {
                bool isErr = line.StartsWith("[err]", StringComparison.OrdinalIgnoreCase);
                if (isErr) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.TextUnformatted(line);
                if (isErr) ImGui.PopStyleColor();
            }
            if (consoleScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4)
                ImGui.SetScrollHereY(1f);
            ImGui.EndChild();

            ImGui.SetNextItemWidth(-80);
            bool execNow = false;
            if (ImGui.InputText("##repl", ref replInput, 512,
                ImGuiInputTextFlags.EnterReturnsTrue)) execNow = true;
            ImGui.SameLine();
            if (ImGui.Button("Run") || execNow)
            {
                if (!string.IsNullOrWhiteSpace(replInput))
                {
                    machine.ConsoleLines.Add($"> {replInput}");
                    machine.ExecLua(replInput);
                    replInput = "";
                    consoleScroll = true;
                }
            }
        }
        ImGui.End();
    }

    // ── Palette Editor ────────────────────────────────────────────────────
    if (showPalEditor)
    {
        ImGui.SetNextWindowPos(new Vector2(GetScreenWidth() - 240, 400), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(240, 260), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Palette Editor", ref showPalEditor))
        {
            ImGui.TextDisabled("Click a color to select it, then edit below.");
            ImGui.Spacing();
            for (int ci = 0; ci < 16; ci++)
            {
                var (pr, pg, pb) = Pico8Machine.Palette[ci];
                var c4 = new Vector4(pr / 255f, pg / 255f, pb / 255f, 1f);
                bool sel2 = (ci == selPalColor);
                if (sel2)
                {
                    ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 0f, 1f));
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f);
                }
                if (ImGui.ColorButton($"##pal{ci}", c4,
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoBorder,
                    new Vector2(24, 24)))
                    selPalColor = ci;
                if (sel2) { ImGui.PopStyleColor(); ImGui.PopStyleVar(); }
                if (ci % 8 != 7) ImGui.SameLine();
            }
            ImGui.Spacing();
            var (er, eg, eb) = Pico8Machine.Palette[selPalColor];
            var col3 = new Vector3(er / 255f, eg / 255f, eb / 255f);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.ColorPicker3($"##palEdit", ref col3,
                ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview))
            {
                Pico8Machine.Palette[selPalColor] =
                    ((byte)(col3.X * 255), (byte)(col3.Y * 255), (byte)(col3.Z * 255));
                sprSheetDirty = true;
                mapDirty = true;
            }
            if (ImGui.Button("Reset All"))
            {
                var def = Pico8Machine.DefaultPalette;
                for (int ci = 0; ci < 16; ci++) Pico8Machine.Palette[ci] = def[ci];
                sprSheetDirty = true;
                mapDirty = true;
            }
        }
        ImGui.End();
    }

    // ── Profiler ──────────────────────────────────────────────────────────
    if (showProfiler)
    {
        ImGui.SetNextWindowPos(new Vector2(220, 20), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(340, 180), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Profiler", ref showProfiler))
        {
            int fi = (frameTimeIdx - 1 + frameTimes.Length) % frameTimes.Length;
            float curFt = frameTimes[fi];
            float maxFt = 0f, sumFt = 0f;
            foreach (var f in frameTimes) { if (f > maxFt) maxFt = f; sumFt += f; }
            float avgFt = sumFt / frameTimes.Length;

            ImGui.Text($"Frame   {curFt:F1} ms    Avg {avgFt:F1} ms    Peak {maxFt:F1} ms");
            ImGui.Text($"Update  {updateMs:F2} ms    Draw  {drawMs:F2} ms");
            ImGui.Text($"FPS     {(curFt > 0 ? 1000f / curFt : 0):F0}  (target {(unlockFps ? "∞" : "60")})");
            ImGui.Spacing();
            ImGui.PlotLines("##ft", ref frameTimes[0], frameTimes.Length, frameTimeIdx % frameTimes.Length,
                $"frame {curFt:F1}ms", 0f, Math.Max(maxFt * 1.2f, 33.4f), new Vector2(-1, 60));
        }
        ImGui.End();
    }

    RlImGui.End();
    EndDrawing();
}

// ══════════════════════════════════════════════════════════════════════════════
// ── Cleanup
// ══════════════════════════════════════════════════════════════════════════════
StopExternalEdit();
foreach (var se in shaders) UnloadShader(se.Sh);
UnloadRenderTexture(gameRtA);
UnloadRenderTexture(gameRtB);
UnloadTexture(screenTex);
UnloadTexture(sprSheetTex);
UnloadTexture(mapTex);
StopAudioStream(audioStream);
UnloadAudioStream(audioStream);
CloseAudioDevice();
RlImGui.Shutdown();
CloseWindow();
