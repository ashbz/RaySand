using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

/// <summary>
/// All procedural generation: wall textures (as CPU Color arrays) and sounds (WAV → Sound).
/// </summary>
static class ProceduralGen
{
    public const int TEX = 64;

    // ── CPU pixel array → GPU Texture ────────────────────────────────────────
    public static unsafe Texture ToTexture(Color[] pixels, int size)
    {
        fixed (Color* ptr = pixels)
        {
            var img = new Image
            {
                data = ptr, width = size, height = size, mipmaps = 1,
                format = (int)PixelFormat.PIXELFORMAT_UNCOMPRESSED_R8G8B8A8
            };
            return LoadTextureFromImage(img);
        }
    }

    // ── Wall textures ────────────────────────────────────────────────────────
    // Returns flat Color[TEX*TEX] arrays indexed by wall type 1-5 (index 0 unused).

    public static Color[][] GenerateWallTextures()
    {
        var t = new Color[6][];
        t[1] = Brick  (C(170, 38, 38),  C(70, 12, 12));
        t[2] = Organic(C(18, 145, 18),  C(5, 55, 5),   seed: 2);
        t[3] = Stone  (C(45, 75, 215),  C(8, 18, 85),  seed: 3);
        t[4] = Plasma (C(225, 195, 15), C(95, 55, 5));
        t[5] = Circuit(C(195, 85, 18),  C(35, 8, 2),   seed: 5);
        return t;
    }

    // ── Enemy sprites ────────────────────────────────────────────────────────

    public static Color[] GenerateEnemySprite(int seed)
    {
        var rng = new Random(seed);
        var p   = new Color[TEX * TEX];

        byte er = (byte)(90  + rng.Next(165));
        byte eg = (byte)(15  + rng.Next(75));
        byte eb = (byte)(15  + rng.Next(90));

        float noiseFreq = 2.5f + seed % 3;
        bool  redEyes   = seed % 2 == 1;

        for (int y = 0; y < TEX; y++)
        for (int x = 0; x < TEX; x++)
        {
            float fx = x / (float)TEX;
            float fy = y / (float)TEX;

            // Elliptical body with noise-warped edge
            float dx = (fx - .5f) * 2f, dy2 = (fy - .5f) * 2.2f;
            float dist = MathF.Sqrt(dx * dx + dy2 * dy2);
            float n    = FBM(fx * noiseFreq + seed, fy * noiseFreq + seed, seed);
            float mask = 0.9f - dist + n * 0.4f;

            if (mask > 0.35f)
            {
                float s = Math.Clamp(0.35f + n * 0.65f, 0f, 1f);
                p[y * TEX + x] = C((byte)(er * s), (byte)(eg * s), (byte)(eb * s));
            }
            else
            {
                p[y * TEX + x] = C(3, 3, 9);
            }

            // Eyes
            float eyeY = 0.34f + (seed % 3) * 0.02f;
            bool  le   = MathF.Abs(fx - .31f) < .065f && MathF.Abs(fy - eyeY) < .065f;
            bool  re   = MathF.Abs(fx - .69f) < .065f && MathF.Abs(fy - eyeY) < .065f;
            // Optional third eye
            bool  me   = !redEyes && MathF.Abs(fx - .5f) < .045f && MathF.Abs(fy - (eyeY - .05f)) < .045f;

            if (le || re || me)
                p[y * TEX + x] = redEyes ? C(255, 30, 30) : C(255, 220, 40);
        }
        return p;
    }

    // ── Texture generators ───────────────────────────────────────────────────

    static Color[] Brick(Color light, Color dark)
    {
        var p = new Color[TEX * TEX];
        for (int y = 0; y < TEX; y++)
        for (int x = 0; x < TEX; x++)
        {
            int row = y / 8;
            int bx  = (x + (row & 1) * 20) % 32;
            bool mortar = (y % 8 == 0) || (bx == 0);
            float n = H(x, y, 1) * 0.22f;
            p[y * TEX + x] = Lerp(dark, light, mortar ? 0.22f : 0.62f + n);
        }
        return p;
    }

    static Color[] Organic(Color light, Color dark, int seed)
    {
        var p = new Color[TEX * TEX];
        for (int y = 0; y < TEX; y++)
        for (int x = 0; x < TEX; x++)
            p[y * TEX + x] = Lerp(dark, light, FBM(x * .11f, y * .11f, seed));
        return p;
    }

