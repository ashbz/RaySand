using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace RayEngine;

// ─────────────────────────────────────────────────────────────────────────────
// Log helpers
// ─────────────────────────────────────────────────────────────────────────────
enum LogLevel { Info, Warning, Error }
record LogEntry(LogLevel Level, string Message, double Time);

// ─────────────────────────────────────────────────────────────────────────────
public class Editor
{
    // ── scene ─────────────────────────────────────────────────────────────────
    Scene _scene = new();

    // ── viewport ──────────────────────────────────────────────────────────────
    RenderTexture _vpTex;
    int           _vpW, _vpH;
    bool          _vpIs3D;
    Camera3D      _cam3D;
    float         _camYaw, _camPitch;

    // viewport drag state
    enum DragMode { None, Move, ResizeTL, ResizeT, ResizeTR, ResizeR, ResizeBR, ResizeB, ResizeBL, ResizeL }
    DragMode   _dragMode   = DragMode.None;
    SceneNode? _dragNode;
    Vector2    _dragStart;
    Vector3    _dragNodeStart;
    Vector2    _dragSizeStart;
    Vector2    _vpLastMouse;
    Vector2    _vpPanelPos;  // screen-space top-left of the image widget

    // ── particles ─────────────────────────────────────────────────────────────
    readonly ParticleSystem _particles = new();

    // ── editor state ──────────────────────────────────────────────────────────
    bool    _playing;
    double  _totalTime;
    string? _playTempFile;   // path of the last temp scene file used for play

    // ── rename inline ─────────────────────────────────────────────────────────
    string     _renameBuf = "";
    SceneNode? _addParent;

    // ── assets browser ────────────────────────────────────────────────────────
    string _assetRoot = "";
    string _assetCwd  = "";

    // ── log ───────────────────────────────────────────────────────────────────
    readonly List<LogEntry> _log           = new();
    bool                    _logAutoScroll = true;

    // ── panel visibility ──────────────────────────────────────────────────────
    bool _showHierarchy = true;
    bool _showInspector = true;
    bool _showViewport  = true;
    bool _showAssets    = true;
    bool _showConsole   = true;

    // ─────────────────────────────────────────────────────────────────────────
    public void Initialize(int sw, int sh)
    {
        _vpW   = sw / 2;
        _vpH   = sh / 2;
        _vpTex = LoadRenderTexture(_vpW, _vpH);

        _cam3D = new Camera3D
        {
            position   = new(5, 3, 5),
            target     = Vector3.Zero,
            up         = Vector3.UnitY,
            fovy       = 60f,
            projection = (int)CameraProjection.CAMERA_PERSPECTIVE,
        };

        _assetRoot = Path.Combine(AppContext.BaseDirectory, "assets");
        foreach (var sub in new[] { "scenes", "sprites", "scripts", "audio", "fonts", "meshes" })
            Directory.CreateDirectory(Path.Combine(_assetRoot, sub));
        _assetCwd = _assetRoot;

        RlImGui.Setup();
        Log("RayEngine ready.  Welcome!");
    }

    // ─────────────────────────────────────────────────────────────────────────
    public void Update(double dt)
    {
        _totalTime += dt;
        _particles.Update(_scene.Root, (float)dt);
        HandleShortcuts();
    }

    void HandleShortcuts()
    {
        bool ctrl = IsKeyDown(KeyboardKey.KEY_LEFT_CONTROL) || IsKeyDown(KeyboardKey.KEY_RIGHT_CONTROL);
        if (ctrl && IsKeyPressed(KeyboardKey.KEY_S)) SaveScene();
        if (ctrl && IsKeyPressed(KeyboardKey.KEY_N)) NewScene();
        if (IsKeyPressed(KeyboardKey.KEY_F5))        TogglePlay();
        if (IsKeyPressed(KeyboardKey.KEY_DELETE) && !ImGui.GetIO().WantCaptureKeyboard)
            _scene.DeleteSelected();
    }

