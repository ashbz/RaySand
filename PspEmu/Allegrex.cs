using System.Runtime.CompilerServices;

namespace PspEmu;

/// <summary>
/// Allegrex CPU: MIPS32 Release 2 core with FPU (COP1) at 333 MHz.
/// HLE-oriented: syscall instructions trap into the HLE kernel.
/// MIPS32r2 has hardware interlocks — no software load delay tracking needed.
/// </summary>
sealed partial class Allegrex
{
    public readonly uint[] Gpr = new uint[32];
    public uint Pc;
    public uint Hi, Lo;
    public uint[] Cop0 = new uint[32];

    public readonly float[] Fpr = new float[32];
    public uint FpuCcr;

    public readonly float[] Vpr = new float[128];
    public uint VfpuCc;

    bool _inDelaySlot;
    bool _branchTaken;
    uint _branchTarget;

    public PspBus Bus { get; set; } = null!;
    public HleKernel Kernel { get; set; } = null!;

    public long CyclesExecuted;
    public bool InterruptsEnabled = true;
    bool _halted;
    public bool WaitingVblank;

    public bool Halted { get => _halted; set => _halted = value; }

    public void Reset()
    {
        Array.Clear(Gpr);
        Array.Clear(Fpr);
        Array.Clear(Vpr);
        Array.Clear(Cop0);
        Pc = 0; Hi = 0; Lo = 0;
        FpuCcr = 0; VfpuCc = 0;
        _inDelaySlot = false;
        _branchTaken = false;
        CyclesExecuted = 0;
        _halted = false;
        InterruptsEnabled = true;
    }

    /// <summary>
    /// Execute N instructions using unsafe direct RAM access for instruction fetch.
    /// This is the performance-critical hot loop — avoids VirtToPhys and Span overhead.
    /// </summary>
    public unsafe void StepN(int count)
    {
        fixed (byte* ramBase = Bus.Ram)
        {
            for (int i = 0; i < count; i++)
            {
                if (_halted | WaitingVblank) return;

                uint pc = Pc;
                Pc = pc + 4;
                CyclesExecuted++;

                // Fast instruction fetch: most code lives in user RAM 0x08000000-0x0BFFFFFF
                uint instr;
                uint phys;
                if (pc >= 0x0800_0000u && pc < 0x0C00_0000u)
                {
                    phys = pc - 0x0800_0000u;
                    instr = *(uint*)(ramBase + phys);
                }
                else if (pc >= 0x8000_0000u && pc < 0xC000_0000u)
                {
                    phys = pc & 0x1FFF_FFFFu;
                    if (phys < PspBus.RamSize - 3)
                        instr = *(uint*)(ramBase + phys);
                    else
                        instr = Bus.Read32(pc);
                }
                else
                {
                    instr = Bus.Read32(pc);
                }

                bool wasInDelaySlot = _inDelaySlot;
                Execute(instr);

                if (wasInDelaySlot)
                {
                    Pc = _branchTarget;
                    _inDelaySlot = false;
                }
                if (_branchTaken)
                {
                    _branchTaken = false;
                    _inDelaySlot = true;
                }

                Gpr[0] = 0;
            }
        }
    }

