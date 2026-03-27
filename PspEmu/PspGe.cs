using System.Numerics;
using System.Runtime.CompilerServices;

namespace PspEmu;

/// <summary>
/// PSP Graphics Engine: processes display lists containing GE commands.
/// Commands are 32-bit: [cmd:8][data:24]. Delegates rendering to GeRenderer.
/// </summary>
sealed class PspGe
{
    // GE command IDs
    const int CMD_NOP       = 0x00;
    const int CMD_VADDR     = 0x01;
    const int CMD_IADDR     = 0x02;
    const int CMD_PRIM      = 0x04;
    const int CMD_BEZIER    = 0x05;
    const int CMD_SPLINE    = 0x06;
    const int CMD_JUMP      = 0x08;
    const int CMD_CALL      = 0x0A;
    const int CMD_RET       = 0x0B;
    const int CMD_END       = 0x0C;
    const int CMD_SIGNAL    = 0x0E;
    const int CMD_FINISH    = 0x0F;
    const int CMD_BASE      = 0x10;
    const int CMD_VTYPE     = 0x12;
    const int CMD_OFFSET    = 0x13;
    const int CMD_ORIGIN    = 0x14;
    const int CMD_REGION1   = 0x15;
    const int CMD_REGION2   = 0x16;
    const int CMD_LTE       = 0x17;  // lighting enable
    const int CMD_LTE0      = 0x18;  // light 0 enable
    const int CMD_LTE1      = 0x19;
    const int CMD_LTE2      = 0x1A;
    const int CMD_LTE3      = 0x1B;
    const int CMD_CLIP_EN   = 0x1C;  // clip enable
    const int CMD_BCE       = 0x1D;  // backface cull enable
    const int CMD_TME       = 0x1E;  // texture mapping enable
    const int CMD_FGE       = 0x1F;  // fog enable
    const int CMD_DTE       = 0x20;  // dither enable
    const int CMD_ABE       = 0x21;  // alpha blend enable
    const int CMD_ATE       = 0x22;  // alpha test enable
    const int CMD_ZTE       = 0x23;  // depth test enable
    const int CMD_STE       = 0x24;  // stencil test enable
    const int CMD_AAE       = 0x25;  // antialias enable
    const int CMD_PCE       = 0x26;  // patch cull enable
    const int CMD_CTE       = 0x27;  // color test enable
    const int CMD_LOE       = 0x28;  // logic op enable
    const int CMD_BOFS      = 0x2A;  // bone matrix offset
    const int CMD_BONE      = 0x2B;  // bone matrix data
    const int CMD_MW0       = 0x2C;  // morph weight 0
    const int CMD_PSUB      = 0x34;  // patch subdivision
    const int CMD_PPRIM     = 0x35;  // patch primitive
    const int CMD_PFACE     = 0x36;  // patch facing
    const int CMD_WORLD     = 0x3A;  // world matrix
    const int CMD_VIEW      = 0x3C;  // view matrix
    const int CMD_PROJ      = 0x3E;  // projection matrix
    const int CMD_TGEN      = 0x40;  // texgen matrix
    const int CMD_SX        = 0x42;  // viewport X scale
    const int CMD_SY        = 0x43;  // viewport Y scale
    const int CMD_SZ        = 0x44;  // viewport Z scale
    const int CMD_TX        = 0x45;  // viewport X translate
    const int CMD_TY        = 0x46;  // viewport Y translate
    const int CMD_TZ        = 0x47;  // viewport Z translate
    const int CMD_SU        = 0x48;  // texture scale U
    const int CMD_SV        = 0x49;  // texture scale V
    const int CMD_TU        = 0x4A;  // texture offset U
    const int CMD_TV        = 0x4B;  // texture offset V
    const int CMD_OFFSETX   = 0x4C;  // screen offset X
    const int CMD_OFFSETY   = 0x4D;  // screen offset Y
    const int CMD_SHADE     = 0x50;  // shade model
    const int CMD_RNORM     = 0x51;  // reverse normals
    const int CMD_CMAT      = 0x53;  // color material
    const int CMD_EMC       = 0x54;  // emissive color
    const int CMD_AMC       = 0x55;  // ambient color (material)
    const int CMD_DMC       = 0x56;  // diffuse color (material)
    const int CMD_SMC       = 0x57;  // specular color (material)
    const int CMD_AMA       = 0x58;  // ambient alpha
    const int CMD_SPOW      = 0x5B;  // specular power
    const int CMD_ALC       = 0x5C;  // ambient light color
    const int CMD_ALA       = 0x5D;  // ambient light alpha
    const int CMD_LMODE     = 0x5E;  // light mode
    const int CMD_LT0       = 0x5F;  // light 0 type
    const int CMD_TBP0      = 0xA0;  // texture base pointer 0
    const int CMD_TBW0      = 0xA8;  // texture buffer width 0
    const int CMD_CBP       = 0xB0;  // CLUT base pointer
    const int CMD_CBW       = 0xB1;  // CLUT buffer width
    const int CMD_TRXSBP    = 0xB2;  // transfer src base
    const int CMD_TRXSBW    = 0xB3;  // transfer src width
    const int CMD_TRXDBP    = 0xB4;  // transfer dst base
    const int CMD_TRXDBW    = 0xB5;  // transfer dst width
    const int CMD_TSIZE0    = 0xB8;  // texture size 0
    const int CMD_TMAP      = 0xC0;  // texture map mode
    const int CMD_TSHADE    = 0xC1;  // texture shade
    const int CMD_TMODE     = 0xC2;  // texture mode
    const int CMD_TPF       = 0xC3;  // texture pixel format
    const int CMD_CLOAD     = 0xC4;  // CLUT load
    const int CMD_CMODE     = 0xC5;  // CLUT mode
    const int CMD_TFLT      = 0xC6;  // texture filter
    const int CMD_TWRAP     = 0xC7;  // texture wrap
    const int CMD_TLEVEL    = 0xC8;  // texture level/bias
    const int CMD_TFUNC     = 0xC9;  // texture function
    const int CMD_TEC       = 0xCA;  // texture env color
    const int CMD_TFLUSH    = 0xCB;  // texture flush
    const int CMD_TSYNC     = 0xCC;  // texture sync
    const int CMD_FFX       = 0xCD;  // fog range far
    const int CMD_FFY       = 0xCE;  // fog range near (actually end)
    const int CMD_FC        = 0xCF;  // fog color
    const int CMD_TSLOPE    = 0xD0;  // texture slope
    const int CMD_FPF       = 0xD2;  // frame pixel format
    const int CMD_CLEAR     = 0xD3;  // clear mode
    const int CMD_SCISSOR1  = 0xD4;  // scissor min
    const int CMD_SCISSOR2  = 0xD5;  // scissor max
    const int CMD_MINZ      = 0xD6;  // min Z
    const int CMD_MAXZ      = 0xD7;  // max Z
    const int CMD_CTEST     = 0xD8;  // color test
    const int CMD_CREF      = 0xD9;  // color test ref
    const int CMD_CMSK      = 0xDA;  // color test mask
    const int CMD_ATST      = 0xDB;  // alpha test
    const int CMD_STST      = 0xDC;  // stencil test
    const int CMD_SOP       = 0xDD;  // stencil op
    const int CMD_ZTST      = 0xDE;  // depth test function
    const int CMD_ALPHA     = 0xDF;  // alpha blend
    const int CMD_SFIX      = 0xE0;  // blend src fix
    const int CMD_DFIX      = 0xE1;  // blend dst fix
    const int CMD_DTH0      = 0xE2;  // dither matrix 0
    const int CMD_LOP       = 0xE6;  // logic op
    const int CMD_ZMSK      = 0xE7;  // depth mask
    const int CMD_PMSK1     = 0xE8;  // pixel mask 1
    const int CMD_PMSK2     = 0xE9;  // pixel mask 2
    const int CMD_TRXPOS    = 0xEA;  // transfer position
    const int CMD_TRXDPOS   = 0xEB;  // transfer dest position
    const int CMD_TRXSIZE   = 0xEC;  // transfer size
    const int CMD_TRXKICK   = 0xED;  // transfer kick
    const int CMD_TRXSPOS   = 0xEE;
    const int CMD_FBP       = 0x9C;  // framebuffer pointer
    const int CMD_FBW       = 0x9D;  // framebuffer width
    const int CMD_ZBP       = 0x9E;  // zbuffer pointer
    const int CMD_ZBW       = 0x9F;  // zbuffer width

