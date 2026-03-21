using System.Runtime.CompilerServices;

namespace PsxEmu;

/// <summary>
/// MIPS R3000A CPU.
/// Modelled closely after ProjectPSX's proven implementation.
/// All hot methods are AggressiveInlining for JIT optimization.
/// </summary>
unsafe class MipsCpu
{
    public readonly uint[] GPR = new uint[32];
    public uint PC = 0xBFC0_0000;
    public uint Hi, Lo;

    // COP0: SR=12, Cause=13, EPC=14, BadVAddr=8, PRId=15
    public readonly uint[] COP0 = new uint[32];

    // Load delay slot
    uint _ldReg, _ldVal;
    uint _delayLdReg, _delayLdVal;
    uint _writeBackReg; // register written by Execute this step (takes priority over pending load)

    // Branch delay slot
    uint _pcNow;
    uint _pcPredictor;
    bool _isBranch, _isDelaySlot;
    bool _tookBranch, _delayTookBranch;

    public ulong TotalCycles { get; private set; }

    // TTY output capture (BIOS putchar B0:0x3D)
    readonly System.Text.StringBuilder _ttyBuf = new();
    public string TtyOutput => _ttyBuf.ToString();
    public void ClearTty() => _ttyBuf.Clear();

    // Debug instrumentation — disabled for performance, enable via DebugMode
    public bool DebugMode;
    public uint TraceStartPC;
    public bool Tracing;
    public int TraceCount;
    const int MaxTrace = 2000;
    bool _badPcDetected;
    readonly uint[] _pcRing = new uint[256];
    readonly uint[] _instrRing = new uint[256];
    int _pcRingIdx;

    readonly PsxBus _bus;
    public readonly PsxGte Gte = new();
    bool _dontIsolateCache;

    public MipsCpu(PsxBus bus) => _bus = bus;

    public void SetPC(uint addr)
    {
        PC = addr;
        _pcPredictor = addr + 4;
        _isBranch = _isDelaySlot = _tookBranch = _delayTookBranch = false;
    }

    public void Reset()
    {
        PC = 0xBFC0_0000;
        _pcPredictor = 0xBFC0_0004;
        Hi = Lo = 0;
        Array.Clear(GPR);
        Array.Clear(COP0);
        COP0[12] = 0x0000_0400; // SR: BEV=0, IEc=0
        COP0[15] = 0x2;         // PRId
        GPR[29] = 0x801F_FFF0;  // SP
        _ldReg = _ldVal = _delayLdReg = _delayLdVal = _writeBackReg = 0;
        _isBranch = _isDelaySlot = _tookBranch = _delayTookBranch = false;
        _dontIsolateCache = true;
        TotalCycles = 0;
    }

    // ── Interrupt handling (called before each step) ─────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleInterrupts()
    {
        if (_bus.IRQPending)
            COP0[13] |= 0x400;
        else
            COP0[13] &= ~0x400u;

        bool iec = (COP0[12] & 1) != 0;
        uint im  = (COP0[12] >> 8) & 0xFF;
        uint ip  = (COP0[13] >> 8) & 0xFF;

        if (iec && (im & ip) > 0)
            Exception(ExCode.INTERRUPT);
    }

