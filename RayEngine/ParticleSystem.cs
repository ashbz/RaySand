using System.Numerics;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace RayEngine;

// ─────────────────────────────────────────────────────────────────────────────
// Single particle (value type for cache-friendly iteration)
// ─────────────────────────────────────────────────────────────────────────────
struct Particle
{
    public Vector2 Pos, Vel;
    public float   Life, MaxLife;
    public bool    Active;
}

// ─────────────────────────────────────────────────────────────────────────────
// Per-emitter runtime state (pool + accumulator)
// ─────────────────────────────────────────────────────────────────────────────
class EmitterState
{
    readonly Particle[] _pool;
    int                 _cursor;
    float               _accum;
    readonly Random     _rng = new();

    public EmitterState(int maxParticles)
    {
        _pool = new Particle[Math.Clamp(maxParticles, 1, 5000)];
    }

    public void Update(SceneNode cfg, float dt)
    {
        // Age existing particles
        for (int i = 0; i < _pool.Length; i++)
        {
            ref var p = ref _pool[i];
            if (!p.Active) continue;
            p.Life -= dt;
            if (p.Life <= 0f) { p.Active = false; continue; }
            p.Vel.Y += cfg.EmitGravity * dt;
            p.Pos   += p.Vel * dt;
        }

        // Emit new ones
        _accum += dt * cfg.EmitRate;
        while (_accum >= 1f)
        {
            Emit(cfg);
            _accum -= 1f;
        }
    }

    void Emit(SceneNode cfg)
    {
        // Find an inactive slot (or overwrite oldest when pool is full)
        int start = _cursor;
        do
        {
            ref var p = ref _pool[_cursor % _pool.Length];
            _cursor++;
            if (!p.Active) { SpawnAt(ref p, cfg); return; }
        }
        while ((_cursor - start) < _pool.Length);

        // Pool full → overwrite
        ref var pw = ref _pool[_cursor % _pool.Length];
        _cursor++;
        SpawnAt(ref pw, cfg);
    }

    void SpawnAt(ref Particle p, SceneNode cfg)
    {
        float halfSpread = cfg.EmitSpread;
        float angleDeg   = cfg.EmitDirection + (_rng.NextSingle() * halfSpread * 2f - halfSpread);
        float angleRad   = angleDeg * MathF.PI / 180f;
        float speed      = cfg.EmitSpeed * (0.7f + _rng.NextSingle() * 0.6f);
        float life       = cfg.EmitLifetime * (0.75f + _rng.NextSingle() * 0.5f);
        p = new Particle
        {
            Active  = true,
            Pos     = new(cfg.Position.X, cfg.Position.Y),
            Vel     = new(MathF.Cos(angleRad) * speed, MathF.Sin(angleRad) * speed),
            Life    = life,
            MaxLife = life,
        };
    }

    public void Draw(SceneNode cfg)
    {
        var cs = cfg.EmitColorStart;
        var ce = cfg.EmitColorEnd;

        foreach (var p in _pool)
        {
            if (!p.Active) continue;
            float t  = 1f - p.Life / MathF.Max(p.MaxLife, 0.001f);
            float r  = cs.X + (ce.X - cs.X) * t;
            float g  = cs.Y + (ce.Y - cs.Y) * t;
            float b  = cs.Z + (ce.Z - cs.Z) * t;
            float a  = cs.W + (ce.W - cs.W) * t;
            float sz = cfg.EmitSizeStart + (cfg.EmitSizeEnd - cfg.EmitSizeStart) * t;

            var col = new Color
            {
                r = (byte)Math.Clamp((int)(r * 255), 0, 255),
                g = (byte)Math.Clamp((int)(g * 255), 0, 255),
                b = (byte)Math.Clamp((int)(b * 255), 0, 255),
                a = (byte)Math.Clamp((int)(a * 255), 0, 255),
            };

            if (sz >= 1f)
                DrawCircleV(p.Pos, sz * 0.5f, col);
            else
                DrawPixelV(p.Pos, col);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Global particle manager — owns one EmitterState per ParticleEmitter node
// ─────────────────────────────────────────────────────────────────────────────
public class ParticleSystem
{
    readonly Dictionary<Guid, EmitterState> _states = new();

    public void Update(SceneNode root, float dt)
    {
        var activeIds = new HashSet<Guid>();

        foreach (var node in root.Descendants())
        {
            if (node.Type != NodeType.ParticleEmitter || !node.Visible) continue;
            activeIds.Add(node.Id);

            if (!_states.TryGetValue(node.Id, out var state))
            {
                state = new EmitterState(node.EmitMaxParticles);
                _states[node.Id] = state;
            }
            state.Update(node, dt);
        }

        // Remove states for emitters that no longer exist in the scene
        foreach (var id in _states.Keys.Except(activeIds).ToList())
            _states.Remove(id);
    }

    public void Draw(SceneNode root)
    {
        foreach (var node in root.Descendants())
        {
            if (node.Type != NodeType.ParticleEmitter || !node.Visible) continue;
            if (_states.TryGetValue(node.Id, out var state))
                state.Draw(node);
        }
    }

    /// <summary>Destroys all live particles (e.g. after a hot-reload or scene change).</summary>
    public void Reset() => _states.Clear();

    /// <summary>Destroys only the particle pool for a specific emitter (e.g. after resizing MaxParticles).</summary>
    public void ResetEmitter(Guid id) => _states.Remove(id);
}