    static Color[] Stone(Color light, Color dark, int seed)
    {
        var p = new Color[TEX * TEX];
        for (int y = 0; y < TEX; y++)
        for (int x = 0; x < TEX; x++)
        {
            // Voronoi / cracked-stone look
            float md = 999f;
            for (int cy = -1; cy <= 1; cy++)
            for (int cx = -1; cx <= 1; cx++)
            {
                int gx = (int)MathF.Floor(x / 12f) + cx;
                int gy = (int)MathF.Floor(y / 12f) + cy;
                float jx = H(gx, gy, seed * 3) * 12f;
                float jy = H(gx, gy, seed * 7) * 12f;
                float ddx = x - gx * 12f - jx;
                float ddy = y - gy * 12f - jy;
                float d   = MathF.Sqrt(ddx * ddx + ddy * ddy);
                if (d < md) md = d;
            }
            float n = Math.Clamp(md / 6f, 0f, 1f) + H(x, y, seed) * 0.18f;
            p[y * TEX + x] = Lerp(dark, light, n);
        }
        return p;
    }

    static Color[] Plasma(Color light, Color dark)
    {
        var p = new Color[TEX * TEX];
        for (int y = 0; y < TEX; y++)
        for (int x = 0; x < TEX; x++)
        {
            float fx = x / (float)TEX, fy = y / (float)TEX;
            float v  = MathF.Sin(fx * 11f)
                     + MathF.Sin(fy * 11f)
                     + MathF.Sin((fx + fy) * 8.5f)
                     + MathF.Sin(MathF.Sqrt(fx * fx + fy * fy) * 20f);
            p[y * TEX + x] = Lerp(dark, light, (v + 4f) / 8f);
        }
        return p;
    }

