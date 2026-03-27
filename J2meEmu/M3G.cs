using System.IO.Compression;

namespace J2meEmu;

// ═══════════════════════════════════════════════════════════════════
//  JSR-184 (M3G) Software 3D — types, renderer, .m3g loader
// ═══════════════════════════════════════════════════════════════════

// ── 4×4 row-major matrix math ──────────────────────────────────────

static class M3GMat
{
    public static float[] Identity() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1
    };

    public static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                float s = 0;
                for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j];
                r[i * 4 + j] = s;
            }
        return r;
    }

    public static float[] Translation(float x, float y, float z) => new float[]
    {
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1
    };

    public static float[] Scale(float sx, float sy, float sz) => new float[]
    {
        sx, 0, 0, 0,
        0, sy, 0, 0,
        0, 0, sz, 0,
        0, 0, 0, 1
    };

    public static float[] Rotation(float angleDeg, float ax, float ay, float az)
    {
        float len = MathF.Sqrt(ax * ax + ay * ay + az * az);
        if (len < 1e-9f) return Identity();
        ax /= len; ay /= len; az /= len;
        float rad = angleDeg * MathF.PI / 180f;
        float c = MathF.Cos(rad), s = MathF.Sin(rad), t = 1 - c;
        return new float[]
        {
            t*ax*ax+c,    t*ax*ay-s*az, t*ax*az+s*ay, 0,
            t*ax*ay+s*az, t*ay*ay+c,    t*ay*az-s*ax, 0,
            t*ax*az-s*ay, t*ay*az+s*ax, t*az*az+c,    0,
            0,            0,            0,            1
        };
    }

    public static float[] Perspective(float fovyDeg, float aspect, float near, float far)
    {
        float h = MathF.Tan(fovyDeg * MathF.PI / 360f);
        float w = h * aspect;
        return new float[]
        {
            1f/w, 0,    0,                           0,
            0,    1f/h, 0,                           0,
            0,    0,    -(far+near)/(far-near),      -2f*far*near/(far-near),
            0,    0,    -1,                          0
        };
    }

    public static float[] Parallel(float fovyDeg, float aspect, float near, float far)
    {
        float h = MathF.Tan(fovyDeg * MathF.PI / 360f) * near;
        float w = h * aspect;
        return new float[]
        {
            1f/w, 0,    0,                       0,
            0,    1f/h, 0,                       0,
            0,    0,    -2f/(far-near),          -(far+near)/(far-near),
            0,    0,    0,                       1
        };
    }

    public static bool Invert(float[] m, out float[] inv)
    {
        inv = new float[16];
        float[] s = new float[6], c = new float[6];
        s[0] = m[0]*m[5]-m[4]*m[1]; s[1] = m[0]*m[6]-m[4]*m[2]; s[2] = m[0]*m[7]-m[4]*m[3];
        s[3] = m[1]*m[6]-m[5]*m[2]; s[4] = m[1]*m[7]-m[5]*m[3]; s[5] = m[2]*m[7]-m[6]*m[3];
        c[0] = m[8]*m[13]-m[12]*m[9]; c[1] = m[8]*m[14]-m[12]*m[10]; c[2] = m[8]*m[15]-m[12]*m[11];
        c[3] = m[9]*m[14]-m[13]*m[10]; c[4] = m[9]*m[15]-m[13]*m[11]; c[5] = m[10]*m[15]-m[14]*m[11];
        float det = s[0]*c[5]-s[1]*c[4]+s[2]*c[3]+s[3]*c[2]-s[4]*c[1]+s[5]*c[0];
        if (MathF.Abs(det) < 1e-12f) { inv = Identity(); return false; }
        float id = 1f / det;
        inv[0]  = ( m[5]*c[5]-m[6]*c[4]+m[7]*c[3])*id;
        inv[1]  = (-m[1]*c[5]+m[2]*c[4]-m[3]*c[3])*id;
        inv[2]  = ( m[13]*s[5]-m[14]*s[4]+m[15]*s[3])*id;
        inv[3]  = (-m[9]*s[5]+m[10]*s[4]-m[11]*s[3])*id;
        inv[4]  = (-m[4]*c[5]+m[6]*c[2]-m[7]*c[1])*id;
        inv[5]  = ( m[0]*c[5]-m[2]*c[2]+m[3]*c[1])*id;
        inv[6]  = (-m[12]*s[5]+m[14]*s[2]-m[15]*s[1])*id;
        inv[7]  = ( m[8]*s[5]-m[10]*s[2]+m[11]*s[1])*id;
        inv[8]  = ( m[4]*c[4]-m[5]*c[2]+m[7]*c[0])*id;
        inv[9]  = (-m[0]*c[4]+m[1]*c[2]-m[3]*c[0])*id;
        inv[10] = ( m[12]*s[4]-m[13]*s[2]+m[15]*s[0])*id;
        inv[11] = (-m[8]*s[4]+m[9]*s[2]-m[11]*s[0])*id;
        inv[12] = (-m[4]*c[3]+m[5]*c[1]-m[6]*c[0])*id;
        inv[13] = ( m[0]*c[3]-m[1]*c[1]+m[2]*c[0])*id;
        inv[14] = (-m[12]*s[3]+m[13]*s[1]-m[14]*s[0])*id;
        inv[15] = ( m[8]*s[3]-m[9]*s[1]+m[10]*s[0])*id;
        return true;
    }

    public static void TransformPoint(float[] m, float x, float y, float z,
        out float ox, out float oy, out float oz, out float ow)
    {
        ox = m[0]*x + m[1]*y + m[2]*z + m[3];
        oy = m[4]*x + m[5]*y + m[6]*z + m[7];
        oz = m[8]*x + m[9]*y + m[10]*z + m[11];
        ow = m[12]*x + m[13]*y + m[14]*z + m[15];
    }

    public static void TransformDir(float[] m, float x, float y, float z,
        out float ox, out float oy, out float oz)
    {
        ox = m[0]*x + m[1]*y + m[2]*z;
        oy = m[4]*x + m[5]*y + m[6]*z;
        oz = m[8]*x + m[9]*y + m[10]*z;
    }
}

// ── M3G object hierarchy ───────────────────────────────────────────

class M3GTransform : M3GObject3D
{
    public float[] Matrix = M3GMat.Identity();
}

class M3GObject3D
{
    public int UserID;
    public object? UserObject;
}

class M3GTransformable : M3GObject3D
{
    float[] _translation = new float[3];
    float[] _scale = { 1, 1, 1 };
    float _orientAngle;
    float[] _orientAxis = { 0, 0, 1 };
    bool _hasComponent;
    float[] _generalTransform = M3GMat.Identity();
    bool _hasGeneral;

    public void SetTranslation(float x, float y, float z)
    {
        _translation[0] = x; _translation[1] = y; _translation[2] = z;
        _hasComponent = true;
    }

    public void GetTranslation(float[] t)
    {
        t[0] = _translation[0]; t[1] = _translation[1]; t[2] = _translation[2];
    }

    public void SetScale(float sx, float sy, float sz)
    {
        _scale[0] = sx; _scale[1] = sy; _scale[2] = sz;
        _hasComponent = true;
    }

    public void GetScale(float[] s) { s[0] = _scale[0]; s[1] = _scale[1]; s[2] = _scale[2]; }

    public void SetOrientation(float angle, float ax, float ay, float az)
    {
        float len = MathF.Sqrt(ax * ax + ay * ay + az * az);
        if (len > 1e-9f) { ax /= len; ay /= len; az /= len; }
        _orientAngle = angle; _orientAxis[0] = ax; _orientAxis[1] = ay; _orientAxis[2] = az;
        _hasComponent = true;
    }

    public void GetOrientation(float[] o)
    {
        o[0] = _orientAngle; o[1] = _orientAxis[0]; o[2] = _orientAxis[1]; o[3] = _orientAxis[2];
    }

    public void Translate(float x, float y, float z)
    {
        _translation[0] += x; _translation[1] += y; _translation[2] += z;
        _hasComponent = true;
    }

    public void PostRotate(float angle, float ax, float ay, float az)
    {
        var r = M3GMat.Rotation(angle, ax, ay, az);
        _generalTransform = M3GMat.Multiply(_generalTransform, r);
        _hasGeneral = true;
    }

    public void PreRotate(float angle, float ax, float ay, float az)
    {
        var r = M3GMat.Rotation(angle, ax, ay, az);
        _generalTransform = M3GMat.Multiply(r, _generalTransform);
        _hasGeneral = true;
    }

    public void SetTransform(M3GTransform t)
    {
        Array.Copy(t.Matrix, _generalTransform, 16);
        _hasGeneral = true;
    }

