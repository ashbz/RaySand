using Raylib_CsLo;

class Enemy
{
    public double X, Y;           // world X and Z (Y is "up" in Raylib 3D)
    public Color[] Sprite;
    public Sound   Growl;
    public float   GrowlTimer;
    public bool    Alive = true;
    public int     HitPoints = 2;
    public int     TypeIndex;
    public float   RotAngle;      // current rotation angle in degrees (accumulates)
    public float   BobPhase;      // unique per-enemy phase offset for bobbing/scaling

    const double SPEED = 1.25;

    public Enemy(double x, double y, Color[] sprite, Sound growl, int typeIndex)
    {
        X = x; Y = y; Sprite = sprite; Growl = growl; TypeIndex = typeIndex;
        GrowlTimer = Random.Shared.NextSingle() * 5f;
        BobPhase   = Random.Shared.NextSingle() * MathF.PI * 2f;
    }

    /// <summary>Returns true when the enemy wants to growl (caller handles sound).</summary>
    public bool Update(double px, double pz, double dt, int[,] map)
    {
        if (!Alive) return false;

        // Spin continuously
        RotAngle += 80f * (float)dt;

        double ex = px - X, ey = pz - Y;
        double dist = Math.Sqrt(ex * ex + ey * ey);
        if (dist < 0.55) return false;

        double nx = X + (ex / dist) * SPEED * dt;
        double ny = Y + (ey / dist) * SPEED * dt;

        int rows = map.GetLength(0), cols = map.GetLength(1);
        if ((int)Y >= 0 && (int)Y < rows && (int)nx >= 0 && (int)nx < cols && map[(int)Y, (int)nx] == 0)
            X = nx;
        if ((int)ny >= 0 && (int)ny < rows && (int)X >= 0 && (int)X < cols && map[(int)ny, (int)X] == 0)
            Y = ny;

        GrowlTimer -= (float)dt;
        if (GrowlTimer <= 0 && dist < 10.0)
        {
            GrowlTimer = 2.2f + Random.Shared.NextSingle() * 3.5f;
            return true;
        }
        return false;
    }

    public void Hit()
    {
        HitPoints--;
        if (HitPoints <= 0) Alive = false;
    }

    // DistSq in XZ plane (pz = player's world Z)
    public double DistSq(double px, double pz) => (X - px) * (X - px) + (Y - pz) * (Y - pz);
}
