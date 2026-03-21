using System.Numerics;
using ImGuiNET;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;
using static Raylib_CsLo.RlGl;

namespace RayEngine;

/// <summary>
/// Bridges Raylib-CsLo with ImGui.NET.
/// Call Setup() once after InitWindow(), Begin()/End() each frame.
/// </summary>
internal static unsafe class RlImGui
{
    static Texture _fontTex;
    static bool    _ready;

    // Map Raylib key → ImGui key
    static readonly (KeyboardKey rl, ImGuiKey im)[] _keys =
    {
        (KeyboardKey.KEY_TAB,           ImGuiKey.Tab),
        (KeyboardKey.KEY_LEFT,          ImGuiKey.LeftArrow),
        (KeyboardKey.KEY_RIGHT,         ImGuiKey.RightArrow),
        (KeyboardKey.KEY_UP,            ImGuiKey.UpArrow),
        (KeyboardKey.KEY_DOWN,          ImGuiKey.DownArrow),
        (KeyboardKey.KEY_PAGE_UP,       ImGuiKey.PageUp),
        (KeyboardKey.KEY_PAGE_DOWN,     ImGuiKey.PageDown),
        (KeyboardKey.KEY_HOME,          ImGuiKey.Home),
        (KeyboardKey.KEY_END,           ImGuiKey.End),
        (KeyboardKey.KEY_INSERT,        ImGuiKey.Insert),
        (KeyboardKey.KEY_DELETE,        ImGuiKey.Delete),
        (KeyboardKey.KEY_BACKSPACE,     ImGuiKey.Backspace),
        (KeyboardKey.KEY_SPACE,         ImGuiKey.Space),
        (KeyboardKey.KEY_ENTER,         ImGuiKey.Enter),
        (KeyboardKey.KEY_ESCAPE,        ImGuiKey.Escape),
        (KeyboardKey.KEY_LEFT_CONTROL,  ImGuiKey.LeftCtrl),
        (KeyboardKey.KEY_LEFT_SHIFT,    ImGuiKey.LeftShift),
        (KeyboardKey.KEY_LEFT_ALT,      ImGuiKey.LeftAlt),
        (KeyboardKey.KEY_RIGHT_CONTROL, ImGuiKey.RightCtrl),
        (KeyboardKey.KEY_RIGHT_SHIFT,   ImGuiKey.RightShift),
        (KeyboardKey.KEY_RIGHT_ALT,     ImGuiKey.RightAlt),
        (KeyboardKey.KEY_A,             ImGuiKey.A),
        (KeyboardKey.KEY_C,             ImGuiKey.C),
        (KeyboardKey.KEY_V,             ImGuiKey.V),
        (KeyboardKey.KEY_X,             ImGuiKey.X),
        (KeyboardKey.KEY_Y,             ImGuiKey.Y),
        (KeyboardKey.KEY_Z,             ImGuiKey.Z),
        (KeyboardKey.KEY_F1,            ImGuiKey.F1),
        (KeyboardKey.KEY_F2,            ImGuiKey.F2),
        (KeyboardKey.KEY_F5,            ImGuiKey.F5),
        (KeyboardKey.KEY_F11,           ImGuiKey.F11),
    };

    // ─────────────────────────────────────────────────────────────────────────
    public static void Setup()
    {
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        // Only allow windows to be moved by dragging the title bar
        io.ConfigWindowsMoveFromTitleBarOnly = true;

        ApplyStyle();
        BuildFontAtlas();
        _ready = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    static void BuildFontAtlas()
    {
        var io = ImGui.GetIO();
        // Load slightly larger default font
        io.Fonts.AddFontDefault();

        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int w, out int h);

        var img = new Image
        {
            data    = pixels,
            width   = w,
            height  = h,
            mipmaps = 1,
            format  = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8,
        };
        _fontTex = LoadTextureFromImage(img);
        SetTextureFilter(_fontTex, TextureFilter.TEXTURE_FILTER_BILINEAR);

        io.Fonts.SetTexID(new IntPtr(_fontTex.id));
        io.Fonts.ClearTexData();
    }

