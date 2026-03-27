using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace N64Emu;

enum UcodeType { Fast3D, F3DEX, F3DEX2 }

sealed class RspHle
{
    public N64Bus? Bus;

    UcodeType _ucodeType;
    bool _ucodeDetected;

    readonly Matrix4x4[] _mtxStack = new Matrix4x4[32];
    int _mtxTop;
    Matrix4x4 _projection = Matrix4x4.Identity;
    Matrix4x4 _combined = Matrix4x4.Identity;

    readonly Vertex[] _vtxBuf = new Vertex[80];
    uint _geometryMode;

    float _vpScaleX = 160, _vpScaleY = 120, _vpTransX = 160, _vpTransY = 120;

    bool _texEnabled;
    int _texTile;
    float _texScaleS = 1, _texScaleT = 1;

    readonly uint[] _segments = new uint[16];

    uint SegmentAddr(uint addr)
    {
        int seg = (int)(addr >> 24) & 0x0F;
        return (_segments[seg] + (addr & 0x00FF_FFFF)) & 0x00FF_FFFF;
    }

    uint _colorImageAddr;
    uint _zImageAddr;
    int _fbWidth = 320;
    int _fbHeight = 240;

    readonly List<RenderTriangle> _triBuffer = new(4096);
    ushort[]? _zBuffer;

    public static bool DiagDump = true;
    int _diagVtxCount, _diagTriCount, _diagMtxCount, _diagCmdCount;
    const int MaxDiagVtx = 60;
    const int MaxDiagTri = 30;
    const int MaxDiagMtx = 10;
    const int MaxDiagCmd = 80;

    public struct Vertex
    {
        public float X, Y, Z, W;
        public float Sx, Sy, Sz;
        public float S, T;
        public byte R, G, B, A;
        public bool Clip;
    }

    public struct RenderTriangle
    {
        public Vertex V0, V1, V2;
        public int Tile;
    }

    public void Reset()
    {
        _mtxTop = 0;
        for (int i = 0; i < _mtxStack.Length; i++) _mtxStack[i] = Matrix4x4.Identity;
        _projection = Matrix4x4.Identity;
        _combined = Matrix4x4.Identity;
        _geometryMode = 0;
        _vpScaleX = 160; _vpScaleY = 120; _vpTransX = 160; _vpTransY = 120;
        _texEnabled = false; _texTile = 0; _texScaleS = 1; _texScaleT = 1;
        Array.Clear(_segments);
        _colorImageAddr = _zImageAddr = 0;
        _fbWidth = 320; _fbHeight = 240;
        _triBuffer.Clear();
        _zBuffer = null;
        _ucodeDetected = false;
    }

    public void ProcessTask()
    {
        if (Bus == null) return;
        uint taskType = Bus.Rsp.ReadDmem32(0xFC0);
        if (taskType == 1)
            ProcessGfxTask();
        else if (taskType == 2)
            ProcessAudioTask();
    }

    void ProcessGfxTask()
    {
        if (Bus == null) return;

        if (!_ucodeDetected)
        {
            DetectMicrocode();
            _ucodeDetected = true;
        }

        uint dlAddr = Bus.Rsp.ReadDmem32(0xFF0);
        dlAddr &= 0x00FF_FFFF;

        _mtxTop = 0;
        _mtxStack[0] = Matrix4x4.Identity;
        _projection = Matrix4x4.Identity;
        _triBuffer.Clear();
        Array.Clear(_segments);

        _diagVtxCount = 0;
        _diagTriCount = 0;
        _diagMtxCount = 0;
        _diagCmdCount = 0;

        if (DiagDump)
        {
            uint ucodeAddr = Bus.Rsp.ReadDmem32(0xFD0);
            uint ucodeDataAddr = Bus.Rsp.ReadDmem32(0xFD8);
            uint dataSize = Bus.Rsp.ReadDmem32(0xFF4);
            N64Machine.DiagWrite($"[RSP-HLE] === Frame GFX Task === ucode={_ucodeType} dlAddr=0x{dlAddr:X6} dataSize=0x{dataSize:X} ucodeAddr=0x{ucodeAddr:X8} ucodeDataAddr=0x{ucodeDataAddr:X8}");
            N64Machine.DiagWrite($"  First 8 DL bytes @ 0x{dlAddr:X6}: {ReadRdram32(dlAddr):X8} {ReadRdram32(dlAddr + 4):X8}");
        }

        ProcessDisplayList(dlAddr, 0);
        FlushTriangles();

        if (DiagDump)
            N64Machine.DiagWrite($"[RSP-HLE] Frame done: {_diagVtxCount} vertices, {_diagTriCount} triangles, {_diagMtxCount} matrices");
    }

    void DetectMicrocode()
    {
        if (Bus == null) { _ucodeType = UcodeType.F3DEX2; return; }

        uint ucodeDataAddr = Bus.Rsp.ReadDmem32(0xFD8) & 0x00FF_FFFF;
        var sb = new StringBuilder();
        string? foundText = null;

        for (int i = 0; i < 4096 && foundText == null; i++)
        {
            byte b = ReadRdram8(ucodeDataAddr + (uint)i);
            if (b >= 0x20 && b < 0x7F)
                sb.Append((char)b);
            else
            {
                if (sb.Length > 4) foundText = sb.ToString();
                sb.Clear();
            }
        }
        foundText ??= sb.ToString();

        if (foundText.Contains("F3DEX2") || foundText.Contains("F3DZEX") || foundText.Contains("F3DEX 2"))
            _ucodeType = UcodeType.F3DEX2;
        else if (foundText.Contains("F3DEX") && !foundText.Contains("F3DEX2"))
            _ucodeType = UcodeType.F3DEX;
        else if (foundText.Contains("Fast3D") || foundText.Contains("RSP SW Version"))
            _ucodeType = UcodeType.Fast3D;
        else
        {
            _ucodeType = DetectByOpcodes();
        }

        N64Machine.DiagWrite($"[RSP-HLE] Microcode detected: {_ucodeType} (text: \"{(foundText.Length > 80 ? foundText[..80] : foundText)}\")");
    }

    UcodeType DetectByOpcodes()
    {
        if (Bus == null) return UcodeType.F3DEX;

        uint dlAddr = Bus.Rsp.ReadDmem32(0xFF0) & 0x00FF_FFFF;
        int f3dexScore = 0, f3dex2Score = 0;

        for (int i = 0; i < 200; i++)
        {
            uint w0 = ReadRdram32(dlAddr + (uint)(i * 8));
            int op = (int)(w0 >> 24);

            // Opcodes unique to F3DEX
            if (op == 0xB6 || op == 0xB7 || op == 0xBB || op == 0xBC || op == 0xBD || op == 0xBF)
                f3dexScore++;
            if (op == 0xB9 || op == 0xBA) f3dexScore++;
            if (op == 0x03 || op == 0x04) f3dexScore++;

            // Opcodes unique to F3DEX2
            if (op == 0xD7 || op == 0xD8 || op == 0xD9 || op == 0xDA || op == 0xDB || op == 0xDC)
                f3dex2Score++;
            if (op == 0xDE || op == 0xDF) f3dex2Score++;
            if (op == 0xE2 || op == 0xE3) f3dex2Score++;
            if (op == 0x05 || op == 0x07) f3dex2Score++;

            // End of list sentinel
            if (op == 0xB8 || op == 0xDF) break;
        }

        N64Machine.DiagWrite($"[RSP-HLE] Opcode detection: F3DEX={f3dexScore} F3DEX2={f3dex2Score}");
        return f3dex2Score > f3dexScore ? UcodeType.F3DEX2 : UcodeType.F3DEX;
    }