    // Display list queue
    sealed class DisplayList
    {
        public int Id;
        public uint StartAddr;
        public uint StallAddr;
        public uint CurrentAddr;
        public bool Done;
    }

    readonly List<DisplayList> _lists = new();
    int _nextListId;
    public bool Busy { get; private set; }

    // GE state (registers)
    public readonly uint[] CmdRegs = new uint[256];

    // Matrix state
    public readonly float[] WorldMatrix = new float[16];
    public readonly float[] ViewMatrix = new float[16];
    public readonly float[] ProjMatrix = new float[16];
    public readonly float[] TgenMatrix = new float[16];
    public readonly float[][] BoneMatrices = new float[8][];
    int _worldIdx, _viewIdx, _projIdx, _tgenIdx, _boneIdx, _boneMatNum;

    // Viewport
    public float ViewportScaleX, ViewportScaleY, ViewportScaleZ;
    public float ViewportTransX, ViewportTransY, ViewportTransZ;
    public float OffsetX, OffsetY;
    public float TexScaleU = 1f, TexScaleV = 1f;
    public float TexOffsetU, TexOffsetV;

    // Vertex state
    public uint VertexAddr;
    public uint IndexAddr;
    public uint BaseAddr;

    // Render state
    public bool TextureEnable;
    public bool LightingEnable;
    public bool AlphaBlendEnable;
    public bool AlphaTestEnable;
    public bool DepthTestEnable;
    public bool CullEnable;
    public bool FogEnable;
    public int ShadeModel; // 0 = flat, 1 = Gouraud
    public int CullFace; // 0 = CW, 1 = CCW