    // ─────────────────────────────────────────────────────────────────────────
    public void Render()
    {
        RenderViewportScene();

        RlImGui.Begin();
        DrawMainDockspace();
        if (_showHierarchy) DrawSceneHierarchy();
        if (_showInspector) DrawInspector();
        if (_showViewport)  DrawViewport();
        if (_showAssets)    DrawAssetBrowser();
        if (_showConsole)   DrawConsole();
        RlImGui.End();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Main dockspace + menu bar
    // ═════════════════════════════════════════════════════════════════════════

    void DrawMainDockspace()
    {
        var vp    = ImGui.GetMainViewport();
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
                  | ImGuiWindowFlags.NoResize   | ImGuiWindowFlags.NoMove
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

        DrawMainMenuBar();
        ImGui.DockSpace(ImGui.GetID("MainDock"), Vector2.Zero, ImGuiDockNodeFlags.None);
        ImGui.End();
    }

    void DrawMainMenuBar()
    {
        if (!ImGui.BeginMenuBar()) return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New Scene",   "Ctrl+N")) NewScene();
            if (ImGui.MenuItem("Open Scene…"))           Log("Open: not yet implemented", LogLevel.Warning);
            if (ImGui.MenuItem("Save Scene",  "Ctrl+S")) SaveScene();
            ImGui.Separator();
            if (ImGui.MenuItem("Exit"))                  Environment.Exit(0);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z")) Log("Undo not yet implemented", LogLevel.Warning);
            if (ImGui.MenuItem("Redo", "Ctrl+Y")) Log("Redo not yet implemented", LogLevel.Warning);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Scene"))
        {
            if (ImGui.BeginMenu("Add Node"))
            {
                foreach (var t in Enum.GetValues<NodeType>())
                    if (ImGui.MenuItem(t.ToString()))
                    {
                        var n = _scene.AddNode(t, parent: _scene.Selected ?? _scene.Root);
                        _scene.Selected = n;
                        Log($"Added {t} '{n.Name}'");
                    }
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Delete Selected", "Del")) _scene.DeleteSelected();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            ImGui.MenuItem("Scene Hierarchy", null, ref _showHierarchy);
            ImGui.MenuItem("Inspector",       null, ref _showInspector);
            ImGui.MenuItem("Viewport",        null, ref _showViewport);
            ImGui.MenuItem("Content Browser", null, ref _showAssets);
            ImGui.MenuItem("Console",         null, ref _showConsole);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("About")) Log("RayEngine v0.1  —  Raylib + ImGui + MoonSharp");
            ImGui.EndMenu();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        float avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - 230f);

        ImGui.PushStyleColor(ImGuiCol.Button,
            _playing ? new Vector4(0.65f, 0.20f, 0.20f, 1f) : new Vector4(0.18f, 0.55f, 0.22f, 1f));
        if (ImGui.Button(_playing ? "  ■  Stop  " : "  ▶  Play  "))
            TogglePlay();
        ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.Button("  ⏸  Pause")) Log("Pause not yet implemented", LogLevel.Warning);

        ImGui.SameLine();
        ImGui.TextDisabled(_vpIs3D ? "[ 3D ]" : "[ 2D ]");
        if (ImGui.IsItemClicked()) _vpIs3D = !_vpIs3D;

        ImGui.EndMenuBar();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Scene Hierarchy
    // ═════════════════════════════════════════════════════════════════════════

    void DrawSceneHierarchy()
    {
        ImGui.SetNextWindowPos(new Vector2(0, 20),    ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(280, 680), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Scene Hierarchy")) { ImGui.End(); return; }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.42f, 0.72f, 0.85f));
        if (ImGui.Button("+ Add")) { _addParent = _scene.Selected ?? _scene.Root; ImGui.OpenPopup("AddNodePopup"); }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.Button("Del") && _scene.Selected != null) { _scene.DeleteSelected(); Log("Node deleted"); }
        ImGui.SameLine();
        if (ImGui.ArrowButton("up",   ImGuiDir.Up))   _scene.MoveSelectedUp();
        ImGui.SameLine();
        if (ImGui.ArrowButton("down", ImGuiDir.Down)) _scene.MoveSelectedDown();

        ImGui.Separator();

        if (ImGui.BeginPopup("AddNodePopup"))
        {
            ImGui.TextDisabled("Add to: " + (_addParent?.Name ?? "Root"));
            ImGui.Separator();
            foreach (var t in Enum.GetValues<NodeType>())
                if (ImGui.Selectable($"{NodeIcon(t)}  {t}"))
                {
                    var n = _scene.AddNode(t, parent: _addParent);
                    _scene.Selected = n;
                    Log($"Added {t} '{n.Name}'");
                    ImGui.CloseCurrentPopup();
                }
            ImGui.EndPopup();
        }

        DrawNodeTree(_scene.Root);
        ImGui.End();
    }

    void DrawNodeTree(SceneNode node, int depth = 0)
    {
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow;
        if (node.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (node == _scene.Selected)  flags |= ImGuiTreeNodeFlags.Selected;
        if (node.Expanded || depth == 0) flags |= ImGuiTreeNodeFlags.DefaultOpen;

        if (!node.Visible) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f));
        bool open = ImGui.TreeNodeEx($"{NodeIcon(node.Type)}  {node.Name}##{node.Id}", flags);
        node.Expanded = open;
        if (!node.Visible) ImGui.PopStyleColor();

        if (ImGui.IsItemClicked()) _scene.Selected = node;
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _renameBuf = node.Name;
            ImGui.OpenPopup($"Ren_{node.Id}");
        }