    void Execute(uint instr)
    {
        if (instr == 0) return; // NOP

        uint op = instr >> 26;
        switch (op)
        {
            case 0x00: ExecSpecial(instr); break;
            case 0x01: ExecRegimm(instr); break;
            case 0x02: J(instr); break;
            case 0x03: Jal(instr); break;
            case 0x04: Beq(instr); break;
            case 0x05: Bne(instr); break;
            case 0x06: Blez(instr); break;
            case 0x07: Bgtz(instr); break;
            case 0x08: Addi(instr); break;
            case 0x09: Addiu(instr); break;
            case 0x0A: Slti(instr); break;
            case 0x0B: Sltiu(instr); break;
            case 0x0C: Andi(instr); break;
            case 0x0D: Ori(instr); break;
            case 0x0E: Xori(instr); break;
            case 0x0F: Lui(instr); break;
            case 0x10: ExecCop0(instr); break;
            case 0x11: ExecCop1(instr); break;
            case 0x12: ExecVfpu(instr); break; // COP2 = VFPU
            case 0x14: Beql(instr); break;
            case 0x15: Bnel(instr); break;
            case 0x16: Blezl(instr); break;
            case 0x17: Bgtzl(instr); break;
            case 0x1C: ExecSpecial2(instr); break;
            case 0x1F: ExecSpecial3(instr); break;
            case 0x20: Lb(instr); break;
            case 0x21: Lh(instr); break;
            case 0x22: Lwl(instr); break;
            case 0x23: Lw(instr); break;
            case 0x24: Lbu(instr); break;
            case 0x25: Lhu(instr); break;
            case 0x26: Lwr(instr); break;
            case 0x28: Sb(instr); break;
            case 0x29: Sh(instr); break;
            case 0x2A: Swl(instr); break;
            case 0x2B: Sw(instr); break;
            case 0x2E: Swr(instr); break;
            case 0x2F: break; // CACHE - nop for HLE
            case 0x30: Ll(instr); break;
            case 0x31: Lwc1(instr); break;
            case 0x32: ExecLvVfpu(instr); break;
            case 0x38: Sc(instr); break;
            case 0x39: Swc1(instr); break;
            case 0x3A: ExecSvVfpu(instr); break;

            case 0x36: ExecLvVfpu(instr); break; // LV.Q (COP2 64-bit load)
            case 0x3E: ExecSvVfpu(instr); break; // SV.Q (COP2 64-bit store)

            default:
                Log.Warn(LogCat.CPU, $"Unknown opcode {op:X2} at PC={Pc - 4:X8} instr={instr:X8}");
                break;
        }
    }

    // ── Decode helpers ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Rs(uint i) => (i >> 21) & 0x1F;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Rt(uint i) => (i >> 16) & 0x1F;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Rd(uint i) => (i >> 11) & 0x1F;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Sa(uint i) => (i >> 6) & 0x1F;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Funct(uint i) => i & 0x3F;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short Imm16(uint i) => (short)(ushort)i;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Imm16U(uint i) => i & 0xFFFF;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint JumpTarget(uint i, uint pc) => ((pc) & 0xF000_0000) | ((i & 0x03FF_FFFF) << 2);

    // ── Branch helpers ──

    void DoBranch(uint target)
    {
        _branchTaken = true;
        _branchTarget = target;
    }

    void DoBranchLikely(bool condition, uint target)
    {
        if (condition)
        {
            DoBranch(target);
        }
        else
        {
            Pc += 4; // skip delay slot
        }
    }

    // ── SPECIAL (opcode 0x00) ──

