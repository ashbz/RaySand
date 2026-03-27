using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PspEmu;

/// <summary>
/// Software renderer for the PSP Graphics Engine.
/// Reads vertices, transforms, rasterizes triangles/sprites, writes to VRAM/RAM framebuffer.
/// </summary>
sealed class GeRenderer
{
    readonly PspBus _bus;
    readonly ushort[] _depthBuf = new ushort[512 * 512]; // max FB size

    public GeRenderer(PspBus bus)
    {
        _bus = bus;
    }

    public void ClearBuffers(int flags)
    {
        // flags: 1=color, 2=stencil/alpha, 4=depth
        if ((flags & 4) != 0)
            Array.Clear(_depthBuf);
    }

    // ── Vertex decoding ──

    struct DecodedVertex
    {
        public Vector3 Pos;
        public Vector2 Uv;
        public uint Color;
        public Vector3 Normal;
        public float Weight;
        public DecodedVertex() { Weight = 0; }
    }

    public void DrawPrimitive(PspGe ge, int primType, int count, uint vtype, uint vertexAddr, uint indexAddr)
    {
        // Decode vertex format from vtype register
        int texFmt  = (int)((vtype >> 0) & 3);   // 0=none, 1=u8, 2=u16, 3=float
        int colFmt  = (int)((vtype >> 2) & 7);   // 0=none, 4=5650, 5=5551, 6=4444, 7=8888
        int nrmFmt  = (int)((vtype >> 5) & 3);   // 0=none, 1=s8, 2=s16, 3=float
        int posFmt  = (int)((vtype >> 7) & 3);   // 1=s8, 2=s16, 3=float
        int wtFmt   = (int)((vtype >> 9) & 3);   // weight format
        int wtCount = (int)((vtype >> 14) & 7);
        int idxFmt  = (int)((vtype >> 11) & 3);  // 0=none, 1=u8, 2=u16
        int morphCount = (int)((vtype >> 18) & 7);
        bool through = ((vtype >> 23) & 1) != 0; // transform bypass

        int vertexSize = CalcVertexSize(texFmt, colFmt, nrmFmt, posFmt, wtFmt, wtCount);
        if (vertexSize == 0 || posFmt == 0) return;

        // Decode all vertices
        var verts = new DecodedVertex[count];
        for (int i = 0; i < count; i++)
        {
            int idx;
            if (idxFmt == 1)
                idx = _bus.Read8(indexAddr + (uint)i);
            else if (idxFmt == 2)
                idx = _bus.Read16(indexAddr + (uint)(i * 2));
            else
                idx = i;

            uint addr = vertexAddr + (uint)(idx * vertexSize);
            verts[i] = DecodeVertex(addr, texFmt, colFmt, nrmFmt, posFmt, wtFmt, wtCount);
        }

        // Transform (unless through mode)
        if (!through)
        {
            var world = BuildMatrix4x4From3x4(ge.WorldMatrix);
            var view = BuildMatrix4x4From3x4(ge.ViewMatrix);
            var proj = BuildMatrix4x4(ge.ProjMatrix);
            var mvp = world * view * proj;

            for (int i = 0; i < count; i++)
            {
                var v = verts[i];
                var pos4 = Vector4.Transform(v.Pos, mvp);

                if (pos4.W != 0)
                {
                    float invW = 1f / pos4.W;
                    v.Pos.X = pos4.X * invW * ge.ViewportScaleX + ge.ViewportTransX - ge.OffsetX;
                    v.Pos.Y = pos4.Y * invW * ge.ViewportScaleY + ge.ViewportTransY - ge.OffsetY;
                    v.Pos.Z = pos4.Z * invW * ge.ViewportScaleZ + ge.ViewportTransZ;
                }
                else
                {
                    v.Pos = new Vector3(pos4.X - ge.OffsetX, pos4.Y - ge.OffsetY, pos4.Z);
                }

                // Apply texture scale/offset
                v.Uv.X = v.Uv.X * ge.TexScaleU + ge.TexOffsetU;
                v.Uv.Y = v.Uv.Y * ge.TexScaleV + ge.TexOffsetV;

                verts[i] = v;
            }
        }

        // Rasterize — GE fb pointers are always VRAM offsets (relative to EDRAM base)
        int fbWidth = ge.FbWidth > 0 ? ge.FbWidth : 512;
        uint fbAddr = ge.FbPtr;
        int fbFmt = ge.FbPixelFormat;
        byte[] fbMem = _bus.Vram;
        uint fbBase = (fbAddr >= 0x0400_0000) ? (fbAddr - 0x0400_0000) : fbAddr;

        switch (primType)
        {
            case 0: // Points
                for (int i = 0; i < count; i++)
                    DrawPoint(ref verts[i], fbMem, fbBase, fbWidth, fbFmt, ge);
                break;
            case 1: // Lines
                for (int i = 0; i + 1 < count; i += 2)
                    DrawLine(ref verts[i], ref verts[i + 1], fbMem, fbBase, fbWidth, fbFmt, ge);
                break;
            case 2: // Line strip
                for (int i = 0; i + 1 < count; i++)
                    DrawLine(ref verts[i], ref verts[i + 1], fbMem, fbBase, fbWidth, fbFmt, ge);
                break;
            case 3: // Triangles
                for (int i = 0; i + 2 < count; i += 3)
                    DrawTriangle(ref verts[i], ref verts[i + 1], ref verts[i + 2], fbMem, fbBase, fbWidth, fbFmt, ge, through);
                break;
            case 4: // Triangle strip
                for (int i = 0; i + 2 < count; i++)
                {
                    if ((i & 1) == 0)
                        DrawTriangle(ref verts[i], ref verts[i + 1], ref verts[i + 2], fbMem, fbBase, fbWidth, fbFmt, ge, through);
                    else
                        DrawTriangle(ref verts[i + 1], ref verts[i], ref verts[i + 2], fbMem, fbBase, fbWidth, fbFmt, ge, through);
                }
                break;
            case 5: // Triangle fan
                for (int i = 1; i + 1 < count; i++)
                    DrawTriangle(ref verts[0], ref verts[i], ref verts[i + 1], fbMem, fbBase, fbWidth, fbFmt, ge, through);
                break;
            case 6: // Sprites (rectangles, 2 vertices each)
                for (int i = 0; i + 1 < count; i += 2)
                    DrawSprite(ref verts[i], ref verts[i + 1], fbMem, fbBase, fbWidth, fbFmt, ge);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint AlignUp(uint off, uint align) => (off + align - 1) & ~(align - 1);

    DecodedVertex DecodeVertex(uint addr, int texFmt, int colFmt, int nrmFmt, int posFmt, int wtFmt, int wtCount)
    {
        var v = new DecodedVertex { Color = 0xFFFFFFFF };
        uint off = addr;

        // Weight (align to element size first)
        if (wtCount > 0 && wtFmt > 0)
        {
            uint wa = wtFmt switch { 1 => 1, 2 => 2, 3 => 4, _ => 1 };
            off = AlignUp(off, wa);
            for (int w = 0; w < wtCount; w++)
                off += wa;
        }

        // Texture (align to element size)
        if (texFmt > 0)
        {
            uint ta = texFmt switch { 1 => 1, 2 => 2, 3 => 4, _ => 1 };
            off = AlignUp(off, ta);
        }
        switch (texFmt)
        {
            case 1:
                v.Uv.X = _bus.Read8(off) / 128f; off++;
                v.Uv.Y = _bus.Read8(off) / 128f; off++;
                break;
            case 2:
                v.Uv.X = (short)_bus.Read16(off) / 32768f; off += 2;
                v.Uv.Y = (short)_bus.Read16(off) / 32768f; off += 2;
                break;
            case 3:
                v.Uv.X = ReadFloat(off); off += 4;
                v.Uv.Y = ReadFloat(off); off += 4;
                break;
        }

        // Color (align to element size: 16-bit formats=2, 32-bit=4)
        if (colFmt >= 4)
        {
            uint ca = colFmt == 7 ? 4u : 2u;
            off = AlignUp(off, ca);
        }
        switch (colFmt)
        {
            case 4: // 5650
            {
                ushort c = _bus.Read16(off); off += 2;
                uint r = ((uint)(c & 0x1F) * 255 / 31);
                uint g = ((uint)((c >> 5) & 0x3F) * 255 / 63);
                uint b = ((uint)((c >> 11) & 0x1F) * 255 / 31);
                v.Color = 0xFF000000 | (r << 16) | (g << 8) | b;
                break;
            }
            case 5: // 5551
            {
                ushort c = _bus.Read16(off); off += 2;
                uint r = ((uint)(c & 0x1F) * 255 / 31);
                uint g = ((uint)((c >> 5) & 0x1F) * 255 / 31);
                uint b = ((uint)((c >> 10) & 0x1F) * 255 / 31);
                uint a = (c & 0x8000) != 0 ? 255u : 0u;
                v.Color = (a << 24) | (r << 16) | (g << 8) | b;
                break;
            }
            case 6: // 4444
            {
                ushort c = _bus.Read16(off); off += 2;
                uint r = ((uint)(c & 0xF) * 255 / 15);
                uint g = ((uint)((c >> 4) & 0xF) * 255 / 15);
                uint b = ((uint)((c >> 8) & 0xF) * 255 / 15);
                uint a = ((uint)((c >> 12) & 0xF) * 255 / 15);
                v.Color = (a << 24) | (r << 16) | (g << 8) | b;
                break;
            }
            case 7: // 8888
                v.Color = _bus.Read32(off); off += 4;
                break;
        }

        // Normal (align to element size)
        if (nrmFmt > 0)
        {
            uint na = nrmFmt switch { 1 => 1, 2 => 2, 3 => 4, _ => 1 };
            off = AlignUp(off, na);
        }
        switch (nrmFmt)
        {
            case 1:
                v.Normal.X = (sbyte)_bus.Read8(off) / 127f; off++;
                v.Normal.Y = (sbyte)_bus.Read8(off) / 127f; off++;
                v.Normal.Z = (sbyte)_bus.Read8(off) / 127f; off++;
                break;
            case 2:
                v.Normal.X = (short)_bus.Read16(off) / 32767f; off += 2;
                v.Normal.Y = (short)_bus.Read16(off) / 32767f; off += 2;
                v.Normal.Z = (short)_bus.Read16(off) / 32767f; off += 2;
                break;
            case 3:
                v.Normal.X = ReadFloat(off); off += 4;
                v.Normal.Y = ReadFloat(off); off += 4;
                v.Normal.Z = ReadFloat(off); off += 4;
                break;
        }

        // Position (align to element size)
        if (posFmt > 0)
        {
            uint pa = posFmt switch { 1 => 1, 2 => 2, 3 => 4, _ => 1 };
            off = AlignUp(off, pa);
        }
        switch (posFmt)
        {
            case 1:
                v.Pos.X = (sbyte)_bus.Read8(off); off++;
                v.Pos.Y = (sbyte)_bus.Read8(off); off++;
                v.Pos.Z = (sbyte)_bus.Read8(off); off++;
                break;
            case 2:
                v.Pos.X = (short)_bus.Read16(off); off += 2;
                v.Pos.Y = (short)_bus.Read16(off); off += 2;
                v.Pos.Z = (short)_bus.Read16(off); off += 2;
                break;
            case 3:
                v.Pos.X = ReadFloat(off); off += 4;
                v.Pos.Y = ReadFloat(off); off += 4;
                v.Pos.Z = ReadFloat(off); off += 4;
                break;
        }

        return v;
    }

    static int ComponentAlign(int fmt, int[] alignTable) =>
        fmt > 0 && fmt <= alignTable.Length ? alignTable[fmt - 1] : 1;

    int CalcVertexSize(int texFmt, int colFmt, int nrmFmt, int posFmt, int wtFmt, int wtCount)
    {
        int[] genAlign = { 1, 2, 4 }; // s8/u8=1, s16/u16=2, float=4
        int[] colAlign = { 0, 0, 0, 2, 2, 2, 4 }; // 4=5650(2), 5=5551(2), 6=4444(2), 7=8888(4)

        int maxAlign = 1;
        int size = 0;

        // Weights
        if (wtCount > 0 && wtFmt > 0)
        {
            int wa = ComponentAlign(wtFmt, genAlign);
            if (wa > maxAlign) maxAlign = wa;
            size = (size + wa - 1) & ~(wa - 1);
            size += wtCount * wa;
        }

        // Texture
        if (texFmt > 0)
        {
            int ta = ComponentAlign(texFmt, genAlign);
            if (ta > maxAlign) maxAlign = ta;
            size = (size + ta - 1) & ~(ta - 1);
            size += texFmt switch { 1 => 2, 2 => 4, 3 => 8, _ => 0 };
        }

        // Color
        if (colFmt >= 4)
        {
            int ca = colAlign[colFmt - 1];
            if (ca > maxAlign) maxAlign = ca;
            size = (size + ca - 1) & ~(ca - 1);
            size += colFmt switch { 4 or 5 or 6 => 2, 7 => 4, _ => 0 };
        }

        // Normal
        if (nrmFmt > 0)
        {
            int na = ComponentAlign(nrmFmt, genAlign);
            if (na > maxAlign) maxAlign = na;
            size = (size + na - 1) & ~(na - 1);
            size += nrmFmt switch { 1 => 3, 2 => 6, 3 => 12, _ => 0 };
        }

        // Position (always present)
        if (posFmt > 0)
        {
            int pa = ComponentAlign(posFmt, genAlign);
            if (pa > maxAlign) maxAlign = pa;
            size = (size + pa - 1) & ~(pa - 1);
            size += posFmt switch { 1 => 3, 2 => 6, 3 => 12, _ => 0 };
        }

        // Align total to max component alignment
        size = (size + maxAlign - 1) & ~(maxAlign - 1);
        return size;
    }

    // ── Rasterization ──

    void DrawPoint(ref DecodedVertex v, byte[] fb, uint fbBase, int stride, int fmt, PspGe ge)
    {
        int x = (int)v.Pos.X, y = (int)v.Pos.Y;
        if (x < ge.ScissorX1 || x >= ge.ScissorX2 || y < ge.ScissorY1 || y >= ge.ScissorY2) return;
        WritePixel(fb, fbBase, stride, fmt, x, y, v.Color);
    }

    void DrawLine(ref DecodedVertex v0, ref DecodedVertex v1, byte[] fb, uint fbBase, int stride, int fmt, PspGe ge)
    {
        int x0 = (int)v0.Pos.X, y0 = (int)v0.Pos.Y;
        int x1 = (int)v1.Pos.X, y1 = (int)v1.Pos.Y;
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (x0 >= ge.ScissorX1 && x0 < ge.ScissorX2 && y0 >= ge.ScissorY1 && y0 < ge.ScissorY2)
                WritePixel(fb, fbBase, stride, fmt, x0, y0, v0.Color);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    void DrawTriangle(ref DecodedVertex v0, ref DecodedVertex v1, ref DecodedVertex v2,
        byte[] fb, uint fbBase, int stride, int fmt, PspGe ge, bool through)
    {
        bool clearMode = ge.ClearMode;

        // Bounding box
        int minX = Math.Max((int)MathF.Floor(Math.Min(v0.Pos.X, Math.Min(v1.Pos.X, v2.Pos.X))), ge.ScissorX1);
        int maxX = Math.Min((int)MathF.Ceiling(Math.Max(v0.Pos.X, Math.Max(v1.Pos.X, v2.Pos.X))), ge.ScissorX2 - 1);
        int minY = Math.Max((int)MathF.Floor(Math.Min(v0.Pos.Y, Math.Min(v1.Pos.Y, v2.Pos.Y))), ge.ScissorY1);
        int maxY = Math.Min((int)MathF.Ceiling(Math.Max(v0.Pos.Y, Math.Max(v1.Pos.Y, v2.Pos.Y))), ge.ScissorY2 - 1);

        float area = EdgeFunction(v0.Pos, v1.Pos, v2.Pos);
        if (MathF.Abs(area) < 0.001f) return;

        if (!clearMode && ge.CullEnable)
        {
            bool cw = area > 0;
            if (ge.CullFace == 0 && cw) return;
            if (ge.CullFace == 1 && !cw) return;
        }

        float invArea = 1f / area;
        int clearFlags = ge.ClearFlags;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector3(x + 0.5f, y + 0.5f, 0);
                float w0 = EdgeFunction(v1.Pos, v2.Pos, p);
                float w1 = EdgeFunction(v2.Pos, v0.Pos, p);
                float w2 = EdgeFunction(v0.Pos, v1.Pos, p);

                if (area > 0)
                {
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                }
                else
                {
                    if (w0 > 0 || w1 > 0 || w2 > 0) continue;
                }

                w0 *= invArea; w1 *= invArea; w2 *= invArea;

                // Interpolate color
                uint color;
                if (ge.ShadeModel == 1)
                {
                    uint r = (uint)(w0 * ((v0.Color >> 16) & 0xFF) + w1 * ((v1.Color >> 16) & 0xFF) + w2 * ((v2.Color >> 16) & 0xFF));
                    uint g = (uint)(w0 * ((v0.Color >> 8) & 0xFF) + w1 * ((v1.Color >> 8) & 0xFF) + w2 * ((v2.Color >> 8) & 0xFF));
                    uint b = (uint)(w0 * (v0.Color & 0xFF) + w1 * (v1.Color & 0xFF) + w2 * (v2.Color & 0xFF));
                    uint a = (uint)(w0 * ((v0.Color >> 24) & 0xFF) + w1 * ((v1.Color >> 24) & 0xFF) + w2 * ((v2.Color >> 24) & 0xFF));
                    color = (Math.Min(a, 255) << 24) | (Math.Min(r, 255) << 16) | (Math.Min(g, 255) << 8) | Math.Min(b, 255);
                }
                else
                {
                    color = v2.Color;
                }

                if (clearMode)
                {
                    if ((clearFlags & 1) != 0)
                        WritePixel(fb, fbBase, stride, fmt, x, y, color);
                    if ((clearFlags & 4) != 0)
                    {
                        float z = w0 * v0.Pos.Z + w1 * v1.Pos.Z + w2 * v2.Pos.Z;
                        int dbIdx = y * stride + x;
                        if (dbIdx >= 0 && dbIdx < _depthBuf.Length)
                            _depthBuf[dbIdx] = (ushort)Math.Clamp(z, 0, 65535);
                    }
                    continue;
                }

                // Depth
                float zn = w0 * v0.Pos.Z + w1 * v1.Pos.Z + w2 * v2.Pos.Z;
                ushort depth = (ushort)Math.Clamp(zn, 0, 65535);

                if (ge.DepthTestEnable)
                {
                    int dbIdx = y * stride + x;
                    if (dbIdx >= 0 && dbIdx < _depthBuf.Length)
                    {
                        if (!DepthTest(ge.DepthFunc, depth, _depthBuf[dbIdx])) continue;
                        if (ge.DepthWriteEnable) _depthBuf[dbIdx] = depth;
                    }
                }

                // Texture
                if (ge.TextureEnable && ge.TexWidth[0] > 0 && ge.TexHeight[0] > 0)
                {
                    float u = w0 * v0.Uv.X + w1 * v1.Uv.X + w2 * v2.Uv.X;
                    float v = w0 * v0.Uv.Y + w1 * v1.Uv.Y + w2 * v2.Uv.Y;
                    uint texel = SampleTexture(ge, u, v);
                    color = BlendTexture(color, texel, ge.TexFunction);
                }

                // Alpha test
                if (ge.AlphaTestEnable)
                {
                    int alpha = (int)((color >> 24) & 0xFF) & ge.AlphaMask;
                    if (!AlphaTest(ge.AlphaFunc, alpha, ge.AlphaRef & ge.AlphaMask)) continue;
                }

                // Alpha blend
                if (ge.AlphaBlendEnable)
                {
                    uint dst = ReadPixel(fb, fbBase, stride, fmt, x, y);
                    color = BlendPixels(color, dst, ge.BlendSrc, ge.BlendDst, ge.BlendOp, ge.BlendFixSrc, ge.BlendFixDst);
                }

                WritePixel(fb, fbBase, stride, fmt, x, y, color);
            }
        }
    }

    void DrawSprite(ref DecodedVertex v0, ref DecodedVertex v1, byte[] fb, uint fbBase, int stride, int fmt, PspGe ge)
    {
        bool clearMode = ge.ClearMode;
        int x0 = (int)v0.Pos.X, y0 = (int)v0.Pos.Y;
        int x1 = (int)v1.Pos.X, y1 = (int)v1.Pos.Y;
        if (x0 > x1) { (x0, x1) = (x1, x0); }
        if (y0 > y1) { (y0, y1) = (y1, y0); }

        x0 = Math.Max(x0, ge.ScissorX1); y0 = Math.Max(y0, ge.ScissorY1);
        x1 = Math.Min(x1, ge.ScissorX2); y1 = Math.Min(y1, ge.ScissorY2);

        if (clearMode)
        {
            int clearFlags = ge.ClearFlags;
            uint clearColor = v1.Color;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if ((clearFlags & 1) != 0)
                        WritePixel(fb, fbBase, stride, fmt, x, y, clearColor);
                    if ((clearFlags & 4) != 0)
                    {
                        int dbIdx = y * stride + x;
                        if (dbIdx >= 0 && dbIdx < _depthBuf.Length)
                            _depthBuf[dbIdx] = (ushort)Math.Clamp(v1.Pos.Z, 0, 65535);
                    }
                }
            }
            return;
        }