    public void GetTransform(M3GTransform t) => Array.Copy(GetCompositeTransform(), t.Matrix, 16);

    public float[] GetCompositeTransform()
    {
        var result = M3GMat.Identity();
        if (_hasGeneral) result = (float[])_generalTransform.Clone();
        if (_hasComponent)
        {
            if (_scale[0] != 1 || _scale[1] != 1 || _scale[2] != 1)
                result = M3GMat.Multiply(M3GMat.Scale(_scale[0], _scale[1], _scale[2]), result);
            if (_orientAngle != 0)
                result = M3GMat.Multiply(M3GMat.Rotation(_orientAngle, _orientAxis[0], _orientAxis[1], _orientAxis[2]), result);
            result = M3GMat.Multiply(M3GMat.Translation(_translation[0], _translation[1], _translation[2]), result);
        }
        return result;
    }

    public void LoadComponentTransform(float[] translation, float[] scale, float orientAngle, float[] orientAxis)
    {
        Array.Copy(translation, _translation, 3);
        Array.Copy(scale, _scale, 3);
        _orientAngle = orientAngle;
        Array.Copy(orientAxis, _orientAxis, 3);
        _hasComponent = true;
    }

    public void LoadGeneralTransform(float[] m)
    {
        Array.Copy(m, _generalTransform, 16);
        _hasGeneral = true;
    }
}

class M3GNode : M3GTransformable
{
    public bool RenderingEnabled = true;
    public bool PickingEnabled = true;
    public float AlphaFactor = 1f;
    public int Scope = -1;
    public M3GGroup? Parent;
}

class M3GGroup : M3GNode
{
    public List<M3GNode> Children = new();

    public void AddChild(M3GNode n)
    {
        if (!Children.Contains(n)) { Children.Add(n); n.Parent = this; }
    }

    public void RemoveChild(M3GNode n)
    {
        Children.Remove(n);
        if (n.Parent == this) n.Parent = null;
    }
}

class M3GWorld : M3GGroup
{
    public M3GCamera? ActiveCamera;
    public M3GBackground? Background;
}

class M3GCamera : M3GNode
{
    public const int GENERIC = 48, PARALLEL = 49, PERSPECTIVE = 50;
    public int ProjectionType = PERSPECTIVE;
    public float Fovy = 60, AspectRatio = 1, Near = 0.1f, Far = 1000f;
    public float[] GenericMatrix = M3GMat.Identity();

    public void SetPerspective(float fovy, float aspect, float near, float far)
    {
        ProjectionType = PERSPECTIVE; Fovy = fovy; AspectRatio = aspect; Near = near; Far = far;
    }

    public void SetParallel(float fovy, float aspect, float near, float far)
    {
        ProjectionType = PARALLEL; Fovy = fovy; AspectRatio = aspect; Near = near; Far = far;
    }

    public float[] GetProjectionMatrix() => ProjectionType switch
    {
        PERSPECTIVE => M3GMat.Perspective(Fovy, AspectRatio, Near, Far),
        PARALLEL => M3GMat.Parallel(Fovy, AspectRatio, Near, Far),
        _ => (float[])GenericMatrix.Clone()
    };
}

class M3GLight : M3GNode
{
    public const int AMBIENT = 128, DIRECTIONAL = 129, OMNI = 130, SPOT = 131;
    public int Mode = DIRECTIONAL;
    public int Color = 0xFFFFFF;
    public float Intensity = 1f;
    public float SpotAngle = 45f, SpotExponent = 0f;
    public float AttConst = 1f, AttLin = 0f, AttQuad = 0f;
}

class M3GMesh : M3GNode
{
    public M3GVertexBuffer? VertexBuffer;
    public M3GTriangleStripArray?[] IndexBuffers = Array.Empty<M3GTriangleStripArray?>();
    public M3GAppearance?[] Appearances = Array.Empty<M3GAppearance?>();

    public M3GAppearance? GetAppearance(int i) =>
        i >= 0 && i < Appearances.Length ? Appearances[i] : null;
    public void SetAppearance(int i, M3GAppearance? a)
    {
        if (i >= 0 && i < Appearances.Length) Appearances[i] = a;
    }
}

class M3GVertexArray : M3GObject3D
{
    public int ComponentCount; // 2, 3, or 4
    public int ComponentSize;  // 1=byte, 2=short, 4=float
    public int VertexCount;
    public float[] Data = Array.Empty<float>(); // all components flattened

    public void Get(int vertexIndex, float[] outComponents)
    {
        int off = vertexIndex * ComponentCount;
        for (int i = 0; i < ComponentCount && off + i < Data.Length; i++)
            outComponents[i] = Data[off + i];
    }
}

class M3GVertexBuffer : M3GObject3D
{
    public M3GVertexArray? Positions;
    public float PositionScale = 1f;
    public float[] PositionBias = { 0, 0, 0 };
    public M3GVertexArray? Normals;
    public M3GVertexArray? Colors;
    public M3GVertexArray?[] TexCoords = Array.Empty<M3GVertexArray?>();
    public float[][] TexCoordBias = Array.Empty<float[]>();
    public float[] TexCoordScale = Array.Empty<float>();
    public int DefaultColor = unchecked((int)0xFFFFFFFF);
}

class M3GTriangleStripArray : M3GObject3D
{
    public int[]? ExplicitIndices;
    public int FirstIndex;
    public int[] StripLengths = Array.Empty<int>();

    public List<(int, int, int)> GetTriangles()
    {
        var tris = new List<(int, int, int)>();
        int pos = 0;
        foreach (int stripLen in StripLengths)
        {
            for (int i = 0; i < stripLen - 2; i++)
            {
                int i0 = GetIndex(pos + i);
                int i1 = GetIndex(pos + i + 1);
                int i2 = GetIndex(pos + i + 2);
                if (i0 == i1 || i1 == i2 || i0 == i2) continue; // degenerate
                if ((i & 1) == 0) tris.Add((i0, i1, i2));
                else tris.Add((i0, i2, i1)); // flip winding for even/odd
            }
            pos += stripLen;
        }
        return tris;
    }

    int GetIndex(int i)
    {
        if (ExplicitIndices != null)
            return i < ExplicitIndices.Length ? ExplicitIndices[i] : 0;
        return FirstIndex + i;
    }
}

class M3GAppearance : M3GObject3D
{
    public int Layer;
    public M3GCompositingMode? CompositingMode;
    public M3GPolygonMode? PolygonMode;
    public M3GMaterial? Material;
    public M3GFog? Fog;
    public M3GTexture2D?[] Textures = Array.Empty<M3GTexture2D?>();
}

class M3GCompositingMode : M3GObject3D
{
    public const int ALPHA = 64, ALPHA_ADD = 65, MODULATE = 66, MODULATE_X2 = 67, REPLACE = 68;
    public bool DepthTestEnabled = true;
    public bool DepthWriteEnabled = true;
    public bool ColorWriteEnabled = true;
    public bool AlphaWriteEnabled = true;
    public int Blending = REPLACE;
    public float AlphaThreshold;
}

class M3GPolygonMode : M3GObject3D
{
    public const int CULL_NONE = 160, CULL_BACK = 161, CULL_FRONT = 162;
    public const int SHADE_FLAT = 164, SHADE_SMOOTH = 165;
    public const int WINDING_CCW = 168, WINDING_CW = 169;
    public int Culling = CULL_BACK;
    public int Shading = SHADE_SMOOTH;
    public int Winding = WINDING_CCW;
    public bool PerspectiveCorrection = true;
    public bool TwoSidedLighting;
}

class M3GMaterial : M3GObject3D
{
    public int AmbientColor = 0x333333;
    public int DiffuseColor = unchecked((int)0xFFCCCCCC);
    public int EmissiveColor = 0x000000;
    public int SpecularColor = 0x000000;
    public float Shininess;
    public bool VertexColorTracking;
}

class M3GFog : M3GObject3D
{
    public int Color;
    public int Mode = 81; // LINEAR
    public float Density = 1f;
    public float Near, Far = 1f;
}

class M3GTexture2D : M3GTransformable
{
    public M3GImage2D? Image;
    public int BlendColor;
    public int Blending = 224; // MODULATE
    public int WrapS = 241, WrapT = 241; // REPEAT
    public int LevelFilter = 208, ImageFilter = 210; // BASE_LEVEL, NEAREST
}

class M3GImage2D : M3GObject3D
{
    public int Format; // 96=ALPHA, 97=LUM, 98=LUM_A, 99=RGB, 100=RGBA
    public int Width, Height;
    public int[] Pixels = Array.Empty<int>(); // ARGB
}