    // Texture state
    public uint[] TexBasePtr = new uint[8];
    public int[] TexBufWidth = new int[8];
    public int[] TexWidth = new int[8];
    public int[] TexHeight = new int[8];
    public int TexPixelFormat;
    public int TexMapMode;
    public int TexFunction;
    public int TexFilter;
    public int TexWrapU, TexWrapV;

    // CLUT
    public uint ClutBasePtr;
    public int ClutMode;
    public int ClutShift, ClutMask, ClutOffset;

    // Framebuffer
    public uint FbPtr;
    public int FbWidth;
    public int FbPixelFormat;
    public uint ZbPtr;
    public int ZbWidth;

    // Blend
    public int BlendSrc, BlendDst, BlendOp;
    public uint BlendFixSrc, BlendFixDst;

    // Alpha test
    public int AlphaFunc, AlphaRef, AlphaMask;

    // Depth test
    public int DepthFunc;
    public bool DepthWriteEnable = true;

    // Scissor
    public int ScissorX1, ScissorY1, ScissorX2, ScissorY2;

    // Clear
    public bool ClearMode;
    public int ClearFlags;

    // Transfer
    uint _trxSrcBase, _trxSrcWidth, _trxDstBase, _trxDstWidth;
    int _trxSrcX, _trxSrcY, _trxDstX, _trxDstY, _trxWidth, _trxHeight;

    // Colors
    public uint AmbientColor = 0xFFFFFFFF;
    public uint MaterialEmissive, MaterialAmbient = 0xFFFFFFFF, MaterialDiffuse = 0xFFFFFFFF;
    public uint MaterialSpecular;

    // Call stack
    readonly Stack<uint> _callStack = new();

    // Dependencies
    readonly PspBus _bus;
    public GeRenderer Renderer { get; set; } = null!;

    public PspGe(PspBus bus)
    {
        _bus = bus;
        for (int i = 0; i < 8; i++) BoneMatrices[i] = new float[12];
        for (int i = 0; i < 8; i++) { TexWidth[i] = 1; TexHeight[i] = 1; TexBufWidth[i] = 0; }
        Matrix4x4.Identity.CopyTo(WorldMatrix);
        Matrix4x4.Identity.CopyTo(ViewMatrix);
        Matrix4x4.Identity.CopyTo(ProjMatrix);
        ScissorX2 = 480; ScissorY2 = 272;
    }

    // ── HW register interface ──

    public uint ReadReg(uint pa)
    {
        uint off = (pa - 0x1D40_0000) >> 2;
        if (off < 256) return CmdRegs[off];
        return 0;
    }

