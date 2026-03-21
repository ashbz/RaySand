using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RayEngine;

// ─────────────────────────────────────────────────────────────────────────────
// Node types
// ─────────────────────────────────────────────────────────────────────────────
public enum NodeType
{
    Node, Sprite2D, Label2D, Camera2D, AudioPlayer,
    Node3D, MeshInstance, PointLight,
    ParticleEmitter,
}

// ─────────────────────────────────────────────────────────────────────────────
// Flat DTO used for JSON serialisation (no circular parent refs)
// ─────────────────────────────────────────────────────────────────────────────
public class NodeData
{
    public string          Name      { get; set; } = "Node";
    public string          Type      { get; set; } = "Node";
    public bool            Visible   { get; set; } = true;
    public float[]         Position  { get; set; } = [0, 0, 0];
    public float[]         Rotation  { get; set; } = [0, 0, 0];
    public float[]         Scale     { get; set; } = [1, 1, 1];
    public float[]         Color     { get; set; } = [1, 1, 1, 1];
    public float[]         Size      { get; set; } = [64, 64];
    public string          LabelText { get; set; } = "";
    public float           FontSize  { get; set; } = 16f;
    public string?         LuaScript { get; set; }
    public float[]         LightColor     { get; set; } = [1, 0.95f, 0.8f, 1];
    public float           LightRange     { get; set; } = 10f;
    public float           LightIntensity { get; set; } = 1f;

    // Particle Emitter
    public float   EmitRate        { get; set; } = 30f;
    public float   EmitLifetime    { get; set; } = 1.2f;
    public float   EmitSpeed       { get; set; } = 80f;
    public float   EmitSpread      { get; set; } = 25f;
    public float   EmitGravity     { get; set; } = 60f;
    public float   EmitDirection   { get; set; } = -90f;
    public float   EmitSizeStart   { get; set; } = 7f;
    public float   EmitSizeEnd     { get; set; } = 0f;
    public int     EmitMaxParticles { get; set; } = 300;
    public float[] EmitColorStart  { get; set; } = [1f, 0.70f, 0.10f, 1f];
    public float[] EmitColorEnd    { get; set; } = [1f, 0.10f, 0.00f, 0f];

    public List<NodeData>  Children  { get; set; } = [];
}

// ─────────────────────────────────────────────────────────────────────────────
// Scene node (runtime)
// ─────────────────────────────────────────────────────────────────────────────
public class SceneNode
{
    public Guid     Id       { get; }          = Guid.NewGuid();
    public string   Name     { get; set; }     = "Node";
    public NodeType Type     { get; set; }     = NodeType.Node;
    public bool     Visible  { get; set; }     = true;
    public bool     Expanded { get; set; }     = true;

    public SceneNode?       Parent   { get; internal set; }
    public List<SceneNode>  Children { get; }  = new();

    // Transform
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Vector3 Rotation { get; set; } = Vector3.Zero;
    public Vector3 Scale    { get; set; } = Vector3.One;

    // Sprite2D / Label
    public Vector4 Color       { get; set; } = new(1f, 1f, 1f, 1f);
    public Vector2 Size        { get; set; } = new(64f, 64f);
    public string? TexturePath { get; set; }

    // Label2D
    public string  LabelText { get; set; } = "Label";
    public float   FontSize  { get; set; } = 16f;

    // PointLight
    public float   LightRange     { get; set; } = 10f;
    public float   LightIntensity { get; set; } = 1f;
    public Vector4 LightColor     { get; set; } = new(1f, 0.95f, 0.8f, 1f);

    // AudioPlayer
    public string? AudioPath { get; set; }
    public bool    AutoPlay  { get; set; } = false;
    public float   Volume    { get; set; } = 1f;

    // Scripting
    public string? LuaScript { get; set; }

    // Particle Emitter
    public float   EmitRate        { get; set; } = 30f;
    public float   EmitLifetime    { get; set; } = 1.2f;
    public float   EmitSpeed       { get; set; } = 80f;
    public float   EmitSpread      { get; set; } = 25f;
    public float   EmitGravity     { get; set; } = 60f;
    public float   EmitDirection   { get; set; } = -90f;
    public float   EmitSizeStart   { get; set; } = 7f;
    public float   EmitSizeEnd     { get; set; } = 0f;
    public int     EmitMaxParticles { get; set; } = 300;
    public Vector4 EmitColorStart  { get; set; } = new(1f, 0.70f, 0.10f, 1f);
    public Vector4 EmitColorEnd    { get; set; } = new(1f, 0.10f, 0.00f, 0f);