class M3GBackground : M3GObject3D
{
    public int Color;
    public M3GImage2D? Image;
    public bool ColorClearEnabled = true;
    public bool DepthClearEnabled = true;
    public int CropX, CropY, CropW, CropH;
}

class M3GAnimationController : M3GObject3D
{
    public float Speed = 1f, Weight = 1f;
}

class M3GAnimationTrack : M3GObject3D { }
class M3GKeyframeSequence : M3GObject3D { }
class M3GSprite3D : M3GNode { }
class M3GRayIntersection : M3GObject3D { }

// ── Software 3D Renderer ──────────────────────────────────────────

class M3GRenderer
{
    int[] _fb = Array.Empty<int>();
    float[] _zb = Array.Empty<float>();
    int _w, _h;
    int _vpX, _vpY, _vpW, _vpH;

    M3GCamera? _camera;
    float[] _cameraTransform = M3GMat.Identity();
    List<(M3GLight light, float[] transform)> _lights = new();

    public void BindTarget(int[] framebuffer, int width, int height)
    {
        _fb = framebuffer; _w = width; _h = height;
        if (_zb.Length != width * height)
            _zb = new float[width * height];
        _vpX = 0; _vpY = 0; _vpW = width; _vpH = height;
    }

    public void ReleaseTarget() { _fb = Array.Empty<int>(); }

    public void SetViewport(int x, int y, int w, int h)
    {
        _vpX = x; _vpY = y; _vpW = w; _vpH = h;
    }

    public void SetCamera(M3GCamera? cam, float[]? transform)
    {
        _camera = cam;
        _cameraTransform = transform ?? M3GMat.Identity();
    }

    public void AddLight(M3GLight light, float[]? transform)
    {
        _lights.Add((light, transform ?? M3GMat.Identity()));
    }

    public void ResetLights() => _lights.Clear();

    public void Clear(M3GBackground? bg)
    {
        int color = 0;
        if (bg != null)
        {
            if (bg.ColorClearEnabled)
            {
                color = bg.Color;
                for (int i = 0; i < _fb.Length; i++) _fb[i] = color;
            }
            if (bg.DepthClearEnabled)
                Array.Fill(_zb, 1f);
        }
        else
        {
            Array.Fill(_fb, 0);
            Array.Fill(_zb, 1f);
        }
    }

    public void RenderWorld(M3GWorld world)
    {
        if (world.ActiveCamera != null)
        {
            _camera = world.ActiveCamera;
            _cameraTransform = GetWorldTransform(world.ActiveCamera);
        }
        Clear(world.Background);
        _lights.Clear();
        CollectLights(world, M3GMat.Identity());
        if (_lights.Count == 0)
        {
            var defaultLight = new M3GLight { Mode = M3GLight.DIRECTIONAL, Color = 0xFFFFFF, Intensity = 0.7f };
            _lights.Add((defaultLight, M3GMat.Identity()));
        }
        RenderNode(world, M3GMat.Identity());
    }

    void CollectLights(M3GNode node, float[] parentTransform)
    {
        if (!node.RenderingEnabled) return;
        var worldTf = M3GMat.Multiply(parentTransform, node.GetCompositeTransform());
        if (node is M3GLight light)
            _lights.Add((light, worldTf));
        if (node is M3GGroup group)
            foreach (var child in group.Children)
                CollectLights(child, worldTf);
    }

    float[] GetWorldTransform(M3GNode node)
    {
        var chain = new List<M3GNode>();
        for (var n = node; n != null; n = n.Parent) chain.Add(n);
        var m = M3GMat.Identity();
        for (int i = chain.Count - 1; i >= 0; i--)
            m = M3GMat.Multiply(m, chain[i].GetCompositeTransform());
        return m;
    }

    void RenderNode(M3GNode node, float[] parentTransform)
    {
        if (!node.RenderingEnabled) return;
        var worldTf = M3GMat.Multiply(parentTransform, node.GetCompositeTransform());
        if (node is M3GMesh mesh)
            RenderMesh(mesh.VertexBuffer, mesh.IndexBuffers, mesh.Appearances, worldTf);
        if (node is M3GGroup group)
            foreach (var child in group.Children)
                RenderNode(child, worldTf);
    }

    public void RenderMeshImmediate(M3GVertexBuffer? vb, M3GTriangleStripArray? ib,
        M3GAppearance? app, float[]? modelTransform)
    {
        if (vb == null || ib == null) return;
        var ibs = new[] { ib };
        var apps = new[] { app };
        RenderMesh(vb, ibs, apps, modelTransform ?? M3GMat.Identity());
    }