    void ProcessDisplayList(uint addr, int depth)
    {
        if (Bus == null || depth > 18) return;

        for (int cmdCount = 0; cmdCount < 100000; cmdCount++)
        {
            uint w0 = ReadRdram32(addr);
            uint w1 = ReadRdram32(addr + 4);
            addr += 8;

            int op = (int)(w0 >> 24);

            if (DiagDump && _diagCmdCount < MaxDiagCmd)
            {
                _diagCmdCount++;
                N64Machine.DiagWrite($"  CMD[{cmdCount}] @0x{addr - 8:X6}: op=0x{op:X2} w0=0x{w0:X8} w1=0x{w1:X8}");
            }

            // RDP passthrough commands (0xE4+) are the same for all microcodes
            if (op >= 0xE4)
            {
                HandleRdpPassthrough(op, w0, w1, ref addr);
                continue;
            }

            if (_ucodeType == UcodeType.F3DEX2)
            {
                switch (op)
                {
                    case 0x00: break; // G_SPNOOP
                    case 0x01: CmdVtxF3DEX2(w0, w1); break;
                    case 0x02: break; // G_MODIFYVTX (stub)
                    case 0x03: break; // G_CULLDL (stub)
                    case 0x04: break; // G_BRANCH_Z (stub)
                    case 0x05: CmdTri1F3DEX2(w0); break;
                    case 0x06: CmdTri2F3DEX2(w0, w1); break;
                    case 0x07: break; // G_QUAD (stub)

                    case 0xD7: CmdTexture(w0, w1); break;
                    case 0xD8: // G_POPMTX
                        if (_mtxTop > 0) _mtxTop--;
                        UpdateCombined();
                        break;
                    case 0xD9: // G_GEOMETRYMODE (combined clear/set)
                        _geometryMode = (_geometryMode & ~(w0 & 0x00FF_FFFF)) | w1;
                        break;
                    case 0xDA: CmdMtxF3DEX2(w0, w1); break;
                    case 0xDB: CmdMoveWordF3DEX2(w0, w1); break;
                    case 0xDC: CmdMoveMemF3DEX2(w0, w1); break;
                    case 0xDE: // G_DL
                    {
                        uint sub = SegmentAddr(w1);
                        bool push = ((w0 >> 16) & 0xFF) == 0;
                        if (push)
                            ProcessDisplayList(sub, depth + 1);
                        else { addr = sub; continue; }
                        break;
                    }
                    case 0xDF: return; // G_ENDDL

                    case 0xE1: break; // G_RDPHALF_1
                    case 0xE2: // G_SETOTHERMODE_L (F3DEX2 encoding)
                    {
                        uint mask = BuildMaskF3DEX2(w0);
                        Bus.Rdp.OtherModeL = (Bus.Rdp.OtherModeL & ~mask) | (w1 & mask);
                        break;
                    }
                    case 0xE3: // G_SETOTHERMODE_H (F3DEX2 encoding)
                    {
                        uint mask = BuildMaskF3DEX2(w0);
                        Bus.Rdp.OtherModeH = (Bus.Rdp.OtherModeH & ~mask) | (w1 & mask);
                        break;
                    }
                }
            }
            else // F3DEX or Fast3D (same opcodes, different field packing for VTX/TRI)
            {
                switch (op)
                {
                    case 0x00: break; // G_SPNOOP
                    case 0x01: CmdMtxF3DEX(w0, w1); break;
                    case 0x03: CmdMoveMemF3DEX(w0, w1); break;
                    case 0x04:
                        if (_ucodeType == UcodeType.Fast3D)
                            CmdVtxFast3D(w0, w1);
                        else
                            CmdVtxF3DEX(w0, w1);
                        break;
                    case 0x06: // G_DL
                    {
                        uint sub = SegmentAddr(w1);
                        bool push = ((w0 >> 16) & 0xFF) == 0;
                        if (push)
                            ProcessDisplayList(sub, depth + 1);
                        else { addr = sub; continue; }
                        break;
                    }

                    case 0xB1: CmdTri2F3DEX_w1(w0, w1); break; // G_TRI2 (F3DEX)
                    case 0xB4: break; // G_RDPHALF_1
                    case 0xB6: _geometryMode &= ~w1; break; // G_CLEARGEOMETRYMODE
                    case 0xB7: _geometryMode |= w1; break;  // G_SETGEOMETRYMODE
                    case 0xB8: return; // G_ENDDL
                    case 0xB9:
                        Bus.Rdp.OtherModeL = (Bus.Rdp.OtherModeL & ~BuildMask(w0)) | (w1 & BuildMask(w0));
                        break;
                    case 0xBA:
                        Bus.Rdp.OtherModeH = (Bus.Rdp.OtherModeH & ~BuildMask(w0)) | (w1 & BuildMask(w0));
                        break;
                    case 0xBB: CmdTexture(w0, w1); break;
                    case 0xBC: CmdMoveWordF3DEX(w0, w1); break;
                    case 0xBD: // G_POPMTX
                        if (_mtxTop > 0) _mtxTop--;
                        UpdateCombined();
                        break;
                    case 0xBF:
                        if (_ucodeType == UcodeType.Fast3D)
                            CmdTri1Fast3D(w0, w1);
                        else
                            CmdTri1F3DEX(w1);
                        break;
                }
            }
        }
    }

    // ───────── F3DEX command handlers ─────────

    void CmdMtxF3DEX(uint w0, uint w1)
    {
        if (Bus == null) return;
        uint addr = SegmentAddr(w1);
        byte rawFlags = (byte)((w0 >> 16) & 0xFF);
        byte flags = (byte)(rawFlags ^ 1); // undo G_MTX_PUSH XOR applied at encoding time
        bool projection = (flags & 4) != 0;
        bool load = (flags & 2) != 0;
        bool push = (flags & 1) != 0;

        var mtx = ReadMatrix(addr);
        ApplyMatrix(mtx, projection, load, push);
    }

    void CmdVtxF3DEX(uint w0, uint w1)
    {
        if (Bus == null) return;
        // F3DEX G_VTX: bits 23-16 = v0*2 (8 bits), bits 15-10 = n (6 bits), bits 9-0 = sizeof(Vtx)*n-1
        int n = (int)((w0 >> 10) & 0x3F);
        int v0 = (int)(((w0 >> 16) & 0xFF) / 2);
        uint addr = SegmentAddr(w1);

        if (DiagDump && _diagVtxCount < MaxDiagVtx)
            N64Machine.DiagWrite($"  [G_VTX] w0=0x{w0:X8} w1=0x{w1:X8} n={n} v0={v0} addr=0x{addr:X6}");

        LoadVertices(addr, n, v0);
    }

    // ── Fast3D vertex/triangle handlers ──