    // ─────────────────────────────────────────────────────────────────────────
    public static void Begin()
    {
        if (!_ready) return;

        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(GetScreenWidth(), GetScreenHeight());
        io.DeltaTime   = MathF.Max(GetFrameTime(), 0.0001f);

        // Mouse
        io.MousePos     = GetMousePosition();
        io.MouseDown[0] = IsMouseButtonDown(MouseButton.MOUSE_BUTTON_LEFT);
        io.MouseDown[1] = IsMouseButtonDown(MouseButton.MOUSE_BUTTON_RIGHT);
        io.MouseDown[2] = IsMouseButtonDown(MouseButton.MOUSE_BUTTON_MIDDLE);
        io.MouseWheel  += GetMouseWheelMove();

        // Modifiers
        io.KeyCtrl  = IsKeyDown(KeyboardKey.KEY_LEFT_CONTROL)  || IsKeyDown(KeyboardKey.KEY_RIGHT_CONTROL);
        io.KeyShift = IsKeyDown(KeyboardKey.KEY_LEFT_SHIFT)    || IsKeyDown(KeyboardKey.KEY_RIGHT_SHIFT);
        io.KeyAlt   = IsKeyDown(KeyboardKey.KEY_LEFT_ALT)      || IsKeyDown(KeyboardKey.KEY_RIGHT_ALT);

        foreach (var (rl, im) in _keys)
            io.AddKeyEvent(im, IsKeyDown(rl));

        int ch;
        while ((ch = GetCharPressed()) != 0)
            io.AddInputCharacter((uint)ch);

        ImGui.NewFrame();
    }

    // ─────────────────────────────────────────────────────────────────────────
    public static void End()
    {
        if (!_ready) return;
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    // ─────────────────────────────────────────────────────────────────────────
    static void RenderDrawData(ImDrawDataPtr data)
    {
        int sh = GetScreenHeight();

        // Flush any pending Raylib geometry so it doesn't interfere
        rlDrawRenderBatchActive();

        rlDisableBackfaceCulling();
        rlDisableDepthTest();

        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            var vtx  = (ImDrawVert*)list.VtxBuffer.Data;
            var idx  = (ushort*)   list.IdxBuffer.Data;

            for (int ci = 0; ci < list.CmdBuffer.Size; ci++)
            {
                var cmd = list.CmdBuffer[ci];
                if (cmd.UserCallback != IntPtr.Zero) continue;

                // ── Scissor ──────────────────────────────────────────────────
                // ImGui Y=0 is top; OpenGL scissor Y=0 is bottom → flip Y
                rlEnableScissorTest();
                rlScissor(
                    (int)cmd.ClipRect.X,
                    sh - (int)cmd.ClipRect.W,
                    (int)(cmd.ClipRect.Z - cmd.ClipRect.X),
                    (int)(cmd.ClipRect.W - cmd.ClipRect.Y));

                // ── Geometry ─────────────────────────────────────────────────
                // IMPORTANT: rlBegin() resets the active texture to Raylib's
                // default 1×1 white whenever the draw-mode changes (which it
                // does on the first call after any batch flush because Raylib
                // resets to RL_QUADS).  Call rlSetTexture AFTER rlBegin so
                // the reset is immediately overwritten.
                rlBegin(4); // RL_TRIANGLES
                rlSetTexture((uint)(nint)cmd.TextureId);

                for (uint e = 0; e < cmd.ElemCount; e++)
                {
                    ushort    vi = idx[cmd.IdxOffset + e];
                    ImDrawVert v  = vtx[cmd.VtxOffset + vi];
                    uint c = v.col;
                    rlColor4ub((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24));
                    rlTexCoord2f(v.uv.X, v.uv.Y);
                    rlVertex2f(v.pos.X, v.pos.Y);
                }

                rlEnd();

                // ── Per-command flush ─────────────────────────────────────────
                // Raylib's batch applies the *current* GL scissor rect at flush
                // time, not at vertex-submission time.  Without flushing here,
                // every command's triangles would be clipped by the LAST scissor
                // set in the loop.  Flushing after each command makes the clip
                // rect match the command it was set for.
                rlDrawRenderBatchActive();
            }
        }

        rlSetTexture(0);
        rlDisableScissorTest();
        // Do NOT re-enable depth test / backface culling here —
        // BeginDrawing() disabled them for 2-D rendering and we must leave
        // that state intact for anything Raylib draws after us.
    }