    void RenderMesh(M3GVertexBuffer? vb, M3GTriangleStripArray?[] ibs,
        M3GAppearance?[] apps, float[] modelWorld)
    {
        if (vb?.Positions == null || _camera == null) return;

        float[] view;
        var camWorld = _cameraTransform;
        M3GMat.Invert(camWorld, out view);
        var proj = _camera.GetProjectionMatrix();
        var mvp = M3GMat.Multiply(proj, M3GMat.Multiply(view, modelWorld));
        var mv = M3GMat.Multiply(view, modelWorld);

        int vertCount = vb.Positions.VertexCount;
        var clipVerts = new float[vertCount * 4]; // x,y,z,w in clip space
        var worldPos = new float[vertCount * 3];
        var worldNrm = new float[vertCount * 3];
        var texCoords = new float[vertCount * 2];
        var vertColors = new int[vertCount];
        Array.Fill(vertColors, vb.DefaultColor);

        float[] raw = new float[4];
        for (int i = 0; i < vertCount; i++)
        {
            vb.Positions.Get(i, raw);
            float px = raw[0] * vb.PositionScale + vb.PositionBias[0];
            float py = raw[1] * vb.PositionScale + vb.PositionBias[1];
            float pz = raw[2] * vb.PositionScale + vb.PositionBias[2];

            M3GMat.TransformPoint(mvp, px, py, pz, out clipVerts[i * 4], out clipVerts[i * 4 + 1],
                out clipVerts[i * 4 + 2], out clipVerts[i * 4 + 3]);

            M3GMat.TransformPoint(modelWorld, px, py, pz,
                out worldPos[i * 3], out worldPos[i * 3 + 1], out worldPos[i * 3 + 2], out _);
        }

        if (vb.Normals != null)
        {
            M3GMat.Invert(modelWorld, out var modelInv);
            var normalMat = Transpose3x3(modelInv);
            for (int i = 0; i < vertCount && i < vb.Normals.VertexCount; i++)
            {
                vb.Normals.Get(i, raw);
                M3GMat.TransformDir(normalMat, raw[0], raw[1], raw[2],
                    out worldNrm[i * 3], out worldNrm[i * 3 + 1], out worldNrm[i * 3 + 2]);
                float len = MathF.Sqrt(worldNrm[i*3]*worldNrm[i*3]+worldNrm[i*3+1]*worldNrm[i*3+1]+worldNrm[i*3+2]*worldNrm[i*3+2]);
                if (len > 1e-9f) { worldNrm[i*3] /= len; worldNrm[i*3+1] /= len; worldNrm[i*3+2] /= len; }
            }
        }
        else
        {
            for (int i = 0; i < vertCount; i++)
                worldNrm[i * 3 + 2] = -1f; // default normal facing -Z
        }

        if (vb.Colors != null)
        {
            for (int i = 0; i < vertCount && i < vb.Colors.VertexCount; i++)
            {
                vb.Colors.Get(i, raw);
                int r = Clamp255((int)raw[0]), g = Clamp255((int)raw[1]), b = Clamp255((int)raw[2]);
                int a = vb.Colors.ComponentCount >= 4 ? Clamp255((int)raw[3]) : 255;
                vertColors[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        if (vb.TexCoords.Length > 0 && vb.TexCoords[0] != null)
        {
            var tc = vb.TexCoords[0]!;
            float tcScale = vb.TexCoordScale.Length > 0 ? vb.TexCoordScale[0] : 1f;
            float tcBiasU = vb.TexCoordBias.Length > 0 ? vb.TexCoordBias[0][0] : 0;
            float tcBiasV = vb.TexCoordBias.Length > 0 ? vb.TexCoordBias[0][1] : 0;
            for (int i = 0; i < vertCount && i < tc.VertexCount; i++)
            {
                tc.Get(i, raw);
                texCoords[i * 2] = raw[0] * tcScale + tcBiasU;
                texCoords[i * 2 + 1] = raw[1] * tcScale + tcBiasV;
            }
        }

        for (int sub = 0; sub < ibs.Length; sub++)
        {
            var ib = ibs[sub];
            if (ib == null) continue;
            var app = sub < apps.Length ? apps[sub] : null;

            M3GImage2D? tex = null;
            if (app?.Textures.Length > 0 && app.Textures[0]?.Image != null)
                tex = app.Textures[0]!.Image;

            int matDiffuse = unchecked((int)0xFFCCCCCC);
            int matAmbient = 0x333333;
            int matEmissive = 0x000000;
            if (app?.Material != null)
            {
                matDiffuse = app.Material.DiffuseColor;
                matAmbient = app.Material.AmbientColor;
                matEmissive = app.Material.EmissiveColor;
            }

            bool depthTest = app?.CompositingMode?.DepthTestEnabled ?? true;
            bool depthWrite = app?.CompositingMode?.DepthWriteEnabled ?? true;
            int blending = app?.CompositingMode?.Blending ?? M3GCompositingMode.REPLACE;
            int culling = app?.PolygonMode?.Culling ?? M3GPolygonMode.CULL_BACK;

            var triangles = ib.GetTriangles();
            foreach (var (i0, i1, i2) in triangles)
            {
                if (i0 >= vertCount || i1 >= vertCount || i2 >= vertCount) continue;
                RasterizeTriangle(
                    clipVerts, worldPos, worldNrm, texCoords, vertColors,
                    i0, i1, i2,
                    matDiffuse, matAmbient, matEmissive,
                    tex, depthTest, depthWrite, blending, culling);
            }
        }
    }

    void RasterizeTriangle(
        float[] clip, float[] wpos, float[] wnrm, float[] tc, int[] vcol,
        int i0, int i1, int i2,
        int matDiffuse, int matAmbient, int matEmissive,
        M3GImage2D? tex, bool depthTest, bool depthWrite, int blending, int culling)
    {
        float cx0 = clip[i0*4], cy0 = clip[i0*4+1], cz0 = clip[i0*4+2], cw0 = clip[i0*4+3];
        float cx1 = clip[i1*4], cy1 = clip[i1*4+1], cz1 = clip[i1*4+2], cw1 = clip[i1*4+3];
        float cx2 = clip[i2*4], cy2 = clip[i2*4+1], cz2 = clip[i2*4+2], cw2 = clip[i2*4+3];

        // near plane clip: discard if all behind w<0.001
        if (cw0 < 0.001f && cw1 < 0.001f && cw2 < 0.001f) return;

        // near plane clipping with vertex splitting
        if (cw0 < 0.001f || cw1 < 0.001f || cw2 < 0.001f)
        {
            ClipAndRasterize(clip, wpos, wnrm, tc, vcol, i0, i1, i2,
                matDiffuse, matAmbient, matEmissive, tex, depthTest, depthWrite, blending, culling);
            return;
        }

        // perspective divide
        float invW0 = 1f / cw0, invW1 = 1f / cw1, invW2 = 1f / cw2;
        float ndcX0 = cx0 * invW0, ndcY0 = cy0 * invW0, ndcZ0 = cz0 * invW0;
        float ndcX1 = cx1 * invW1, ndcY1 = cy1 * invW1, ndcZ1 = cz1 * invW1;
        float ndcX2 = cx2 * invW2, ndcY2 = cy2 * invW2, ndcZ2 = cz2 * invW2;

        // viewport transform (Y is flipped)
        float sx0 = _vpX + (ndcX0 * 0.5f + 0.5f) * _vpW;
        float sy0 = _vpY + (0.5f - ndcY0 * 0.5f) * _vpH;
        float sx1 = _vpX + (ndcX1 * 0.5f + 0.5f) * _vpW;
        float sy1 = _vpY + (0.5f - ndcY1 * 0.5f) * _vpH;
        float sx2 = _vpX + (ndcX2 * 0.5f + 0.5f) * _vpW;
        float sy2 = _vpY + (0.5f - ndcY2 * 0.5f) * _vpH;

        // backface culling
        float cross = (sx1 - sx0) * (sy2 - sy0) - (sy1 - sy0) * (sx2 - sx0);
        if (culling == M3GPolygonMode.CULL_BACK && cross >= 0) return;
        if (culling == M3GPolygonMode.CULL_FRONT && cross <= 0) return;
        if (MathF.Abs(cross) < 0.001f) return;

        // compute lighting for each vertex
        int c0 = ComputeVertexColor(wpos, wnrm, vcol, i0, matDiffuse, matAmbient, matEmissive, tex != null);
        int c1 = ComputeVertexColor(wpos, wnrm, vcol, i1, matDiffuse, matAmbient, matEmissive, tex != null);
        int c2 = ComputeVertexColor(wpos, wnrm, vcol, i2, matDiffuse, matAmbient, matEmissive, tex != null);

        float u0 = tc[i0*2], v0 = tc[i0*2+1];
        float u1 = tc[i1*2], v1 = tc[i1*2+1];
        float u2 = tc[i2*2], v2 = tc[i2*2+1];

        // bounding box
        int minX = Math.Max(_vpX, (int)MathF.Floor(MathF.Min(sx0, MathF.Min(sx1, sx2))));
        int maxX = Math.Min(_vpX + _vpW - 1, (int)MathF.Ceiling(MathF.Max(sx0, MathF.Max(sx1, sx2))));
        int minY = Math.Max(_vpY, (int)MathF.Floor(MathF.Min(sy0, MathF.Min(sy1, sy2))));
        int maxY = Math.Min(_vpY + _vpH - 1, (int)MathF.Ceiling(MathF.Max(sy0, MathF.Max(sy1, sy2))));

        float invCross = 1f / cross;

        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                float ex = px + 0.5f, ey = py + 0.5f;

                float w0 = (sx1 - sx2) * (ey - sy2) - (sy1 - sy2) * (ex - sx2);
                float w1 = (sx2 - sx0) * (ey - sy0) - (sy2 - sy0) * (ex - sx0);
                float w2 = (sx0 - sx1) * (ey - sy1) - (sy0 - sy1) * (ex - sx1);

                if (cross < 0) { if (w0 < 0 || w1 < 0 || w2 < 0) continue; }
                else { if (w0 > 0 || w1 > 0 || w2 > 0) continue; }

                float b0 = w0 * invCross, b1 = w1 * invCross, b2 = w2 * invCross;

                // perspective-correct interpolation
                float oneOverW = b0 * invW0 + b1 * invW1 + b2 * invW2;
                if (oneOverW <= 0) continue;
                float wInterp = 1f / oneOverW;

                float z = b0 * ndcZ0 + b1 * ndcZ1 + b2 * ndcZ2;

                if (px < 0 || px >= _w || py < 0 || py >= _h) continue;
                int idx = py * _w + px;

                if (depthTest && z >= _zb[idx]) continue;
                if (depthWrite) _zb[idx] = z;

                int pixelColor;
                if (tex != null)
                {
                    float tu = (b0 * u0 * invW0 + b1 * u1 * invW1 + b2 * u2 * invW2) * wInterp;
                    float tv = (b0 * v0 * invW0 + b1 * v1 * invW1 + b2 * v2 * invW2) * wInterp;
                    int texColor = SampleTexture(tex, tu, tv);

                    float lr = (b0*(((c0>>16)&0xFF)*invW0) + b1*(((c1>>16)&0xFF)*invW1) + b2*(((c2>>16)&0xFF)*invW2)) * wInterp;
                    float lg = (b0*(((c0>>8)&0xFF)*invW0) + b1*(((c1>>8)&0xFF)*invW1) + b2*(((c2>>8)&0xFF)*invW2)) * wInterp;
                    float lb = (b0*((c0&0xFF)*invW0) + b1*((c1&0xFF)*invW1) + b2*((c2&0xFF)*invW2)) * wInterp;

                    int tr = ((texColor >> 16) & 0xFF), tg = ((texColor >> 8) & 0xFF), tb = (texColor & 0xFF);
                    int ta = ((texColor >> 24) & 0xFF);
                    int fr = Clamp255((int)(tr * lr / 255f));
                    int fg = Clamp255((int)(tg * lg / 255f));
                    int fb = Clamp255((int)(tb * lb / 255f));
                    pixelColor = (ta << 24) | (fr << 16) | (fg << 8) | fb;
                }
                else
                {
                    float cr = (b0*(((c0>>16)&0xFF)*invW0) + b1*(((c1>>16)&0xFF)*invW1) + b2*(((c2>>16)&0xFF)*invW2)) * wInterp;
                    float cg = (b0*(((c0>>8)&0xFF)*invW0) + b1*(((c1>>8)&0xFF)*invW1) + b2*(((c2>>8)&0xFF)*invW2)) * wInterp;
                    float cb = (b0*((c0&0xFF)*invW0) + b1*((c1&0xFF)*invW1) + b2*((c2&0xFF)*invW2)) * wInterp;
                    float ca = (b0*(((c0>>24)&0xFF)*invW0) + b1*(((c1>>24)&0xFF)*invW1) + b2*(((c2>>24)&0xFF)*invW2)) * wInterp;
                    pixelColor = (Clamp255((int)ca) << 24) | (Clamp255((int)cr) << 16) | (Clamp255((int)cg) << 8) | Clamp255((int)cb);
                }

                if (blending == M3GCompositingMode.REPLACE || ((pixelColor >> 24) & 0xFF) >= 254)
                    _fb[idx] = pixelColor | unchecked((int)0xFF000000);
                else if (blending == M3GCompositingMode.ALPHA || blending == M3GCompositingMode.ALPHA_ADD)
                    _fb[idx] = AlphaBlend(pixelColor, _fb[idx]);
            }
        }
    }

    void ClipAndRasterize(float[] clip, float[] wpos, float[] wnrm, float[] tc, int[] vcol,
        int i0, int i1, int i2,
        int matDiffuse, int matAmbient, int matEmissive,
        M3GImage2D? tex, bool depthTest, bool depthWrite, int blending, int culling)
    {
        // Sutherland-Hodgman near plane clipping (w >= epsilon)
        const float EPS = 0.001f;
        var input = new List<int> { i0, i1, i2 };
        var output = new List<ClipVert>();

        // build clip vertices
        var cverts = new List<ClipVert>();
        foreach (int idx in input)
            cverts.Add(new ClipVert(clip, wpos, wnrm, tc, vcol, idx));

        // clip against near plane (w >= EPS)
        for (int i = 0; i < cverts.Count; i++)
        {
            var curr = cverts[i];
            var next = cverts[(i + 1) % cverts.Count];
            bool currIn = curr.W >= EPS;
            bool nextIn = next.W >= EPS;

            if (currIn && nextIn) output.Add(next);
            else if (currIn && !nextIn)
            {
                float t = (curr.W - EPS) / (curr.W - next.W);
                output.Add(ClipVert.Lerp(curr, next, t));
            }
            else if (!currIn && nextIn)
            {
                float t = (curr.W - EPS) / (curr.W - next.W);
                output.Add(ClipVert.Lerp(curr, next, t));
                output.Add(next);
            }
        }

        if (output.Count < 3) return;

        // inject clipped vertices into temporary arrays and rasterize
        int baseIdx = clip.Length / 4;
        int totalVerts = baseIdx + output.Count;
        var newClip = new float[totalVerts * 4];
        var newWpos = new float[totalVerts * 3];
        var newWnrm = new float[totalVerts * 3];
        var newTc = new float[totalVerts * 2];
        var newVcol = new int[totalVerts];
        Array.Copy(clip, newClip, clip.Length);
        Array.Copy(wpos, newWpos, wpos.Length);
        Array.Copy(wnrm, newWnrm, wnrm.Length);
        Array.Copy(tc, newTc, tc.Length);
        Array.Copy(vcol, newVcol, vcol.Length);

        for (int i = 0; i < output.Count; i++)
        {
            int vi = baseIdx + i;
            var v = output[i];
            newClip[vi*4] = v.X; newClip[vi*4+1] = v.Y; newClip[vi*4+2] = v.Z; newClip[vi*4+3] = v.W;
            newWpos[vi*3] = v.WX; newWpos[vi*3+1] = v.WY; newWpos[vi*3+2] = v.WZ;
            newWnrm[vi*3] = v.NX; newWnrm[vi*3+1] = v.NY; newWnrm[vi*3+2] = v.NZ;
            newTc[vi*2] = v.U; newTc[vi*2+1] = v.V;
            newVcol[vi] = v.Col;
        }

        // fan triangulate the clipped polygon
        for (int i = 1; i < output.Count - 1; i++)
        {
            RasterizeTriangle(newClip, newWpos, newWnrm, newTc, newVcol,
                baseIdx, baseIdx + i, baseIdx + i + 1,
                matDiffuse, matAmbient, matEmissive, tex, depthTest, depthWrite, blending, culling);
        }
    }

    struct ClipVert
    {
        public float X, Y, Z, W;
        public float WX, WY, WZ;
        public float NX, NY, NZ;
        public float U, V;
        public int Col;

        public ClipVert(float[] clip, float[] wpos, float[] wnrm, float[] tc, int[] vcol, int i)
        {
            X = clip[i*4]; Y = clip[i*4+1]; Z = clip[i*4+2]; W = clip[i*4+3];
            WX = wpos[i*3]; WY = wpos[i*3+1]; WZ = wpos[i*3+2];
            NX = wnrm[i*3]; NY = wnrm[i*3+1]; NZ = wnrm[i*3+2];
            U = tc[i*2]; V = tc[i*2+1];
            Col = vcol[i];
        }

        public static ClipVert Lerp(ClipVert a, ClipVert b, float t)
        {
            float it = 1 - t;
            var r = new ClipVert();
            r.X = a.X * it + b.X * t; r.Y = a.Y * it + b.Y * t;
            r.Z = a.Z * it + b.Z * t; r.W = a.W * it + b.W * t;
            r.WX = a.WX * it + b.WX * t; r.WY = a.WY * it + b.WY * t; r.WZ = a.WZ * it + b.WZ * t;
            r.NX = a.NX * it + b.NX * t; r.NY = a.NY * it + b.NY * t; r.NZ = a.NZ * it + b.NZ * t;
            r.U = a.U * it + b.U * t; r.V = a.V * it + b.V * t;
            r.Col = LerpColor(a.Col, b.Col, t);
            return r;
        }

        static int LerpColor(int a, int b, float t)
        {
            float it = 1 - t;
            int ar = Clamp255((int)(((a >> 16) & 0xFF) * it + ((b >> 16) & 0xFF) * t));
            int ag = Clamp255((int)(((a >> 8) & 0xFF) * it + ((b >> 8) & 0xFF) * t));
            int ab = Clamp255((int)((a & 0xFF) * it + (b & 0xFF) * t));
            int aa = Clamp255((int)(((a >> 24) & 0xFF) * it + ((b >> 24) & 0xFF) * t));
            return (aa << 24) | (ar << 16) | (ag << 8) | ab;
        }
    }

    int ComputeVertexColor(float[] wpos, float[] wnrm, int[] vcol, int i,
        int matDiffuse, int matAmbient, int matEmissive, bool hasTexture)
    {
        float nx = wnrm[i * 3], ny = wnrm[i * 3 + 1], nz = wnrm[i * 3 + 2];
        float px = wpos[i * 3], py = wpos[i * 3 + 1], pz = wpos[i * 3 + 2];

        float ambR = 0, ambG = 0, ambB = 0;
        float difR = 0, difG = 0, difB = 0;

        foreach (var (light, ltf) in _lights)
        {
            float lr = ((light.Color >> 16) & 0xFF) / 255f * light.Intensity;
            float lg = ((light.Color >> 8) & 0xFF) / 255f * light.Intensity;
            float lb = (light.Color & 0xFF) / 255f * light.Intensity;

            if (light.Mode == M3GLight.AMBIENT)
            {
                ambR += lr; ambG += lg; ambB += lb;
            }
            else if (light.Mode == M3GLight.DIRECTIONAL)
            {
                float dx = -ltf[8], dy = -ltf[9], dz = -ltf[10];
                float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len > 1e-9f) { dx /= len; dy /= len; dz /= len; }
                float dot = Math.Max(0, nx * dx + ny * dy + nz * dz);
                difR += lr * dot; difG += lg * dot; difB += lb * dot;
            }
            else // OMNI or SPOT
            {
                float lx = ltf[3] - px, ly = ltf[7] - py, lz = ltf[11] - pz;
                float dist = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
                if (dist < 1e-9f) continue;
                lx /= dist; ly /= dist; lz /= dist;
                float atten = 1f / (light.AttConst + light.AttLin * dist + light.AttQuad * dist * dist);
                float dot = Math.Max(0, nx * lx + ny * ly + nz * lz);
                difR += lr * dot * atten; difG += lg * dot * atten; difB += lb * dot * atten;
            }
        }

        if (_lights.Count == 0) { ambR = 0.3f; ambG = 0.3f; ambB = 0.3f; difR = 0.7f; difG = 0.7f; difB = 0.7f; }

        float mdr = ((matDiffuse >> 16) & 0xFF) / 255f;
        float mdg = ((matDiffuse >> 8) & 0xFF) / 255f;
        float mdb = (matDiffuse & 0xFF) / 255f;
        float mda = ((matDiffuse >> 24) & 0xFF) / 255f;
        float mar = ((matAmbient >> 16) & 0xFF) / 255f;
        float mag = ((matAmbient >> 8) & 0xFF) / 255f;
        float mab = (matAmbient & 0xFF) / 255f;
        float mer = ((matEmissive >> 16) & 0xFF) / 255f;
        float meg = ((matEmissive >> 8) & 0xFF) / 255f;
        float meb = (matEmissive & 0xFF) / 255f;

        float fr = mer + mar * ambR + mdr * difR;
        float fg = meg + mag * ambG + mdg * difG;
        float fb = meb + mab * ambB + mdb * difB;

        if (hasTexture) { fr = Math.Min(fr * 255, 255); fg = Math.Min(fg * 255, 255); fb = Math.Min(fb * 255, 255); }
        else { fr *= 255; fg *= 255; fb *= 255; }

        return (Clamp255((int)(mda * 255)) << 24) |
               (Clamp255((int)fr) << 16) |
               (Clamp255((int)fg) << 8) |
               Clamp255((int)fb);
    }

