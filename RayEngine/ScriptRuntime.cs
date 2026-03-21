using MoonSharp.Interpreter;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace RayEngine;

// ─────────────────────────────────────────────────────────────────────────────
// Lua-facing proxy for a scene node
// ─────────────────────────────────────────────────────────────────────────────
[MoonSharpUserData]
public class LuaNode
{
    readonly SceneNode _n;
    public LuaNode(SceneNode n) => _n = n;

    public float  x    { get => _n.Position.X;  set { var p = _n.Position; p.X = value; _n.Position = p; } }
    public float  y    { get => _n.Position.Y;  set { var p = _n.Position; p.Y = value; _n.Position = p; } }
    public float  z    { get => _n.Position.Z;  set { var p = _n.Position; p.Z = value; _n.Position = p; } }
    public float  w    { get => _n.Size.X;       set { var s = _n.Size;     s.X = value; _n.Size     = s; } }
    public float  h    { get => _n.Size.Y;       set { var s = _n.Size;     s.Y = value; _n.Size     = s; } }
    public string name { get => _n.Name;         set => _n.Name = value; }
    public bool visible { get => _n.Visible;     set => _n.Visible = value; }

    public void Move(float dx, float dy)
    {
        var p = _n.Position;
        p.X += dx; p.Y += dy;
        _n.Position = p;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Lua-facing input helper
// ─────────────────────────────────────────────────────────────────────────────
[MoonSharpUserData]
public class LuaInput
{
    // Direct property shortcuts (used as: if Input.W then ... end)
    public bool W     => IsKeyDown(KeyboardKey.KEY_W);
    public bool A     => IsKeyDown(KeyboardKey.KEY_A);
    public bool S     => IsKeyDown(KeyboardKey.KEY_S);
    public bool D     => IsKeyDown(KeyboardKey.KEY_D);
    public bool Space => IsKeyDown(KeyboardKey.KEY_SPACE);
    public bool Left  => IsKeyDown(KeyboardKey.KEY_LEFT);
    public bool Right => IsKeyDown(KeyboardKey.KEY_RIGHT);
    public bool Up    => IsKeyDown(KeyboardKey.KEY_UP);
    public bool Down  => IsKeyDown(KeyboardKey.KEY_DOWN);
    public bool Escape => IsKeyDown(KeyboardKey.KEY_ESCAPE);

    // Generic key test:  Input.IsDown("F") or Input.IsDown("ENTER")
    public bool IsDown(string key)
    {
        if (Enum.TryParse<KeyboardKey>("KEY_" + key.ToUpperInvariant(), out var kk))
            return IsKeyDown(kk);
        return false;
    }

    public bool IsPressed(string key)
    {
        if (Enum.TryParse<KeyboardKey>("KEY_" + key.ToUpperInvariant(), out var kk))
            return IsKeyPressed(kk);
        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Manages per-node Lua scripts
// ─────────────────────────────────────────────────────────────────────────────
public class ScriptRuntime
{
    record Entry(SceneNode Node, Script Script, bool HasUpdate, bool HasStart);

    readonly List<Entry> _entries = new();
    readonly List<string> _errors = new();

    public IReadOnlyList<string> Errors => _errors;

    public void Initialize(SceneNode root)
    {
        _entries.Clear();
        _errors.Clear();

        UserData.RegisterType<LuaNode>();
        UserData.RegisterType<LuaInput>();

        var input = new LuaInput();

        foreach (var node in root.Descendants())
        {
            if (string.IsNullOrWhiteSpace(node.LuaScript)) continue;

            var s = new Script(CoreModules.Preset_SoftSandbox | CoreModules.IO);
            s.Globals["self"]  = new LuaNode(node);
            s.Globals["Input"] = input;

            try
            {
                s.DoString(node.LuaScript);
            }
            catch (ScriptRuntimeException ex)
            {
                _errors.Add($"[{node.Name}] init: {ex.DecoratedMessage}");
                continue;
            }
            catch (SyntaxErrorException ex)
            {
                _errors.Add($"[{node.Name}] syntax: {ex.DecoratedMessage}");
                continue;
            }

            bool hasUpdate = s.Globals.Get("update").Type == DataType.Function;
            bool hasStart  = s.Globals.Get("start").Type  == DataType.Function;
            _entries.Add(new Entry(node, s, hasUpdate, hasStart));
        }
    }

    // Call start() once for each script
    public void Start()
    {
        foreach (var e in _entries)
        {
            if (!e.HasStart) continue;
            try { e.Script.Call(e.Script.Globals["start"]); }
            catch (ScriptRuntimeException ex) { _errors.Add($"[{e.Node.Name}] start: {ex.DecoratedMessage}"); }
        }
    }

    // Call update(dt) every game frame
    public void Update(float dt)
    {
        foreach (var e in _entries)
        {
            if (!e.HasUpdate) continue;
            try { e.Script.Call(e.Script.Globals["update"], (double)dt); }
            catch (ScriptRuntimeException ex) { _errors.Add($"[{e.Node.Name}] update: {ex.DecoratedMessage}"); }
        }
    }
}