    void ExecSpecial(uint instr)
    {
        uint fn = Funct(instr);
        switch (fn)
        {
            case 0x00: // SLL (and ROTR when bit 21 set in some encodings)
            {
                uint sa = Sa(instr);
                uint rd = Rd(instr), rt = Rt(instr);
                if (rd != 0) Gpr[rd] = Gpr[rt] << (int)sa;
                break;
            }
            case 0x02: // SRL / ROTR
            {
                uint sa = Sa(instr), rd = Rd(instr), rt = Rt(instr);
                bool rotr = ((instr >> 21) & 1) != 0;
                if (rd != 0)
                    Gpr[rd] = rotr ? (Gpr[rt] >> (int)sa) | (Gpr[rt] << (32 - (int)sa))
                                   : Gpr[rt] >> (int)sa;
                break;
            }
            case 0x03: // SRA
            {
                uint sa = Sa(instr), rd = Rd(instr), rt = Rt(instr);
                if (rd != 0) Gpr[rd] = (uint)((int)Gpr[rt] >> (int)sa);
                break;
            }
            case 0x04: // SLLV
            {
                uint rd = Rd(instr), rt = Rt(instr), rs = Rs(instr);
                if (rd != 0) Gpr[rd] = Gpr[rt] << (int)(Gpr[rs] & 0x1F);
                break;
            }
            case 0x06: // SRLV / ROTRV
            {
                uint rd = Rd(instr), rt = Rt(instr), rs = Rs(instr);
                int amt = (int)(Gpr[rs] & 0x1F);
                bool rotr = ((instr >> 6) & 1) != 0;
                if (rd != 0)
                    Gpr[rd] = rotr ? (Gpr[rt] >> amt) | (Gpr[rt] << (32 - amt))
                                   : Gpr[rt] >> amt;
                break;
            }
            case 0x07: // SRAV
            {
                uint rd = Rd(instr), rt = Rt(instr), rs = Rs(instr);
                if (rd != 0) Gpr[rd] = (uint)((int)Gpr[rt] >> (int)(Gpr[rs] & 0x1F));
                break;
            }
            case 0x08: // JR
                DoBranch(Gpr[Rs(instr)]);
                break;
            case 0x09: // JALR
            {
                uint rd = Rd(instr);
                uint ret = Pc + 4; // after delay slot
                DoBranch(Gpr[Rs(instr)]);
                if (rd != 0) Gpr[rd] = ret;
                break;
            }
            case 0x0A: // MOVZ
            {
                uint rd = Rd(instr), rs = Rs(instr), rt = Rt(instr);
                if (rd != 0 && Gpr[rt] == 0) Gpr[rd] = Gpr[rs];
                break;
            }
            case 0x0B: // MOVN
            {
                uint rd = Rd(instr), rs = Rs(instr), rt = Rt(instr);
                if (rd != 0 && Gpr[rt] != 0) Gpr[rd] = Gpr[rs];
                break;
            }
            case 0x0C: // SYSCALL
            {
                uint code = (instr >> 6) & 0xFFFFF;
                Kernel.HandleSyscall(code);
                break;
            }
            case 0x0D: // BREAK
                Log.Write(LogCat.CPU, $"BREAK at {Pc - 4:X8}");
                _halted = true;
                break;
            case 0x0F: break; // SYNC - nop for HLE
            case 0x10: // MFHI
                if (Rd(instr) != 0) Gpr[Rd(instr)] = Hi;
                break;
            case 0x11: // MTHI
                Hi = Gpr[Rs(instr)];
                break;
            case 0x12: // MFLO
                if (Rd(instr) != 0) Gpr[Rd(instr)] = Lo;
                break;
            case 0x13: // MTLO
                Lo = Gpr[Rs(instr)];
                break;
            case 0x18: // MULT
            {
                long result = (long)(int)Gpr[Rs(instr)] * (int)Gpr[Rt(instr)];
                Lo = (uint)result;
                Hi = (uint)(result >> 32);
                break;
            }
            case 0x19: // MULTU
            {
                ulong result = (ulong)Gpr[Rs(instr)] * Gpr[Rt(instr)];
                Lo = (uint)result;
                Hi = (uint)(result >> 32);
                break;
            }
            case 0x1A: // DIV
            {
                int rs = (int)Gpr[Rs(instr)], rt = (int)Gpr[Rt(instr)];
                if (rt != 0) { Lo = (uint)(rs / rt); Hi = (uint)(rs % rt); }
                break;
            }
            case 0x1B: // DIVU
            {
                uint rs = Gpr[Rs(instr)], rt = Gpr[Rt(instr)];
                if (rt != 0) { Lo = rs / rt; Hi = rs % rt; }
                break;
            }
            case 0x20: // ADD
            case 0x21: // ADDU
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = Gpr[Rs(instr)] + Gpr[Rt(instr)];
                break;
            }
            case 0x22: // SUB
            case 0x23: // SUBU
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = Gpr[Rs(instr)] - Gpr[Rt(instr)];
                break;
            }
            case 0x24: // AND
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = Gpr[Rs(instr)] & Gpr[Rt(instr)];
                break;
            }
            case 0x25: // OR
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = Gpr[Rs(instr)] | Gpr[Rt(instr)];
                break;
            }
            case 0x26: // XOR
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = Gpr[Rs(instr)] ^ Gpr[Rt(instr)];
                break;
            }
            case 0x27: // NOR
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = ~(Gpr[Rs(instr)] | Gpr[Rt(instr)]);
                break;
            }
            case 0x2A: // SLT
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = (int)Gpr[Rs(instr)] < (int)Gpr[Rt(instr)] ? 1u : 0u;
                break;
            }
            case 0x2B: // SLTU
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = Gpr[Rs(instr)] < Gpr[Rt(instr)] ? 1u : 0u;
                break;
            }
            case 0x2C: // MAX (Allegrex)
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = (int)Gpr[Rs(instr)] > (int)Gpr[Rt(instr)] ? Gpr[Rs(instr)] : Gpr[Rt(instr)];
                break;
            }
            case 0x2D: // MIN (Allegrex)
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = (int)Gpr[Rs(instr)] < (int)Gpr[Rt(instr)] ? Gpr[Rs(instr)] : Gpr[Rt(instr)];
                break;
            }
            default:
                Log.Warn(LogCat.CPU, $"SPECIAL fn={fn:X2} at {Pc - 4:X8}");
                break;
        }
    }

    // ── SPECIAL2 (opcode 0x1C) ──

    void ExecSpecial2(uint instr)
    {
        uint fn = Funct(instr);
        switch (fn)
        {
            case 0x00: // MADD
            {
                long acc = ((long)Hi << 32) | Lo;
                acc += (long)(int)Gpr[Rs(instr)] * (int)Gpr[Rt(instr)];
                Lo = (uint)acc; Hi = (uint)(acc >> 32);
                break;
            }
            case 0x01: // MADDU
            {
                ulong acc = ((ulong)Hi << 32) | Lo;
                acc += (ulong)Gpr[Rs(instr)] * Gpr[Rt(instr)];
                Lo = (uint)acc; Hi = (uint)(acc >> 32);
                break;
            }
            case 0x02: // MUL (rd = rs * rt, no Hi/Lo)
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = (uint)((int)Gpr[Rs(instr)] * (int)Gpr[Rt(instr)]);
                break;
            }
            case 0x04: // MSUB
            {
                long acc = ((long)Hi << 32) | Lo;
                acc -= (long)(int)Gpr[Rs(instr)] * (int)Gpr[Rt(instr)];
                Lo = (uint)acc; Hi = (uint)(acc >> 32);
                break;
            }
            case 0x05: // MSUBU
            {
                ulong acc = ((ulong)Hi << 32) | Lo;
                acc -= (ulong)Gpr[Rs(instr)] * Gpr[Rt(instr)];
                Lo = (uint)acc; Hi = (uint)(acc >> 32);
                break;
            }
            case 0x20: // CLZ
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = (uint)System.Numerics.BitOperations.LeadingZeroCount(Gpr[Rs(instr)]);
                break;
            }
            case 0x21: // CLO
            {
                uint rd = Rd(instr);
                if (rd != 0) Gpr[rd] = (uint)System.Numerics.BitOperations.LeadingZeroCount(~Gpr[Rs(instr)]);
                break;
            }
            default:
                Log.Warn(LogCat.CPU, $"SPECIAL2 fn={fn:X2} at {Pc - 4:X8}");
                break;
        }
    }

    // ── SPECIAL3 (opcode 0x1F) — EXT, INS, BSHFL ──

    void ExecSpecial3(uint instr)
    {
        uint fn = Funct(instr);
        switch (fn)
        {
            case 0x00: // EXT (extract bit field)
            {
                uint rt = Rt(instr), rs = Rs(instr);
                int pos = (int)Sa(instr);
                int size = (int)Rd(instr) + 1;
                uint mask = (size >= 32) ? 0xFFFF_FFFF : (1u << size) - 1;
                if (rt != 0) Gpr[rt] = (Gpr[rs] >> pos) & mask;
                break;
            }
            case 0x04: // INS (insert bit field)
            {
                uint rt = Rt(instr), rs = Rs(instr);
                int pos = (int)Sa(instr);
                int msb = (int)Rd(instr);
                int size = msb - pos + 1;
                uint mask = (size >= 32) ? 0xFFFF_FFFF : (1u << size) - 1;
                if (rt != 0) Gpr[rt] = (Gpr[rt] & ~(mask << pos)) | ((Gpr[rs] & mask) << pos);
                break;
            }
            case 0x20: // BSHFL sub-functions
            {
                uint bshfl = Sa(instr);
                uint rd = Rd(instr), rt = Rt(instr);
                switch (bshfl)
                {
                    case 0x02: // WSBH (word swap bytes within halfwords)
                        if (rd != 0)
                        {
                            uint v = Gpr[rt];
                            Gpr[rd] = ((v & 0x00FF00FF) << 8) | ((v & 0xFF00FF00) >> 8);
                        }
                        break;
                    case 0x10: // SEB (sign-extend byte)
                        if (rd != 0) Gpr[rd] = (uint)(int)(sbyte)Gpr[rt];
                        break;
                    case 0x18: // SEH (sign-extend halfword)
                        if (rd != 0) Gpr[rd] = (uint)(int)(short)Gpr[rt];
                        break;
                    case 0x14: // BITREV (Allegrex: bit reverse)
                        if (rd != 0)
                        {
                            uint v = Gpr[rt];
                            v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
                            v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
                            v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
                            v = ((v >> 8) & 0x00FF00FF) | ((v & 0x00FF00FF) << 8);
                            v = (v >> 16) | (v << 16);
                            Gpr[rd] = v;
                        }
                        break;
                    default:
                        Log.Warn(LogCat.CPU, $"BSHFL sa={bshfl:X2} at {Pc - 4:X8}");
                        break;
                }
                break;
            }
            default:
                Log.Warn(LogCat.CPU, $"SPECIAL3 fn={fn:X2} at {Pc - 4:X8}");
                break;
        }
    }

    // ── REGIMM (opcode 0x01) ──

    void ExecRegimm(uint instr)
    {
        uint rt = Rt(instr);
        uint target = Pc + (uint)(Imm16(instr) << 2);
        switch (rt)
        {
            case 0x00: // BLTZ
                if ((int)Gpr[Rs(instr)] < 0) DoBranch(target);
                break;
            case 0x01: // BGEZ
                if ((int)Gpr[Rs(instr)] >= 0) DoBranch(target);
                break;
            case 0x02: // BLTZL
                DoBranchLikely((int)Gpr[Rs(instr)] < 0, target);
                break;
            case 0x03: // BGEZL
                DoBranchLikely((int)Gpr[Rs(instr)] >= 0, target);
                break;
            case 0x10: // BLTZAL
                Gpr[31] = Pc + 4;
                if ((int)Gpr[Rs(instr)] < 0) DoBranch(target);
                break;
            case 0x11: // BGEZAL
                Gpr[31] = Pc + 4;
                if ((int)Gpr[Rs(instr)] >= 0) DoBranch(target);
                break;
            case 0x12: // BLTZALL
                Gpr[31] = Pc + 4;
                DoBranchLikely((int)Gpr[Rs(instr)] < 0, target);
                break;
            case 0x13: // BGEZALL
                Gpr[31] = Pc + 4;
                DoBranchLikely((int)Gpr[Rs(instr)] >= 0, target);
                break;
            default:
                Log.Warn(LogCat.CPU, $"REGIMM rt={rt:X2} at {Pc - 4:X8}");
                break;
        }
    }

    // ── Standard I-type instructions ──

    void J(uint instr) => DoBranch(JumpTarget(instr, Pc));
    void Jal(uint instr) { Gpr[31] = Pc + 4; DoBranch(JumpTarget(instr, Pc)); }

    void Beq(uint instr)  { if (Gpr[Rs(instr)] == Gpr[Rt(instr)]) DoBranch(Pc + (uint)(Imm16(instr) << 2)); }
    void Bne(uint instr)  { if (Gpr[Rs(instr)] != Gpr[Rt(instr)]) DoBranch(Pc + (uint)(Imm16(instr) << 2)); }
    void Blez(uint instr) { if ((int)Gpr[Rs(instr)] <= 0) DoBranch(Pc + (uint)(Imm16(instr) << 2)); }
    void Bgtz(uint instr) { if ((int)Gpr[Rs(instr)] > 0) DoBranch(Pc + (uint)(Imm16(instr) << 2)); }

    void Beql(uint instr)  => DoBranchLikely(Gpr[Rs(instr)] == Gpr[Rt(instr)], Pc + (uint)(Imm16(instr) << 2));
    void Bnel(uint instr)  => DoBranchLikely(Gpr[Rs(instr)] != Gpr[Rt(instr)], Pc + (uint)(Imm16(instr) << 2));
    void Blezl(uint instr) => DoBranchLikely((int)Gpr[Rs(instr)] <= 0, Pc + (uint)(Imm16(instr) << 2));
    void Bgtzl(uint instr) => DoBranchLikely((int)Gpr[Rs(instr)] > 0, Pc + (uint)(Imm16(instr) << 2));

    void Addi(uint instr)  { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = (uint)((int)Gpr[Rs(instr)] + Imm16(instr)); }
    void Addiu(uint instr) { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = (uint)((int)Gpr[Rs(instr)] + Imm16(instr)); }
    void Slti(uint instr)  { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = (int)Gpr[Rs(instr)] < Imm16(instr) ? 1u : 0u; }
    void Sltiu(uint instr) { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = Gpr[Rs(instr)] < (uint)Imm16(instr) ? 1u : 0u; }
    void Andi(uint instr)  { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = Gpr[Rs(instr)] & Imm16U(instr); }
    void Ori(uint instr)   { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = Gpr[Rs(instr)] | Imm16U(instr); }
    void Xori(uint instr)  { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = Gpr[Rs(instr)] ^ Imm16U(instr); }
    void Lui(uint instr)   { uint rt = Rt(instr); if (rt != 0) Gpr[rt] = Imm16U(instr) << 16; }

    // ── Load/Store ──

    void Lb(uint instr)
    {
        uint rt = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        if (rt != 0) Gpr[rt] = (uint)(int)(sbyte)Bus.Read8(addr);
    }

    void Lbu(uint instr)
    {
        uint rt = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        if (rt != 0) Gpr[rt] = Bus.Read8(addr);
    }

    void Lh(uint instr)
    {
        uint rt = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        if (rt != 0) Gpr[rt] = (uint)(int)(short)Bus.Read16(addr);
    }

    void Lhu(uint instr)
    {
        uint rt = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        if (rt != 0) Gpr[rt] = Bus.Read16(addr);
    }

    void Lw(uint instr)
    {
        uint rt = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        if (rt != 0) Gpr[rt] = Bus.Read32(addr);
    }

    void Lwl(uint instr)
    {
        uint rt = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        uint aligned = addr & ~3u;
        uint word = Bus.Read32(aligned);
        int shift = (int)(addr & 3);
        uint mask = 0xFFFFFFFF << ((3 - shift) * 8);
        uint val = (word << ((3 - shift) * 8)) | (Gpr[rt] & ~mask);
        if (rt != 0) Gpr[rt] = val;
    }

    void Lwr(uint instr)
    {
        uint rt = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        uint aligned = addr & ~3u;
        uint word = Bus.Read32(aligned);
        int shift = (int)(addr & 3);
        uint mask = 0xFFFFFFFF >> (shift * 8);
        uint val = (word >> (shift * 8)) | (Gpr[rt] & ~mask);
        if (rt != 0) Gpr[rt] = val;
    }

    void Sb(uint instr)
    {
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        Bus.Write8(addr, (byte)Gpr[Rt(instr)]);
    }

    void Sh(uint instr)
    {
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        Bus.Write16(addr, (ushort)Gpr[Rt(instr)]);
    }

    void Sw(uint instr)
    {
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        Bus.Write32(addr, Gpr[Rt(instr)]);
    }

    void Swl(uint instr)
    {
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        uint aligned = addr & ~3u;
        uint word = Bus.Read32(aligned);
        int shift = (int)(addr & 3);
        uint val = (word & ~(0xFFFFFFFF >> ((3 - shift) * 8))) | (Gpr[Rt(instr)] >> ((3 - shift) * 8));
        Bus.Write32(aligned, val);
    }

    void Swr(uint instr)
    {
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        uint aligned = addr & ~3u;
        uint word = Bus.Read32(aligned);
        int shift = (int)(addr & 3);
        uint val = (word & ~(0xFFFFFFFF << (shift * 8))) | (Gpr[Rt(instr)] << (shift * 8));
        Bus.Write32(aligned, val);
    }

    void Ll(uint instr) => Lw(instr); // LL acts like LW for HLE
    void Sc(uint instr)
    {
        Sw(instr);
        uint rt = Rt(instr);
        if (rt != 0) Gpr[rt] = 1; // always succeeds in HLE
    }

    // ── COP0 ──

    void ExecCop0(uint instr)
    {
        uint rs = Rs(instr);
        switch (rs)
        {
            case 0x00: // MFC0
            {
                uint rt = Rt(instr), rd = Rd(instr);
                if (rt != 0) Gpr[rt] = Cop0[rd];
                break;
            }
            case 0x04: // MTC0
            {
                uint rt = Rt(instr), rd = Rd(instr);
                Cop0[rd] = Gpr[rt];
                break;
            }
            case 0x10: // CO functions
            {
                uint fn = Funct(instr);
                if (fn == 0x18) // ERET
                {
                    Pc = Cop0[14]; // EPC
                    Cop0[12] &= ~2u; // clear EXL
                }
                break;
            }
        }
    }

    // ── COP1 (FPU) ──

    void ExecCop1(uint instr)
    {
        uint rs = Rs(instr);
        switch (rs)
        {
            case 0x00: // MFC1
            {
                uint rt = Rt(instr), fs = Rd(instr);
                if (rt != 0) Gpr[rt] = BitConverter.SingleToUInt32Bits(Fpr[fs]);
                break;
            }
            case 0x02: // CFC1
            {
                uint rt = Rt(instr), fs = Rd(instr);
                if (rt != 0) Gpr[rt] = fs == 31 ? FpuCcr : 0;
                break;
            }
            case 0x04: // MTC1
            {
                uint fs = Rd(instr);
                Fpr[fs] = BitConverter.UInt32BitsToSingle(Gpr[Rt(instr)]);
                break;
            }
            case 0x06: // CTC1
            {
                uint fs = Rd(instr);
                if (fs == 31) FpuCcr = Gpr[Rt(instr)];
                break;
            }
            case 0x08: // BC1F/BC1T
            {
                bool cc = (FpuCcr & (1 << 23)) != 0;
                bool tf = (Rt(instr) & 1) != 0;
                uint target = Pc + (uint)(Imm16(instr) << 2);
                bool likely = (Rt(instr) & 2) != 0;
                bool cond = tf ? cc : !cc;
                if (likely) DoBranchLikely(cond, target);
                else if (cond) DoBranch(target);
                break;
            }
            case 0x10: // S format (single precision)
                ExecFpuS(instr);
                break;
            case 0x14: // W format (word/integer)
                ExecFpuW(instr);
                break;
            default:
                Log.Warn(LogCat.FPU, $"COP1 rs={rs:X2} at {Pc - 4:X8}");
                break;
        }
    }

    void ExecFpuS(uint instr)
    {
        uint fn = Funct(instr);
        uint fd = Sa(instr), fs = Rd(instr), ft = Rt(instr);
        switch (fn)
        {
            case 0x00: Fpr[fd] = Fpr[fs] + Fpr[ft]; break; // ADD.S
            case 0x01: Fpr[fd] = Fpr[fs] - Fpr[ft]; break; // SUB.S
            case 0x02: Fpr[fd] = Fpr[fs] * Fpr[ft]; break; // MUL.S
            case 0x03: // DIV.S
                Fpr[fd] = Fpr[ft] != 0 ? Fpr[fs] / Fpr[ft] : 0;
                break;
            case 0x04: Fpr[fd] = MathF.Sqrt(Fpr[fs]); break; // SQRT.S
            case 0x05: Fpr[fd] = MathF.Abs(Fpr[fs]); break;  // ABS.S
            case 0x06: Fpr[fd] = Fpr[fs]; break;              // MOV.S
            case 0x07: Fpr[fd] = -Fpr[fs]; break;             // NEG.S
            case 0x0C: // ROUND.W.S
                Fpr[fd] = BitConverter.UInt32BitsToSingle((uint)(int)MathF.Round(Fpr[fs]));
                break;
            case 0x0D: // TRUNC.W.S
                Fpr[fd] = BitConverter.UInt32BitsToSingle((uint)(int)MathF.Truncate(Fpr[fs]));
                break;
            case 0x0E: // CEIL.W.S
                Fpr[fd] = BitConverter.UInt32BitsToSingle((uint)(int)MathF.Ceiling(Fpr[fs]));
                break;
            case 0x0F: // FLOOR.W.S
                Fpr[fd] = BitConverter.UInt32BitsToSingle((uint)(int)MathF.Floor(Fpr[fs]));
                break;
            case 0x24: // CVT.W.S
                Fpr[fd] = BitConverter.UInt32BitsToSingle((uint)(int)Fpr[fs]);
                break;
            case >= 0x30 and <= 0x3F: // C.cond.S
            {
                bool unordered = float.IsNaN(Fpr[fs]) || float.IsNaN(Fpr[ft]);
                bool less = !unordered && Fpr[fs] < Fpr[ft];
                bool equal = !unordered && Fpr[fs] == Fpr[ft];
                uint cond = fn & 0xF;
                bool result = ((cond & 1) != 0 && unordered) ||
                              ((cond & 2) != 0 && equal) ||
                              ((cond & 4) != 0 && less);
                if (result) FpuCcr |= 1u << 23;
                else FpuCcr &= ~(1u << 23);
                break;
            }
            default:
                Log.Warn(LogCat.FPU, $"FPU.S fn={fn:X2} at {Pc - 4:X8}");
                break;
        }
    }

    void ExecFpuW(uint instr)
    {
        uint fn = Funct(instr);
        uint fd = Sa(instr), fs = Rd(instr);
        switch (fn)
        {
            case 0x20: // CVT.S.W
                Fpr[fd] = (float)(int)BitConverter.SingleToUInt32Bits(Fpr[fs]);
                break;
            default:
                Log.Warn(LogCat.FPU, $"FPU.W fn={fn:X2} at {Pc - 4:X8}");
                break;
        }
    }

    // COP1 load/store
    void Lwc1(uint instr)
    {
        uint ft = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        Fpr[ft] = BitConverter.UInt32BitsToSingle(Bus.Read32(addr));
    }

    void Swc1(uint instr)
    {
        uint ft = Rt(instr);
        uint addr = (uint)((int)Gpr[Rs(instr)] + Imm16(instr));
        Bus.Write32(addr, BitConverter.SingleToUInt32Bits(Fpr[ft]));
    }
}