    static int SampleTexture(M3GImage2D tex, float u, float v)
    {
        u = u - MathF.Floor(u);
        v = v - MathF.Floor(v);
        int tx = Math.Clamp((int)(u * tex.Width), 0, tex.Width - 1);
        int ty = Math.Clamp((int)(v * tex.Height), 0, tex.Height - 1);
        return tex.Pixels[ty * tex.Width + tx];
    }

    static int AlphaBlend(int src, int dst)
    {
        int sa = (src >> 24) & 0xFF;
        if (sa == 0) return dst;
        int inv = 255 - sa;
        int r = (((src >> 16) & 0xFF) * sa + ((dst >> 16) & 0xFF) * inv) >> 8;
        int g = (((src >> 8) & 0xFF) * sa + ((dst >> 8) & 0xFF) * inv) >> 8;
        int b = ((src & 0xFF) * sa + (dst & 0xFF) * inv) >> 8;
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    static float[] Transpose3x3(float[] m)
    {
        var r = (float[])m.Clone();
        (r[1], r[4]) = (r[4], r[1]);
        (r[2], r[8]) = (r[8], r[2]);
        (r[6], r[9]) = (r[9], r[6]);
        return r;
    }
}

// ── .m3g binary file loader ───────────────────────────────────────

class M3GLoader
{
    List<M3GObject3D?> _objects = new() { null }; // 1-indexed, 0 = null
    byte[] _data = Array.Empty<byte>();
    int _pos;