    // ─────────────────────────────────────────────────────────────────────────
    public void AddChild(SceneNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void Remove()
    {
        Parent?.Children.Remove(this);
        Parent = null;
    }

    public IEnumerable<SceneNode> Descendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Descendants())
                yield return d;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Scene
// ─────────────────────────────────────────────────────────────────────────────
public class Scene
{
    public string     Name     { get; set; } = "Untitled Scene";
    public SceneNode  Root     { get; }      = new() { Name = "Scene", Expanded = true };
    public SceneNode? Selected { get; set; }

    // ── Mutation helpers ─────────────────────────────────────────────────────
    public SceneNode AddNode(NodeType type, string? name = null, SceneNode? parent = null)
    {
        var node = new SceneNode { Name = name ?? type.ToString(), Type = type };
        (parent ?? Root).AddChild(node);
        return node;
    }

    public void DeleteSelected()
    {
        if (Selected == null || Selected == Root) return;
        var next = Selected.Parent;
        Selected.Remove();
        Selected = next;
    }

    public void MoveSelectedUp()
    {
        if (Selected?.Parent is not { } p) return;
        int i = p.Children.IndexOf(Selected);
        if (i > 0) (p.Children[i], p.Children[i - 1]) = (p.Children[i - 1], p.Children[i]);
    }

    public void MoveSelectedDown()
    {
        if (Selected?.Parent is not { } p) return;
        int i = p.Children.IndexOf(Selected);
        if (i < p.Children.Count - 1) (p.Children[i], p.Children[i + 1]) = (p.Children[i + 1], p.Children[i]);
    }

    // ── Serialisation ────────────────────────────────────────────────────────
    static readonly JsonSerializerOptions _json =
        new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public void SaveTo(string path)
    {
        var data = NodeToData(Root);
        File.WriteAllText(path, JsonSerializer.Serialize(data, _json));
    }

    public static Scene LoadFrom(string path)
    {
        var data = JsonSerializer.Deserialize<NodeData>(File.ReadAllText(path), _json)
                   ?? throw new Exception("Failed to deserialise scene");
        var s = new Scene(empty: true);
        s.Name = data.Name;
        foreach (var child in data.Children)
            s.Root.AddChild(DataToNode(child));
        return s;
    }

    static NodeData NodeToData(SceneNode n) => new()
    {
        Name           = n.Name,
        Type           = n.Type.ToString(),
        Visible        = n.Visible,
        Position       = [n.Position.X, n.Position.Y, n.Position.Z],
        Rotation       = [n.Rotation.X, n.Rotation.Y, n.Rotation.Z],
        Scale          = [n.Scale.X,    n.Scale.Y,    n.Scale.Z],
        Color          = [n.Color.X,    n.Color.Y,    n.Color.Z,    n.Color.W],
        Size           = [n.Size.X,     n.Size.Y],
        LabelText      = n.LabelText,
        FontSize       = n.FontSize,
        LuaScript      = n.LuaScript,
        LightColor     = [n.LightColor.X, n.LightColor.Y, n.LightColor.Z, n.LightColor.W],
        LightRange     = n.LightRange,
        LightIntensity = n.LightIntensity,
        EmitRate        = n.EmitRate,
        EmitLifetime    = n.EmitLifetime,
        EmitSpeed       = n.EmitSpeed,
        EmitSpread      = n.EmitSpread,
        EmitGravity     = n.EmitGravity,
        EmitDirection   = n.EmitDirection,
        EmitSizeStart   = n.EmitSizeStart,
        EmitSizeEnd     = n.EmitSizeEnd,
        EmitMaxParticles = n.EmitMaxParticles,
        EmitColorStart  = [n.EmitColorStart.X, n.EmitColorStart.Y, n.EmitColorStart.Z, n.EmitColorStart.W],
        EmitColorEnd    = [n.EmitColorEnd.X,   n.EmitColorEnd.Y,   n.EmitColorEnd.Z,   n.EmitColorEnd.W],
        Children        = n.Children.Select(NodeToData).ToList(),
    };