    public void WriteReg(uint pa, uint val)
    {
        uint off = (pa - 0x1D40_0000) >> 2;
        if (off < 256) CmdRegs[off] = val;
    }

    // ── Display list management ──

    public int EnqueueList(uint listAddr, uint stallAddr)
    {
        var dl = new DisplayList
        {
            Id = _nextListId++,
            StartAddr = listAddr,
            StallAddr = stallAddr,
            CurrentAddr = listAddr,
        };
        _lists.Add(dl);
        Log.Write(LogCat.GE, $"EnqueueList id={dl.Id} addr={listAddr:X8} stall={stallAddr:X8}");
        return dl.Id;
    }

    public void UpdateStallAddr(int listId, uint stallAddr)
    {
        var dl = _lists.Find(l => l.Id == listId);
        if (dl != null)
        {
            dl.StallAddr = stallAddr;
        }
    }

    public int ListSync(int listId, int syncMode)
    {
        var dl = _lists.Find(l => l.Id == listId);
        if (dl == null || dl.Done) return 0; // done
        return 1; // drawing
    }

    public int DrawSync(int syncMode)
    {
        // Process all pending display lists
        ProcessAllLists();
        return 0;
    }

    public void Continue()
    {
        ProcessAllLists();
    }

    public uint GetCmd(int cmd)
    {
        if (cmd >= 0 && cmd < CmdRegs.Length)
            return CmdRegs[cmd];
        return 0;
    }

    public void ProcessAllLists()
    {
        Busy = true;
        for (int i = 0; i < _lists.Count; i++)
        {
            var dl = _lists[i];
            if (!dl.Done)
                ProcessList(dl);
        }
        _lists.RemoveAll(l => l.Done);
        Busy = false;
    }

    void ProcessList(DisplayList dl)
    {
        int maxCmds = 1_000_000; // safety limit
        for (int i = 0; i < maxCmds; i++)
        {
            if (dl.StallAddr != 0 && dl.CurrentAddr >= dl.StallAddr)
                break;

            uint cmd = _bus.Read32(dl.CurrentAddr);
            dl.CurrentAddr += 4;

            int op = (int)(cmd >> 24);
            uint data = cmd & 0x00FFFFFF;

            if (ExecuteCommand(op, data, cmd, dl))
            {
                dl.Done = true;
                break;
            }
        }
        
    }