    public M3GObject3D?[] Load(byte[] fileData)
    {
        _objects = new List<M3GObject3D?> { null };
        if (fileData.Length < 12) return _objects.ToArray();

        int filePos = 0;
        // verify header
        byte[] magic = { 0xAB, 0x4A, 0x53, 0x52, 0x31, 0x38, 0x34, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
        for (int i = 0; i < 12; i++)
            if (fileData[i] != magic[i]) return _objects.ToArray();
        filePos = 12;

        while (filePos < fileData.Length - 1)
        {
            if (filePos + 9 > fileData.Length) break;
            int compressionScheme = fileData[filePos++];
            int totalSectionLen = ReadU32(fileData, ref filePos);
            int uncompressedLen = ReadU32(fileData, ref filePos);

            int dataLen = totalSectionLen - 4 - 4; // minus uncompLen + checksum
            if (dataLen < 0 || filePos + dataLen > fileData.Length) break;

            byte[] sectionData;
            if (compressionScheme == 0)
            {
                sectionData = new byte[dataLen];
                Array.Copy(fileData, filePos, sectionData, 0, dataLen);
            }
            else
            {
                try
                {
                    using var ms = new MemoryStream(fileData, filePos, dataLen);
                    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    ds.CopyTo(outMs);
                    sectionData = outMs.ToArray();
                }
                catch
                {
                    // try skipping first 2 bytes (zlib header)
                    if (dataLen > 2)
                    {
                        try
                        {
                            using var ms = new MemoryStream(fileData, filePos + 2, dataLen - 2);
                            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                            using var outMs = new MemoryStream();
                            ds.CopyTo(outMs);
                            sectionData = outMs.ToArray();
                        }
                        catch { filePos += dataLen; continue; }
                    }
                    else { filePos += dataLen; continue; }
                }
            }
            filePos += dataLen;

            _data = sectionData;
            _pos = 0;
            while (_pos < _data.Length)
            {
                if (_pos + 5 > _data.Length) break;
                int objectType = ReadByte();
                int objectLen = ReadI32();
                int endPos = _pos + objectLen;
                if (endPos > _data.Length) break;

                try { ParseObject(objectType); }
                catch { }
                _pos = endPos;
            }
        }

        // link parent references for groups
        foreach (var obj in _objects)
            if (obj is M3GGroup grp)
                foreach (var child in grp.Children)
                    child.Parent = grp;

        return _objects.ToArray();
    }

    void ParseObject(int type)
    {
        switch (type)
        {
            case 0: ParseHeader(); break;
            case 1: _objects.Add(ParseAnimationController()); break;
            case 2: _objects.Add(ParseAnimationTrack()); break;
            case 3: _objects.Add(ParseAppearance()); break;
            case 4: _objects.Add(ParseBackground()); break;
            case 5: _objects.Add(ParseCamera()); break;
            case 6: _objects.Add(ParseCompositingMode()); break;
            case 7: _objects.Add(ParseFog()); break;
            case 8: _objects.Add(ParseGroup()); break;
            case 9: _objects.Add(ParseImage2D()); break;
            case 11: _objects.Add(ParseKeyframeSequence()); break;
            case 12: _objects.Add(ParseLight()); break;
            case 13: _objects.Add(ParseMaterial()); break;
            case 14: _objects.Add(ParseMesh()); break;
            case 15: _objects.Add(ParseMorphingMesh()); break;
            case 16: _objects.Add(ParsePolygonMode()); break;
            case 17: _objects.Add(ParseSkinnedMesh()); break;
            case 18: _objects.Add(ParseSprite3D()); break;
            case 19: _objects.Add(ParseTexture2D()); break;
            case 20: _objects.Add(ParseTriangleStripArray()); break;
            case 21: _objects.Add(ParseVertexArray()); break;
            case 22: _objects.Add(ParseVertexBuffer()); break;
            case 23: _objects.Add(ParseWorld()); break;
            default: _objects.Add(new M3GObject3D()); break;
        }
    }

    // ── base type readers ─────────────────────────────────────────

    void ReadObject3D(M3GObject3D obj)
    {
        obj.UserID = ReadI32();
        int animCount = ReadI32();
        for (int i = 0; i < animCount; i++) ReadI32(); // skip anim track refs
        int paramCount = ReadI32();
        for (int i = 0; i < paramCount; i++)
        {
            ReadI32(); // paramID
            int len = ReadI32();
            _pos += len;
        }
    }

    void ReadTransformable(M3GTransformable obj)
    {
        ReadObject3D(obj);
        bool hasComponent = ReadBool();
        if (hasComponent)
        {
            float tx = ReadF32(), ty = ReadF32(), tz = ReadF32();
            float sx = ReadF32(), sy = ReadF32(), sz = ReadF32();
            float angle = ReadF32();
            float ax = ReadF32(), ay = ReadF32(), az = ReadF32();
            obj.LoadComponentTransform(new[] { tx, ty, tz }, new[] { sx, sy, sz }, angle, new[] { ax, ay, az });
        }
        bool hasGeneral = ReadBool();
        if (hasGeneral)
        {
            var m = new float[16];
            for (int i = 0; i < 16; i++) m[i] = ReadF32();
            obj.LoadGeneralTransform(m);
        }
    }

    void ReadNode(M3GNode node)
    {
        ReadTransformable(node);
        node.RenderingEnabled = ReadBool();
        node.PickingEnabled = ReadBool();
        node.AlphaFactor = ReadByte() / 255f;
        node.Scope = ReadI32();
        bool hasAlignment = ReadBool();
        if (hasAlignment) { ReadByte(); ReadByte(); ReadI32(); ReadI32(); }
    }

    // ── specific type parsers ─────────────────────────────────────

    void ParseHeader()
    {
        ReadByte(); ReadByte(); // version
        ReadBool(); // hasExternalRefs
        ReadI32(); // totalFileSize
        ReadI32(); // approxContentSize
        // authoring field - string
        int len = ReadI32();
        _pos += len;
    }

    M3GAnimationController ParseAnimationController()
    {
        var ac = new M3GAnimationController();
        ReadObject3D(ac);
        ac.Speed = ReadF32();
        ac.Weight = ReadF32();
        ReadI32(); ReadI32(); // active interval start/end
        ReadF32(); // reference sequence time
        ReadI32(); // reference world time
        return ac;
    }

    M3GAnimationTrack ParseAnimationTrack()
    {
        var at = new M3GAnimationTrack();
        ReadObject3D(at);
        ReadI32(); // keyframe sequence ref
        ReadI32(); // animation controller ref
        ReadI32(); // property ID
        return at;
    }

    M3GAppearance ParseAppearance()
    {
        var app = new M3GAppearance();
        ReadObject3D(app);
        app.Layer = ReadByte();
        app.CompositingMode = GetObj<M3GCompositingMode>(ReadI32());
        app.Fog = GetObj<M3GFog>(ReadI32());
        app.PolygonMode = GetObj<M3GPolygonMode>(ReadI32());
        app.Material = GetObj<M3GMaterial>(ReadI32());
        int texCount = ReadI32();
        app.Textures = new M3GTexture2D?[texCount];
        for (int i = 0; i < texCount; i++)
            app.Textures[i] = GetObj<M3GTexture2D>(ReadI32());
        return app;
    }

    M3GBackground ParseBackground()
    {
        var bg = new M3GBackground();
        ReadObject3D(bg);
        bg.Color = ReadColorRGBA();
        bg.Image = GetObj<M3GImage2D>(ReadI32());
        ReadByte(); ReadByte(); // image mode X, Y
        bg.CropX = ReadI32(); bg.CropY = ReadI32();
        bg.CropW = ReadI32(); bg.CropH = ReadI32();
        bg.DepthClearEnabled = ReadBool();
        bg.ColorClearEnabled = ReadBool();
        return bg;
    }

    M3GCamera ParseCamera()
    {
        var cam = new M3GCamera();
        ReadNode(cam);
        int projType = ReadByte();
        cam.ProjectionType = projType;
        if (projType == M3GCamera.GENERIC)
        {
            for (int i = 0; i < 16; i++) cam.GenericMatrix[i] = ReadF32();
        }
        else
        {
            cam.Fovy = ReadF32();
            cam.AspectRatio = ReadF32();
            cam.Near = ReadF32();
            cam.Far = ReadF32();
        }
        return cam;
    }

    M3GCompositingMode ParseCompositingMode()
    {
        var cm = new M3GCompositingMode();
        ReadObject3D(cm);
        cm.DepthTestEnabled = ReadBool();
        cm.DepthWriteEnabled = ReadBool();
        cm.ColorWriteEnabled = ReadBool();
        cm.AlphaWriteEnabled = ReadBool();
        cm.Blending = ReadByte();
        cm.AlphaThreshold = ReadByte() / 255f;
        ReadF32(); ReadF32(); // depth offset
        return cm;
    }

    M3GFog ParseFog()
    {
        var fog = new M3GFog();
        ReadObject3D(fog);
        fog.Color = ReadColorRGB();
        fog.Mode = ReadByte();
        if (fog.Mode == 80) fog.Density = ReadF32(); // EXPONENTIAL
        else { fog.Near = ReadF32(); fog.Far = ReadF32(); } // LINEAR
        return fog;
    }

    M3GGroup ParseGroup()
    {
        var grp = new M3GGroup();
        ReadNode(grp);
        int childCount = ReadI32();
        for (int i = 0; i < childCount; i++)
        {
            var child = GetObj<M3GNode>(ReadI32());
            if (child != null) grp.Children.Add(child);
        }
        return grp;
    }

    M3GImage2D ParseImage2D()
    {
        var img = new M3GImage2D();
        ReadObject3D(img);
        img.Format = ReadByte();
        bool isMutable = ReadBool();
        img.Width = ReadI32();
        img.Height = ReadI32();
        img.Pixels = new int[img.Width * img.Height];

        if (!isMutable)
        {
            int paletteSize = ReadI32();
            byte[]? palette = null;
            if (paletteSize > 0)
            {
                palette = new byte[paletteSize];
                for (int i = 0; i < paletteSize; i++) palette[i] = ReadByteRaw();
            }

            int pixelCount = ReadI32();
            byte[] pixelData = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++) pixelData[i] = ReadByteRaw();

            if (palette != null)
                DecodeIndexedPixels(img, palette, pixelData);
            else
                DecodeRawPixels(img, pixelData);
        }
        else
        {
            Array.Fill(img.Pixels, unchecked((int)0xFFFFFFFF));
        }

        return img;
    }