    void CmdVtxFast3D(uint w0, uint w1)
    {
        if (Bus == null) return;
        int n = (int)(w0 & 0xFFFF) / 16; // byte length / sizeof(Vtx)
        int v0 = (int)((w0 >> 16) & 0xFF);
        uint addr = SegmentAddr(w1);

        if (DiagDump && _diagVtxCount < MaxDiagVtx)
            N64Machine.DiagWrite($"  [G_VTX Fast3D] w0=0x{w0:X8} w1=0x{w1:X8} n={n} v0={v0} addr=0x{addr:X6}");

        LoadVertices(addr, n, v0);
    }

    void CmdTri1Fast3D(uint w0, uint w1)
    {
        // Fast3D G_TRI1: indices in w1 byte-packed, multiplied by 10
        int i0 = (int)(((w1 >> 16) & 0xFF) / 10);
        int i1 = (int)(((w1 >> 8) & 0xFF) / 10);
        int i2 = (int)((w1 & 0xFF) / 10);
        AddTriangle(i0, i1, i2);
    }

    // ── F3DEX vertex/triangle handlers ──

    void CmdTri1F3DEX(uint w1)
    {
        int i0 = (int)((w1 >> 17) & 0x7F);
        int i1 = (int)((w1 >> 9) & 0x7F);
        int i2 = (int)((w1 >> 1) & 0x7F);
        AddTriangle(i0, i1, i2);
    }

    void CmdTri2F3DEX_w1(uint w0, uint w1)
    {
        int i0 = (int)((w0 >> 17) & 0x7F);
        int i1 = (int)((w0 >> 9) & 0x7F);
        int i2 = (int)((w0 >> 1) & 0x7F);
        AddTriangle(i0, i1, i2);

        int i3 = (int)((w1 >> 17) & 0x7F);
        int i4 = (int)((w1 >> 9) & 0x7F);
        int i5 = (int)((w1 >> 1) & 0x7F);
        AddTriangle(i3, i4, i5);
    }

    void CmdMoveMemF3DEX(uint w0, uint w1)
    {
        if (Bus == null) return;
        uint addr = SegmentAddr(w1);
        int type = (int)((w0 >> 16) & 0xFF);

        if (type == 0x80) // Viewport
            LoadViewport(addr);
    }

    void CmdMoveWordF3DEX(uint w0, uint w1)
    {
        int which = (int)(w0 & 0xFF);
        int offset = (int)((w0 >> 8) & 0xFFFF);

        if (which == 0x06) // G_MW_SEGMENT
        {
            int seg = offset / 4;
            if ((uint)seg < (uint)_segments.Length)
                _segments[seg] = w1 & 0x00FF_FFFF;
        }
    }

    // ───────── F3DEX2 command handlers ─────────

    void CmdMtxF3DEX2(uint w0, uint w1)
    {
        if (Bus == null) return;
        uint addr = SegmentAddr(w1);
        byte flags = (byte)(w0 & 0xFF);
        // F3DEX2 (after XOR with G_MTX_PUSH in GBI):
        // bit 0 = projection, bit 1 = load, bit 2 = ~push (inverted)
        bool projection = (flags & 1) != 0;
        bool load = (flags & 2) != 0;
        bool push = (flags & 4) == 0;

        var mtx = ReadMatrix(addr);
        ApplyMatrix(mtx, projection, load, push);
    }

    void CmdVtxF3DEX2(uint w0, uint w1)
    {
        if (Bus == null) return;
        // F3DEX2 G_VTX: bits 19-12 = n (8 bits), bits 7-1 = v0+n (7 bits)
        int n = (int)((w0 >> 12) & 0xFF);
        int vEnd = (int)((w0 >> 1) & 0x7F);
        int v0 = vEnd - n;
        if (v0 < 0) v0 = 0;
        uint addr = SegmentAddr(w1);

        LoadVertices(addr, n, v0);
    }

    void CmdTri1F3DEX2(uint w0)
    {
        // F3DEX2 G_TRI1: indices in w0, multiplied by 2
        int i0 = (int)((w0 >> 17) & 0x7F);
        int i1 = (int)((w0 >> 9) & 0x7F);
        int i2 = (int)((w0 >> 1) & 0x7F);
        AddTriangle(i0, i1, i2);
    }

    void CmdTri2F3DEX2(uint w0, uint w1)
    {
        int i0 = (int)((w0 >> 17) & 0x7F);
        int i1 = (int)((w0 >> 9) & 0x7F);
        int i2 = (int)((w0 >> 1) & 0x7F);
        AddTriangle(i0, i1, i2);

        int i3 = (int)((w1 >> 17) & 0x7F);
        int i4 = (int)((w1 >> 9) & 0x7F);
        int i5 = (int)((w1 >> 1) & 0x7F);
        AddTriangle(i3, i4, i5);
    }

    void CmdMoveMemF3DEX2(uint w0, uint w1)
    {
        if (Bus == null) return;
        uint addr = SegmentAddr(w1);
        int index = (int)((w0 >> 8) & 0xFF);

        if (index == 8) // G_MV_VIEWPORT
            LoadViewport(addr);
    }

    void CmdMoveWordF3DEX2(uint w0, uint w1)
    {
        int index = (int)((w0 >> 16) & 0xFF);
        int offset = (int)(w0 & 0xFFFF);

        if (index == 0x06) // G_MW_SEGMENT
        {
            int seg = offset / 4;
            if ((uint)seg < (uint)_segments.Length)
                _segments[seg] = w1 & 0x00FF_FFFF;
        }
    }

    void CmdGeometryModeF3DEX2(uint w0, uint w1)
    {
        _geometryMode = (_geometryMode & ~(w0 & 0x00FF_FFFF)) | w1;
    }

    // ───────── Shared command handlers ─────────

    void CmdTexture(uint w0, uint w1)
    {
        _texEnabled = ((w0 >> 1) & 1) != 0 || w1 != 0;
        _texTile = (int)((w0 >> 8) & 7);
        _texScaleS = (w1 >> 16) / 65536f;
        _texScaleT = (w1 & 0xFFFF) / 65536f;
        if (_texScaleS == 0) _texScaleS = 1;
        if (_texScaleT == 0) _texScaleT = 1;
    }

    void ApplyMatrix(Matrix4x4 mtx, bool projection, bool load, bool push)
    {
        if (DiagDump && _diagMtxCount < MaxDiagMtx)
        {
            _diagMtxCount++;
            N64Machine.DiagWrite($"[RSP-HLE] MTX {(projection ? "PROJ" : "MV")} {(load ? "LOAD" : "MUL")} {(push ? "PUSH" : "NOPUSH")}");
            DiagLogMatrix(ref mtx);
        }

        if (projection)
        {
            _projection = load ? mtx : mtx * _projection;
        }
        else
        {
            if (push && _mtxTop < _mtxStack.Length - 1)
            {
                _mtxStack[_mtxTop + 1] = _mtxStack[_mtxTop];
                _mtxTop++;
            }
            _mtxStack[_mtxTop] = load ? mtx : mtx * _mtxStack[_mtxTop];
        }
        UpdateCombined();
    }