    /// <summary>Execute a single GE command. Returns true if list should end.</summary>
    bool ExecuteCommand(int op, uint data, uint fullCmd, DisplayList dl)
    {
        CmdRegs[op] = data;

        switch (op)
        {
            case CMD_NOP: break;
            case CMD_VADDR:
                VertexAddr = (BaseAddr | data);
                break;
            case CMD_IADDR:
                IndexAddr = (BaseAddr | data);
                break;
            case CMD_BASE:
                BaseAddr = (data << 8) & 0xFF000000;
                break;
            case CMD_ORIGIN:
                BaseAddr = dl.CurrentAddr - 4;
                break;
            case CMD_OFFSET:
                BaseAddr = data << 8;
                break;

            // ── Drawing ──
            case CMD_PRIM:
                DrawPrimitive(data);
                break;

            // ── Control flow ──
            case CMD_JUMP:
            {
                uint target = (BaseAddr | data) & ~3u;
                dl.CurrentAddr = target;
                break;
            }
            case CMD_CALL:
            {
                _callStack.Push(dl.CurrentAddr);
                dl.CurrentAddr = (BaseAddr | data) & ~3u;
                break;
            }
            case CMD_RET:
                if (_callStack.Count > 0)
                    dl.CurrentAddr = _callStack.Pop();
                break;
            case CMD_END: return true;
            case CMD_FINISH: return true;
            case CMD_SIGNAL: break;

            // ── Vertex type ──
            case CMD_VTYPE: break; // stored in CmdRegs

            // ── Viewport ──
            case CMD_SX: ViewportScaleX = CmdToFloat(data); break;
            case CMD_SY: ViewportScaleY = CmdToFloat(data); break;
            case CMD_SZ: ViewportScaleZ = CmdToFloat(data); break;
            case CMD_TX: ViewportTransX = CmdToFloat(data); break;
            case CMD_TY: ViewportTransY = CmdToFloat(data); break;
            case CMD_TZ: ViewportTransZ = CmdToFloat(data); break;
            case CMD_OFFSETX: OffsetX = (data & 0xFFFF) / 16f; break;
            case CMD_OFFSETY: OffsetY = (data & 0xFFFF) / 16f; break;
            case CMD_SU: TexScaleU = CmdToFloat(data); break;
            case CMD_SV: TexScaleV = CmdToFloat(data); break;
            case CMD_TU: TexOffsetU = CmdToFloat(data); break;
            case CMD_TV: TexOffsetV = CmdToFloat(data); break;

            // ── Matrices ──
            case CMD_WORLD: _worldIdx = 0; break;
            case CMD_WORLD + 1:
                if (_worldIdx < 12) WorldMatrix[_worldIdx++] = CmdToFloat(data);
                break;
            case CMD_VIEW: _viewIdx = 0; break;
            case CMD_VIEW + 1:
                if (_viewIdx < 12) ViewMatrix[_viewIdx++] = CmdToFloat(data);
                break;
            case CMD_PROJ: _projIdx = 0; break;
            case CMD_PROJ + 1:
                if (_projIdx < 16) ProjMatrix[_projIdx++] = CmdToFloat(data);
                break;
            case CMD_TGEN: _tgenIdx = 0; break;
            case CMD_TGEN + 1:
                if (_tgenIdx < 12) TgenMatrix[_tgenIdx++] = CmdToFloat(data);
                break;
            case CMD_BOFS: _boneMatNum = (int)(data / 12); _boneIdx = 0; break;
            case CMD_BONE:
                if (_boneMatNum < 8 && _boneIdx < 12)
                    BoneMatrices[_boneMatNum][_boneIdx++] = CmdToFloat(data);
                break;

            // ── Render state ──
            case CMD_TME: TextureEnable = data != 0; break;
            case CMD_LTE: LightingEnable = data != 0; break;
            case CMD_ABE: AlphaBlendEnable = data != 0; break;
            case CMD_ATE: AlphaTestEnable = data != 0; break;
            case CMD_ZTE: DepthTestEnable = data != 0; break;
            case CMD_BCE: CullEnable = data != 0; break;
            case CMD_FGE: FogEnable = data != 0; break;
            case CMD_SHADE: ShadeModel = (int)(data & 1); break;
            case CMD_PFACE: CullFace = (int)(data & 1); break;
            case CMD_CLEAR:
                ClearMode = (data & 1) != 0;
                ClearFlags = (int)((data >> 8) & 0xFF);
                if (ClearMode) Renderer?.ClearBuffers(ClearFlags);
                break;

            // ── Framebuffer ──
            case CMD_FBP:
                FbPtr = (FbPtr & 0xFF000000) | data;
                break;
            case CMD_FBW:
                FbPtr = (FbPtr & 0x00FFFFFF) | ((data & 0xFF0000) << 8);
                FbWidth = (int)(data & 0xFFFF);
                break;
            case CMD_ZBP:
                ZbPtr = (ZbPtr & 0xFF000000) | data;
                break;
            case CMD_ZBW:
                ZbPtr = (ZbPtr & 0x00FFFFFF) | ((data & 0xFF0000) << 8);
                ZbWidth = (int)(data & 0xFFFF);
                break;
            case CMD_FPF: FbPixelFormat = (int)(data & 3); break;

            // ── Scissor ──
            case CMD_SCISSOR1:
                ScissorX1 = (int)(data & 0x3FF);
                ScissorY1 = (int)((data >> 10) & 0x3FF);
                break;
            case CMD_SCISSOR2:
                ScissorX2 = (int)(data & 0x3FF) + 1;
                ScissorY2 = (int)((data >> 10) & 0x3FF) + 1;
                break;

            // ── Textures ──
            case >= CMD_TBP0 and < CMD_TBP0 + 8:
                TexBasePtr[op - CMD_TBP0] = (TexBasePtr[op - CMD_TBP0] & 0xFF000000) | data;
                break;
            case >= CMD_TBW0 and < CMD_TBW0 + 8:
            {
                int level = op - CMD_TBW0;
                TexBufWidth[level] = (int)(data & 0xFFFF);
                TexBasePtr[level] = (TexBasePtr[level] & 0x00FFFFFF) | ((data & 0xFF0000) << 8);
                break;
            }
            case >= CMD_TSIZE0 and < CMD_TSIZE0 + 8:
            {
                int level = op - CMD_TSIZE0;
                TexWidth[level] = 1 << (int)(data & 0xF);
                TexHeight[level] = 1 << (int)((data >> 8) & 0xF);
                break;
            }
            case CMD_TPF: TexPixelFormat = (int)(data & 0xF); break;
            case CMD_TMAP: TexMapMode = (int)(data & 3); break;
            case CMD_TFUNC: TexFunction = (int)(data & 7); break;
            case CMD_TFLT: TexFilter = (int)data; break;
            case CMD_TWRAP:
                TexWrapU = (int)(data & 1);
                TexWrapV = (int)((data >> 8) & 1);
                break;
            case CMD_TMODE: break; // swizzle mode etc
            case CMD_TFLUSH: break;
            case CMD_TSYNC: break;

            // ── CLUT ──
            case CMD_CBP: ClutBasePtr = (ClutBasePtr & 0xFF000000) | data; break;
            case CMD_CBW:
                ClutBasePtr = (ClutBasePtr & 0x00FFFFFF) | ((data & 0xFF0000) << 8);
                break;
            case CMD_CLOAD: break; // CLUT load count
            case CMD_CMODE:
                ClutMode = (int)(data & 3);
                ClutShift = (int)((data >> 2) & 0x1F);
                ClutMask = (int)((data >> 8) & 0xFF);
                ClutOffset = (int)((data >> 16) & 0x1F);
                break;

            // ── Blend ──
            case CMD_ALPHA:
                BlendSrc = (int)(data & 0xF);
                BlendDst = (int)((data >> 4) & 0xF);
                BlendOp = (int)((data >> 8) & 0xF);
                break;
            case CMD_SFIX: BlendFixSrc = data; break;
            case CMD_DFIX: BlendFixDst = data; break;

            // ── Alpha/Depth test ──
            case CMD_ATST:
                AlphaFunc = (int)(data & 7);
                AlphaRef = (int)((data >> 8) & 0xFF);
                AlphaMask = (int)((data >> 16) & 0xFF);
                break;
            case CMD_ZTST: DepthFunc = (int)(data & 7); break;
            case CMD_ZMSK: DepthWriteEnable = data == 0; break;

            // ── Colors ──
            case CMD_AMC: MaterialAmbient = data | 0xFF000000; break;
            case CMD_AMA: MaterialAmbient = (MaterialAmbient & 0x00FFFFFF) | (data << 24); break;
            case CMD_DMC: MaterialDiffuse = data | 0xFF000000; break;
            case CMD_EMC: MaterialEmissive = data | 0xFF000000; break;
            case CMD_SMC: MaterialSpecular = data | 0xFF000000; break;
            case CMD_ALC: AmbientColor = data | 0xFF000000; break;
            case CMD_ALA: AmbientColor = (AmbientColor & 0x00FFFFFF) | (data << 24); break;

            // ── Transfer ──
            case CMD_TRXSBP: _trxSrcBase = (_trxSrcBase & 0xFF000000) | data; break;
            case CMD_TRXSBW:
                _trxSrcBase = (_trxSrcBase & 0x00FFFFFF) | ((data & 0xFF0000) << 8);
                _trxSrcWidth = data & 0xFFFF;
                break;
            case CMD_TRXDBP: _trxDstBase = (_trxDstBase & 0xFF000000) | data; break;
            case CMD_TRXDBW:
                _trxDstBase = (_trxDstBase & 0x00FFFFFF) | ((data & 0xFF0000) << 8);
                _trxDstWidth = data & 0xFFFF;
                break;
            case CMD_TRXPOS:
                _trxSrcX = (int)(data & 0x3FF);
                _trxSrcY = (int)((data >> 10) & 0x3FF);
                break;
            case CMD_TRXDPOS:
                _trxDstX = (int)(data & 0x3FF);
                _trxDstY = (int)((data >> 10) & 0x3FF);
                break;
            case CMD_TRXSIZE:
                _trxWidth = (int)(data & 0x3FF) + 1;
                _trxHeight = (int)((data >> 10) & 0x3FF) + 1;
                break;
            case CMD_TRXKICK:
                ExecuteTransfer((int)(data & 1));
                break;

            // Regions, lighting details, morph weights — stored but mostly stub
            case CMD_REGION1: break;
            case CMD_REGION2: break;
            case CMD_MINZ: break;
            case CMD_MAXZ: break;
            case >= CMD_LT0 and <= CMD_LT0 + 3: break; // light types
            case >= CMD_LTE0 and <= CMD_LTE3: break; // light enables
            case CMD_LMODE: break;
            case CMD_RNORM: break;
            case CMD_CMAT: break;
            case CMD_SPOW: break;
            case >= CMD_MW0 and < CMD_MW0 + 8: break; // morph weights
            case CMD_PSUB: break;
            case CMD_PPRIM: break;
            case CMD_CLIP_EN: break;
            case CMD_DTE: break;
            case CMD_STE: break;
            case CMD_AAE: break;
            case CMD_PCE: break;
            case CMD_CTE: break;
            case CMD_LOE: break;
            case CMD_STST: break;
            case CMD_SOP: break;
            case CMD_CTEST: break;
            case CMD_CREF: break;
            case CMD_CMSK: break;
            case CMD_FFX: break;
            case CMD_FFY: break;
            case CMD_FC: break;
            case CMD_TSLOPE: break;
            case CMD_TSHADE: break;
            case CMD_TLEVEL: break;
            case CMD_TEC: break;
            case >= CMD_DTH0 and < CMD_DTH0 + 4: break;
            case CMD_LOP: break;
            case CMD_PMSK1: break;
            case CMD_PMSK2: break;
            case CMD_BEZIER: break;
            case CMD_SPLINE: break;
        }

        return false;
    }