    void DecodeRawPixels(M3GImage2D img, byte[] data)
    {
        int p = 0;
        for (int i = 0; i < img.Pixels.Length && p < data.Length; i++)
        {
            switch (img.Format)
            {
                case 96: // ALPHA
                    img.Pixels[i] = (data[p++] << 24) | 0xFFFFFF;
                    break;
                case 97: // LUMINANCE
                    { int l = data[p++]; img.Pixels[i] = unchecked((int)0xFF000000) | (l << 16) | (l << 8) | l; }
                    break;
                case 98: // LUMINANCE_ALPHA
                    { int l = data[p++]; int a = p < data.Length ? data[p++] : 255;
                      img.Pixels[i] = (a << 24) | (l << 16) | (l << 8) | l; }
                    break;
                case 99: // RGB
                    { int r = data[p++]; int g = p < data.Length ? data[p++] : 0; int b = p < data.Length ? data[p++] : 0;
                      img.Pixels[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b; }
                    break;
                case 100: // RGBA
                    { int r = data[p++]; int g = p < data.Length ? data[p++] : 0;
                      int b = p < data.Length ? data[p++] : 0; int a = p < data.Length ? data[p++] : 255;
                      img.Pixels[i] = (a << 24) | (r << 16) | (g << 8) | b; }
                    break;
            }
        }
    }

    void DecodeIndexedPixels(M3GImage2D img, byte[] palette, byte[] indices)
    {
        int bpp = img.Format switch { 96 => 1, 97 => 1, 98 => 2, 99 => 3, 100 => 4, _ => 3 };
        for (int i = 0; i < img.Pixels.Length && i < indices.Length; i++)
        {
            int idx = indices[i] & 0xFF;
            int poff = idx * bpp;
            if (poff + bpp > palette.Length) { img.Pixels[i] = unchecked((int)0xFFFF00FF); continue; }
            switch (img.Format)
            {
                case 99: img.Pixels[i] = unchecked((int)0xFF000000) | (palette[poff] << 16) | (palette[poff + 1] << 8) | palette[poff + 2]; break;
                case 100: img.Pixels[i] = (palette[poff + 3] << 24) | (palette[poff] << 16) | (palette[poff + 1] << 8) | palette[poff + 2]; break;
                default:
                    { int l = palette[poff]; img.Pixels[i] = unchecked((int)0xFF000000) | (l << 16) | (l << 8) | l; }
                    break;
            }
        }
    }

    M3GKeyframeSequence ParseKeyframeSequence()
    {
        var ks = new M3GKeyframeSequence();
        ReadObject3D(ks);
        ReadByte(); // interpolation
        ReadByte(); // repeat mode
        ReadByte(); // encoding
        ReadI32(); // duration
        ReadI32(); // valid range start
        ReadI32(); // valid range end
        int compCount = ReadI32();
        int keyframeCount = ReadI32();
        // skip keyframe data
        for (int i = 0; i < keyframeCount; i++)
        {
            ReadI32(); // time
            for (int j = 0; j < compCount; j++) ReadF32();
        }
        return ks;
    }

    M3GLight ParseLight()
    {
        var light = new M3GLight();
        ReadNode(light);
        light.AttConst = ReadF32();
        light.AttLin = ReadF32();
        light.AttQuad = ReadF32();
        light.Color = ReadColorRGB();
        light.Mode = ReadByte();
        light.Intensity = ReadF32();
        light.SpotAngle = ReadF32();
        light.SpotExponent = ReadF32();
        return light;
    }

    M3GMaterial ParseMaterial()
    {
        var mat = new M3GMaterial();
        ReadObject3D(mat);
        mat.AmbientColor = ReadColorRGB();
        mat.DiffuseColor = ReadColorRGBA();
        mat.EmissiveColor = ReadColorRGB();
        mat.SpecularColor = ReadColorRGB();
        mat.Shininess = ReadF32();
        mat.VertexColorTracking = ReadBool();
        return mat;
    }

    M3GMesh ParseMesh()
    {
        var mesh = new M3GMesh();
        ReadNode(mesh);
        mesh.VertexBuffer = GetObj<M3GVertexBuffer>(ReadI32());
        int subCount = ReadI32();
        mesh.IndexBuffers = new M3GTriangleStripArray?[subCount];
        mesh.Appearances = new M3GAppearance?[subCount];
        for (int i = 0; i < subCount; i++)
        {
            mesh.IndexBuffers[i] = GetObj<M3GTriangleStripArray>(ReadI32());
            mesh.Appearances[i] = GetObj<M3GAppearance>(ReadI32());
        }
        return mesh;
    }

    M3GMesh ParseMorphingMesh()
    {
        var mesh = new M3GMesh();
        ReadNode(mesh);
        mesh.VertexBuffer = GetObj<M3GVertexBuffer>(ReadI32());
        int morphTargetCount = ReadI32();
        for (int i = 0; i < morphTargetCount; i++) { ReadI32(); ReadF32(); }
        int subCount = ReadI32();
        mesh.IndexBuffers = new M3GTriangleStripArray?[subCount];
        mesh.Appearances = new M3GAppearance?[subCount];
        for (int i = 0; i < subCount; i++)
        {
            mesh.IndexBuffers[i] = GetObj<M3GTriangleStripArray>(ReadI32());
            mesh.Appearances[i] = GetObj<M3GAppearance>(ReadI32());
        }
        return mesh;
    }

    M3GMesh ParseSkinnedMesh()
    {
        var mesh = new M3GMesh();
        ReadNode(mesh);
        mesh.VertexBuffer = GetObj<M3GVertexBuffer>(ReadI32());
        int subCount = ReadI32();
        mesh.IndexBuffers = new M3GTriangleStripArray?[subCount];
        mesh.Appearances = new M3GAppearance?[subCount];
        for (int i = 0; i < subCount; i++)
        {
            mesh.IndexBuffers[i] = GetObj<M3GTriangleStripArray>(ReadI32());
            mesh.Appearances[i] = GetObj<M3GAppearance>(ReadI32());
        }
        ReadI32(); // skeleton group ref
        int transformRefCount = ReadI32();
        for (int i = 0; i < transformRefCount; i++)
        {
            ReadI32(); // transform node ref
            ReadI32(); ReadI32(); ReadI32(); // firstVertex, vertexCount, weight
        }
        return mesh;
    }

    M3GPolygonMode ParsePolygonMode()
    {
        var pm = new M3GPolygonMode();
        ReadObject3D(pm);
        pm.Culling = ReadByte();
        pm.Shading = ReadByte();
        pm.Winding = ReadByte();
        pm.TwoSidedLighting = ReadBool();
        ReadBool(); // localCameraLighting
        pm.PerspectiveCorrection = ReadBool();
        return pm;
    }

    M3GSprite3D ParseSprite3D()
    {
        var sp = new M3GSprite3D();
        ReadNode(sp);
        ReadI32(); // image ref
        ReadI32(); // appearance ref
        ReadBool(); // isScaled
        ReadI32(); ReadI32(); ReadI32(); ReadI32(); // crop
        return sp;
    }

    M3GTexture2D ParseTexture2D()
    {
        var tex = new M3GTexture2D();
        ReadTransformable(tex);
        tex.Image = GetObj<M3GImage2D>(ReadI32());
        tex.BlendColor = ReadColorRGB();
        tex.Blending = ReadByte();
        tex.WrapS = ReadByte();
        tex.WrapT = ReadByte();
        tex.LevelFilter = ReadByte();
        tex.ImageFilter = ReadByte();
        return tex;
    }

    M3GTriangleStripArray ParseTriangleStripArray()
    {
        var tsa = new M3GTriangleStripArray();
        ReadObject3D(tsa);
        int encoding = ReadByte();

        if (encoding == 0)
        {
            tsa.FirstIndex = ReadI32();
        }
        else
        {
            int indexCount = ReadI32();
            tsa.ExplicitIndices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                tsa.ExplicitIndices[i] = encoding switch
                {
                    128 => ReadI32(),
                    129 => ReadByte(),
                    130 => ReadU16(),
                    _ => ReadI32()
                };
            }
        }

        int stripCount = ReadI32();
        tsa.StripLengths = new int[stripCount];
        for (int i = 0; i < stripCount; i++)
            tsa.StripLengths[i] = ReadI32();
        return tsa;
    }