    static Color[] Circuit(Color light, Color dark, int seed)
    {
        var p   = new Color[TEX * TEX];
        var rng = new Random(seed);
        for (int i = 0; i < p.Length; i++) p[i] = dark;

        // Horizontal traces
        for (int i = 0; i < 12; i++)
        {
            int ry = rng.Next(TEX);
            int x0 = rng.Next(TEX / 2), x1 = x0 + rng.Next(TEX / 2 - x0 + 1);
            for (int x = x0; x <= x1; x++) p[ry * TEX + x] = light;
        }
        // Vertical traces
        for (int i = 0; i < 12; i++)
        {
            int rx = rng.Next(TEX);
            int y0 = rng.Next(TEX / 2), y1 = y0 + rng.Next(TEX / 2 - y0 + 1);
            for (int y = y0; y <= y1; y++) p[y * TEX + rx] = light;
        }
        // Junction pads
        for (int i = 0; i < 16; i++)
        {
            int cx = 2 + rng.Next(TEX - 4), cy = 2 + rng.Next(TEX - 4);
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx >= 0 && nx < TEX && ny >= 0 && ny < TEX)
                    p[ny * TEX + nx] = C(255, 200, 70);
            }
        }
        return p;
    }

    // ── Quake-palette texture generators ─────────────────────────────────────
    // Each returns a GPU Texture using the Quake dungeon colour palette.

    public static Texture MakeQuakeTex(int type, int size)
    {
        Color[] px = type switch
        {
            1 => QuakeStone(size),
            2 => QuakeBrown(size),
            3 => QuakeMetal(size),
            4 => QuakeRust(size),
            5 => QuakeSlime(size),
            _ => QuakeStone(size)
        };
        return ToTexture(px, size);
    }

    public static Texture MakeQuakeFloor(int size) => ToTexture(QuakeFloorPx(size), size);
    public static Texture MakeQuakeCeil(int size)  => ToTexture(QuakeCeilPx(size),  size);

    static Color[] QuakeStone(int s)
    {
        var p = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            int   row    = y * 8 / s;
            int   bx     = (x * 16 / s + (row & 1) * 8) % 16;
            bool  mortar = (y * 8 % s < 1) || (bx == 0);
            float n      = H(x, y, 11) * 0.28f + FBM(x * 0.17f, y * 0.17f, 11) * 0.22f;
            p[y * s + x] = mortar
                ? Lerp(C(24, 17, 11), C(38, 28, 18), n)
                : Lerp(C(52, 40, 28), C(105, 82, 56), n);
        }
        return p;
    }

    static Color[] QuakeBrown(int s)
    {
        var p = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float v = FBM(x * 0.13f, y * 0.13f, 22) * 0.65f
                    + FBM(x * 0.31f, y * 0.31f, 23) * 0.35f;
            p[y * s + x] = Lerp(C(35, 22, 12), C(112, 84, 50), v);
        }
        return p;
    }

    static Color[] QuakeMetal(int s)
    {
        var p = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            bool seam  = (x * 16 / s == 0) || (y * 16 / s == 0);
            int  rpx   = (x * 16) % s, rpy = (y * 16) % s;
            bool rivet = (rpx < s / 28 || rpx >= s - s / 28) &&
                         (rpy < s / 28 || rpy >= s - s / 28);
            float n = H(x, y, 33) * 0.18f;
            Color baseCol = seam
                ? Lerp(C(30, 32, 38), C(42, 44, 52), n)
                : Lerp(C(42, 44, 52), C(62, 65, 76), 0.55f + n);
            p[y * s + x] = rivet ? C(108, 112, 125) : baseCol;
        }
        return p;
    }

    static Color[] QuakeRust(int s)
    {
        var p = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float iron  = FBM(x * 0.12f, y * 0.12f, 44);
            float rust  = FBM(x * 0.25f, y * 0.25f, 45);
            float rMask = Math.Clamp(FBM(x * 0.07f, y * 0.07f, 46) - 0.32f, 0f, 1f) * 2.2f;
            var   ic    = Lerp(C(30, 24, 18), C(62, 50, 38), iron);
            var   rc    = Lerp(C(105, 44, 14), C(162, 78, 22), rust);
            p[y * s + x] = Lerp(ic, rc, Math.Clamp(rMask, 0f, 1f));
        }
        return p;
    }

    static Color[] QuakeSlime(int s)
    {
        var p = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float v = FBM(x * 0.13f, y * 0.13f, 55) * 0.68f
                    + FBM(x * 0.30f, y * 0.30f, 56) * 0.32f;
            p[y * s + x] = Lerp(C(28, 42, 20), C(66, 94, 40), Math.Clamp(v, 0f, 1f));
        }
        return p;
    }

    static Color[] QuakeFloorPx(int s)
    {
        var p = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            bool grout = (x * 14 % s < 1) || (y * 14 % s < 1);
            float n    = H(x / (s / 8), y / (s / 8), 66) * 0.24f + H(x, y, 67) * 0.10f;
            p[y * s + x] = grout
                ? C(16, 13, 9)
                : Lerp(C(48, 40, 30), C(80, 68, 50), 0.35f + n);
        }
        return p;
    }

    static Color[] QuakeCeilPx(int s)
    {
        var p = new Color[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            int  bx    = x * 8 / s, by = y * 8 / s;
            bool beam  = (bx == 0) || (bx == 4) || (by == 0) || (by == 4);
            float n    = H(x, y, 77) * 0.20f;
            p[y * s + x] = beam
                ? Lerp(C(38, 34, 28), C(60, 54, 44), n)
                : Lerp(C(16, 14, 11), C(30, 26, 20), n);
        }
        return p;
    }

    // ── Sound generation ─────────────────────────────────────────────────────

    public static Sound MakeAmbient()
    {
        const int SR = 44100;
        const float DUR = 4.5f;
        int n = (int)(SR * DUR);
        var s = new short[n];
        for (int i = 0; i < n; i++)
        {
            float t   = i / (float)SR;
            float lfo = 0.55f + 0.45f * MathF.Sin(MathF.PI * 2f * 0.10f * t);
            float w   = 0.40f * MathF.Sin(MathF.PI * 2f * 55.0f * t)
                      + 0.28f * MathF.Sin(MathF.PI * 2f * 82.5f * t + 0.5f)
                      + 0.18f * MathF.Sin(MathF.PI * 2f * 110.0f * t + 1.1f)
                      + 0.10f * MathF.Sin(MathF.PI * 2f * 165.0f * t + 2.2f)
                      + 0.04f * MathF.Sin(MathF.PI * 2f * 220.0f * t + 3.0f);
            s[i] = Smp(w * lfo * 0.20f);
        }
        return WavToSound(s, SR);
    }

    public static Sound MakeFootstep(int seed)
    {
        const int SR = 44100;
        var rng = new Random(seed);
        int n   = (int)(SR * 0.13f);
        var s   = new short[n];
        float prev  = 0;
        float pitch = 60f + (float)(rng.NextDouble() * 50f);
        for (int i = 0; i < n; i++)
        {
            float t     = i / (float)n;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            prev = prev * 0.62f + noise * 0.38f;
            float env  = MathF.Exp(-t * 22f);
            float bass = MathF.Sin(MathF.PI * 2f * pitch * i / SR);
            s[i] = Smp((prev * 0.35f + bass * 0.65f) * env * 0.88f);
        }
        return WavToSound(s, SR);
    }

    public static Sound MakeEnemyGrowl(int seed)
    {
        const int SR = 44100;
        var   rng  = new Random(seed + 777);
        float freq = 65f + (float)(rng.NextDouble() * 100f);
        float dur  = 0.30f + (float)(rng.NextDouble() * 0.40f);
        int   n    = (int)(SR * dur);
        var   s    = new short[n];
        for (int i = 0; i < n; i++)
        {
            float t   = i / (float)SR;
            float env = MathF.Exp(-t * 3.2f) * (0.5f + 0.5f * MathF.Sin(MathF.PI * 2f * 7f * t));
            // Sawtooth + first overtone
            float saw = (t * freq % 1f) * 2f - 1f;
            saw += 0.5f * ((t * freq * 2f % 1f) * 2f - 1f);
            saw /= 1.5f;
            s[i] = Smp(saw * env * 0.62f);
        }
        return WavToSound(s, SR);
    }

    public static Sound MakeShot()
    {
        const int SR = 44100;
        int n   = (int)(SR * 0.09f);
        var rng = new Random(7);
        var s   = new short[n];
        for (int i = 0; i < n; i++)
        {
            float t    = i / (float)n;
            float env  = MathF.Exp(-t * 38f);
            float nois = (float)(rng.NextDouble() * 2 - 1);
            float bang = MathF.Sin(MathF.PI * 2f * 200f * i / SR);
            s[i] = Smp((nois * 0.55f + bang * 0.45f) * env * 0.92f);
        }
        return WavToSound(s, SR);
    }

    public static Sound MakeHit()
    {
        const int SR = 44100;
        int n   = (int)(SR * 0.12f);
        var rng = new Random(13);
        var s   = new short[n];
        for (int i = 0; i < n; i++)
        {
            float t   = i / (float)n;
            float env = MathF.Exp(-t * 22f);
            float nois = (float)(rng.NextDouble() * 2 - 1);
            float pitch = MathF.Sin(MathF.PI * 2f * 350f * i / SR) * MathF.Exp(-t * 15f);
            s[i] = Smp((nois * 0.4f + pitch * 0.6f) * env * 0.85f);
        }
        return WavToSound(s, SR);
    }

    // ── WAV helpers ──────────────────────────────────────────────────────────

    static Sound WavToSound(short[] samples, int sampleRate)
    {
        string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        int ds = samples.Length * 2;
        using (var fs = File.OpenWrite(tmp))
        using (var bw = new BinaryWriter(fs))
        {
            var enc = System.Text.Encoding.ASCII;
            bw.Write(enc.GetBytes("RIFF")); bw.Write(36 + ds);
            bw.Write(enc.GetBytes("WAVE"));
            bw.Write(enc.GetBytes("fmt ")); bw.Write(16);
            bw.Write((short)1);           // PCM
            bw.Write((short)1);           // mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);     // byte rate
            bw.Write((short)2);           // block align
            bw.Write((short)16);          // bits per sample
            bw.Write(enc.GetBytes("data")); bw.Write(ds);
            foreach (short v in samples)  bw.Write(v);
        }
        var sound = LoadSound(tmp);
        File.Delete(tmp);
        return sound;
    }

    // ── Math helpers ─────────────────────────────────────────────────────────

    static float H(int x, int y, int seed)
    {
        int n = x * 1619 + y * 31337 + seed * 1013904223;
        n ^= n >> 13;
        return ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / (float)0x7fffffff;
    }

    static float VN(float x, float y, int seed)
    {
        int   xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        float u  = xf * xf * (3 - 2 * xf);
        float v  = yf * yf * (3 - 2 * yf);
        return H(xi,   yi,   seed) * (1-u) * (1-v)
             + H(xi+1, yi,   seed) * u     * (1-v)
             + H(xi,   yi+1, seed) * (1-u) * v
             + H(xi+1, yi+1, seed) * u     * v;
    }

    static float FBM(float x, float y, int seed)
    {
        float val = 0, amp = .5f, freq = 1f, mx = 0;
        for (int i = 0; i < 4; i++)
        {
            val += VN(x * freq, y * freq, seed + i) * amp;
            mx  += amp; amp *= .5f; freq *= 2f;
        }
        return val / mx;
    }

    static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return C(
            (byte)(a.r + (b.r - a.r) * t),
            (byte)(a.g + (b.g - a.g) * t),
            (byte)(a.b + (b.b - a.b) * t));
    }

    static short Smp(float v)
        => (short)(Math.Clamp(v, -1f, 1f) * 32767f);

    static Color C(byte r, byte g, byte b)
        => new Color { r = r, g = g, b = b, a = 255 };

    static Color C(int r, int g, int b)
        => new Color { r = (byte)r, g = (byte)g, b = (byte)b, a = 255 };
}