    // ── Draw primitives ──

    void DrawPrimitive(uint data)
    {
        int primType = (int)((data >> 16) & 7);
        int count = (int)(data & 0xFFFF);
        if (count == 0) return;

        uint vtype = CmdRegs[CMD_VTYPE];
        Renderer?.DrawPrimitive(this, primType, count, vtype, VertexAddr, IndexAddr);
    }

    // ── Memory transfer (DMA copy) ──

    void ExecuteTransfer(int pixelSize)
    {
        int bpp = pixelSize == 0 ? 2 : 4;
        for (int y = 0; y < _trxHeight; y++)
        {
            for (int x = 0; x < _trxWidth; x++)
            {
                uint srcOff = (uint)((_trxSrcY + y) * (int)_trxSrcWidth + _trxSrcX + x) * (uint)bpp;
                uint dstOff = (uint)((_trxDstY + y) * (int)_trxDstWidth + _trxDstX + x) * (uint)bpp;

                uint srcAddr = (_trxSrcBase + srcOff);
                uint dstAddr = (_trxDstBase + dstOff);

                for (int b = 0; b < bpp; b++)
                {
                    byte val = ReadGeMem(srcAddr + (uint)b);
                    WriteGeMem(dstAddr + (uint)b, val);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    byte ReadGeMem(uint addr)
    {
        if (addr >= 0x0400_0000 && addr < 0x0400_0000 + PspBus.VramSize)
            return _bus.Vram[addr - 0x0400_0000];
        if (addr < PspBus.RamSize)
            return _bus.Ram[addr];
        return _bus.Read8(addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void WriteGeMem(uint addr, byte val)
    {
        if (addr >= 0x0400_0000 && addr < 0x0400_0000 + PspBus.VramSize)
            _bus.Vram[addr - 0x0400_0000] = val;
        else if (addr < PspBus.RamSize)
            _bus.Ram[addr] = val;
        else
            _bus.Write8(addr, val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float CmdToFloat(uint data) =>
        BitConverter.UInt32BitsToSingle(data << 8);
}

static class MatrixExt
{
    public static void CopyTo(this Matrix4x4 m, float[] arr)
    {
        arr[0] = m.M11; arr[1] = m.M12; arr[2] = m.M13; arr[3] = m.M14;
        arr[4] = m.M21; arr[5] = m.M22; arr[6] = m.M23; arr[7] = m.M24;
        arr[8] = m.M31; arr[9] = m.M32; arr[10] = m.M33; arr[11] = m.M34;
        if (arr.Length >= 16)
        {
            arr[12] = m.M41; arr[13] = m.M42; arr[14] = m.M43; arr[15] = m.M44;
        }
    }
}