    // ─────────────────────────────────────────────────────────────────────────
    public static void Shutdown()
    {
        if (!_ready) return;
        UnloadTexture(_fontTex);
        ImGui.DestroyContext();
        _ready = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    static void ApplyStyle()
    {
        ImGui.StyleColorsDark();
        var s = ImGui.GetStyle();
        s.WindowRounding    = 5f;
        s.FrameRounding     = 3f;
        s.GrabRounding      = 3f;
        s.TabRounding       = 4f;
        s.ScrollbarRounding = 3f;
        s.WindowBorderSize  = 1f;
        s.FrameBorderSize   = 0f;
        s.ItemSpacing       = new Vector2(6, 4);
        s.FramePadding      = new Vector2(6, 4);

        var c = s.Colors;
        c[(int)ImGuiCol.WindowBg]           = V(0.13f, 0.14f, 0.17f);
        c[(int)ImGuiCol.ChildBg]            = V(0.10f, 0.11f, 0.14f);
        c[(int)ImGuiCol.PopupBg]            = V4(0.12f, 0.13f, 0.16f, 0.98f);
        c[(int)ImGuiCol.TitleBg]            = V(0.08f, 0.08f, 0.11f);
        c[(int)ImGuiCol.TitleBgActive]      = V(0.10f, 0.17f, 0.30f);
        c[(int)ImGuiCol.TitleBgCollapsed]   = V(0.08f, 0.08f, 0.11f);
        c[(int)ImGuiCol.MenuBarBg]          = V(0.09f, 0.09f, 0.11f);
        c[(int)ImGuiCol.ScrollbarBg]        = V4(0.09f, 0.09f, 0.11f, 0.5f);
        c[(int)ImGuiCol.ScrollbarGrab]      = V(0.24f, 0.26f, 0.32f);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = V(0.32f, 0.35f, 0.44f);
        c[(int)ImGuiCol.ScrollbarGrabActive] = V(0.38f, 0.42f, 0.54f);
        c[(int)ImGuiCol.FrameBg]            = V(0.17f, 0.19f, 0.24f);
        c[(int)ImGuiCol.FrameBgHovered]     = V(0.24f, 0.27f, 0.34f);
        c[(int)ImGuiCol.FrameBgActive]      = V(0.20f, 0.23f, 0.30f);
        c[(int)ImGuiCol.Header]             = V4(0.22f, 0.33f, 0.55f, 0.75f);
        c[(int)ImGuiCol.HeaderHovered]      = V4(0.28f, 0.41f, 0.66f, 0.80f);
        c[(int)ImGuiCol.HeaderActive]       = V(0.20f, 0.32f, 0.54f);
        c[(int)ImGuiCol.Tab]                  = V4(0.12f, 0.18f, 0.30f, 0.86f);
        c[(int)ImGuiCol.TabHovered]           = V4(0.22f, 0.36f, 0.60f, 0.80f);
        c[(int)ImGuiCol.TabSelected]          = V(0.18f, 0.28f, 0.48f);
        c[(int)ImGuiCol.TabDimmed]            = V4(0.10f, 0.14f, 0.22f, 0.97f);
        c[(int)ImGuiCol.TabDimmedSelected]    = V(0.16f, 0.22f, 0.36f);
        c[(int)ImGuiCol.DockingPreview]     = V4(0.28f, 0.52f, 0.90f, 0.55f);
        c[(int)ImGuiCol.DockingEmptyBg]     = V(0.10f, 0.11f, 0.14f);
        c[(int)ImGuiCol.Button]             = V4(0.18f, 0.30f, 0.52f, 0.75f);
        c[(int)ImGuiCol.ButtonHovered]      = V(0.26f, 0.42f, 0.70f);
        c[(int)ImGuiCol.ButtonActive]       = V(0.16f, 0.28f, 0.50f);
        c[(int)ImGuiCol.CheckMark]          = V(0.44f, 0.70f, 1.00f);
        c[(int)ImGuiCol.SliderGrab]         = V(0.34f, 0.56f, 0.90f);
        c[(int)ImGuiCol.SliderGrabActive]   = V(0.44f, 0.66f, 1.00f);
        c[(int)ImGuiCol.Separator]          = V(0.22f, 0.24f, 0.29f);
        c[(int)ImGuiCol.SeparatorHovered]   = V4(0.32f, 0.44f, 0.64f, 0.78f);
        c[(int)ImGuiCol.SeparatorActive]    = V(0.32f, 0.50f, 0.80f);
        c[(int)ImGuiCol.ResizeGrip]         = V4(0.22f, 0.36f, 0.62f, 0.22f);
        c[(int)ImGuiCol.ResizeGripHovered]  = V4(0.27f, 0.44f, 0.74f, 0.67f);
        c[(int)ImGuiCol.ResizeGripActive]   = V4(0.27f, 0.44f, 0.74f, 0.95f);
        c[(int)ImGuiCol.Text]               = V(0.92f, 0.94f, 0.97f);
        c[(int)ImGuiCol.TextDisabled]       = V(0.50f, 0.53f, 0.60f);
        c[(int)ImGuiCol.Border]             = V4(0.22f, 0.24f, 0.30f, 0.60f);
    }

    static Vector4 V(float r, float g, float b)             => new(r, g, b, 1f);
    static Vector4 V4(float r, float g, float b, float a)   => new(r, g, b, a);
}