        float du = x1 > x0 ? (v1.Uv.X - v0.Uv.X) / (x1 - x0) : 0;
        float dv = y1 > y0 ? (v1.Uv.Y - v0.Uv.Y) / (y1 - y0) : 0;

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                uint color = v1.Color;

                if (ge.TextureEnable && ge.TexWidth[0] > 0)
                {
                    float u = v0.Uv.X + (x - x0) * du;
                    float v = v0.Uv.Y + (y - y0) * dv;
                    uint texel = SampleTexture(ge, u, v);
                    color = BlendTexture(color, texel, ge.TexFunction);
                }

                if (ge.AlphaBlendEnable)
                {
                    uint dst = ReadPixel(fb, fbBase, stride, fmt, x, y);
                    color = BlendPixels(color, dst, ge.BlendSrc, ge.BlendDst, ge.BlendOp, ge.BlendFixSrc, ge.BlendFixDst);
                }

                WritePixel(fb, fbBase, stride, fmt, x, y, color);
            }
        }
    }

    // ── Texture sampling ──

    uint SampleTexture(PspGe ge, float u, float v)
    {
        int tw = ge.TexWidth[0], th = ge.TexHeight[0];
        int tx = (int)(u * tw) % tw;
        int ty = (int)(v * th) % th;
        if (tx < 0) tx += tw;
        if (ty < 0) ty += th;

        uint texAddr = ge.TexBasePtr[0];
        int bufWidth = ge.TexBufWidth[0] > 0 ? ge.TexBufWidth[0] : tw;

        switch (ge.TexPixelFormat)
        {
            case 0: // 5650
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx) * 2;
                ushort c = ReadMem16(off);
                uint r = ((uint)(c & 0x1F) * 255 / 31);
                uint g = ((uint)((c >> 5) & 0x3F) * 255 / 63);
                uint b = ((uint)((c >> 11) & 0x1F) * 255 / 31);
                return 0xFF000000 | (r << 16) | (g << 8) | b;
            }
            case 1: // 5551
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx) * 2;
                ushort c = ReadMem16(off);
                uint r = ((uint)(c & 0x1F) * 255 / 31);
                uint g = ((uint)((c >> 5) & 0x1F) * 255 / 31);
                uint b = ((uint)((c >> 10) & 0x1F) * 255 / 31);
                uint a = (c & 0x8000) != 0 ? 255u : 0u;
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            case 2: // 4444
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx) * 2;
                ushort c = ReadMem16(off);
                uint r = ((uint)(c & 0xF) * 255 / 15);
                uint g = ((uint)((c >> 4) & 0xF) * 255 / 15);
                uint b = ((uint)((c >> 8) & 0xF) * 255 / 15);
                uint a = ((uint)((c >> 12) & 0xF) * 255 / 15);
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            case 3: // 8888
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx) * 4;
                return ReadMem32(off);
            }
            case 4: // CLUT4 (indexed)
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx) / 2;
                byte b = ReadMem8(off);
                int idx = ((tx & 1) == 0) ? (b & 0xF) : (b >> 4);
                return ReadClutEntry(ge, idx);
            }
            case 5: // CLUT8
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx);
                int idx = ReadMem8(off);
                return ReadClutEntry(ge, idx);
            }
            case 6: // CLUT16
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx) * 2;
                int idx = ReadMem16(off);
                return ReadClutEntry(ge, idx);
            }
            case 7: // CLUT32
            {
                uint off = texAddr + (uint)(ty * bufWidth + tx) * 4;
                int idx = (int)ReadMem32(off);
                return ReadClutEntry(ge, idx);
            }
            default:
                return 0xFFFF00FF; // magenta for unhandled
        }
    }

    uint ReadClutEntry(PspGe ge, int index)
    {
        index = ((index >> ge.ClutShift) & ge.ClutMask) | ge.ClutOffset;
        uint addr = ge.ClutBasePtr;

        switch (ge.ClutMode)
        {
            case 0: // 5650
            {
                ushort c = ReadMem16(addr + (uint)(index * 2));
                uint r = ((uint)(c & 0x1F) * 255 / 31);
                uint g = ((uint)((c >> 5) & 0x3F) * 255 / 63);
                uint b = ((uint)((c >> 11) & 0x1F) * 255 / 31);
                return 0xFF000000 | (r << 16) | (g << 8) | b;
            }
            case 1: // 5551
            {
                ushort c = ReadMem16(addr + (uint)(index * 2));
                uint r = ((uint)(c & 0x1F) * 255 / 31);
                uint g = ((uint)((c >> 5) & 0x1F) * 255 / 31);
                uint b = ((uint)((c >> 10) & 0x1F) * 255 / 31);
                uint a = (c & 0x8000) != 0 ? 255u : 0u;
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            case 2: // 4444
            {
                ushort c = ReadMem16(addr + (uint)(index * 2));
                uint r = ((uint)(c & 0xF) * 255 / 15);
                uint g = ((uint)((c >> 4) & 0xF) * 255 / 15);
                uint b = ((uint)((c >> 8) & 0xF) * 255 / 15);
                uint a = ((uint)((c >> 12) & 0xF) * 255 / 15);
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            case 3: // 8888
                return ReadMem32(addr + (uint)(index * 4));
            default:
                return 0xFFFF00FF;
        }
    }

    // ── Blend helpers ──

    static uint BlendTexture(uint vertColor, uint texel, int texFunc)
    {
        switch (texFunc)
        {
            case 0: // Modulate
            {
                uint tr = (texel >> 16) & 0xFF, tg = (texel >> 8) & 0xFF, tb = texel & 0xFF, ta = (texel >> 24) & 0xFF;
                uint vr = (vertColor >> 16) & 0xFF, vg = (vertColor >> 8) & 0xFF, vb = vertColor & 0xFF;
                uint r = tr * vr / 255, g = tg * vg / 255, b = tb * vb / 255;
                return (ta << 24) | (r << 16) | (g << 8) | b;
            }
            case 1: // Decal
                return texel;
            case 2: // Blend (uses texture env color, simplified)
                return texel;
            case 3: // Replace
                return texel;
            case 4: // Add
            {
                uint tr = (texel >> 16) & 0xFF, tg = (texel >> 8) & 0xFF, tb = texel & 0xFF;
                uint vr = (vertColor >> 16) & 0xFF, vg = (vertColor >> 8) & 0xFF, vb = vertColor & 0xFF;
                uint r = Math.Min(tr + vr, 255), g = Math.Min(tg + vg, 255), b = Math.Min(tb + vb, 255);
                uint a = (texel >> 24) & 0xFF;
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            default:
                return texel;
        }
    }

    static uint BlendPixels(uint src, uint dst, int srcFactor, int dstFactor, int blendOp,
        uint fixSrc, uint fixDst)
    {
        float sr = ((src >> 16) & 0xFF) / 255f, sg = ((src >> 8) & 0xFF) / 255f;
        float sb = (src & 0xFF) / 255f, sa = ((src >> 24) & 0xFF) / 255f;
        float dr = ((dst >> 16) & 0xFF) / 255f, dg = ((dst >> 8) & 0xFF) / 255f;
        float db = (dst & 0xFF) / 255f, da = ((dst >> 24) & 0xFF) / 255f;

        GetBlendFactors(srcFactor, sa, da, fixSrc, out float sfr, out float sfg, out float sfb, out float sfa);
        GetBlendFactors(dstFactor, sa, da, fixDst, out float dfr, out float dfg, out float dfb, out float dfa);

        float rr = sr * sfr + dr * dfr;
        float rg = sg * sfg + dg * dfg;
        float rb = sb * sfb + db * dfb;
        float ra = sa * sfa + da * dfa;

        return ((uint)Math.Clamp(ra * 255, 0, 255) << 24) |
               ((uint)Math.Clamp(rr * 255, 0, 255) << 16) |
               ((uint)Math.Clamp(rg * 255, 0, 255) << 8) |
               (uint)Math.Clamp(rb * 255, 0, 255);
    }

    static void GetBlendFactors(int factor, float srcAlpha, float dstAlpha, uint fix,
        out float r, out float g, out float b, out float a)
    {
        switch (factor)
        {
            case 0:  r = g = b = a = 0; break;       // ZERO
            case 1:  r = g = b = a = 1; break;       // ONE
            case 2:  r = g = b = a = srcAlpha; break; // SRC_ALPHA
            case 3:  r = g = b = a = 1 - srcAlpha; break; // INV_SRC_ALPHA
            case 4:  r = g = b = a = dstAlpha; break; // DST_ALPHA
            case 5:  r = g = b = a = 1 - dstAlpha; break; // INV_DST_ALPHA
            case 10: // FIX
                r = (fix & 0xFF) / 255f;
                g = ((fix >> 8) & 0xFF) / 255f;
                b = ((fix >> 16) & 0xFF) / 255f;
                a = 1f;
                break;
            default: r = g = b = a = 1; break;
        }
    }

    // ── Tests ──

    static bool DepthTest(int func, ushort src, ushort dst) => func switch
    {
        0 => false,      // NEVER
        1 => true,       // ALWAYS
        2 => src == dst, // EQUAL
        3 => src != dst, // NOTEQUAL
        4 => src < dst,  // LESS
        5 => src <= dst, // LEQUAL
        6 => src > dst,  // GREATER
        7 => src >= dst, // GEQUAL
        _ => true,
    };

    static bool AlphaTest(int func, int src, int reference) => func switch
    {
        0 => false,
        1 => true,
        2 => src == reference,
        3 => src != reference,
        4 => src < reference,
        5 => src <= reference,
        6 => src > reference,
        7 => src >= reference,
        _ => true,
    };

    // ── Pixel read/write ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void WritePixel(byte[] fb, uint fbBase, int stride, int fmt, int x, int y, uint color)
    {
        switch (fmt)
        {
            case 0: // 5650
            {
                uint off = fbBase + (uint)(y * stride + x) * 2;
                if (off + 1 >= fb.Length) return;
                uint r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
                ushort px = (ushort)(((r >> 3) & 0x1F) | (((g >> 2) & 0x3F) << 5) | (((b >> 3) & 0x1F) << 11));
                fb[off] = (byte)px;
                fb[off + 1] = (byte)(px >> 8);
                break;
            }
            case 1: // 5551
            {
                uint off = fbBase + (uint)(y * stride + x) * 2;
                if (off + 1 >= fb.Length) return;
                uint r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF, a = (color >> 24) & 0xFF;
                ushort px = (ushort)(((r >> 3) & 0x1F) | (((g >> 3) & 0x1F) << 5) | (((b >> 3) & 0x1F) << 10) | ((uint)(a >= 128 ? 1 : 0) << 15));
                fb[off] = (byte)px;
                fb[off + 1] = (byte)(px >> 8);
                break;
            }
            case 2: // 4444
            {
                uint off = fbBase + (uint)(y * stride + x) * 2;
                if (off + 1 >= fb.Length) return;
                uint r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF, a = (color >> 24) & 0xFF;
                ushort px = (ushort)(((r >> 4) & 0xF) | (((g >> 4) & 0xF) << 4) | (((b >> 4) & 0xF) << 8) | (((a >> 4) & 0xF) << 12));
                fb[off] = (byte)px;
                fb[off + 1] = (byte)(px >> 8);
                break;
            }
            case 3: // 8888
            default:
            {
                uint off = fbBase + (uint)(y * stride + x) * 4;
                if (off + 3 >= fb.Length) return;
                fb[off] = (byte)(color & 0xFF);         // R
                fb[off + 1] = (byte)((color >> 8) & 0xFF); // G
                fb[off + 2] = (byte)((color >> 16) & 0xFF); // B
                fb[off + 3] = (byte)((color >> 24) & 0xFF); // A
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint ReadPixel(byte[] fb, uint fbBase, int stride, int fmt, int x, int y)
    {
        switch (fmt)
        {
            case 3:
            {
                uint off = fbBase + (uint)(y * stride + x) * 4;
                if (off + 3 >= fb.Length) return 0;
                return fb[off] | ((uint)fb[off + 1] << 8) | ((uint)fb[off + 2] << 16) | ((uint)fb[off + 3] << 24);
            }
            default:
                return 0;
        }
    }

    // ── Memory helpers ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    byte ReadMem8(uint addr)
    {
        if (addr >= 0x0400_0000 && addr < 0x0400_0000 + PspBus.VramSize)
            return _bus.Vram[addr - 0x0400_0000];
        uint pa = PspBus.VirtToPhys(addr);
        if (pa < PspBus.RamSize) return _bus.Ram[pa];
        return _bus.Read8(addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ushort ReadMem16(uint addr)
    {
        if (addr >= 0x0400_0000 && addr < 0x0400_0000 + PspBus.VramSize - 1)
            return BinaryPrimitives.ReadUInt16LittleEndian(_bus.Vram.AsSpan((int)(addr - 0x0400_0000)));
        uint pa = PspBus.VirtToPhys(addr);
        if (pa < PspBus.RamSize - 1)
            return BinaryPrimitives.ReadUInt16LittleEndian(_bus.Ram.AsSpan((int)pa));
        return _bus.Read16(addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint ReadMem32(uint addr)
    {
        if (addr >= 0x0400_0000 && addr < 0x0400_0000 + PspBus.VramSize - 3)
            return BinaryPrimitives.ReadUInt32LittleEndian(_bus.Vram.AsSpan((int)(addr - 0x0400_0000)));
        uint pa = PspBus.VirtToPhys(addr);
        if (pa < PspBus.RamSize - 3)
            return BinaryPrimitives.ReadUInt32LittleEndian(_bus.Ram.AsSpan((int)pa));
        return _bus.Read32(addr);
    }

    float ReadFloat(uint addr) => BitConverter.UInt32BitsToSingle(ReadMem32(addr));

    // ── Math helpers ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float EdgeFunction(Vector3 a, Vector3 b, Vector3 c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static Matrix4x4 BuildMatrix4x4From3x4(float[] m)
    {
        if (m.Length < 12) return Matrix4x4.Identity;
        return new Matrix4x4(
            m[0], m[1], m[2], 0,
            m[3], m[4], m[5], 0,
            m[6], m[7], m[8], 0,
            m[9], m[10], m[11], 1);
    }

    static Matrix4x4 BuildMatrix4x4(float[] m)
    {
        if (m.Length < 16) return Matrix4x4.Identity;
        return new Matrix4x4(
            m[0], m[1], m[2], m[3],
            m[4], m[5], m[6], m[7],
            m[8], m[9], m[10], m[11],
            m[12], m[13], m[14], m[15]);
    }
}