    void LoadViewport(uint addr)
    {
        short vscaleX = (short)ReadRdram16(addr);
        short vscaleY = (short)ReadRdram16(addr + 2);
        short vtransX = (short)ReadRdram16(addr + 8);
        short vtransY = (short)ReadRdram16(addr + 10);

        _vpScaleX = vscaleX / 4.0f;
        _vpScaleY = vscaleY / 4.0f;
        _vpTransX = vtransX / 4.0f;
        _vpTransY = vtransY / 4.0f;

        if (DiagDump)
            N64Machine.DiagWrite($"[RSP-HLE] Viewport: scale=({_vpScaleX:F1},{_vpScaleY:F1}) trans=({_vpTransX:F1},{_vpTransY:F1})");
    }

    void LoadVertices(uint addr, int n, int v0)
    {
        if (Bus == null) return;

        for (int i = 0; i < n && v0 + i < _vtxBuf.Length; i++)
        {
            uint vAddr = addr + (uint)(i * 16);
            ref var vtx = ref _vtxBuf[v0 + i];

            short px = (short)ReadRdram16(vAddr);
            short py = (short)ReadRdram16(vAddr + 2);
            short pz = (short)ReadRdram16(vAddr + 4);
            short ts = (short)ReadRdram16(vAddr + 8);
            short tt = (short)ReadRdram16(vAddr + 10);
            vtx.R = ReadRdram8(vAddr + 12);
            vtx.G = ReadRdram8(vAddr + 13);
            vtx.B = ReadRdram8(vAddr + 14);
            vtx.A = ReadRdram8(vAddr + 15);

            float x = px, y = py, z = pz, w = 1;
            TransformVertex(ref x, ref y, ref z, ref w);

            vtx.X = x; vtx.Y = y; vtx.Z = z; vtx.W = w;

            if (MathF.Abs(w) < 0.001f)
            {
                vtx.Clip = true;
                vtx.Sx = vtx.Sy = vtx.Sz = 0;
            }
            else
            {
                float invW = 1.0f / w;
                vtx.Sx = x * invW * _vpScaleX + _vpTransX;
                vtx.Sy = y * invW * -_vpScaleY + _vpTransY;
                vtx.Sz = (z * invW + 1.0f) * 0.5f;
                vtx.Clip = false;
            }

            vtx.S = ts * _texScaleS / 32.0f;
            vtx.T = tt * _texScaleT / 32.0f;

            if (DiagDump && _diagVtxCount < MaxDiagVtx)
            {
                _diagVtxCount++;
                N64Machine.DiagWrite($"  vtx[{v0 + i}]: model=({px},{py},{pz}) clip=({x:F1},{y:F1},{z:F1},{w:F1}) screen=({vtx.Sx:F1},{vtx.Sy:F1}) z={vtx.Sz:F3} rgba=({vtx.R},{vtx.G},{vtx.B},{vtx.A}) clip={vtx.Clip}");
            }
        }
    }

    void AddTriangle(int i0, int i1, int i2)
    {
        if (i0 >= _vtxBuf.Length || i1 >= _vtxBuf.Length || i2 >= _vtxBuf.Length)
            return;
        if (i0 < 0 || i1 < 0 || i2 < 0)
            return;

        ref var v0 = ref _vtxBuf[i0];
        ref var v1 = ref _vtxBuf[i1];
        ref var v2 = ref _vtxBuf[i2];

        if (v0.Clip && v1.Clip && v2.Clip) return;

        // Backface culling
        float cross = (v1.Sx - v0.Sx) * (v2.Sy - v0.Sy) - (v1.Sy - v0.Sy) * (v2.Sx - v0.Sx);
        if ((_geometryMode & 0x2000) != 0 && cross < 0) return; // G_CULL_BACK
        if ((_geometryMode & 0x1000) != 0 && cross > 0) return; // G_CULL_FRONT

        _triBuffer.Add(new RenderTriangle
        {
            V0 = v0, V1 = v1, V2 = v2, Tile = _texTile
        });

        if (DiagDump && _diagTriCount < MaxDiagTri)
        {
            _diagTriCount++;
            N64Machine.DiagWrite($"  tri({i0},{i1},{i2}): screen=[({v0.Sx:F1},{v0.Sy:F1}),({v1.Sx:F1},{v1.Sy:F1}),({v2.Sx:F1},{v2.Sy:F1})] cross={cross:F1}");
        }
    }

    void HandleRdpPassthrough(int op, uint w0, uint w1, ref uint addr)
    {
        if (Bus == null) return;
        switch (op)
        {
            case 0xE4: // G_TEXRECT
            {
                uint w2 = ReadRdram32(addr); uint w3 = ReadRdram32(addr + 4);
                addr += 8;
                uint w4 = ReadRdram32(addr); uint w5 = ReadRdram32(addr + 4);
                addr += 8;
                break;
            }
            case 0xE5: addr += 16; break; // G_TEXRECTFLIP
            case 0xE6: break; // G_RDPLOADSYNC
            case 0xE7: break; // G_RDPPIPESYNC
            case 0xE8: break; // G_RDPTILESYNC
            case 0xE9: // G_RDPFULLSYNC
                Bus.Mi.SetInterrupt(N64Mi.MI_INTR_DP);
                break;
            case 0xED: // G_SETSCISSOR
                Bus.Rdp.ScissorXH = (w0 >> 12) & 0xFFF;
                Bus.Rdp.ScissorYH = w0 & 0xFFF;
                Bus.Rdp.ScissorXL = (w1 >> 12) & 0xFFF;
                Bus.Rdp.ScissorYL = w1 & 0xFFF;
                break;
            case 0xF0: // G_LOADTLUT
                Bus.Rdp.ProcessCommand(0x30, ((ulong)w0 << 32) | w1, 0);
                break;
            case 0xF2: // G_SETTILESIZE
                Bus.Rdp.ProcessCommand(0x32, ((ulong)w0 << 32) | w1, 0);
                break;
            case 0xF3: // G_LOADBLOCK
                Bus.Rdp.ProcessCommand(0x33, ((ulong)w0 << 32) | w1, 0);
                break;
            case 0xF4: // G_LOADTILE
                Bus.Rdp.ProcessCommand(0x34, ((ulong)w0 << 32) | w1, 0);
                break;
            case 0xF5: // G_SETTILE
                Bus.Rdp.ProcessCommand(0x35, ((ulong)w0 << 32) | w1, 0);
                break;
            case 0xF6: // G_FILLRECT
                FlushTriangles();
                Bus.Rdp.FillRect(w0, w1);
                break;
            case 0xF7: Bus.Rdp.FillColor = w1; break;
            case 0xF8: Bus.Rdp.FogColor = w1; break;
            case 0xF9: Bus.Rdp.BlendColor = w1; break;
            case 0xFA: Bus.Rdp.PrimColor = w1; break;
            case 0xFB: Bus.Rdp.EnvColor = w1; break;
            case 0xFC: Bus.Rdp.CombineMode = ((ulong)w0 << 32) | w1; break;
            case 0xFD: // G_SETTIMG
            {
                uint resolvedTimg = SegmentAddr(w1);
                Bus.Rdp.ProcessCommand(0x3D, ((ulong)w0 << 32) | resolvedTimg, 0);
                break;
            }
            case 0xFE: // G_SETZIMG
                _zImageAddr = SegmentAddr(w1);
                Bus.Rdp.ZImageAddr = _zImageAddr;
                break;
            case 0xFF: // G_SETCIMG
                FlushTriangles();
                _colorImageAddr = SegmentAddr(w1);
                _fbWidth = (int)((w0 & 0x3FF) + 1);
                Bus.Rdp.ColorImageAddr = _colorImageAddr;
                Bus.Rdp.ColorImageWidth = (uint)_fbWidth;
                Bus.Rdp.ColorImageSize = (w0 >> 19) & 3;
                Bus.Rdp.ColorImageFormat = (w0 >> 21) & 7;
                break;
        }
    }