        if (ImGui.BeginPopupContextItem($"ctx_{node.Id}"))
        {
            ImGui.TextDisabled(node.Name);
            ImGui.Separator();
            if (ImGui.MenuItem("Rename"))    { _renameBuf = node.Name; _scene.Selected = node; ImGui.OpenPopup($"Ren_{node.Id}"); }
            if (ImGui.MenuItem("Duplicate")) DuplicateNode(node);
            ImGui.Separator();
            if (ImGui.BeginMenu("Add Child"))
            {
                foreach (var t in Enum.GetValues<NodeType>())
                    if (ImGui.MenuItem($"{NodeIcon(t)}  {t}"))
                    { var n = _scene.AddNode(t, parent: node); _scene.Selected = n; }
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (node.Visible  && ImGui.MenuItem("Hide")) node.Visible = false;
            if (!node.Visible && ImGui.MenuItem("Show")) node.Visible = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Delete", node != _scene.Root)) { _scene.Selected = node; _scene.DeleteSelected(); }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup($"Ren_{node.Id}"))
        {
            ImGui.SetKeyboardFocusHere();
            if (ImGui.InputText("Name", ref _renameBuf, 128, ImGuiInputTextFlags.EnterReturnsTrue))
            { node.Name = _renameBuf; ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("OK")) { node.Name = _renameBuf; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }

        if (open && node.Children.Count > 0)
        {
            foreach (var child in node.Children.ToList()) DrawNodeTree(child, depth + 1);
            ImGui.TreePop();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Inspector
    // ═════════════════════════════════════════════════════════════════════════

    void DrawInspector()
    {
        ImGui.SetNextWindowPos(new Vector2(1060, 20),  ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(320, 680), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Inspector")) { ImGui.End(); return; }

        if (_scene.Selected is not { } node) { ImGui.TextDisabled("Nothing selected"); ImGui.End(); return; }

        // Header
        ImGui.PushStyleColor(ImGuiCol.Text, NodeTypeColor(node.Type));
        ImGui.Text(NodeIcon(node.Type)); ImGui.PopStyleColor(); ImGui.SameLine();
        string name = node.Name;
        ImGui.SetNextItemWidth(-26f);
        if (ImGui.InputText("##nm", ref name, 128)) node.Name = name;
        ImGui.SameLine();
        bool vis = node.Visible;
        if (ImGui.Checkbox("##vis", ref vis)) node.Visible = vis;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Visible");

        ImGui.Spacing();

        // Transform
        if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed))
        {
            ImGui.Indent(8f);
            var pos = node.Position;
            if (DragVec3("Position", ref pos, 0.5f)) node.Position = pos;
            var rot = node.Rotation;
            if (DragVec3("Rotation", ref rot, 0.25f)) node.Rotation = rot;
            var scl = node.Scale;
            if (DragVec3("Scale", ref scl, 0.01f, 0.001f, 999f)) node.Scale = scl;
            ImGui.Unindent(8f);
        }

        ImGui.Spacing();

        // Type-specific props
        switch (node.Type)
        {
            case NodeType.Sprite2D:        DrawSprite2DProps(node);        break;
            case NodeType.Label2D:         DrawLabel2DProps(node);         break;
            case NodeType.PointLight:      DrawPointLightProps(node);      break;
            case NodeType.AudioPlayer:     DrawAudioProps(node);           break;
            case NodeType.ParticleEmitter: DrawParticleEmitterProps(node); break;
        }

        // ── Script panel (all node types) ─────────────────────────────────────
        ImGui.Spacing();
        DrawScriptPanel(node);

        ImGui.End();
    }

    void DrawSprite2DProps(SceneNode n)
    {
        if (!ImGui.CollapsingHeader("Sprite2D", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed)) return;
        ImGui.Indent(8f);
        var col = n.Color;
        if (ImGui.ColorEdit4("Color", ref col)) n.Color = col;
        var sz = n.Size;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat2("Size", ref sz, 1f, 1f, 8192f)) n.Size = sz;
        string tp = n.TexturePath ?? "";
        ImGui.SetNextItemWidth(-60f);
        ImGui.InputText("Texture", ref tp, 512, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGui.Button("...##tex")) Log("Texture picker not yet implemented", LogLevel.Warning);
        ImGui.Unindent(8f);
    }

    void DrawLabel2DProps(SceneNode n)
    {
        if (!ImGui.CollapsingHeader("Label2D", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed)) return;
        ImGui.Indent(8f);
        string t = n.LabelText;
        if (ImGui.InputTextMultiline("Text", ref t, 1024, new(0, 55))) n.LabelText = t;
        float fs = n.FontSize;
        if (ImGui.DragFloat("Font Size", ref fs, 0.5f, 6f, 144f)) n.FontSize = fs;
        var col = n.Color;
        if (ImGui.ColorEdit4("Color##lbl", ref col)) n.Color = col;
        ImGui.Unindent(8f);
    }

    void DrawPointLightProps(SceneNode n)
    {
        if (!ImGui.CollapsingHeader("PointLight", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed)) return;
        ImGui.Indent(8f);
        var lc = n.LightColor;
        if (ImGui.ColorEdit4("Light Color", ref lc)) n.LightColor = lc;
        float r = n.LightRange;
        if (ImGui.DragFloat("Range", ref r, 0.1f, 0.1f, 500f)) n.LightRange = r;
        float intensity = n.LightIntensity;
        if (ImGui.DragFloat("Intensity", ref intensity, 0.01f, 0f, 10f)) n.LightIntensity = intensity;
        ImGui.Unindent(8f);
    }

    void DrawAudioProps(SceneNode n)
    {
        if (!ImGui.CollapsingHeader("AudioPlayer", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed)) return;
        ImGui.Indent(8f);
        string ap = n.AudioPath ?? "";
        ImGui.SetNextItemWidth(-60f);
        ImGui.InputText("File", ref ap, 512, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGui.Button("...##aud")) Log("Audio picker not yet implemented", LogLevel.Warning);
        float vol = n.Volume;
        if (ImGui.SliderFloat("Volume", ref vol, 0f, 1f)) n.Volume = vol;
        bool ap2 = n.AutoPlay;
        if (ImGui.Checkbox("Auto-play", ref ap2)) n.AutoPlay = ap2;
        ImGui.Unindent(8f);
    }

    void DrawParticleEmitterProps(SceneNode n)
    {
        if (!ImGui.CollapsingHeader("Particle Emitter", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed)) return;
        ImGui.Indent(8f);

        float rate = n.EmitRate;
        if (ImGui.DragFloat("Emit Rate", ref rate, 0.5f, 0f, 2000f, "%.1f /s")) n.EmitRate = rate;

        float life = n.EmitLifetime;
        if (ImGui.DragFloat("Lifetime", ref life, 0.02f, 0.05f, 20f, "%.2f s")) n.EmitLifetime = life;

        float speed = n.EmitSpeed;
        if (ImGui.DragFloat("Speed", ref speed, 0.5f, 0f, 2000f)) n.EmitSpeed = speed;

        float dir = n.EmitDirection;
        if (ImGui.DragFloat("Direction (°)", ref dir, 0.5f, -180f, 180f)) n.EmitDirection = dir;

        float spread = n.EmitSpread;
        if (ImGui.DragFloat("Spread (°)", ref spread, 0.5f, 0f, 180f)) n.EmitSpread = spread;

        float grav = n.EmitGravity;
        if (ImGui.DragFloat("Gravity", ref grav, 0.5f, -500f, 500f)) n.EmitGravity = grav;

        ImGui.Spacing();

        float ss = n.EmitSizeStart;
        if (ImGui.DragFloat("Size Start", ref ss, 0.1f, 0f, 200f)) n.EmitSizeStart = ss;

        float se = n.EmitSizeEnd;
        if (ImGui.DragFloat("Size End", ref se, 0.1f, 0f, 200f)) n.EmitSizeEnd = se;

        int mp = n.EmitMaxParticles;
        if (ImGui.DragInt("Max Particles", ref mp, 1, 1, 5000))
        {
            n.EmitMaxParticles = mp;
            _particles.ResetEmitter(n.Id);   // rebuild pool with new size
        }

        ImGui.Spacing();

        var cs = n.EmitColorStart;
        if (ImGui.ColorEdit4("Color Start", ref cs)) n.EmitColorStart = cs;

        var ce = n.EmitColorEnd;
        if (ImGui.ColorEdit4("Color End",   ref ce)) n.EmitColorEnd   = ce;

        ImGui.Unindent(8f);
    }

    void DrawScriptPanel(SceneNode n)
    {
        bool hdr = ImGui.CollapsingHeader("Script (Lua)",
            ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed);
        if (!hdr) return;

        ImGui.Indent(8f);
        if (n.LuaScript == null)
        {
            ImGui.TextDisabled("No script attached.");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.42f, 0.72f, 0.85f));
            if (ImGui.Button("Attach Script"))
            {
                n.LuaScript = """
                    -- Available globals:
                    --   self   : the current node (self.x, self.y, self.w, self.h, self.visible)
                    --   Input  : Input.W / Input.A / Input.S / Input.D / Input.IsDown("KEY")
                    --   dt     : delta-time (seconds) passed as argument to update()

                    function start()
                    end

                    function update(dt)
                    end
                    """;
                Log($"Script attached to '{n.Name}'");
            }
            ImGui.PopStyleColor();
        }
        else
        {
            string script = n.LuaScript;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputTextMultiline("##lua", ref script, 16384,
                new Vector2(0, 200), ImGuiInputTextFlags.AllowTabInput))
                n.LuaScript = script;

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.18f, 0.18f, 0.85f));
            if (ImGui.Button("Remove Script"))
            {
                n.LuaScript = null;
                Log($"Script removed from '{n.Name}'");
            }
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled("Ctrl+S to push changes live");
        }
        ImGui.Unindent(8f);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Viewport
    // ═════════════════════════════════════════════════════════════════════════

    void DrawViewport()
    {
        ImGui.SetNextWindowPos(new Vector2(282, 20),   ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(776, 540), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (!ImGui.Begin("Viewport")) { ImGui.PopStyleVar(); ImGui.End(); return; }
        ImGui.PopStyleVar();

        DrawViewportToolbar();
        ImGui.Separator();

        // ── Resize render texture ─────────────────────────────────────────────
        var avail = ImGui.GetContentRegionAvail();
        int nw = Math.Max((int)avail.X, 64);
        int nh = Math.Max((int)avail.Y, 64);
        if (nw != _vpW || nh != _vpH)
        {
            UnloadRenderTexture(_vpTex);
            _vpTex = LoadRenderTexture(nw, nh);
            _vpW = nw; _vpH = nh;
        }

        _vpPanelPos = ImGui.GetCursorScreenPos();

        ImGui.Image((nint)_vpTex.texture.id, new Vector2(_vpW, _vpH),
            new Vector2(0, 1), new Vector2(1, 0));

        HandleViewportInput();

        // ── Play overlay ──────────────────────────────────────────────────────
        if (_playing)
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(_vpPanelPos, _vpPanelPos + new Vector2(_vpW, _vpH), 0x18_00_CC_00u);
            dl.AddText(_vpPanelPos + new Vector2(8, 6), 0xFF_88_FF_88u, "PLAYING  F5 to stop");
        }

        ImGui.End();
    }

    void DrawViewportToolbar()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,
            _vpIs3D ? new Vector4(0.22f, 0.40f, 0.70f, 1f) : new Vector4(0.17f, 0.20f, 0.27f, 1f));
        if (ImGui.SmallButton("3D")) _vpIs3D = true;
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button,
            !_vpIs3D ? new Vector4(0.22f, 0.40f, 0.70f, 1f) : new Vector4(0.17f, 0.20f, 0.27f, 1f));
        if (ImGui.SmallButton("2D")) _vpIs3D = false;
        ImGui.PopStyleColor();

        ImGui.SameLine(); ImGui.TextDisabled(" | ");
        ImGui.SameLine(); ImGui.TextDisabled("LMB: select  Drag: move  Handle: resize");
        if (_vpIs3D) { ImGui.SameLine(); ImGui.TextDisabled("  RMB: orbit  Wheel: zoom"); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    void HandleViewportInput()
    {
        bool hovered = ImGui.IsItemHovered();
        bool lDown   = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool lClick  = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        bool lRel    = ImGui.IsMouseReleased(ImGuiMouseButton.Left);

        var mouse = ImGui.GetMousePos() - _vpPanelPos; // viewport-local coords

        // ── 3D orbit ─────────────────────────────────────────────────────────
        if (_vpIs3D && hovered)
        {
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                var d = ImGui.GetMouseDragDelta(ImGuiMouseButton.Right);
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Right);
                _camYaw   += d.X * 0.3f;
                _camPitch  = Math.Clamp(_camPitch + d.Y * 0.3f, -89f, 89f);
                float dist = Vector3.Distance(_cam3D.position, _cam3D.target);
                float yr = _camYaw * MathF.PI / 180f, pr = _camPitch * MathF.PI / 180f;
                _cam3D.position = _cam3D.target + new Vector3(
                    dist * MathF.Cos(pr) * MathF.Sin(yr),
                    dist * MathF.Sin(pr),
                    dist * MathF.Cos(pr) * MathF.Cos(yr));
            }
            float scroll = GetMouseWheelMove();
            if (scroll != 0)
            {
                float dist = Math.Clamp(Vector3.Distance(_cam3D.position, _cam3D.target) - scroll * 0.5f, 0.5f, 100f);
                float yr = _camYaw * MathF.PI / 180f, pr = _camPitch * MathF.PI / 180f;
                _cam3D.position = _cam3D.target + new Vector3(
                    dist * MathF.Cos(pr) * MathF.Sin(yr),
                    dist * MathF.Sin(pr),
                    dist * MathF.Cos(pr) * MathF.Cos(yr));
            }
        }

        if (_vpIs3D) { _vpLastMouse = mouse; return; }

        // ── 2D mouse interaction ──────────────────────────────────────────────

        // Resolve which handle/area is under mouse for the selected Sprite2D
        DragMode hoverMode = DragMode.None;
        if (_scene.Selected is { Type: NodeType.Sprite2D } sel)
            hoverMode = HitTestHandles(sel, mouse);

        // Change cursor hint
        if (hovered && _dragMode == DragMode.None)
        {
            // (ImGui cursor hints for resize would go here if we had cursor support)
        }

        // Mouse press: begin drag
        if (lClick && hovered && _dragMode == DragMode.None)
        {
            if (hoverMode != DragMode.None)
            {
                // Grabbed a resize handle on the selected node
                _dragMode      = hoverMode;
                _dragNode      = _scene.Selected;
                _dragStart     = mouse;
                _dragNodeStart = _dragNode!.Position;
                _dragSizeStart = _dragNode.Size;
            }
            else
            {
                // Try to select whatever is under the mouse
                var hit = FindNodeAt(mouse);
                if (hit != null)
                {
                    _scene.Selected = hit;
                    _dragMode       = DragMode.Move;
                    _dragNode       = hit;
                    _dragStart      = mouse;
                    _dragNodeStart  = hit.Position;
                    _dragSizeStart  = hit.Size;
                }
                else
                {
                    _scene.Selected = null;
                }
            }
        }

        // Mouse held: apply drag
        if (lDown && _dragMode != DragMode.None && _dragNode != null)
        {
            var delta = mouse - _dragStart;
            ApplyDrag(_dragNode, delta);
        }

        // Mouse release: end drag
        if (lRel)
        {
            _dragMode = DragMode.None;
            _dragNode = null;
        }

        _vpLastMouse = mouse;
    }

    // Hit-test the 8 resize handles + body of a Sprite2D node
    // Returns DragMode.None if no hit, or the relevant mode.
    DragMode HitTestHandles(SceneNode node, Vector2 mouse)
    {
        float hx = node.Position.X, hy = node.Position.Y;
        float hw = node.Size.X, hh = node.Size.Y;
        float left = hx - hw / 2f, right = left + hw;
        float top  = hy - hh / 2f, bottom = top  + hh;
        const float R = 7f; // handle radius in pixels

        bool Near(float ax, float ay) => MathF.Abs(mouse.X - ax) <= R && MathF.Abs(mouse.Y - ay) <= R;

        if (Near(left,         top))    return DragMode.ResizeTL;
        if (Near((left+right)/2, top))  return DragMode.ResizeT;
        if (Near(right,        top))    return DragMode.ResizeTR;
        if (Near(right, (top+bottom)/2)) return DragMode.ResizeR;
        if (Near(right,        bottom)) return DragMode.ResizeBR;
        if (Near((left+right)/2,bottom)) return DragMode.ResizeB;
        if (Near(left,         bottom)) return DragMode.ResizeBL;
        if (Near(left, (top+bottom)/2)) return DragMode.ResizeL;

        // Body
        if (mouse.X >= left && mouse.X <= right && mouse.Y >= top && mouse.Y <= bottom)
            return DragMode.Move;

        return DragMode.None;
    }

    void ApplyDrag(SceneNode node, Vector2 delta)
    {
        float ox  = _dragNodeStart.X, oy  = _dragNodeStart.Y;
        float osw = _dragSizeStart.X, osh = _dragSizeStart.Y;

        switch (_dragMode)
        {
            case DragMode.Move:
                node.Position = new(ox + delta.X, oy + delta.Y, node.Position.Z);
                break;

            case DragMode.ResizeBR:
                node.Size     = new(Math.Max(8, osw + delta.X), Math.Max(8, osh + delta.Y));
                break;
            case DragMode.ResizeTL:
                node.Size     = new(Math.Max(8, osw - delta.X), Math.Max(8, osh - delta.Y));
                node.Position = new(ox + (osw - node.Size.X) / 2f,
                                    oy + (osh - node.Size.Y) / 2f, node.Position.Z);
                break;
            case DragMode.ResizeTR:
                node.Size     = new(Math.Max(8, osw + delta.X), Math.Max(8, osh - delta.Y));
                node.Position = new(ox, oy + (osh - node.Size.Y) / 2f, node.Position.Z);
                break;
            case DragMode.ResizeBL:
                node.Size     = new(Math.Max(8, osw - delta.X), Math.Max(8, osh + delta.Y));
                node.Position = new(ox + (osw - node.Size.X) / 2f, oy, node.Position.Z);
                break;
            case DragMode.ResizeT:
                node.Size     = new(osw, Math.Max(8, osh - delta.Y));
                node.Position = new(ox, oy + (osh - node.Size.Y) / 2f, node.Position.Z);
                break;
            case DragMode.ResizeB:
                node.Size     = new(osw, Math.Max(8, osh + delta.Y));
                break;
            case DragMode.ResizeL:
                node.Size     = new(Math.Max(8, osw - delta.X), osh);
                node.Position = new(ox + (osw - node.Size.X) / 2f, oy, node.Position.Z);
                break;
            case DragMode.ResizeR:
                node.Size     = new(Math.Max(8, osw + delta.X), osh);
                break;
        }
    }

    SceneNode? FindNodeAt(Vector2 pt)
    {
        foreach (var node in _scene.Root.Descendants().Skip(1).Reverse())
        {
            if (!node.Visible) continue;
            if (node.Type == NodeType.Sprite2D)
            {
                var tl = new Vector2(node.Position.X - node.Size.X / 2f, node.Position.Y - node.Size.Y / 2f);
                var br = tl + node.Size;
                if (pt.X >= tl.X && pt.X <= br.X && pt.Y >= tl.Y && pt.Y <= br.Y)
                    return node;
            }
            else
            {
                if (Vector2.Distance(new(node.Position.X, node.Position.Y), pt) < 10f)
                    return node;
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scene rendering into the RenderTexture
    // ─────────────────────────────────────────────────────────────────────────

    void RenderViewportScene()
    {
        BeginTextureMode(_vpTex);
        ClearBackground(new Color { r = 28, g = 30, b = 36, a = 255 });
        if (_vpIs3D) Render3DScene(); else Render2DScene();
        EndTextureMode();
    }

    void Render2DScene()
    {
        int gs = 32;
        var gc = new Color { r = 55, g = 58, b = 70, a = 255 };
        for (int x = 0; x < _vpW; x += gs) DrawLine(x, 0, x, _vpH, gc);
        for (int y = 0; y < _vpH; y += gs) DrawLine(0, y, _vpW, y, gc);

        var axCol = new Color { r = 85, g = 90, b = 110, a = 200 };
        DrawLine(0, _vpH / 2, _vpW, _vpH / 2, axCol);
        DrawLine(_vpW / 2, 0, _vpW / 2, _vpH, axCol);

        foreach (var node in _scene.Root.Descendants().Skip(1))
        {
            if (!node.Visible) continue;
            Draw2DNode(node);
        }

        // Particles are drawn after all solid geometry so they blend on top
        _particles.Draw(_scene.Root);
    }

    void Draw2DNode(SceneNode node)
    {
        bool sel = node == _scene.Selected;

        switch (node.Type)
        {
            case NodeType.Sprite2D:
            {
                var c   = node.Color;
                var col = new Color { r=(byte)(c.X*255), g=(byte)(c.Y*255), b=(byte)(c.Z*255), a=(byte)(c.W*255) };
                var rect = new Rectangle(
                    node.Position.X - node.Size.X * .5f, node.Position.Y - node.Size.Y * .5f,
                    node.Size.X, node.Size.Y);
                DrawRectangleRec(rect, col);

                if (sel)
                {
                    DrawSelectionHandles(rect);
                }
                else
                {
                    DrawRectangleLinesEx(rect, 1f, new Color { r=255, g=255, b=255, a=25 });
                }

                DrawText(node.Name,
                    (int)(rect.x + 3), (int)(rect.y + 3), 9,
                    new Color { r=255, g=255, b=255, a=150 });

                // Show script indicator
                if (node.LuaScript != null)
                    DrawText("[S]", (int)(rect.x + rect.width - 16), (int)(rect.y + 3), 9,
                        new Color { r=120, g=220, b=120, a=200 });
                break;
            }

            case NodeType.Camera2D:
            {
                int cx = (int)node.Position.X, cy = (int)node.Position.Y;
                var cc = new Color { r=200, g=200, b=120, a=sel?(byte)255:(byte)180 };
                DrawRectangleLinesEx(new Rectangle(cx-28, cy-20, 56, 40), 1.5f, cc);
                int m = 6;
                DrawLine(cx-28,cy-20, cx-28+m,cy-20, cc); DrawLine(cx-28,cy-20, cx-28,cy-20+m, cc);
                DrawLine(cx+28,cy-20, cx+28-m,cy-20, cc); DrawLine(cx+28,cy-20, cx+28,cy-20+m, cc);
                DrawLine(cx-28,cy+20, cx-28+m,cy+20, cc); DrawLine(cx-28,cy+20, cx-28,cy+20-m, cc);
                DrawLine(cx+28,cy+20, cx+28-m,cy+20, cc); DrawLine(cx+28,cy+20, cx+28,cy+20-m, cc);
                DrawText(node.Name, cx-24, cy+24, 9, cc);
                break;
            }

            case NodeType.Label2D:
            {
                var lc = node.Color;
                var col = new Color { r=(byte)(lc.X*255), g=(byte)(lc.Y*255), b=(byte)(lc.Z*255), a=(byte)(lc.W*255) };
                int fs = Math.Clamp((int)node.FontSize, 8, 80);
                DrawText(node.LabelText, (int)node.Position.X, (int)node.Position.Y, fs, col);
                if (sel)
                {
                    int tw = MeasureText(node.LabelText, fs);
                    DrawSelectionHandles(new Rectangle(node.Position.X-2, node.Position.Y-2, tw+4, fs+4));
                }
                break;
            }

            case NodeType.PointLight:
            {
                int lx = (int)node.Position.X, ly = (int)node.Position.Y;
                var lc = new Color { r=(byte)(node.LightColor.X*255), g=(byte)(node.LightColor.Y*255), b=(byte)(node.LightColor.Z*255), a=60 };
                DrawCircleLines(lx, ly, node.LightRange*4, lc);
                lc.a = sel ? (byte)255 : (byte)200;
                DrawCircleLines(lx, ly, 5, lc);
                DrawText(node.Name, lx+7, ly-8, 9, lc);
                break;
            }

            case NodeType.ParticleEmitter:
            {
                int ex = (int)node.Position.X, ey = (int)node.Position.Y;
                var ec = sel
                    ? new Color { r=255, g=220, b=60,  a=255 }
                    : new Color { r=255, g=165, b=40,  a=200 };
                // Cross + circle gizmo
                DrawCircleLines(ex, ey, 8f, ec);
                DrawLine(ex - 12, ey, ex + 12, ey, ec);
                DrawLine(ex, ey - 12, ex, ey + 12, ec);
                DrawText(node.Name, ex + 13, ey - 7, 9, ec);
                if (sel) DrawCircleLines(ex, ey, 10f, YELLOW);
                break;
            }

            default:
            {
                int nx = (int)node.Position.X, ny = (int)node.Position.Y;
                var nc = sel ? new Color{r=255,g=220,b=60,a=255} : new Color{r=160,g=160,b=160,a=200};
                DrawPoly(new(nx, ny), 4, 6f, 45f, nc);
                DrawText(node.Name, nx+9, ny-7, 9, nc);
                break;
            }
        }
    }

    void DrawSelectionHandles(Rectangle r)
    {
        // Dashed outline
        DrawRectangleLinesEx(r, 1.5f, YELLOW);
        // 8 handles
        const int hs = 6;
        void H(float x, float y)
        {
            DrawRectangle((int)(x - hs/2f), (int)(y - hs/2f), hs, hs, YELLOW);
            DrawRectangleLinesEx(new Rectangle(x-hs/2f-1, y-hs/2f-1, hs+2, hs+2), 1f,
                new Color{r=0,g=0,b=0,a=180});
        }
        H(r.x,              r.y);
        H(r.x+r.width/2f,  r.y);
        H(r.x+r.width,     r.y);
        H(r.x+r.width,     r.y+r.height/2f);
        H(r.x+r.width,     r.y+r.height);
        H(r.x+r.width/2f,  r.y+r.height);
        H(r.x,             r.y+r.height);
        H(r.x,             r.y+r.height/2f);
    }

    void Render3DScene()
    {
        BeginMode3D(_cam3D);
        DrawGrid(20, 1.0f);
        foreach (var node in _scene.Root.Descendants().Skip(1))
        {
            if (!node.Visible) continue;
            bool sel = node == _scene.Selected;
            if (node.Type is NodeType.Node3D or NodeType.MeshInstance)
            {
                var c = node.Color;
                var col = new Color{r=(byte)(c.X*255),g=(byte)(c.Y*255),b=(byte)(c.Z*255),a=(byte)(c.W*255)};
                DrawCube(node.Position, node.Scale.X, node.Scale.Y, node.Scale.Z, col);
                if (sel) DrawCubeWires(node.Position, node.Scale.X*1.01f, node.Scale.Y*1.01f, node.Scale.Z*1.01f, YELLOW);
            }
        }
        EndMode3D();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Content Browser
    // ═════════════════════════════════════════════════════════════════════════

    void DrawAssetBrowser()
    {
        ImGui.SetNextWindowPos(new Vector2(282, 562),  ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(390, 160), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Content Browser")) { ImGui.End(); return; }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.17f, 0.22f, 1f));
        if (_assetCwd != _assetRoot && ImGui.Button(".."))
        {
            var parent = Directory.GetParent(_assetCwd);
            if (parent != null) _assetCwd = parent.FullName;
        }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled("assets/" + Path.GetRelativePath(_assetRoot, _assetCwd).Replace('\\', '/'));

        ImGui.Separator();

        if (!Directory.Exists(_assetCwd)) { ImGui.TextColored(new(1,0.4f,0.4f,1), "Dir not found"); ImGui.End(); return; }

        foreach (var dir in Directory.GetDirectories(_assetCwd))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.75f, 0.85f, 1f, 1f));
            bool o = ImGui.Selectable("[/] " + Path.GetFileName(dir), false, ImGuiSelectableFlags.AllowDoubleClick);
            ImGui.PopStyleColor();
            if (o && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) _assetCwd = dir;
        }
        try
        {
            foreach (var file in Directory.GetFiles(_assetCwd))
            {
                string fn = Path.GetFileName(file), ext = Path.GetExtension(file).ToLowerInvariant();
                var col = ext switch
                {
                    ".png" or ".jpg" or ".bmp" => new Vector4(0.85f, 0.95f, 0.70f, 1f),
                    ".wav" or ".ogg" or ".mp3" => new Vector4(0.90f, 0.75f, 0.95f, 1f),
                    ".cs" or ".lua"            => new Vector4(0.70f, 0.90f, 1.00f, 1f),
                    ".json" or ".scene"        => new Vector4(1.00f, 0.88f, 0.55f, 1f),
                    _                          => new Vector4(0.80f, 0.80f, 0.80f, 1f),
                };
                ImGui.PushStyleColor(ImGuiCol.Text, col);
                bool cl = ImGui.Selectable(FileIcon(ext) + " " + fn, false, ImGuiSelectableFlags.AllowDoubleClick);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(file);
                if (cl && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) Log($"Open: {fn}");
            }
        }
        catch (Exception ex) { ImGui.TextColored(new(1,.4f,.4f,1), ex.Message); }

        ImGui.End();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Console
    // ═════════════════════════════════════════════════════════════════════════

    void DrawConsole()
    {
        ImGui.SetNextWindowPos(new Vector2(674, 562),  ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(390, 160), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Console")) { ImGui.End(); return; }

        if (ImGui.SmallButton("Clear")) _log.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _logAutoScroll);
        ImGui.Separator();

        ImGui.BeginChild("##log", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (var e in _log)
        {
            var (text, col) = e.Level switch
            {
                LogLevel.Warning => ($"[WARN]  {e.Message}", new Vector4(1f, 0.85f, 0.20f, 1f)),
                LogLevel.Error   => ($"[ERROR] {e.Message}", new Vector4(1f, 0.38f, 0.28f, 1f)),
                _                => ($"[INFO]  {e.Message}", new Vector4(0.80f, 0.83f, 0.88f, 1f)),
            };
            ImGui.TextColored(col, text);
        }
        if (_logAutoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f)
            ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
        ImGui.End();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    void Log(string msg, LogLevel level = LogLevel.Info)
    {
        _log.Add(new LogEntry(level, msg, _totalTime));
        if (_log.Count > 1000) _log.RemoveAt(0);
    }

    void TogglePlay()
    {
        if (_playing) { _playing = false; _playTempFile = null; Log("Play stopped"); return; }

        // Each Play launch gets its own temp file so multiple windows can coexist.
        string tempFile = Path.Combine(Path.GetTempPath(), $"re_scene_{Guid.NewGuid():N}.json");
        try
        {
            _scene.SaveTo(tempFile);
        }
        catch (Exception ex)
        {
            Log($"Could not save scene: {ex.Message}", LogLevel.Error);
            return;
        }

        _playTempFile = tempFile;   // remember for Ctrl+S hot-reload

        string? exePath = FindRayEngineExe();
        ProcessStartInfo psi;

        if (exePath != null)
        {
            psi = new ProcessStartInfo(exePath, $"--play \"{tempFile}\"");
        }
        else
        {
            string projDir = FindProjectDir();
            psi = new ProcessStartInfo("dotnet",
                $"run --project \"{projDir}\" --no-build -- --play \"{tempFile}\"");
        }

        psi.UseShellExecute = false;
        try
        {
            Process.Start(psi);
            Log($"Play started  (Ctrl+S = hot-reload)  [{Path.GetFileName(tempFile)}]");
        }
        catch (Exception ex)
        {
            Log($"Could not launch play window: {ex.Message}", LogLevel.Error);
            _playTempFile = null;
        }
    }

    static string? FindRayEngineExe()
    {
        string name = OperatingSystem.IsWindows() ? "RayEngine.exe" : "RayEngine";
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    static string FindProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.csproj").Any()) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    void NewScene()
    {
        _scene = new Scene();
        _particles.Reset();
        _playTempFile = null;
        Log("New scene created");
    }

    void SaveScene()
    {
        string path = Path.Combine(_assetRoot, "scenes", _scene.Name + ".json");
        _scene.SaveTo(path);
        Log($"Scene saved → {path}");

        // Hot-reload: push updated scene to any running play window
        if (_playTempFile != null)
        {
            try
            {
                _scene.SaveTo(_playTempFile);
                Log("Hot-reload pushed to play window.");
            }
            catch (Exception ex)
            {
                Log($"Hot-reload push failed: {ex.Message}", LogLevel.Warning);
            }
        }
    }

    void DuplicateNode(SceneNode src)
    {
        var dup = new SceneNode
        {
            Name      = src.Name + "_copy",
            Type      = src.Type,
            Visible   = src.Visible,
            Position  = src.Position + new Vector3(16, 16, 0),
            Rotation  = src.Rotation,
            Scale     = src.Scale,
            Color     = src.Color,
            Size      = src.Size,
            LabelText = src.LabelText,
            LuaScript = src.LuaScript,
            // Particle Emitter
            EmitRate         = src.EmitRate,
            EmitLifetime     = src.EmitLifetime,
            EmitSpeed        = src.EmitSpeed,
            EmitSpread       = src.EmitSpread,
            EmitGravity      = src.EmitGravity,
            EmitDirection    = src.EmitDirection,
            EmitSizeStart    = src.EmitSizeStart,
            EmitSizeEnd      = src.EmitSizeEnd,
            EmitMaxParticles = src.EmitMaxParticles,
            EmitColorStart   = src.EmitColorStart,
            EmitColorEnd     = src.EmitColorEnd,
        };
        (src.Parent ?? _scene.Root).AddChild(dup);
        _scene.Selected = dup;
        Log($"Duplicated '{src.Name}' → '{dup.Name}'");
    }

    // Coloured X/Y/Z drag field (same as before)
    static bool DragVec3(string label, ref Vector3 v,
        float speed, float min = float.MinValue, float max = float.MaxValue)
    {
        bool changed = false;
        float lw = ImGui.CalcTextSize(label).X + ImGui.GetStyle().ItemSpacing.X;
        float fw = Math.Max((ImGui.GetContentRegionAvail().X - lw) / 3f - 2f, 30f);

        ImGui.Text(label); ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.40f, 0.12f, 0.12f, 1f));
        ImGui.SetNextItemWidth(fw);
        if (ImGui.DragFloat("##X" + label, ref v.X, speed, min, max, "X:%.2f")) changed = true;
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 2f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.38f, 0.12f, 1f));
        ImGui.SetNextItemWidth(fw);
        if (ImGui.DragFloat("##Y" + label, ref v.Y, speed, min, max, "Y:%.2f")) changed = true;
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 2f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.20f, 0.45f, 1f));
        ImGui.SetNextItemWidth(fw);
        if (ImGui.DragFloat("##Z" + label, ref v.Z, speed, min, max, "Z:%.2f")) changed = true;
        ImGui.PopStyleColor();

        return changed;
    }

    static string NodeIcon(NodeType t) => t switch
    {
        NodeType.Sprite2D        => "[S]",
        NodeType.Label2D         => "[T]",
        NodeType.Camera2D        => "[C]",
        NodeType.AudioPlayer     => "[A]",
        NodeType.Node3D          => "[3]",
        NodeType.MeshInstance    => "[M]",
        NodeType.PointLight      => "[L]",
        NodeType.ParticleEmitter => "[P]",
        _                        => "[o]",
    };

    static Vector4 NodeTypeColor(NodeType t) => t switch
    {
        NodeType.Sprite2D        => new(0.60f, 0.90f, 0.60f, 1f),
        NodeType.Label2D         => new(1.00f, 0.90f, 0.50f, 1f),
        NodeType.Camera2D        => new(0.90f, 0.85f, 0.30f, 1f),
        NodeType.AudioPlayer     => new(0.85f, 0.55f, 0.95f, 1f),
        NodeType.Node3D          => new(0.50f, 0.80f, 1.00f, 1f),
        NodeType.MeshInstance    => new(0.70f, 0.90f, 1.00f, 1f),
        NodeType.PointLight      => new(1.00f, 0.95f, 0.50f, 1f),
        NodeType.ParticleEmitter => new(1.00f, 0.70f, 0.20f, 1f),
        _                        => new(0.75f, 0.75f, 0.75f, 1f),
    };

    static string FileIcon(string ext) => ext switch
    {
        ".png" or ".jpg" or ".bmp" => "[img]",
        ".wav" or ".ogg" or ".mp3" => "[snd]",
        ".cs"                       => "[cs] ",
        ".lua"                      => "[lua]",
        ".json" or ".scene"         => "[jsn]",
        ".ttf" or ".otf"            => "[fnt]",
        _                           => "[   ]",
    };

    public void Shutdown()
    {
        RlImGui.Shutdown();
        UnloadRenderTexture(_vpTex);
    }
}