    M3GVertexArray ParseVertexArray()
    {
        var va = new M3GVertexArray();
        ReadObject3D(va);
        va.ComponentCount = ReadByte();
        va.ComponentSize = ReadByte();
        int encoding = ReadU16();
        va.VertexCount = ReadU16();

        int total = va.VertexCount * va.ComponentCount;
        va.Data = new float[total];

        if (encoding == 0) // raw
        {
            for (int i = 0; i < total; i++)
            {
                va.Data[i] = va.ComponentSize switch
                {
                    1 => (sbyte)ReadByteRaw(),
                    2 => ReadI16(),
                    _ => ReadF32()
                };
            }
        }
        else // delta encoded
        {
            float[] prev = new float[va.ComponentCount];
            for (int v = 0; v < va.VertexCount; v++)
            {
                for (int c = 0; c < va.ComponentCount; c++)
                {
                    float delta = va.ComponentSize switch
                    {
                        1 => (sbyte)ReadByteRaw(),
                        2 => ReadI16(),
                        _ => ReadF32()
                    };
                    if (v == 0) prev[c] = delta;
                    else prev[c] += delta;
                    va.Data[v * va.ComponentCount + c] = prev[c];
                }
            }
        }
        return va;
    }

    M3GVertexBuffer ParseVertexBuffer()
    {
        var vb = new M3GVertexBuffer();
        ReadObject3D(vb);
        vb.DefaultColor = ReadColorRGBA();
        vb.Positions = GetObj<M3GVertexArray>(ReadI32());
        vb.PositionBias[0] = ReadF32();
        vb.PositionBias[1] = ReadF32();
        vb.PositionBias[2] = ReadF32();
        vb.PositionScale = ReadF32();
        vb.Normals = GetObj<M3GVertexArray>(ReadI32());
        vb.Colors = GetObj<M3GVertexArray>(ReadI32());
        int tcCount = ReadI32();
        vb.TexCoords = new M3GVertexArray?[tcCount];
        vb.TexCoordBias = new float[tcCount][];
        vb.TexCoordScale = new float[tcCount];
        for (int i = 0; i < tcCount; i++)
        {
            vb.TexCoords[i] = GetObj<M3GVertexArray>(ReadI32());
            vb.TexCoordBias[i] = new[] { ReadF32(), ReadF32(), ReadF32() };
            vb.TexCoordScale[i] = ReadF32();
        }
        return vb;
    }

    M3GWorld ParseWorld()
    {
        var world = new M3GWorld();
        ReadNode(world);
        int childCount = ReadI32();
        for (int i = 0; i < childCount; i++)
        {
            var child = GetObj<M3GNode>(ReadI32());
            if (child != null) world.Children.Add(child);
        }
        world.ActiveCamera = GetObj<M3GCamera>(ReadI32());
        world.Background = GetObj<M3GBackground>(ReadI32());
        return world;
    }

    // ── primitive readers ─────────────────────────────────────────

    T? GetObj<T>(int index) where T : M3GObject3D =>
        index > 0 && index < _objects.Count ? _objects[index] as T : null;

    byte ReadByteRaw() => _pos < _data.Length ? _data[_pos++] : (byte)0;
    int ReadByte() => _pos < _data.Length ? _data[_pos++] : 0;
    bool ReadBool() => ReadByte() != 0;

    short ReadI16()
    {
        if (_pos + 2 > _data.Length) return 0;
        short v = (short)(_data[_pos] | (_data[_pos + 1] << 8)); // little-endian
        _pos += 2;
        return v;
    }

    int ReadU16()
    {
        if (_pos + 2 > _data.Length) return 0;
        int v = _data[_pos] | (_data[_pos + 1] << 8);
        _pos += 2;
        return v;
    }

    int ReadI32()
    {
        if (_pos + 4 > _data.Length) return 0;
        int v = _data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24);
        _pos += 4;
        return v;
    }

    float ReadF32()
    {
        if (_pos + 4 > _data.Length) return 0;
        float v = BitConverter.ToSingle(_data, _pos);
        _pos += 4;
        return v;
    }

    int ReadColorRGB()
    {
        int r = ReadByte(), g = ReadByte(), b = ReadByte();
        return (r << 16) | (g << 8) | b;
    }

    int ReadColorRGBA()
    {
        int r = ReadByte(), g = ReadByte(), b = ReadByte(), a = ReadByte();
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    static int ReadU32(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length) return 0;
        int v = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
        pos += 4;
        return v;
    }
}