    // ───────── Rendering internals ─────────

    void FlushTriangles()
    {
        if (Bus == null || _triBuffer.Count == 0) return;

        int fbWidth = _fbWidth;
        int fbHeight = (int)(_vpTransY * 2);
        if (fbHeight <= 0) fbHeight = 240;

        int zbufSize = fbWidth * fbHeight;
        if (_zBuffer == null || _zBuffer.Length < zbufSize)
            _zBuffer = new ushort[zbufSize];

        bool useZbuf = (_geometryMode & 0x20) != 0;
        bool useTexture = _texEnabled;

        var triSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_triBuffer);
        for (int i = 0; i < triSpan.Length; i++)
            RasterizeTriangle(ref triSpan[i], fbWidth, fbHeight, useZbuf, useTexture);

        _triBuffer.Clear();
    }

    void RasterizeTriangle(ref readonly RenderTriangle tri, int fbWidth, int fbHeight, bool useZbuf, bool useTexture)
    {
        if (Bus == null) return;

        float minX = MathF.Min(tri.V0.Sx, MathF.Min(tri.V1.Sx, tri.V2.Sx));
        float maxX = MathF.Max(tri.V0.Sx, MathF.Max(tri.V1.Sx, tri.V2.Sx));
        float minY = MathF.Min(tri.V0.Sy, MathF.Min(tri.V1.Sy, tri.V2.Sy));
        float maxY = MathF.Max(tri.V0.Sy, MathF.Max(tri.V1.Sy, tri.V2.Sy));

        int x0 = Math.Max(0, (int)MathF.Floor(minX));
        int x1 = Math.Min(fbWidth - 1, (int)MathF.Ceiling(maxX));
        int y0 = Math.Max(0, (int)MathF.Floor(minY));
        int y1 = Math.Min(fbHeight - 1, (int)MathF.Ceiling(maxY));

        int sxh = (int)(Bus.Rdp.ScissorXH >> 2);
        int syh = (int)(Bus.Rdp.ScissorYH >> 2);
        int sxl = (int)(Bus.Rdp.ScissorXL >> 2);
        int syl = (int)(Bus.Rdp.ScissorYL >> 2);
        x0 = Math.Max(x0, sxh);
        y0 = Math.Max(y0, syh);
        x1 = Math.Min(x1, sxl - 1);
        y1 = Math.Min(y1, syl - 1);

        float denom = (tri.V1.Sx - tri.V0.Sx) * (tri.V2.Sy - tri.V0.Sy) -
                      (tri.V2.Sx - tri.V0.Sx) * (tri.V1.Sy - tri.V0.Sy);
        if (MathF.Abs(denom) < 0.001f) return;
        float invDenom = 1.0f / denom;

        ref var rdpTile = ref Bus.Rdp.Tiles[tri.Tile];

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;

                float w1 = ((px - tri.V0.Sx) * (tri.V2.Sy - tri.V0.Sy) -
                            (tri.V2.Sx - tri.V0.Sx) * (py - tri.V0.Sy)) * invDenom;
                float w2 = ((tri.V1.Sx - tri.V0.Sx) * (py - tri.V0.Sy) -
                            (px - tri.V0.Sx) * (tri.V1.Sy - tri.V0.Sy)) * invDenom;
                float w0 = 1.0f - w1 - w2;

                if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f) continue;

                float z = tri.V0.Sz * w0 + tri.V1.Sz * w1 + tri.V2.Sz * w2;
                ushort zi = (ushort)Math.Clamp((int)(z * 65535), 0, 65535);

                if (useZbuf)
                {
                    int zIdx = y * fbWidth + x;
                    if ((uint)zIdx < (uint)_zBuffer!.Length)
                    {
                        if (zi >= _zBuffer[zIdx]) continue;
                        _zBuffer[zIdx] = zi;
                    }
                }

                float r = tri.V0.R * w0 + tri.V1.R * w1 + tri.V2.R * w2;
                float g = tri.V0.G * w0 + tri.V1.G * w1 + tri.V2.G * w2;
                float b = tri.V0.B * w0 + tri.V1.B * w1 + tri.V2.B * w2;
                float a = tri.V0.A * w0 + tri.V1.A * w1 + tri.V2.A * w2;

                uint shadeR = (uint)Math.Clamp((int)r, 0, 255);
                uint shadeG = (uint)Math.Clamp((int)g, 0, 255);
                uint shadeB = (uint)Math.Clamp((int)b, 0, 255);
                uint shadeA = (uint)Math.Clamp((int)a, 0, 255);

                uint finalR = shadeR, finalG = shadeG, finalB = shadeB, finalA = shadeA;

                if (useTexture)
                {
                    float s = tri.V0.S * w0 + tri.V1.S * w1 + tri.V2.S * w2;
                    float t = tri.V0.T * w0 + tri.V1.T * w1 + tri.V2.T * w2;
                    uint texel = Bus.Rdp.SampleTexel(ref rdpTile, (int)s, (int)t);

                    uint texR = (texel >> 24) & 0xFF;
                    uint texG = (texel >> 16) & 0xFF;
                    uint texB = (texel >> 8) & 0xFF;
                    uint texA = texel & 0xFF;

                    finalR = texR * shadeR / 255;
                    finalG = texG * shadeG / 255;
                    finalB = texB * shadeB / 255;
                    finalA = texA * shadeA / 255;
                }

                ushort pixel = (ushort)(((finalR / 8) << 11) | ((finalG / 8) << 6) |
                                         ((finalB / 8) << 1) | (finalA > 0 ? 1u : 0u));

                uint fbAddr = _colorImageAddr + (uint)(y * fbWidth + x) * 2;
                if (fbAddr + 1 < (uint)Bus.Rdram.Length)
                {
                    Bus.Rdram[fbAddr] = (byte)(pixel >> 8);
                    Bus.Rdram[fbAddr + 1] = (byte)(pixel);
                }
            }
        }
    }

    // ───────── Audio HLE ─────────

    readonly short[] _aBuf = new short[0x1000]; // 4K sample workspace (RSP DMEM equivalent)
    readonly uint[] _aSegments = new uint[16];
    short[] _adpcmBook = Array.Empty<short>();
    uint _adpcmLoopAddr;

    int _aInOfs, _aOutOfs, _aCount;
    int _aAuxA, _aAuxB, _aAuxC;

    short _volL, _volR;
    short _volTgtL, _volTgtR;
    int _volRateL, _volRateR;

    short _dryGain = 0x7FFF, _wetGain;

    enum AudioAbiVersion { Abi1, Abi2 }
    AudioAbiVersion _audioAbi;

    void ProcessAudioTask()
    {
        if (Bus == null) return;

        uint dataAddr = Bus.Rsp.ReadDmem32(0xFF0) & 0x00FF_FFFF;
        uint dataSize = Bus.Rsp.ReadDmem32(0xFF4);
        uint ucodeDataAddr = Bus.Rsp.ReadDmem32(0xFD8) & 0x00FF_FFFF;

        DetectAudioAbi(ucodeDataAddr);

        Array.Clear(_aBuf);
        Array.Clear(_aSegments);
        _aInOfs = _aOutOfs = _aCount = 0;

        int cmdCount = (int)(dataSize / 8);
        if (cmdCount <= 0) cmdCount = 256;

        for (int i = 0; i < cmdCount; i++)
        {
            uint w0 = ReadRdram32(dataAddr + (uint)(i * 8));
            uint w1 = ReadRdram32(dataAddr + (uint)(i * 8 + 4));
            int op = (int)(w0 >> 24);

            if (_audioAbi == AudioAbiVersion.Abi2)
                DispatchAudioAbi2(op, w0, w1);
            else
                DispatchAudioAbi1(op, w0, w1);
        }
    }

    void DetectAudioAbi(uint ucodeDataAddr)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 4096; i++)
        {
            byte b = ReadRdram8(ucodeDataAddr + (uint)i);
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else if (sb.Length > 4) break;
            else sb.Clear();
        }
        string text = sb.ToString();
        _audioAbi = text.Contains("ABI2") || text.Contains("S72") || text.Contains("Aud2")
            ? AudioAbiVersion.Abi2
            : AudioAbiVersion.Abi1;
    }

    uint AudioSegAddr(uint addr)
    {
        int seg = (int)(addr >> 24) & 0x0F;
        return (_aSegments[seg] + (addr & 0x00FF_FFFF)) & 0x00FF_FFFF;
    }

    void DispatchAudioAbi1(int op, uint w0, uint w1)
    {
        switch (op)
        {
            case 0x00: break; // SPNOOP
            case 0x01: ACmd_ADPCM(w0, w1); break;
            case 0x02: ACmd_CLEARBUFF(w0, w1); break;
            case 0x03: ACmd_ENVMIXER(w0, w1); break;
            case 0x04: ACmd_LOADBUFF(w1); break;
            case 0x05: ACmd_RESAMPLE(w0, w1); break;
            case 0x06: ACmd_SAVEBUFF(w1); break;
            case 0x07: ACmd_SEGMENT(w0, w1); break;
            case 0x08: ACmd_SETBUFF(w0, w1); break;
            case 0x09: ACmd_SETVOL(w0, w1); break;
            case 0x0A: ACmd_DMEMMOVE(w0, w1); break;
            case 0x0B: ACmd_LOADADPCM(w0, w1); break;
            case 0x0C: ACmd_MIXER(w0, w1); break;
            case 0x0D: ACmd_INTERLEAVE(w0, w1); break;
            case 0x0E: break; // POLEF stub
            case 0x0F: _adpcmLoopAddr = AudioSegAddr(w1); break; // SETLOOP
        }
    }

    void DispatchAudioAbi2(int op, uint w0, uint w1)
    {
        switch (op)
        {
            case 0x00: break; // SPNOOP
            case 0x01: ACmd_ADPCM(w0, w1); break;
            case 0x02: ACmd_CLEARBUFF(w0, w1); break;
            case 0x03: ACmd_MIXER(w0, w1); break;
            case 0x04: ACmd_LOADBUFF(w1); break;
            case 0x05: ACmd_RESAMPLE(w0, w1); break;
            case 0x06: ACmd_SAVEBUFF(w1); break;
            case 0x07: ACmd_SEGMENT(w0, w1); break;
            case 0x08: ACmd_SETBUFF(w0, w1); break;
            case 0x09: ACmd_SETVOL(w0, w1); break;
            case 0x0A: ACmd_DMEMMOVE(w0, w1); break;
            case 0x0B: ACmd_LOADADPCM(w0, w1); break;
            case 0x0C: ACmd_INTERLEAVE(w0, w1); break;
            case 0x0D: ACmd_ENVMIXER(w0, w1); break;
            case 0x10: break; // POLEF stub
            case 0x12: _adpcmLoopAddr = AudioSegAddr(w1); break; // SETLOOP
        }
    }

    void ACmd_CLEARBUFF(uint w0, uint w1)
    {
        int dOfs = (int)((w0 & 0xFFFF) >> 0);
        int count = (int)(w1 >> 16);
        for (int i = 0; i < count / 2 && (dOfs / 2 + i) < _aBuf.Length; i++)
            _aBuf[dOfs / 2 + i] = 0;
    }

    void ACmd_SETBUFF(uint w0, uint w1)
    {
        bool flagA = ((w0 >> 16) & 0xFF) != 0;
        if (flagA)
        {
            _aAuxA = (int)(w1 >> 16);
            _aAuxB = (int)(w1 & 0xFFFF);
            _aAuxC = (int)(w0 & 0xFFFF);
        }
        else
        {
            _aInOfs = (int)((w0 & 0xFFFF) >> 0);
            _aOutOfs = (int)(w1 >> 16);
            _aCount = (int)(w1 & 0xFFFF);
        }
    }

    void ACmd_SETVOL(uint w0, uint w1)
    {
        int flags = (int)((w0 >> 16) & 0xFF);
        if ((flags & 0x01) != 0) // volume target/rate
        {
            _volTgtL = (short)(int)(w0 & 0xFFFF);
            _volRateL = (int)w1;
        }
        else if ((flags & 0x02) != 0) // dry/wet gain
        {
            _dryGain = (short)(int)(w0 & 0xFFFF);
            _wetGain = (short)(int)(w1 >> 16);
        }
        else // volume left/right
        {
            _volL = (short)(int)(w0 & 0xFFFF);
            _volR = (short)(int)(w1 >> 16);
            _volTgtR = (short)(int)(w1 & 0xFFFF);
            _volRateR = 0;
        }
    }

    void ACmd_SEGMENT(uint w0, uint w1)
    {
        int seg = (int)((w0 >> 16) & 0xF);
        if (seg < _aSegments.Length)
            _aSegments[seg] = w1 & 0x00FF_FFFF;
    }

    void ACmd_LOADBUFF(uint w1)
    {
        if (Bus == null) return;
        uint rdramAddr = AudioSegAddr(w1) & 0x00FF_FFFF;
        int count = _aCount;
        int ofs = _aInOfs / 2;
        for (int i = 0; i < count / 2 && ofs + i < _aBuf.Length; i++)
        {
            uint a = rdramAddr + (uint)(i * 2);
            if (a + 1 < (uint)Bus.Rdram.Length)
                _aBuf[ofs + i] = (short)BinaryPrimitives.ReadInt16BigEndian(Bus.Rdram.AsSpan((int)a));
        }
    }

    void ACmd_SAVEBUFF(uint w1)
    {
        if (Bus == null) return;
        uint rdramAddr = AudioSegAddr(w1) & 0x00FF_FFFF;
        int count = _aCount;
        int ofs = _aOutOfs / 2;
        for (int i = 0; i < count / 2 && ofs + i < _aBuf.Length; i++)
        {
            uint a = rdramAddr + (uint)(i * 2);
            if (a + 1 < (uint)Bus.Rdram.Length)
                BinaryPrimitives.WriteInt16BigEndian(Bus.Rdram.AsSpan((int)a), _aBuf[ofs + i]);
        }
    }

    void ACmd_DMEMMOVE(uint w0, uint w1)
    {
        int src = (int)(w0 & 0xFFFF) / 2;
        int dst = (int)(w1 >> 16) / 2;
        int count = (int)(w1 & 0xFFFF) / 2;
        for (int i = 0; i < count; i++)
        {
            int si = src + i, di = dst + i;
            if ((uint)si < (uint)_aBuf.Length && (uint)di < (uint)_aBuf.Length)
                _aBuf[di] = _aBuf[si];
        }
    }

    void ACmd_LOADADPCM(uint w0, uint w1)
    {
        if (Bus == null) return;
        int count = (int)((w0 & 0xFFFF) >> 0);
        uint rdramAddr = AudioSegAddr(w1) & 0x00FF_FFFF;
        int nEntries = count / 2;
        _adpcmBook = new short[nEntries];
        for (int i = 0; i < nEntries; i++)
        {
            uint a = rdramAddr + (uint)(i * 2);
            if (a + 1 < (uint)Bus.Rdram.Length)
                _adpcmBook[i] = (short)BinaryPrimitives.ReadInt16BigEndian(Bus.Rdram.AsSpan((int)a));
        }
    }

    void ACmd_ADPCM(uint w0, uint w1)
    {
        if (Bus == null) return;
        bool flagLoop = ((w0 >> 16) & 0x02) != 0;
        bool flagInit = ((w0 >> 16) & 0x01) != 0;

        int inOfs = _aInOfs / 2;
        int outOfs = _aOutOfs / 2;
        int count = _aCount / 2;

        short prev1 = 0, prev2 = 0;
        if (flagLoop && _adpcmLoopAddr != 0)
        {
            uint la = _adpcmLoopAddr & 0x00FF_FFFF;
            if (la + 3 < (uint)Bus.Rdram.Length)
            {
                prev2 = (short)BinaryPrimitives.ReadInt16BigEndian(Bus.Rdram.AsSpan((int)la));
                prev1 = (short)BinaryPrimitives.ReadInt16BigEndian(Bus.Rdram.AsSpan((int)la + 2));
            }
        }
        else if (!flagInit && outOfs >= 2)
        {
            prev2 = (outOfs - 2 >= 0 && outOfs - 2 < _aBuf.Length) ? _aBuf[outOfs - 2] : (short)0;
            prev1 = (outOfs - 1 >= 0 && outOfs - 1 < _aBuf.Length) ? _aBuf[outOfs - 1] : (short)0;
        }

        int order = 2;
        int nPred = _adpcmBook.Length > 0 ? _adpcmBook.Length / 16 : 0;
        if (nPred == 0) nPred = 1;

        int srcIdx = inOfs;
        int dstIdx = outOfs;
        int samplesLeft = count;

        while (samplesLeft > 0)
        {
            if (srcIdx * 2 >= _aBuf.Length) break;

            int headerWord = (ushort)_aBuf[srcIdx++];
            int scale = 1 << ((headerWord >> 12) & 0xF);
            int predictor = (headerWord >> 8) & 0xF;
            if (predictor >= nPred) predictor = 0;

            int bookBase = predictor * 16;
            int frameSamples = Math.Min(16, samplesLeft);

            var decoded = new int[16];
            int nibbleIdx = 0;
            for (int i = 0; i < 8 && srcIdx < _aBuf.Length * 2; i++)
            {
                int rawByte;
                int sampleWordIdx = srcIdx / 2;
                if (sampleWordIdx >= _aBuf.Length) break;
                if ((srcIdx & 1) == 0)
                    rawByte = ((ushort)_aBuf[sampleWordIdx]) >> 8;
                else
                    rawByte = ((ushort)_aBuf[sampleWordIdx]) & 0xFF;
                srcIdx++;

                int hi = (rawByte >> 4) & 0xF;
                int lo = rawByte & 0xF;
                if (hi >= 8) hi -= 16;
                if (lo >= 8) lo -= 16;

                if (nibbleIdx < 16) decoded[nibbleIdx++] = hi * scale;
                if (nibbleIdx < 16) decoded[nibbleIdx++] = lo * scale;
            }

            for (int i = 0; i < frameSamples && dstIdx < _aBuf.Length; i++)
            {
                int predicted = 0;
                if (_adpcmBook.Length > bookBase + i + order)
                {
                    predicted += prev1 * _adpcmBook[bookBase + i];
                    predicted += prev2 * _adpcmBook[bookBase + i + order];
                }
                int sample = decoded[i] + (predicted >> 11);
                sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                _aBuf[dstIdx++] = (short)sample;
                prev2 = prev1;
                prev1 = (short)sample;
            }
            samplesLeft -= frameSamples;
        }
    }

    void ACmd_RESAMPLE(uint w0, uint w1)
    {
        if (Bus == null) return;
        bool flagInit = ((w0 >> 16) & 1) != 0;
        uint pitch = w0 & 0xFFFF;
        uint rdramState = AudioSegAddr(w1) & 0x00FF_FFFF;

        int inOfs = _aInOfs / 2;
        int outOfs = _aOutOfs / 2;
        int count = _aCount / 2;

        uint accumulator = 0;
        if (!flagInit && rdramState + 7 < (uint)Bus.Rdram.Length)
        {
            accumulator = (uint)BinaryPrimitives.ReadUInt32BigEndian(Bus.Rdram.AsSpan((int)rdramState + 4));
        }

        for (int i = 0; i < count && outOfs + i < _aBuf.Length; i++)
        {
            int srcPos = inOfs + (int)(accumulator >> 16);
            int frac = (int)((accumulator >> 6) & 0x3FF);

            short s0 = (srcPos >= 0 && srcPos < _aBuf.Length) ? _aBuf[srcPos] : (short)0;
            short s1 = (srcPos + 1 >= 0 && srcPos + 1 < _aBuf.Length) ? _aBuf[srcPos + 1] : s0;

            int interp = s0 + ((s1 - s0) * frac >> 10);
            _aBuf[outOfs + i] = (short)Math.Clamp(interp, short.MinValue, short.MaxValue);
            accumulator += pitch;
        }

        if (rdramState + 7 < (uint)Bus.Rdram.Length)
            BinaryPrimitives.WriteUInt32BigEndian(Bus.Rdram.AsSpan((int)rdramState + 4), accumulator);
    }

    void ACmd_ENVMIXER(uint w0, uint w1)
    {
        int inOfs = _aInOfs / 2;
        int outOfs = _aOutOfs / 2;
        int count = _aCount / 2;

        int vL = _volL, vR = _volR;
        int tL = _volTgtL, tR = _volTgtR;
        int rL = _volRateL, rR = _volRateR;
        int dry = _dryGain, wet = _wetGain;

        for (int i = 0; i < count; i++)
        {
            int sIdx = inOfs + i;
            short sample = (sIdx >= 0 && sIdx < _aBuf.Length) ? _aBuf[sIdx] : (short)0;

            int outL = sample * vL >> 15;
            int outR = sample * vR >> 15;

            int dryL = outL * dry >> 15;
            int dryR = outR * dry >> 15;

            int dIdx = outOfs + i;
            if (dIdx >= 0 && dIdx < _aBuf.Length)
                _aBuf[dIdx] = (short)Math.Clamp(_aBuf[dIdx] + dryL, short.MinValue, short.MaxValue);

            int auxIdx = _aAuxA / 2 + i;
            if (auxIdx >= 0 && auxIdx < _aBuf.Length)
                _aBuf[auxIdx] = (short)Math.Clamp(_aBuf[auxIdx] + dryR, short.MinValue, short.MaxValue);

            vL += (int)((long)(tL - vL) * rL >> 16);
            vR += (int)((long)(tR - vR) * rR >> 16);
        }
        _volL = (short)Math.Clamp(vL, short.MinValue, short.MaxValue);
        _volR = (short)Math.Clamp(vR, short.MinValue, short.MaxValue);
    }

    void ACmd_MIXER(uint w0, uint w1)
    {
        int gain = (short)(w0 & 0xFFFF);
        int srcOfs = (int)(w1 >> 16) / 2;
        int dstOfs = (int)(w1 & 0xFFFF) / 2;
        int count = _aCount / 2;

        for (int i = 0; i < count; i++)
        {
            int si = srcOfs + i, di = dstOfs + i;
            if ((uint)si < (uint)_aBuf.Length && (uint)di < (uint)_aBuf.Length)
            {
                int val = _aBuf[di] + (_aBuf[si] * gain >> 15);
                _aBuf[di] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
            }
        }
    }

    void ACmd_INTERLEAVE(uint w0, uint w1)
    {
        int outOfs = _aOutOfs / 2;
        int srcL = (int)(w1 >> 16) / 2;
        int srcR = (int)(w1 & 0xFFFF) / 2;
        int count = _aCount / 2;

        var temp = new short[count * 2];
        for (int i = 0; i < count; i++)
        {
            temp[i * 2] = (srcL + i >= 0 && srcL + i < _aBuf.Length) ? _aBuf[srcL + i] : (short)0;
            temp[i * 2 + 1] = (srcR + i >= 0 && srcR + i < _aBuf.Length) ? _aBuf[srcR + i] : (short)0;
        }
        for (int i = 0; i < temp.Length && outOfs + i < _aBuf.Length; i++)
            _aBuf[outOfs + i] = temp[i];
    }

    // ───────── Transform and matrix utilities ─────────

    void TransformVertex(ref float x, ref float y, ref float z, ref float w)
    {
        float ix = x, iy = y, iz = z;
        x = ix * _combined.M11 + iy * _combined.M21 + iz * _combined.M31 + _combined.M41;
        y = ix * _combined.M12 + iy * _combined.M22 + iz * _combined.M32 + _combined.M42;
        z = ix * _combined.M13 + iy * _combined.M23 + iz * _combined.M33 + _combined.M43;
        w = ix * _combined.M14 + iy * _combined.M24 + iz * _combined.M34 + _combined.M44;
    }

    void UpdateCombined()
    {
        _combined = _mtxStack[_mtxTop] * _projection;
    }

    Matrix4x4 ReadMatrix(uint addr)
    {
        if (DiagDump && _diagMtxCount < MaxDiagMtx)
        {
            var sb = new StringBuilder($"  RAW MTX @0x{addr:X6}:");
            for (int i = 0; i < 64; i += 4)
                sb.Append($" {ReadRdram32(addr + (uint)i):X8}");
            N64Machine.DiagWrite(sb.ToString());
        }

        var intParts = new short[16];
        var fracParts = new ushort[16];

        for (int i = 0; i < 16; i++)
            intParts[i] = (short)ReadRdram16(addr + (uint)(i * 2));
        for (int i = 0; i < 16; i++)
            fracParts[i] = ReadRdram16(addr + 32 + (uint)(i * 2));

        var m = new Matrix4x4();
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                int idx = row * 4 + col;
                float val = intParts[idx] + fracParts[idx] / 65536f;
                SetMatrixElement(ref m, row, col, val);
            }
        }
        return m;
    }

    static void SetMatrixElement(ref Matrix4x4 m, int r, int c, float v)
    {
        switch (r * 4 + c)
        {
            case 0: m.M11 = v; break; case 1: m.M12 = v; break;
            case 2: m.M13 = v; break; case 3: m.M14 = v; break;
            case 4: m.M21 = v; break; case 5: m.M22 = v; break;
            case 6: m.M23 = v; break; case 7: m.M24 = v; break;
            case 8: m.M31 = v; break; case 9: m.M32 = v; break;
            case 10: m.M33 = v; break; case 11: m.M34 = v; break;
            case 12: m.M41 = v; break; case 13: m.M42 = v; break;
            case 14: m.M43 = v; break; case 15: m.M44 = v; break;
        }
    }

    static uint BuildMask(uint w0)
    {
        int shift = (int)(w0 >> 8) & 0xFF;
        int len = (int)(w0 & 0xFF) + 1;
        return (uint)(((1L << len) - 1) << shift);
    }

    static uint BuildMaskF3DEX2(uint w0)
    {
        int lenM1 = (int)(w0 & 0xFF);
        int len = lenM1 + 1;
        int shift = 32 - ((int)((w0 >> 8) & 0xFF)) - len;
        if (shift < 0) shift = 0;
        return (uint)(((1L << len) - 1) << shift);
    }

    // ───────── Diagnostics ─────────

    void DiagLogMatrix(ref Matrix4x4 m)
    {
        N64Machine.DiagWrite($"    [{m.M11,9:F4} {m.M12,9:F4} {m.M13,9:F4} {m.M14,9:F4}]");
        N64Machine.DiagWrite($"    [{m.M21,9:F4} {m.M22,9:F4} {m.M23,9:F4} {m.M24,9:F4}]");
        N64Machine.DiagWrite($"    [{m.M31,9:F4} {m.M32,9:F4} {m.M33,9:F4} {m.M34,9:F4}]");
        N64Machine.DiagWrite($"    [{m.M41,9:F4} {m.M42,9:F4} {m.M43,9:F4} {m.M44,9:F4}]");
    }

    // ───────── Memory access ─────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint ReadRdram32(uint addr)
    {
        addr &= 0x00FF_FFFF;
        if (addr + 3 < (uint)Bus!.Rdram.Length)
            return BinaryPrimitives.ReadUInt32BigEndian(Bus.Rdram.AsSpan((int)addr));
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ushort ReadRdram16(uint addr)
    {
        addr &= 0x00FF_FFFF;
        if (addr + 1 < (uint)Bus!.Rdram.Length)
            return BinaryPrimitives.ReadUInt16BigEndian(Bus.Rdram.AsSpan((int)addr));
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    byte ReadRdram8(uint addr)
    {
        addr &= 0x00FF_FFFF;
        return addr < (uint)Bus!.Rdram.Length ? Bus.Rdram[addr] : (byte)0;
    }
}