    // ── Main step (hot path — minimal branching) ─────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Step()
    {
        TotalCycles++;
        _pcNow = PC;
        PC = _pcPredictor;
        _pcPredictor += 4;

        _isDelaySlot = _isBranch;
        _delayTookBranch = _tookBranch;
        _isBranch = false;
        _tookBranch = false;

        uint maskedPC = _pcNow & 0x1FFF_FFFF;
        uint instr = maskedPC < 0x1F00_0000
            ? _bus.LoadFromRam(maskedPC)
            : _bus.LoadFromBios(maskedPC);

        _writeBackReg = 0;
        if (instr != 0)
            Execute(instr);

        MemAccess();
        GPR[0] = 0;

        // BIOS putchar intercept (cheap range check first)
        if (_pcNow <= 0xC0 && _pcNow >= 0xA0)
            BiosIntercept();

        // Debug instrumentation — only when enabled
        if (DebugMode)
            StepDebug(maskedPC, instr);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void BiosIntercept()
    {
        if (_pcNow == 0xB0 && GPR[9] == 0x3D)
            _ttyBuf.Append((char)(GPR[4] & 0xFF));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void StepDebug(uint maskedPC, uint instr)
    {
        _pcRing[_pcRingIdx & 0xFF] = _pcNow;
        _instrRing[_pcRingIdx & 0xFF] = instr;
        _pcRingIdx++;

        if (!_badPcDetected && instr == 0xAAAA_AAAA && maskedPC < 0x1F00_0000)
        {
            _badPcDetected = true;
            Log.Error($"FIRST 0xAA EXEC at PC=0x{_pcNow:X8} (phys=0x{maskedPC:X8}) cycles={TotalCycles:N0}");
            Log.Error($"  ra=0x{GPR[31]:X8} sp=0x{GPR[29]:X8} v0=0x{GPR[2]:X8} a0=0x{GPR[4]:X8}");
            Log.Error($"  EPC=0x{COP0[14]:X8} Cause=0x{COP0[13]:X8} SR=0x{COP0[12]:X8}");
            Log.Error("  PC ring (last 256 instructions, newest last):");
            for (int ri = 0; ri < 256; ri++)
            {
                int idx = (_pcRingIdx - 256 + ri) & 0xFF;
                Log.Error($"    [{ri}] 0x{_pcRing[idx]:X8}: 0x{_instrRing[idx]:X8}");
            }
        }

        if (_pcNow == 0xA0) Log.CPU($"BIOS A0:0x{GPR[9]:X2} ra=0x{GPR[31]:X8} a0=0x{GPR[4]:X8}");
        else if (_pcNow == 0xB0 && GPR[9] != 0x3D) Log.CPU($"BIOS B0:0x{GPR[9]:X2} ra=0x{GPR[31]:X8} a0=0x{GPR[4]:X8}");
        else if (_pcNow == 0xC0) Log.CPU($"BIOS C0:0x{GPR[9]:X2} ra=0x{GPR[31]:X8} a0=0x{GPR[4]:X8}");

        if (TraceStartPC != 0 && _pcNow == TraceStartPC) Tracing = true;
        if (Tracing && TraceCount < MaxTrace)
        {
            TraceCount++;
            Log.CPU($"TRACE PC=0x{_pcNow:X8} instr=0x{instr:X8}");
        }
    }

    // ── Load delay implementation (matching ProjectPSX) ──────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void MemAccess()
    {
        // Apply the pending load from the previous instruction, UNLESS:
        // 1. A new load this step targets the same register (cancels this load)
        // 2. The current instruction wrote to the same register via SetGPR (takes priority,
        //    because on the R3000A pipeline the later instruction's WB comes after the load's WB)
        if (_delayLdReg != _ldReg && _writeBackReg != _ldReg)
            GPR[_ldReg] = _ldVal;
        _ldReg = _delayLdReg;
        _ldVal = _delayLdVal;
        _delayLdReg = 0;
        _writeBackReg = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SetGPR(uint reg, uint val)
    {
        GPR[reg] = val;
        _writeBackReg = reg;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void DelayedLoad(uint reg, uint val)
    {
        _delayLdReg = reg;
        _delayLdVal = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Branch(uint target)
    {
        _isBranch = true;
        _tookBranch = true;
        _pcPredictor = target;
    }

    // ── Instruction decode ───────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Execute(uint i)
    {
        uint op  = i >> 26;
        uint rs  = (i >> 21) & 31;
        uint rt  = (i >> 16) & 31;
        uint rd  = (i >> 11) & 31;
        uint sa  = (i >>  6) & 31;
        uint fn  = i & 63;
        uint imm = (ushort)i;
        uint imm_s = (uint)(short)i;

        switch (op)
        {
            case 0x00: Special(fn, rs, rt, rd, sa); break;
            case 0x01: BCOND(rs, rt, imm_s); break;
            case 0x02: Branch((PC & 0xF000_0000) | ((i & 0x03FF_FFFF) << 2)); break; // J
            case 0x03: SetGPR(31, _pcPredictor); Branch((PC & 0xF000_0000) | ((i & 0x03FF_FFFF) << 2)); break; // JAL
            case 0x04: _isBranch = true; if (GPR[rs] == GPR[rt]) Branch(PC + (imm_s << 2)); break; // BEQ
            case 0x05: _isBranch = true; if (GPR[rs] != GPR[rt]) Branch(PC + (imm_s << 2)); break; // BNE
            case 0x06: _isBranch = true; if ((int)GPR[rs] <= 0) Branch(PC + (imm_s << 2)); break;  // BLEZ
            case 0x07: _isBranch = true; if ((int)GPR[rs] > 0)  Branch(PC + (imm_s << 2)); break;  // BGTZ
            case 0x08: SetGPR(rt, GPR[rs] + imm_s); break; // ADDI
            case 0x09: SetGPR(rt, GPR[rs] + imm_s); break; // ADDIU
            case 0x0A: SetGPR(rt, (int)GPR[rs] < (int)imm_s ? 1u : 0u); break; // SLTI
            case 0x0B: SetGPR(rt, GPR[rs] < imm_s ? 1u : 0u); break;           // SLTIU
            case 0x0C: SetGPR(rt, GPR[rs] & imm); break;  // ANDI
            case 0x0D: SetGPR(rt, GPR[rs] | imm); break;  // ORI
            case 0x0E: SetGPR(rt, GPR[rs] ^ imm); break;  // XORI
            case 0x0F: SetGPR(rt, imm << 16); break;      // LUI
            case 0x10: COP0op(rs, rt, rd); break;
            case 0x12: COP2op(i, rs, rt, rd); break;
            case 0x20: // LB
                if (_dontIsolateCache)
                    DelayedLoad(rt, (uint)(sbyte)_bus.Read8(GPR[rs] + imm_s));
                break;
            case 0x21: // LH
                if (_dontIsolateCache)
                    DelayedLoad(rt, (uint)(short)_bus.Read16(GPR[rs] + imm_s));
                break;
            case 0x22: // LWL
                if (_dontIsolateCache) LWL(rs, rt, imm_s);
                break;
            case 0x23: // LW
                if (_dontIsolateCache)
                    DelayedLoad(rt, _bus.Read32(GPR[rs] + imm_s));
                break;
            case 0x24: // LBU
                if (_dontIsolateCache)
                    DelayedLoad(rt, _bus.Read8(GPR[rs] + imm_s));
                break;
            case 0x25: // LHU
                if (_dontIsolateCache)
                    DelayedLoad(rt, (ushort)_bus.Read16(GPR[rs] + imm_s));
                break;
            case 0x26: // LWR
                if (_dontIsolateCache) LWR(rs, rt, imm_s);
                break;
            case 0x28: if (_dontIsolateCache) _bus.Write8(GPR[rs] + imm_s, (byte)GPR[rt]); break;    // SB
            case 0x29: if (_dontIsolateCache) _bus.Write16(GPR[rs] + imm_s, (ushort)GPR[rt]); break; // SH
            case 0x2A: if (_dontIsolateCache) SWLop(rs, rt, imm_s); break; // SWL
            case 0x2B: if (_dontIsolateCache) _bus.Write32(GPR[rs] + imm_s, GPR[rt]); break;         // SW
            case 0x2E: if (_dontIsolateCache) SWRop(rs, rt, imm_s); break; // SWR
            case 0x32: // LWC2
                if (_dontIsolateCache)
                    Gte.WriteData(rt, _bus.Read32(GPR[rs] + imm_s));
                break;
            case 0x3A: // SWC2
                if (_dontIsolateCache)
                    _bus.Write32(GPR[rs] + imm_s, Gte.LoadData(rt));
                break;
        }
    }

    void Special(uint fn, uint rs, uint rt, uint rd, uint sa)
    {
        switch (fn)
        {
            case 0x00: SetGPR(rd, GPR[rt] << (int)sa); break;                              // SLL
            case 0x02: SetGPR(rd, GPR[rt] >> (int)sa); break;                              // SRL
            case 0x03: SetGPR(rd, (uint)((int)GPR[rt] >> (int)sa)); break;                 // SRA
            case 0x04: SetGPR(rd, GPR[rt] << (int)(GPR[rs] & 31)); break;                  // SLLV
            case 0x06: SetGPR(rd, GPR[rt] >> (int)(GPR[rs] & 31)); break;                  // SRLV
            case 0x07: SetGPR(rd, (uint)((int)GPR[rt] >> (int)(GPR[rs] & 31))); break;     // SRAV
            case 0x08: Branch(GPR[rs]); break;                                              // JR
            case 0x09: SetGPR(rd, _pcPredictor); Branch(GPR[rs]); break;                   // JALR
            case 0x0C: Exception(ExCode.SYSCALL); break;                                    // SYSCALL
            case 0x0D: Exception(ExCode.BREAK); break;                                      // BREAK
            case 0x10: SetGPR(rd, Hi); break; // MFHI
            case 0x11: Hi = GPR[rs]; break;   // MTHI
            case 0x12: SetGPR(rd, Lo); break; // MFLO
            case 0x13: Lo = GPR[rs]; break;   // MTLO
            case 0x18: { long r = (long)(int)GPR[rs] * (int)GPR[rt]; Hi = (uint)(r >> 32); Lo = (uint)r; break; } // MULT
            case 0x19: { ulong r = (ulong)GPR[rs] * GPR[rt]; Hi = (uint)(r >> 32); Lo = (uint)r; break; }        // MULTU
            case 0x1A: // DIV
                if (GPR[rt] != 0) { Lo = (uint)((int)GPR[rs] / (int)GPR[rt]); Hi = (uint)((int)GPR[rs] % (int)GPR[rt]); }
                else { Hi = GPR[rs]; Lo = (int)GPR[rs] >= 0 ? 0xFFFF_FFFFu : 1u; }
                break;
            case 0x1B: // DIVU
                if (GPR[rt] != 0) { Lo = GPR[rs] / GPR[rt]; Hi = GPR[rs] % GPR[rt]; }
                else { Hi = GPR[rs]; Lo = 0xFFFF_FFFFu; }
                break;
            case 0x20: SetGPR(rd, GPR[rs] + GPR[rt]); break; // ADD
            case 0x21: SetGPR(rd, GPR[rs] + GPR[rt]); break; // ADDU
            case 0x22: SetGPR(rd, GPR[rs] - GPR[rt]); break; // SUB
            case 0x23: SetGPR(rd, GPR[rs] - GPR[rt]); break; // SUBU
            case 0x24: SetGPR(rd, GPR[rs] & GPR[rt]); break; // AND
            case 0x25: SetGPR(rd, GPR[rs] | GPR[rt]); break; // OR
            case 0x26: SetGPR(rd, GPR[rs] ^ GPR[rt]); break; // XOR
            case 0x27: SetGPR(rd, ~(GPR[rs] | GPR[rt])); break; // NOR
            case 0x2A: SetGPR(rd, (int)GPR[rs] < (int)GPR[rt] ? 1u : 0u); break; // SLT
            case 0x2B: SetGPR(rd, GPR[rs] < GPR[rt] ? 1u : 0u); break;           // SLTU
        }
    }

    void BCOND(uint rs, uint rt, uint imm_s)
    {
        _isBranch = true;
        bool shouldLink = (rt & 0x1E) == 0x10;
        bool shouldBranch = ((int)(GPR[rs] ^ (rt << 31))) < 0;
        if (shouldLink) SetGPR(31, _pcPredictor);
        if (shouldBranch) Branch(PC + (imm_s << 2));
    }

    void COP0op(uint rs, uint rt, uint rd)
    {
        switch (rs)
        {
            case 0x00: // MFC0 – must use load delay
                DelayedLoad(rt, COP0[rd]);
                break;
            case 0x04: // MTC0
            {
                uint value = GPR[rt];
                if (rd == 13) // CAUSE: only bits 8-9 writable
                {
                    COP0[13] &= ~0x300u;
                    COP0[13] |= value & 0x300;
                }
                else if (rd == 12) // SR
                {
                    _dontIsolateCache = (value & 0x10000) == 0;
                    bool prevIEC = (COP0[12] & 1) == 1;
                    bool curIEC = (value & 1) == 1;
                    COP0[12] = value;
                    // Writing SR can trigger pending software interrupt
                    uint im = (value >> 8) & 0x3;
                    uint ip = (COP0[13] >> 8) & 0x3;
                    if (!prevIEC && curIEC && (im & ip) > 0)
                    {
                        PC = _pcPredictor;
                        Exception(ExCode.INTERRUPT);
                    }
                }
                else
                {
                    COP0[rd] = value;
                }
                break;
            }
            case 0x10: // RFE
            {
                uint mode = COP0[12] & 0x3F;
                COP0[12] &= ~0xFu;
                COP0[12] |= mode >> 2;
                break;
            }
        }
    }

    void COP2op(uint instr, uint rs, uint rt, uint rd)
    {
        if ((instr & 0x2000000) != 0) { Gte.Execute(instr); return; }
        switch (rs)
        {
            case 0x00: DelayedLoad(rt, Gte.LoadData(rd)); break;  // MFC2
            case 0x02: DelayedLoad(rt, Gte.LoadControl(rd)); break; // CFC2
            case 0x04: Gte.WriteData(rd, GPR[rt]); break;          // MTC2
            case 0x06: Gte.WriteControl(rd, GPR[rt]); break;       // CTC2
        }
    }

    // ── Unaligned load/store (corrected from ProjectPSX reference) ───────────

    void LWL(uint rs, uint rt, uint imm_s)
    {
        uint addr = GPR[rs] + imm_s;
        uint aligned = addr & 0xFFFF_FFFC;
        uint word = _bus.Read32(aligned);

        uint cur = GPR[rt];
        if (rt == _ldReg) cur = _ldVal;

        int shift = (int)((addr & 3) << 3);
        uint mask = 0x00FF_FFFFu >> shift;
        uint value = (cur & mask) | (word << (24 - shift));

        DelayedLoad(rt, value);
    }

    void LWR(uint rs, uint rt, uint imm_s)
    {
        uint addr = GPR[rs] + imm_s;
        uint aligned = addr & 0xFFFF_FFFC;
        uint word = _bus.Read32(aligned);

        uint cur = GPR[rt];
        if (rt == _ldReg) cur = _ldVal;

        int shift = (int)((addr & 3) << 3);
        uint mask = 0xFFFF_FF00u << (24 - shift);
        uint value = (cur & mask) | (word >> shift);

        DelayedLoad(rt, value);
    }

    void SWLop(uint rs, uint rt, uint imm_s)
    {
        uint addr = GPR[rs] + imm_s;
        uint aligned = addr & 0xFFFF_FFFC;
        uint word = _bus.Read32(aligned);
        int shift = (int)((addr & 3) << 3);
        uint mask = 0xFFFF_FF00u << shift;
        uint value = (word & mask) | (GPR[rt] >> (24 - shift));
        _bus.Write32(aligned, value);
    }

    void SWRop(uint rs, uint rt, uint imm_s)
    {
        uint addr = GPR[rs] + imm_s;
        uint aligned = addr & 0xFFFF_FFFC;
        uint word = _bus.Read32(aligned);
        int shift = (int)((addr & 3) << 3);
        uint mask = 0x00FF_FFFFu >> (24 - shift);
        uint value = (word & mask) | (GPR[rt] << shift);
        _bus.Write32(aligned, value);
    }

    // ── Exceptions ───────────────────────────────────────────────────────────

    enum ExCode : uint
    {
        INTERRUPT = 0,
        LOAD_ADRESS_ERROR = 4,
        STORE_ADRESS_ERROR = 5,
        SYSCALL = 8,
        BREAK = 9,
        ILLEGAL_INSTR = 10,
        OVERFLOW = 12,
    }

    void Exception(ExCode cause)
    {
        uint mode = COP0[12] & 0x3F;
        COP0[12] &= ~0x3Fu;
        COP0[12] |= (mode << 2) & 0x3F;

        uint oldCause = COP0[13] & 0xFF00;
        COP0[13] = (uint)cause << 2;
        COP0[13] |= oldCause;

        if (cause == ExCode.INTERRUPT)
        {
            COP0[14] = PC; // EPC = instruction about to execute
            _isDelaySlot = _isBranch;
            _delayTookBranch = _tookBranch;
        }
        else
        {
            COP0[14] = _pcNow; // EPC = faulting instruction
        }

        if (_isDelaySlot)
        {
            COP0[14] -= 4;
            COP0[13] |= 1u << 31; // BD bit
        }

        bool bev = (COP0[12] & 0x0040_0000) != 0;
        PC = bev ? 0xBFC0_0180u : 0x8000_0080u;
        _pcPredictor = PC + 4;
    }
}