    static SceneNode DataToNode(NodeData d)
    {
        var cs = d.EmitColorStart;
        var ce = d.EmitColorEnd;
        var n = new SceneNode
        {
            Name           = d.Name,
            Type           = Enum.TryParse<NodeType>(d.Type, out var t) ? t : NodeType.Node,
            Visible        = d.Visible,
            Position       = new(d.Position[0], d.Position[1], d.Position[2]),
            Rotation       = new(d.Rotation[0], d.Rotation[1], d.Rotation[2]),
            Scale          = new(d.Scale[0],    d.Scale[1],    d.Scale[2]),
            Color          = new(d.Color[0],    d.Color[1],    d.Color[2],    d.Color[3]),
            Size           = new(d.Size[0],     d.Size[1]),
            LabelText      = d.LabelText,
            FontSize       = d.FontSize,
            LuaScript      = d.LuaScript,
            LightColor     = new(d.LightColor[0], d.LightColor[1], d.LightColor[2], d.LightColor[3]),
            LightRange     = d.LightRange,
            LightIntensity = d.LightIntensity,
            EmitRate        = d.EmitRate,
            EmitLifetime    = d.EmitLifetime,
            EmitSpeed       = d.EmitSpeed,
            EmitSpread      = d.EmitSpread,
            EmitGravity     = d.EmitGravity,
            EmitDirection   = d.EmitDirection,
            EmitSizeStart   = d.EmitSizeStart,
            EmitSizeEnd     = d.EmitSizeEnd,
            EmitMaxParticles = d.EmitMaxParticles,
            EmitColorStart  = cs.Length >= 4 ? new(cs[0], cs[1], cs[2], cs[3]) : new(1f, 0.7f, 0.1f, 1f),
            EmitColorEnd    = ce.Length >= 4 ? new(ce[0], ce[1], ce[2], ce[3]) : new(1f, 0.1f, 0f,   0f),
        };
        foreach (var child in d.Children)
            n.AddChild(DataToNode(child));
        return n;
    }

    // ── Default demo scene ────────────────────────────────────────────────────
    const string PlayerScript = """
        local speed = 200

        function update(dt)
            if Input.W then self.y = self.y - speed * dt end
            if Input.S then self.y = self.y + speed * dt end
            if Input.A then self.x = self.x - speed * dt end
            if Input.D then self.x = self.x + speed * dt end
        end
        """;

    public Scene() : this(empty: false) { }

    public Scene(bool empty)
    {
        if (empty) return;

        var world = AddNode(NodeType.Node, "World");

        var ground = AddNode(NodeType.Sprite2D, "Ground", world);
        ground.Position = new(400, 550, 0);
        ground.Size     = new(780, 40);
        ground.Color    = new(0.38f, 0.72f, 0.32f, 1f);

        var platform = AddNode(NodeType.Sprite2D, "Platform", world);
        platform.Position = new(350, 400, 0);
        platform.Size     = new(200, 22);
        platform.Color    = new(0.55f, 0.42f, 0.28f, 1f);

        var player = AddNode(NodeType.Sprite2D, "Player", world);
        player.Position  = new(220, 210, 0);
        player.Size      = new(40, 56);
        player.Color     = new(0.30f, 0.58f, 1.00f, 1f);
        player.LuaScript = PlayerScript;

        var label = AddNode(NodeType.Label2D, "PlayerLabel", world);
        label.Position  = new(188, 175, 0);
        label.LabelText = "Player";
        label.FontSize  = 12f;

        // Fire emitter on top of the platform
        var fire = AddNode(NodeType.ParticleEmitter, "FireEmitter", world);
        fire.Position       = new(350, 386, 0);
        fire.EmitRate       = 35f;
        fire.EmitLifetime   = 0.9f;
        fire.EmitSpeed      = 55f;
        fire.EmitSpread     = 22f;
        fire.EmitGravity    = -25f;
        fire.EmitDirection  = -90f;
        fire.EmitSizeStart  = 9f;
        fire.EmitSizeEnd    = 0f;
        fire.EmitMaxParticles = 200;
        fire.EmitColorStart = new(1f, 0.75f, 0.10f, 1f);
        fire.EmitColorEnd   = new(0.9f, 0.10f, 0f,   0f);

        var hud = AddNode(NodeType.Node, "HUD");
        var hp  = AddNode(NodeType.Sprite2D, "HealthBar", hud);
        hp.Position = new(80, 30, 0);
        hp.Size     = new(140, 18);
        hp.Color    = new(0.85f, 0.18f, 0.18f, 1f);

        var cam = AddNode(NodeType.Camera2D, "MainCamera", world);
        cam.Position = new(400, 300, 0);

        Selected = player;
    }
}
