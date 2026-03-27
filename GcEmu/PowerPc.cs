using System.Runtime.CompilerServices;

namespace GcEmu;

unsafe class PowerPc
{
    public uint[] GPR = new uint[32];
    public double[] FPR = new double[32];
    public double[] PS1 = new double[32];

    public uint PC;
    public uint NPC;
    public uint LR;
    public uint CTR;
    public uint XER;
    public uint CR;
    public uint FPSCR;
    public uint MSR;

    public uint[] SRR = new uint[2];
    public uint[] SPRG = new uint[4];
    public uint DSISR, DAR;
    public uint DEC;
    public uint[] IBAT = new uint[8];
    public uint[] DBAT = new uint[8];
    public uint[] GQR = new uint[8];

    public uint HID0, HID1, HID2;
    public uint L2CR;
    public uint MMCR0, MMCR1, PMC1, PMC2, PMC3, PMC4, SIA, UMMCR0, UMMCR1, UPMC1, UPMC2, UPMC3, UPMC4, USIA;
    public uint IABR, DABR;
    public uint TBL, TBU;

    GcBus _bus = null!;
    public long TotalCycles;
    bool _decPending;

    public void Init(GcBus bus) => _bus = bus;

    public void Reset()
    {
        Array.Clear(GPR);
        Array.Clear(FPR);
        Array.Clear(PS1);
        PC = 0xFFF00100;
        NPC = PC + 4;
        LR = 0; CTR = 0; XER = 0; CR = 0; FPSCR = 0;
        MSR = 0x00000040;
        Array.Clear(SRR);
        Array.Clear(SPRG);
        DSISR = 0; DAR = 0;
        DEC = 0;
        Array.Clear(IBAT);
        Array.Clear(DBAT);
        Array.Clear(GQR);
        HID0 = 0; HID1 = 0x80000000; HID2 = 0;
        L2CR = 0;
        TBL = 0; TBU = 0;
        TotalCycles = 0;
        _decPending = false;
        _tbDivider = 0;
    }

    public void SetPC(uint addr) { PC = addr; NPC = addr + 4; }

    int _tbDivider;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Step()
    {
        TotalCycles++;

        if (++_tbDivider >= 12)
        {
            _tbDivider = 0;
            TBL++;
            if (TBL == 0) TBU++;

            uint oldDec = DEC;
            DEC--;
            if ((oldDec & 0x80000000) == 0 && (DEC & 0x80000000) != 0)
                _decPending = true;
        }

        if (PC == 0x8000C354 && !HaltDetected)
        {
            HaltDetected = true;
            HaltLR = LR;
            HaltSP = GPR[1];
            Array.Copy(GPR, HaltGPR, 32);
            Console.Error.WriteLine("  PC history leading to halt (last 50):");
            for (int h = 462; h < 512; h++)
            {
                int idx = (_pcHistIdx + h) & 511;
                uint hpc = _pcHistory[idx];
                if (hpc == 0) continue;
                Console.Error.WriteLine($"    0x{hpc:X8}  instr=0x{_bus.Read32(hpc):X8}");
            }
            Console.Error.WriteLine("  Code at 0x8000EC20-0x8000EC40:");
            for (uint a = 0x8000EC20; a <= 0x8000EC40; a += 4)
                Console.Error.WriteLine($"    0x{a:X8}: 0x{_bus.Read32(a):X8}");
            Console.Error.WriteLine("  Code at 0x8000F240-0x8000F260:");
            for (uint a = 0x8000F240; a <= 0x8000F260; a += 4)
                Console.Error.WriteLine($"    0x{a:X8}: 0x{_bus.Read32(a):X8}");
            Console.Error.WriteLine("  Code at 0x8000C340-0x8000C370:");
            for (uint a = 0x8000C340; a <= 0x8000C370; a += 4)
                Console.Error.WriteLine($"    0x{a:X8}: 0x{_bus.Read32(a):X8}");
        }
        

        if (_pcHistory != null)
        {
            _pcHistory[_pcHistIdx] = PC;
            _pcHistIdx = (_pcHistIdx + 1) & 511;
        }
        if (PC == 0x800033A8 && _vecWatchCount < 10)
        {
            _vecWatchCount++;
            Console.Error.WriteLine($"  memset wrapper entry #{_vecWatchCount}: LR=0x{LR:X8} r1=0x{GPR[1]:X8} r3=0x{GPR[3]:X8} r4=0x{GPR[4]:X8} r5=0x{GPR[5]:X8}");
            Console.Error.WriteLine($"    dest=0x{GPR[3]:X8} fill=0x{GPR[4]:X8} size=0x{GPR[5]:X8} end=0x{GPR[3]+GPR[5]:X8}");
            Console.Error.WriteLine($"    r2=0x{GPR[2]:X8} r13=0x{GPR[13]:X8} MSR=0x{MSR:X8}");
            if (GPR[3] < 0x80000000 || GPR[3] > 0x81800000)
                Console.Error.WriteLine($"    WARNING: dest 0x{GPR[3]:X8} is outside valid virtual RAM range!");
        }

        uint instr = _bus.Read32(PC);
        NPC = PC + 4;
        Execute(instr);

        if (NPC == 0 && PC > 0x10 && _badJumpCount < 5)
        {
            _badJumpCount++;
            Console.Error.WriteLine($"BAD JUMP #{_badJumpCount}: PC=0x{PC:X8} instr=0x{instr:X8} -> NPC=0 LR=0x{LR:X8} CTR=0x{CTR:X8} MSR=0x{MSR:X8}");
            Console.Error.WriteLine($"  r0=0x{GPR[0]:X8} r1=0x{GPR[1]:X8} r3=0x{GPR[3]:X8} r12=0x{GPR[12]:X8}");
            Console.Error.WriteLine("  Code (0x80003380-0x80003400):");
            for (uint a = 0x80003380; a <= 0x80003400; a += 4)
                Console.Error.WriteLine($"    0x{a:X8}: 0x{_bus.Read32(a):X8}{(a == PC ? " <--" : "")}");
            Console.Error.WriteLine("  PC history (last 30, filtered):");
            int shown = 0;
            for (int h = 482; h < 512 && shown < 30; h++)
            {
                int idx = (_pcHistIdx + h) & 511;
                uint hpc = _pcHistory[idx];
                if (hpc == 0) continue;
                Console.Error.WriteLine($"    {shown}: 0x{hpc:X8}");
                shown++;
            }
        }

        PC = NPC;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleInterrupts()
    {
        if ((MSR & 0x8000) == 0) return;

        if (_bus.Pi.InterruptPending())
        {
            Exception(0x0500);
            return;
        }

        if (_decPending)
        {
            _decPending = false;
            DecInterruptCount++;
            Exception(0x0900);
        }
    }

    static int _exceptionTraceCount;
    static int _badJumpCount;
    static int _vecWatchCount;
    static int _nullCallCount;
    public int ViInterruptCount;
    public int DecInterruptCount;
    uint[] _pcHistory = new uint[512];
    int _pcHistIdx;
    public bool HaltDetected;
    public uint HaltLR, HaltSP;
    public uint[] HaltGPR = new uint[32];
    void Exception(uint vector)
    {
        SRR[0] = PC;
        SRR[1] = MSR & 0x87C0FFFFu;
        MSR = (MSR & ~(uint)0x04EF36) | ((MSR & 0x10000) != 0 ? 1u : 0u);
        if (vector == 0x0500 && (_bus.Pi.IntSr & (1u << 8)) != 0) ViInterruptCount++;
        if (_exceptionTraceCount < 40)
        {
            string extra = vector == 0x0500
                ? $" PI INTSR=0x{_bus.Pi.IntSr:X8} INTMR=0x{_bus.Pi.IntMr:X8}"
                : "";
            Console.Error.WriteLine($"  EXC 0x{vector:X4} from PC=0x{PC:X8} MSR=0x{SRR[1]:X8} -> handler=0x{_bus.Read32(vector):X8}{extra}");
            _exceptionTraceCount++;
        }
        PC = vector;
        NPC = PC + 4;
    }

    void Execute(uint i)
    {
        uint op = i >> 26;
        switch (op)
        {
            case 4: Op4(i); break;
            case 7: Mulli(i); break;
            case 8: Subfic(i); break;
            case 10: Cmpli(i); break;
            case 11: Cmpi(i); break;
            case 12: Addic(i, false); break;
            case 13: Addic(i, true); break;
            case 14: Addi(i); break;
            case 15: Addis(i); break;
            case 16: Bc(i); break;
            case 17: Sc(i); break;
            case 18: BranchI(i); break;
            case 19: Op19(i); break;
            case 20: Rlwimi(i); break;
            case 21: Rlwinm(i); break;
            case 23: Rlwnm(i); break;
            case 24: Ori(i); break;
            case 25: Oris(i); break;
            case 26: Xori(i); break;
            case 27: Xoris(i); break;
            case 28: AndiDot(i); break;
            case 29: AndisDot(i); break;
            case 31: Op31(i); break;
            case 32: Lwz(i); break;
            case 33: Lwzu(i); break;
            case 34: Lbz(i); break;
            case 35: Lbzu(i); break;
            case 36: Stw(i); break;
            case 37: Stwu(i); break;
            case 38: Stb(i); break;
            case 39: Stbu(i); break;
            case 40: Lhz(i); break;
            case 41: Lhzu(i); break;
            case 42: Lha(i); break;
            case 43: Lhau(i); break;
            case 44: Sth(i); break;
            case 45: Sthu(i); break;
            case 46: Lmw(i); break;
            case 47: Stmw(i); break;
            case 48: Lfs(i); break;
            case 49: Lfsu(i); break;
            case 50: Lfd(i); break;
            case 51: Lfdu(i); break;
            case 52: Stfs(i); break;
            case 53: Stfsu(i); break;
            case 54: Stfd(i); break;
            case 55: Stfdu(i); break;
            case 56: PsqL(i); break;
            case 57: PsqLu(i); break;
            case 59: Op59(i); break;
            case 60: PsqSt(i); break;
            case 61: PsqStu(i); break;
            case 63: Op63(i); break;
            default: break;
        }
    }

    // ── Integer Arithmetic ──────────────────────────────────────────────

    void Addi(uint i) { int rD = D(i); int rA = A(i); int simm = SIMM(i); GPR[rD] = rA == 0 ? (uint)simm : (uint)((int)GPR[rA] + simm); }
    void Addis(uint i) { int rD = D(i); int rA = A(i); int simm = SIMM(i); GPR[rD] = rA == 0 ? (uint)(simm << 16) : GPR[rA] + (uint)(simm << 16); }
    void Mulli(uint i) { GPR[D(i)] = (uint)((int)GPR[A(i)] * SIMM(i)); }
    void Subfic(uint i) { int rD = D(i); long r = (long)SIMM(i) - (long)GPR[A(i)]; GPR[rD] = (uint)r; SetCA(r >= 0 ? ((ulong)(uint)SIMM(i) >= GPR[A(i)]) : false); /* simplified CA */ }

    void Addic(uint i, bool rc)
    {
        int rD = D(i); uint a = GPR[A(i)]; uint imm = (uint)SIMM(i);
        ulong r = (ulong)a + imm; GPR[rD] = (uint)r;
        SetCA(r > 0xFFFFFFFF);
        if (rc) UpdateCR0(GPR[rD]);
    }

    void Cmpli(uint i) { int crfD = (int)((i >> 21) & 7); uint a = GPR[A(i)]; uint uimm = (uint)(ushort)i; SetCRField(crfD, CompareCRu(a, uimm)); }
    void Cmpi(uint i) { int crfD = (int)((i >> 21) & 7); int a = (int)GPR[A(i)]; int simm = SIMM(i); SetCRField(crfD, CompareCR(a, simm)); }

    void Ori(uint i) { GPR[A(i)] = GPR[D(i)] | (uint)(ushort)i; }
    void Oris(uint i) { GPR[A(i)] = GPR[D(i)] | ((uint)(ushort)i << 16); }
    void Xori(uint i) { GPR[A(i)] = GPR[D(i)] ^ (uint)(ushort)i; }
    void Xoris(uint i) { GPR[A(i)] = GPR[D(i)] ^ ((uint)(ushort)i << 16); }
    void AndiDot(uint i) { GPR[A(i)] = GPR[D(i)] & (uint)(ushort)i; UpdateCR0(GPR[A(i)]); }
    void AndisDot(uint i) { GPR[A(i)] = GPR[D(i)] & ((uint)(ushort)i << 16); UpdateCR0(GPR[A(i)]); }

    // ── Rotate ──────────────────────────────────────────────────────────

    void Rlwinm(uint i)
    {
        int rA = A(i); uint rS = GPR[D(i)];
        int sh = B(i); int mb = (int)((i >> 6) & 0x1F); int me = (int)((i >> 1) & 0x1F);
        uint r = RotL(rS, sh); uint m = Mask(mb, me);
        GPR[rA] = r & m;
        if ((i & 1) != 0) UpdateCR0(GPR[rA]);
    }

    void Rlwimi(uint i)
    {
        int rA = A(i); uint rS = GPR[D(i)];
        int sh = B(i); int mb = (int)((i >> 6) & 0x1F); int me = (int)((i >> 1) & 0x1F);
        uint r = RotL(rS, sh); uint m = Mask(mb, me);
        GPR[rA] = (r & m) | (GPR[rA] & ~m);
        if ((i & 1) != 0) UpdateCR0(GPR[rA]);
    }

    void Rlwnm(uint i)
    {
        int rA = A(i); uint rS = GPR[D(i)]; int sh = (int)(GPR[B(i)] & 0x1F);
        int mb = (int)((i >> 6) & 0x1F); int me = (int)((i >> 1) & 0x1F);
        uint r = RotL(rS, sh); uint m = Mask(mb, me);
        GPR[rA] = r & m;
        if ((i & 1) != 0) UpdateCR0(GPR[rA]);
    }

    // ── Branch ──────────────────────────────────────────────────────────

    void BranchI(uint i)
    {
        int li = (int)(i & 0x03FFFFFC);
        if ((li & 0x02000000) != 0) li |= unchecked((int)0xFC000000);
        if ((i & 1) != 0) LR = PC + 4;
        NPC = (i & 2) != 0 ? (uint)li : (uint)((int)PC + li);
    }

    void Bc(uint i)
    {
        int bo = (int)((i >> 21) & 0x1F);
        int bi = (int)((i >> 16) & 0x1F);
        int bd = (int)(short)(i & 0xFFFC);

        if ((bo & 4) == 0) CTR--;
        bool ctrOk = ((bo & 4) != 0) || (((bo & 2) != 0) == (CTR == 0));
        bool condOk = ((bo & 16) != 0) || (((bo & 8) != 0) == (((CR >> (31 - bi)) & 1) != 0));

        if (ctrOk && condOk)
        {
            if ((i & 1) != 0) LR = PC + 4;
            NPC = (i & 2) != 0 ? (uint)bd : (uint)((int)PC + bd);
        }
    }

    void Sc(uint i) { SRR[0] = NPC; SRR[1] = MSR & 0x87C0FFFFu; MSR = (MSR & ~(uint)0x04EF36) | ((MSR & 0x10000) != 0 ? 1u : 0u); if (_exceptionTraceCount < 20) { Console.Error.WriteLine($"  EXC 0x0C00 (SC) from PC=0x{PC:X8} MSR=0x{SRR[1]:X8}"); _exceptionTraceCount++; } PC = 0x0C00; NPC = 0x0C04; }

    // ── Opcode 19 (CR ops, bclr, bcctr, rfi, isync) ────────────────────

    void Op19(uint i)
    {
        uint xo = (i >> 1) & 0x3FF;
        switch (xo)
        {
            case 16: Bclr(i); break;
            case 528: Bcctr(i); break;
            case 50: Rfi(i); break;
            case 150: break;
            case 0: Mcrf(i); break;
            case 33: Crnor(i); break;
            case 129: Crandc(i); break;
            case 193: Crxor(i); break;
            case 225: Crnand(i); break;
            case 257: Crand(i); break;
            case 289: Creqv(i); break;
            case 417: Crorc(i); break;
            case 449: Cror(i); break;
        }
    }

    void Bclr(uint i)
    {
        int bo = (int)((i >> 21) & 0x1F); int bi = (int)((i >> 16) & 0x1F);
        if ((bo & 4) == 0) CTR--;
        bool ctrOk = ((bo & 4) != 0) || (((bo & 2) != 0) == (CTR == 0));
        bool condOk = ((bo & 16) != 0) || (((bo & 8) != 0) == (((CR >> (31 - bi)) & 1) != 0));
        if (ctrOk && condOk)
        {
            uint target = LR & 0xFFFFFFFC;
            if ((i & 1) != 0)
            {
                LR = PC + 4;
            }
            NPC = target;
        }
    }

    void Bcctr(uint i)
    {
        int bo = (int)((i >> 21) & 0x1F); int bi = (int)((i >> 16) & 0x1F);
        bool condOk = ((bo & 16) != 0) || (((bo & 8) != 0) == (((CR >> (31 - bi)) & 1) != 0));
        if (condOk)
        {
            uint target = CTR & 0xFFFFFFFC;
            if ((i & 1) != 0)
            {
                LR = PC + 4;
            }
            NPC = target;
        }
    }

    void Rfi(uint i) { const uint mask = 0x87C0FFFFu; MSR = (MSR & ~mask) | (SRR[1] & mask); MSR &= 0xFFFBFFFFu; NPC = SRR[0]; }

    void Mcrf(uint i) { int crfD = (int)((i >> 23) & 7); int crfS = (int)((i >> 18) & 7); uint v = (CR >> (28 - crfS * 4)) & 0xF; CR = (CR & ~(0xFu << (28 - crfD * 4))) | (v << (28 - crfD * 4)); }
    void Crand(uint i)  { CrBitOp(i, (a, b) => a & b); }
    void Crandc(uint i) { CrBitOp(i, (a, b) => a & ~b); }
    void Creqv(uint i)  { CrBitOp(i, (a, b) => ~(a ^ b)); }
    void Crnand(uint i) { CrBitOp(i, (a, b) => ~(a & b)); }
    void Crnor(uint i)  { CrBitOp(i, (a, b) => ~(a | b)); }
    void Cror(uint i)   { CrBitOp(i, (a, b) => a | b); }
    void Crorc(uint i)  { CrBitOp(i, (a, b) => a | ~b); }
    void Crxor(uint i)  { CrBitOp(i, (a, b) => a ^ b); }

    void CrBitOp(uint i, Func<uint, uint, uint> fn)
    {
        int crbD = (int)((i >> 21) & 0x1F); int crbA = (int)((i >> 16) & 0x1F); int crbB = (int)((i >> 11) & 0x1F);
        uint a = (CR >> (31 - crbA)) & 1; uint b = (CR >> (31 - crbB)) & 1;
        uint result = fn(a, b) & 1;
        CR = (CR & ~(1u << (31 - crbD))) | (result << (31 - crbD));
    }

    // ── Opcode 31 ───────────────────────────────────────────────────────

    void Op31(uint i)
    {
        uint xo = (i >> 1) & 0x3FF;
        switch (xo)
        {
            case 0: Cmp(i); break;
            case 4: Tw(i); break;
            case 8: Subfc(i); break;
            case 10: Addc(i); break;
            case 11: Mulhwu(i); break;
            case 19: Mfcr(i); break;
            case 20: Lwarx(i); break;
            case 23: Lwzx(i); break;
            case 24: Slw(i); break;
            case 26: Cntlzw(i); break;
            case 28: And(i); break;
            case 32: Cmpl(i); break;
            case 40: Subf(i); break;
            case 54: Dcbst(i); break;
            case 55: Lwzux(i); break;
            case 60: Andc(i); break;
            case 75: Mulhw(i); break;
            case 83: Mfmsr(i); break;
            case 86: Dcbf(i); break;
            case 87: Lbzx(i); break;
            case 104: Neg(i); break;
            case 119: Lbzux(i); break;
            case 124: Nor(i); break;
            case 136: Subfe(i); break;
            case 138: Adde(i); break;
            case 144: Mtcrf(i); break;
            case 146: Mtmsr(i); break;
            case 150: Stwcx(i); break;
            case 151: Stwx(i); break;
            case 183: Stwux(i); break;
            case 200: Subfze(i); break;
            case 202: Addze(i); break;
            case 210: Mtsr(i); break;
            case 215: Stbx(i); break;
            case 232: Subfme(i); break;
            case 234: Addme(i); break;
            case 235: Mullw(i); break;
            case 246: Dcbtst(i); break;
            case 247: Stbux(i); break;
            case 266: Add(i); break;
            case 278: Dcbt(i); break;
            case 279: Lhzx(i); break;
            case 284: Eqv(i); break;
            case 311: Lhzux(i); break;
            case 316: Xor(i); break;
            case 339: Mfspr(i); break;
            case 343: Lhax(i); break;
            case 371: Mftb(i); break;
            case 375: Lhaux(i); break;
            case 407: Sthx(i); break;
            case 412: Orc(i); break;
            case 439: Sthux(i); break;
            case 444: Or(i); break;
            case 459: Divwu(i); break;
            case 467: Mtspr(i); break;
            case 470: Dcbi(i); break;
            case 476: Nand(i); break;
            case 491: Divw(i); break;
            case 512: Mcrxr(i); break;
            case 534: Lwbrx(i); break;
            case 535: Lfsx(i); break;
            case 536: Srw(i); break;
            case 567: Lfsux(i); break;
            case 595: Mfsr(i); break;
            case 597: Lswi(i); break;
            case 598: Sync(i); break;
            case 599: Lfdx(i); break;
            case 631: Lfdux(i); break;
            case 662: Stwbrx(i); break;
            case 663: Stfsx(i); break;
            case 695: Stfsux(i); break;
            case 725: Stswi(i); break;
            case 727: Stfdx(i); break;
            case 759: Stfdux(i); break;
            case 790: Lhbrx(i); break;
            case 792: Sraw(i); break;
            case 824: Srawi(i); break;
            case 854: Eieio(i); break;
            case 918: Sthbrx(i); break;
            case 922: Extsh(i); break;
            case 954: Extsb(i); break;
            case 982: Icbi(i); break;
            case 1014: Dcbz(i); break;
        }
    }

    void Cmp(uint i) { int crfD = (int)((i >> 23) & 7); SetCRField(crfD, CompareCR((int)GPR[A(i)], (int)GPR[B(i)])); }
    void Cmpl(uint i) { int crfD = (int)((i >> 23) & 7); SetCRField(crfD, CompareCRu(GPR[A(i)], GPR[B(i)])); }
    void Tw(uint i) { }
    void Subfc(uint i) { int rD = D(i); ulong r = (ulong)GPR[B(i)] + (ulong)~GPR[A(i)] + 1; GPR[rD] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[rD]); }
    void Addc(uint i) { int rD = D(i); ulong r = (ulong)GPR[A(i)] + GPR[B(i)]; GPR[rD] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[rD]); }
    void Mulhwu(uint i) { GPR[D(i)] = (uint)(((ulong)GPR[A(i)] * GPR[B(i)]) >> 32); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Mulhw(uint i) { GPR[D(i)] = (uint)(((long)(int)GPR[A(i)] * (int)GPR[B(i)]) >> 32); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Mfcr(uint i) { GPR[D(i)] = CR; }
    void Lwarx(uint i) { uint ea = EaX(i); GPR[D(i)] = _bus.Read32(ea); _reserveAddr = ea; _reserveValid = true; }
    uint _reserveAddr; bool _reserveValid;
    void Stwcx(uint i) { uint ea = EaX(i); if (_reserveValid && _reserveAddr == ea) { _bus.Write32(ea, GPR[D(i)]); CR = (CR & 0x0FFFFFFF) | 0x20000000; if ((XER & 0x80000000) != 0) CR |= 0x10000000; } else { CR = (CR & 0x0FFFFFFF); if ((XER & 0x80000000) != 0) CR |= 0x10000000; } _reserveValid = false; }
    void Lwzx(uint i) { GPR[D(i)] = _bus.Read32(EaX(i)); }
    void Lwzux(uint i) { uint ea = EaX(i); GPR[D(i)] = _bus.Read32(ea); GPR[A(i)] = ea; }
    void Lbzx(uint i) { GPR[D(i)] = _bus.Read8(EaX(i)); }
    void Lbzux(uint i) { uint ea = EaX(i); GPR[D(i)] = _bus.Read8(ea); GPR[A(i)] = ea; }
    void Lhzx(uint i) { GPR[D(i)] = _bus.Read16(EaX(i)); }
    void Lhzux(uint i) { uint ea = EaX(i); GPR[D(i)] = _bus.Read16(ea); GPR[A(i)] = ea; }
    void Lhax(uint i) { GPR[D(i)] = (uint)(int)(short)_bus.Read16(EaX(i)); }
    void Lhaux(uint i) { uint ea = EaX(i); GPR[D(i)] = (uint)(int)(short)_bus.Read16(ea); GPR[A(i)] = ea; }
    void Stwx(uint i) { _bus.Write32(EaX(i), GPR[D(i)]); }
    void Stwux(uint i) { uint ea = EaX(i); _bus.Write32(ea, GPR[D(i)]); GPR[A(i)] = ea; }
    void Stbx(uint i) { _bus.Write8(EaX(i), (byte)GPR[D(i)]); }
    void Stbux(uint i) { uint ea = EaX(i); _bus.Write8(ea, (byte)GPR[D(i)]); GPR[A(i)] = ea; }
    void Sthx(uint i) { _bus.Write16(EaX(i), (ushort)GPR[D(i)]); }
    void Sthux(uint i) { uint ea = EaX(i); _bus.Write16(ea, (ushort)GPR[D(i)]); GPR[A(i)] = ea; }
    void Slw(uint i) { int sh = (int)(GPR[B(i)] & 0x3F); GPR[A(i)] = sh >= 32 ? 0 : GPR[D(i)] << sh; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Srw(uint i) { int sh = (int)(GPR[B(i)] & 0x3F); GPR[A(i)] = sh >= 32 ? 0 : GPR[D(i)] >> sh; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Sraw(uint i) { int sh = (int)(GPR[B(i)] & 0x3F); int val = (int)GPR[D(i)]; if (sh >= 32) { GPR[A(i)] = (uint)(val >> 31); SetCA(val < 0); } else { GPR[A(i)] = (uint)(val >> sh); SetCA(val < 0 && (val & ((1 << sh) - 1)) != 0); } if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Srawi(uint i) { int sh = B(i); int val = (int)GPR[D(i)]; GPR[A(i)] = (uint)(val >> sh); SetCA(val < 0 && (val & ((1 << sh) - 1)) != 0); if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Cntlzw(uint i) { uint v = GPR[D(i)]; int c = 0; while (c < 32 && ((v >> (31 - c)) & 1) == 0) c++; GPR[A(i)] = (uint)c; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void And(uint i) { GPR[A(i)] = GPR[D(i)] & GPR[B(i)]; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Andc(uint i) { GPR[A(i)] = GPR[D(i)] & ~GPR[B(i)]; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Or(uint i) { GPR[A(i)] = GPR[D(i)] | GPR[B(i)]; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Orc(uint i) { GPR[A(i)] = GPR[D(i)] | ~GPR[B(i)]; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Xor(uint i) { GPR[A(i)] = GPR[D(i)] ^ GPR[B(i)]; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Nand(uint i) { GPR[A(i)] = ~(GPR[D(i)] & GPR[B(i)]); if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Nor(uint i) { GPR[A(i)] = ~(GPR[D(i)] | GPR[B(i)]); if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Eqv(uint i) { GPR[A(i)] = ~(GPR[D(i)] ^ GPR[B(i)]); if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Subf(uint i) { GPR[D(i)] = GPR[B(i)] - GPR[A(i)]; if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Neg(uint i) { GPR[D(i)] = (uint)(-(int)GPR[A(i)]); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Subfe(uint i) { ulong r = (ulong)GPR[B(i)] + (ulong)~GPR[A(i)] + (GetCA() ? 1u : 0u); GPR[D(i)] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Adde(uint i) { ulong r = (ulong)GPR[A(i)] + GPR[B(i)] + (GetCA() ? 1u : 0u); GPR[D(i)] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Subfze(uint i) { ulong r = (ulong)~GPR[A(i)] + (GetCA() ? 1u : 0u); GPR[D(i)] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Addze(uint i) { ulong r = (ulong)GPR[A(i)] + (GetCA() ? 1u : 0u); GPR[D(i)] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Subfme(uint i) { ulong r = (ulong)~GPR[A(i)] + 0xFFFFFFFF + (GetCA() ? 1u : 0u); GPR[D(i)] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Addme(uint i) { ulong r = (ulong)GPR[A(i)] + 0xFFFFFFFF + (GetCA() ? 1u : 0u); GPR[D(i)] = (uint)r; SetCA(r > 0xFFFFFFFF); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Add(uint i) { GPR[D(i)] = GPR[A(i)] + GPR[B(i)]; if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Mullw(uint i) { GPR[D(i)] = (uint)((int)GPR[A(i)] * (int)GPR[B(i)]); if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Divwu(uint i) { uint a = GPR[A(i)], b = GPR[B(i)]; GPR[D(i)] = b != 0 ? a / b : 0; if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Divw(uint i) { int a = (int)GPR[A(i)], b = (int)GPR[B(i)]; GPR[D(i)] = b != 0 && !(a == int.MinValue && b == -1) ? (uint)(a / b) : 0; if ((i & 1) != 0) UpdateCR0(GPR[D(i)]); }
    void Extsh(uint i) { GPR[A(i)] = (uint)(int)(short)GPR[D(i)]; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }
    void Extsb(uint i) { GPR[A(i)] = (uint)(int)(sbyte)GPR[D(i)]; if ((i & 1) != 0) UpdateCR0(GPR[A(i)]); }

    void Mtcrf(uint i) { uint crm = (i >> 12) & 0xFF; uint val = GPR[D(i)]; for (int f = 0; f < 8; f++) { if ((crm & (1 << (7 - f))) != 0) { uint nibble = (val >> (28 - f * 4)) & 0xF; CR = (CR & ~(0xFu << (28 - f * 4))) | (nibble << (28 - f * 4)); } } }
    void Mfmsr(uint i) { GPR[D(i)] = MSR; }
    static int _mtmsrLog;
    void Mtmsr(uint i) { uint old = MSR; MSR = GPR[D(i)]; if (_mtmsrLog < 10 && MSR != old && (old ^ MSR) >= 0x8000) { int ee = (MSR & 0x8000) != 0 ? 1 : 0; Console.Error.WriteLine($"  mtmsr @ 0x{PC:X8}: 0x{old:X8} -> 0x{MSR:X8} EE={ee}"); _mtmsrLog++; } }
    void Mcrxr(uint i) { int crfD = (int)((i >> 23) & 7); uint bits = (XER >> 28) & 0xF; SetCRField(crfD, (int)bits); XER &= 0x0FFFFFFF; }

    void Lwbrx(uint i) { uint v = _bus.Read32(EaX(i)); GPR[D(i)] = (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24); }
    void Stwbrx(uint i) { uint v = GPR[D(i)]; _bus.Write32(EaX(i), (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24)); }
    void Lhbrx(uint i) { ushort v = _bus.Read16(EaX(i)); GPR[D(i)] = (uint)((v >> 8) | (v << 8)); }
    void Sthbrx(uint i) { ushort v = (ushort)GPR[D(i)]; _bus.Write16(EaX(i), (ushort)((v >> 8) | (v << 8))); }

    void Lmw(uint i) { int rD = D(i); uint ea = EaD(i); for (int r = rD; r < 32; r++) { GPR[r] = _bus.Read32(ea); ea += 4; } }
    void Stmw(uint i) { int rS = D(i); uint ea = EaD(i); for (int r = rS; r < 32; r++) { _bus.Write32(ea, GPR[r]); ea += 4; } }
    void Lswi(uint i) { int rD = D(i); int nb = B(i); if (nb == 0) nb = 32; uint ea = A(i) == 0 ? 0 : GPR[A(i)]; int r = rD; int shift = 24; GPR[r] = 0; for (int n = 0; n < nb; n++) { if (shift < 0) { r = (r + 1) & 31; GPR[r] = 0; shift = 24; } GPR[r] |= (uint)_bus.Read8(ea) << shift; ea++; shift -= 8; } }
    void Stswi(uint i) { int rS = D(i); int nb = B(i); if (nb == 0) nb = 32; uint ea = A(i) == 0 ? 0 : GPR[A(i)]; int r = rS; int shift = 24; for (int n = 0; n < nb; n++) { if (shift < 0) { r = (r + 1) & 31; shift = 24; } _bus.Write8(ea, (byte)(GPR[r] >> shift)); ea++; shift -= 8; } }

    void Dcbf(uint i) { }
    void Dcbst(uint i) { }
    void Dcbt(uint i) { }
    void Dcbtst(uint i) { }
    void Dcbi(uint i) { }
    void Dcbz(uint i) { uint ea = EaX(i) & ~31u; for (int j = 0; j < 32; j += 4) _bus.Write32(ea + (uint)j, 0); }
    void Icbi(uint i) { }
    void Sync(uint i) { }
    void Eieio(uint i) { }
    void Mtsr(uint i) { }
    void Mfsr(uint i) { GPR[D(i)] = 0; }

    // ── SPR access ──────────────────────────────────────────────────────

    void Mfspr(uint i)
    {
        int spr = ((int)((i >> 16) & 0x1F)) | (int)(((i >> 11) & 0x1F) << 5);
        GPR[D(i)] = ReadSpr(spr);
    }

    void Mtspr(uint i)
    {
        int spr = ((int)((i >> 16) & 0x1F)) | (int)(((i >> 11) & 0x1F) << 5);
        WriteSpr(spr, GPR[D(i)]);
    }

    void Mftb(uint i)
    {
        int tbr = ((int)((i >> 16) & 0x1F)) | (int)(((i >> 11) & 0x1F) << 5);
        GPR[D(i)] = tbr == 268 ? TBL : TBU;
    }

    uint ReadSpr(int spr)
    {
        return spr switch
        {
            1 => XER,
            8 => LR,
            9 => CTR,
            18 => DSISR,
            19 => DAR,
            22 => DEC,
            25 => 0,
            26 => SRR[0],
            27 => SRR[1],
            272 => SPRG[0], 273 => SPRG[1], 274 => SPRG[2], 275 => SPRG[3],
            284 => TBL,
            285 => TBU,
            528 => IBAT[0], 529 => IBAT[1], 530 => IBAT[2], 531 => IBAT[3],
            532 => IBAT[4], 533 => IBAT[5], 534 => IBAT[6], 535 => IBAT[7],
            536 => DBAT[0], 537 => DBAT[1], 538 => DBAT[2], 539 => DBAT[3],
            540 => DBAT[4], 541 => DBAT[5], 542 => DBAT[6], 543 => DBAT[7],
            912 => GQR[0], 913 => GQR[1], 914 => GQR[2], 915 => GQR[3],
            916 => GQR[4], 917 => GQR[5], 918 => GQR[6], 919 => GQR[7],
            920 => HID2,
            936 => MMCR0, 940 => PMC1, 941 => PMC2, 937 => MMCR1, 942 => PMC3, 943 => PMC4, 955 => SIA,
            1008 => HID0,
            1009 => HID1,
            1010 => IABR,
            1013 => DABR,
            1017 => L2CR,
            1023 => 0,
            _ => 0
        };
    }

    void WriteSpr(int spr, uint val)
    {
        switch (spr)
        {
            case 1: XER = val; break;
            case 8: LR = val; break;
            case 9: CTR = val; break;
            case 18: DSISR = val; break;
            case 19: DAR = val; break;
            case 22: DEC = val; break;
            case 26: SRR[0] = val; break;
            case 27: SRR[1] = val; break;
            case 272: SPRG[0] = val; break;
            case 273: SPRG[1] = val; break;
            case 274: SPRG[2] = val; break;
            case 275: SPRG[3] = val; break;
            case 284: TBL = val; break;
            case 285: TBU = val; break;
            case 528: IBAT[0] = val; break; case 529: IBAT[1] = val; break;
            case 530: IBAT[2] = val; break; case 531: IBAT[3] = val; break;
            case 532: IBAT[4] = val; break; case 533: IBAT[5] = val; break;
            case 534: IBAT[6] = val; break; case 535: IBAT[7] = val; break;
            case 536: DBAT[0] = val; break; case 537: DBAT[1] = val; break;
            case 538: DBAT[2] = val; break; case 539: DBAT[3] = val; break;
            case 540: DBAT[4] = val; break; case 541: DBAT[5] = val; break;
            case 542: DBAT[6] = val; break; case 543: DBAT[7] = val; break;
            case 912: GQR[0] = val; break; case 913: GQR[1] = val; break;
            case 914: GQR[2] = val; break; case 915: GQR[3] = val; break;
            case 916: GQR[4] = val; break; case 917: GQR[5] = val; break;
            case 918: GQR[6] = val; break; case 919: GQR[7] = val; break;
            case 920: HID2 = val; break;
            case 936: MMCR0 = val; break; case 937: MMCR1 = val; break;
            case 940: PMC1 = val; break; case 941: PMC2 = val; break;
            case 942: PMC3 = val; break; case 943: PMC4 = val; break;
            case 955: SIA = val; break;
            case 1008: HID0 = val; break;
            case 1009: break;
            case 1010: IABR = val; break;
            case 1013: DABR = val; break;
            case 1017: L2CR = val; break;
        }
    }

    // ── Load/Store (D-form) ─────────────────────────────────────────────

    void Lwz(uint i)  { GPR[D(i)] = _bus.Read32(EaD(i)); }
    void Lwzu(uint i) { uint ea = EaD(i); GPR[D(i)] = _bus.Read32(ea); GPR[A(i)] = ea; }
    void Lbz(uint i)  { GPR[D(i)] = _bus.Read8(EaD(i)); }
    void Lbzu(uint i) { uint ea = EaD(i); GPR[D(i)] = _bus.Read8(ea); GPR[A(i)] = ea; }
    void Lhz(uint i)  { GPR[D(i)] = _bus.Read16(EaD(i)); }
    void Lhzu(uint i) { uint ea = EaD(i); GPR[D(i)] = _bus.Read16(ea); GPR[A(i)] = ea; }
    void Lha(uint i)  { GPR[D(i)] = (uint)(int)(short)_bus.Read16(EaD(i)); }
    void Lhau(uint i) { uint ea = EaD(i); GPR[D(i)] = (uint)(int)(short)_bus.Read16(ea); GPR[A(i)] = ea; }
    void Stw(uint i)  { _bus.Write32(EaD(i), GPR[D(i)]); }
    void Stwu(uint i) { uint ea = EaD(i); _bus.Write32(ea, GPR[D(i)]); GPR[A(i)] = ea; }
    void Stb(uint i)  { _bus.Write8(EaD(i), (byte)GPR[D(i)]); }
    void Stbu(uint i) { uint ea = EaD(i); _bus.Write8(ea, (byte)GPR[D(i)]); GPR[A(i)] = ea; }
    void Sth(uint i)  { _bus.Write16(EaD(i), (ushort)GPR[D(i)]); }
    void Sthu(uint i) { uint ea = EaD(i); _bus.Write16(ea, (ushort)GPR[D(i)]); GPR[A(i)] = ea; }

    // ── Floating Point Load/Store ───────────────────────────────────────

    void Lfs(uint i)  { FPR[D(i)] = IntToFloat(_bus.Read32(EaD(i))); PS1[D(i)] = FPR[D(i)]; }
    void Lfsu(uint i) { uint ea = EaD(i); FPR[D(i)] = IntToFloat(_bus.Read32(ea)); PS1[D(i)] = FPR[D(i)]; GPR[A(i)] = ea; }
    void Lfd(uint i)  { FPR[D(i)] = IntToDouble(_bus.Read64(EaD(i))); }
    void Lfdu(uint i) { uint ea = EaD(i); FPR[D(i)] = IntToDouble(_bus.Read64(ea)); GPR[A(i)] = ea; }
    void Stfs(uint i)  { _bus.Write32(EaD(i), FloatToInt((float)FPR[D(i)])); }
    void Stfsu(uint i) { uint ea = EaD(i); _bus.Write32(ea, FloatToInt((float)FPR[D(i)])); GPR[A(i)] = ea; }
    void Stfd(uint i)  { _bus.Write64(EaD(i), DoubleToLong(FPR[D(i)])); }
    void Stfdu(uint i) { uint ea = EaD(i); _bus.Write64(ea, DoubleToLong(FPR[D(i)])); GPR[A(i)] = ea; }
    void Lfsx(uint i)  { FPR[D(i)] = IntToFloat(_bus.Read32(EaX(i))); PS1[D(i)] = FPR[D(i)]; }
    void Lfsux(uint i) { uint ea = EaX(i); FPR[D(i)] = IntToFloat(_bus.Read32(ea)); PS1[D(i)] = FPR[D(i)]; GPR[A(i)] = ea; }
    void Lfdx(uint i)  { FPR[D(i)] = IntToDouble(_bus.Read64(EaX(i))); }
    void Lfdux(uint i) { uint ea = EaX(i); FPR[D(i)] = IntToDouble(_bus.Read64(ea)); GPR[A(i)] = ea; }
    void Stfsx(uint i)  { _bus.Write32(EaX(i), FloatToInt((float)FPR[D(i)])); }
    void Stfsux(uint i) { uint ea = EaX(i); _bus.Write32(ea, FloatToInt((float)FPR[D(i)])); GPR[A(i)] = ea; }
    void Stfdx(uint i)  { _bus.Write64(EaX(i), DoubleToLong(FPR[D(i)])); }
    void Stfdux(uint i) { uint ea = EaX(i); _bus.Write64(ea, DoubleToLong(FPR[D(i)])); GPR[A(i)] = ea; }

    // ── Opcode 59 (Single FP) ───────────────────────────────────────────

    void Op59(uint i)
    {
        uint xo = (i >> 1) & 0x1F;
        switch (xo)
        {
            case 18: Fdivs(i); break;
            case 20: Fsubs(i); break;
            case 21: Fadds(i); break;
            case 24: Fres(i); break;
            case 25: Fmuls(i); break;
            case 28: Fmsubs(i); break;
            case 29: Fmadds(i); break;
            case 30: Fnmsubs(i); break;
            case 31: Fnmadds(i); break;
        }
    }

    void Fdivs(uint i) { double r = (float)(FPR[A(i)] / FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fsubs(uint i) { double r = (float)(FPR[A(i)] - FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fadds(uint i) { double r = (float)(FPR[A(i)] + FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fres(uint i)  { double r = (float)(1.0 / FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fmuls(uint i) { int frC = (int)((i >> 6) & 0x1F); double r = (float)(FPR[A(i)] * FPR[frC]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fmadds(uint i) { int frC = (int)((i >> 6) & 0x1F); double r = (float)(FPR[A(i)] * FPR[frC] + FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fmsubs(uint i) { int frC = (int)((i >> 6) & 0x1F); double r = (float)(FPR[A(i)] * FPR[frC] - FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fnmadds(uint i) { int frC = (int)((i >> 6) & 0x1F); double r = (float)-(FPR[A(i)] * FPR[frC] + FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }
    void Fnmsubs(uint i) { int frC = (int)((i >> 6) & 0x1F); double r = (float)-(FPR[A(i)] * FPR[frC] - FPR[B(i)]); FPR[D(i)] = r; PS1[D(i)] = r; }

    // ── Opcode 63 (Double FP + misc) ────────────────────────────────────

    void Op63(uint i)
    {
        uint xo5 = (i >> 1) & 0x1F;
        uint xo10 = (i >> 1) & 0x3FF;

        switch (xo10)
        {
            case 0: Fcmpu(i); return;
            case 12: Frsp(i); return;
            case 14: Fctiw(i); return;
            case 15: Fctiwz(i); return;
            case 32: Fcmpo(i); return;
            case 38: Mtfsb1(i); return;
            case 40: Fneg(i); return;
            case 64: Mcrfs(i); return;
            case 70: Mtfsb0(i); return;
            case 72: Fmr(i); return;
            case 134: Mtfsfi(i); return;
            case 136: Fnabs(i); return;
            case 264: Fabs(i); return;
            case 583: Mffs(i); return;
            case 711: Mtfsf(i); return;
        }

        switch (xo5)
        {
            case 18: Fdiv(i); break;
            case 20: Fsub(i); break;
            case 21: Fadd(i); break;
            case 22: Fsqrt(i); break;
            case 23: Fsel(i); break;
            case 25: Fmul(i); break;
            case 26: Frsqrte(i); break;
            case 28: Fmsub(i); break;
            case 29: Fmadd(i); break;
            case 30: Fnmsub(i); break;
            case 31: Fnmadd(i); break;
        }
    }

    void Fcmpu(uint i) { int crfD = (int)((i >> 23) & 7); double a = FPR[A(i)], b = FPR[B(i)]; int c = double.IsNaN(a) || double.IsNaN(b) ? 1 : a < b ? 8 : a > b ? 4 : 2; SetCRField(crfD, c); }
    void Fcmpo(uint i) { Fcmpu(i); }
    void Frsp(uint i) { FPR[D(i)] = (float)FPR[B(i)]; }
    void Fctiw(uint i) { FPR[D(i)] = IntToDouble((ulong)(uint)(int)Math.Round(FPR[B(i)])); }
    void Fctiwz(uint i) { FPR[D(i)] = IntToDouble((ulong)(uint)(int)FPR[B(i)]); }
    void Fneg(uint i) { FPR[D(i)] = -FPR[B(i)]; }
    void Fabs(uint i) { FPR[D(i)] = Math.Abs(FPR[B(i)]); }
    void Fnabs(uint i) { FPR[D(i)] = -Math.Abs(FPR[B(i)]); }
    void Fmr(uint i) { FPR[D(i)] = FPR[B(i)]; }
    void Fdiv(uint i) { FPR[D(i)] = FPR[A(i)] / FPR[B(i)]; }
    void Fsub(uint i) { FPR[D(i)] = FPR[A(i)] - FPR[B(i)]; }
    void Fadd(uint i) { FPR[D(i)] = FPR[A(i)] + FPR[B(i)]; }
    void Fsqrt(uint i) { FPR[D(i)] = Math.Sqrt(FPR[B(i)]); }
    void Fsel(uint i) { int frC = (int)((i >> 6) & 0x1F); FPR[D(i)] = FPR[A(i)] >= 0 ? FPR[frC] : FPR[B(i)]; }
    void Fmul(uint i) { int frC = (int)((i >> 6) & 0x1F); FPR[D(i)] = FPR[A(i)] * FPR[frC]; }
    void Frsqrte(uint i) { FPR[D(i)] = 1.0 / Math.Sqrt(FPR[B(i)]); }
    void Fmadd(uint i) { int frC = (int)((i >> 6) & 0x1F); FPR[D(i)] = FPR[A(i)] * FPR[frC] + FPR[B(i)]; }
    void Fmsub(uint i) { int frC = (int)((i >> 6) & 0x1F); FPR[D(i)] = FPR[A(i)] * FPR[frC] - FPR[B(i)]; }
    void Fnmadd(uint i) { int frC = (int)((i >> 6) & 0x1F); FPR[D(i)] = -(FPR[A(i)] * FPR[frC] + FPR[B(i)]); }
    void Fnmsub(uint i) { int frC = (int)((i >> 6) & 0x1F); FPR[D(i)] = -(FPR[A(i)] * FPR[frC] - FPR[B(i)]); }
    void Mffs(uint i) { ulong bits = DoubleToLong(FPR[D(i)]); bits = (bits & 0xFFFFFFFF00000000) | FPSCR; FPR[D(i)] = IntToDouble(bits); }
    void Mtfsf(uint i) { uint fm = (uint)((i >> 17) & 0xFF); ulong bits = DoubleToLong(FPR[B(i)]); uint val = (uint)bits; for (int f = 0; f < 8; f++) { if ((fm & (1 << (7 - f))) != 0) { uint nibble = (val >> (28 - f * 4)) & 0xF; FPSCR = (FPSCR & ~(0xFu << (28 - f * 4))) | (nibble << (28 - f * 4)); } } }
    void Mtfsb0(uint i) { int bit = (int)((i >> 21) & 0x1F); FPSCR &= ~(1u << (31 - bit)); }
    void Mtfsb1(uint i) { int bit = (int)((i >> 21) & 0x1F); FPSCR |= 1u << (31 - bit); }
    void Mtfsfi(uint i) { int crfD = (int)((i >> 23) & 7); uint imm = (uint)((i >> 12) & 0xF); FPSCR = (FPSCR & ~(0xFu << (28 - crfD * 4))) | (imm << (28 - crfD * 4)); }
    void Mcrfs(uint i) { int crfD = (int)((i >> 23) & 7); int crfS = (int)((i >> 18) & 7); uint nibble = (FPSCR >> (28 - crfS * 4)) & 0xF; SetCRField(crfD, (int)nibble); }

    // ── Paired Singles (Opcode 4) ───────────────────────────────────────

    void Op4(uint i)
    {
        uint xo10 = (i >> 1) & 0x3FF;
        uint xo5 = (i >> 1) & 0x1F;

        switch (xo10)
        {
            case 6: PsCmpu0(i); return;
            case 70: PsCmpo0(i); return;
            case 38: PsCmpu1(i); return;
            case 102: PsCmpo1(i); return;
            case 40: PsNeg(i); return;
            case 72: PsMr(i); return;
            case 136: PsNabs(i); return;
            case 264: PsAbs(i); return;
            case 528: PsMerge00(i); return;
            case 560: PsMerge01(i); return;
            case 592: PsMerge10(i); return;
            case 624: PsMerge11(i); return;
            case 24: PsRes(i); return;
            case 26: PsRsqrte(i); return;
        }

        switch (xo5)
        {
            case 10: PsSum0(i); break;
            case 11: PsSum1(i); break;
            case 12: PsMuls0(i); break;
            case 13: PsMuls1(i); break;
            case 14: PsMadds0(i); break;
            case 15: PsMadds1(i); break;
            case 18: PsDiv(i); break;
            case 20: PsSub(i); break;
            case 21: PsAdd(i); break;
            case 23: PsSel(i); break;
            case 25: PsMul(i); break;
            case 28: PsMsub(i); break;
            case 29: PsMadd(i); break;
            case 30: PsNmsub(i); break;
            case 31: PsNmadd(i); break;
        }
    }

    void PsAdd(uint i) { FPR[D(i)] = (float)(FPR[A(i)] + FPR[B(i)]); PS1[D(i)] = (float)(PS1[A(i)] + PS1[B(i)]); }
    void PsSub(uint i) { FPR[D(i)] = (float)(FPR[A(i)] - FPR[B(i)]); PS1[D(i)] = (float)(PS1[A(i)] - PS1[B(i)]); }
    void PsMul(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] * FPR[c]); PS1[D(i)] = (float)(PS1[A(i)] * PS1[c]); }
    void PsDiv(uint i) { FPR[D(i)] = (float)(FPR[A(i)] / FPR[B(i)]); PS1[D(i)] = (float)(PS1[A(i)] / PS1[B(i)]); }
    void PsMadd(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] * FPR[c] + FPR[B(i)]); PS1[D(i)] = (float)(PS1[A(i)] * PS1[c] + PS1[B(i)]); }
    void PsMsub(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] * FPR[c] - FPR[B(i)]); PS1[D(i)] = (float)(PS1[A(i)] * PS1[c] - PS1[B(i)]); }
    void PsNmadd(uint i) { int c = C(i); FPR[D(i)] = (float)-(FPR[A(i)] * FPR[c] + FPR[B(i)]); PS1[D(i)] = (float)-(PS1[A(i)] * PS1[c] + PS1[B(i)]); }
    void PsNmsub(uint i) { int c = C(i); FPR[D(i)] = (float)-(FPR[A(i)] * FPR[c] - FPR[B(i)]); PS1[D(i)] = (float)-(PS1[A(i)] * PS1[c] - PS1[B(i)]); }
    void PsMuls0(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] * FPR[c]); PS1[D(i)] = (float)(PS1[A(i)] * FPR[c]); }
    void PsMuls1(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] * PS1[c]); PS1[D(i)] = (float)(PS1[A(i)] * PS1[c]); }
    void PsMadds0(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] * FPR[c] + FPR[B(i)]); PS1[D(i)] = (float)(PS1[A(i)] * FPR[c] + PS1[B(i)]); }
    void PsMadds1(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] * PS1[c] + FPR[B(i)]); PS1[D(i)] = (float)(PS1[A(i)] * PS1[c] + PS1[B(i)]); }
    void PsSum0(uint i) { int c = C(i); FPR[D(i)] = (float)(FPR[A(i)] + PS1[B(i)]); PS1[D(i)] = (float)PS1[c]; }
    void PsSum1(uint i) { int c = C(i); PS1[D(i)] = (float)(FPR[A(i)] + PS1[B(i)]); FPR[D(i)] = (float)FPR[c]; }
    void PsMerge00(uint i) { FPR[D(i)] = (float)FPR[A(i)]; PS1[D(i)] = (float)FPR[B(i)]; }
    void PsMerge01(uint i) { FPR[D(i)] = (float)FPR[A(i)]; PS1[D(i)] = (float)PS1[B(i)]; }
    void PsMerge10(uint i) { FPR[D(i)] = (float)PS1[A(i)]; PS1[D(i)] = (float)FPR[B(i)]; }
    void PsMerge11(uint i) { FPR[D(i)] = (float)PS1[A(i)]; PS1[D(i)] = (float)PS1[B(i)]; }
    void PsNeg(uint i) { FPR[D(i)] = -FPR[B(i)]; PS1[D(i)] = -PS1[B(i)]; }
    void PsMr(uint i) { FPR[D(i)] = FPR[B(i)]; PS1[D(i)] = PS1[B(i)]; }
    void PsAbs(uint i) { FPR[D(i)] = Math.Abs(FPR[B(i)]); PS1[D(i)] = Math.Abs(PS1[B(i)]); }
    void PsNabs(uint i) { FPR[D(i)] = -Math.Abs(FPR[B(i)]); PS1[D(i)] = -Math.Abs(PS1[B(i)]); }
    void PsRes(uint i) { FPR[D(i)] = (float)(1.0 / FPR[B(i)]); PS1[D(i)] = (float)(1.0 / PS1[B(i)]); }
    void PsRsqrte(uint i) { FPR[D(i)] = (float)(1.0 / Math.Sqrt(FPR[B(i)])); PS1[D(i)] = (float)(1.0 / Math.Sqrt(PS1[B(i)])); }
    void PsSel(uint i) { int c = C(i); FPR[D(i)] = FPR[A(i)] >= 0 ? FPR[c] : FPR[B(i)]; PS1[D(i)] = PS1[A(i)] >= 0 ? PS1[c] : PS1[B(i)]; }

    void PsCmpu0(uint i) { int crfD = (int)((i >> 23) & 7); double a = FPR[A(i)], b = FPR[B(i)]; int c = double.IsNaN(a) || double.IsNaN(b) ? 1 : a < b ? 8 : a > b ? 4 : 2; SetCRField(crfD, c); }
    void PsCmpo0(uint i) { PsCmpu0(i); }
    void PsCmpu1(uint i) { int crfD = (int)((i >> 23) & 7); double a = PS1[A(i)], b = PS1[B(i)]; int c = double.IsNaN(a) || double.IsNaN(b) ? 1 : a < b ? 8 : a > b ? 4 : 2; SetCRField(crfD, c); }
    void PsCmpo1(uint i) { PsCmpu1(i); }

    // ── Quantized Load/Store (PSQ) ──────────────────────────────────────

    void PsqL(uint i)
    {
        int rD = D(i); int rA = A(i); int off = (int)(short)(i & 0xFFF);
        if ((off & 0x800) != 0) off |= unchecked((int)0xFFFFF000);
        int gqrIdx = (int)((i >> 12) & 7);
        bool w = ((i >> 15) & 1) != 0;
        uint ea = (uint)((rA == 0 ? 0 : (int)GPR[rA]) + off);
        DequantLoad(rD, ea, gqrIdx, w);
    }

    void PsqLu(uint i)
    {
        int rD = D(i); int rA = A(i); int off = (int)(short)(i & 0xFFF);
        if ((off & 0x800) != 0) off |= unchecked((int)0xFFFFF000);
        int gqrIdx = (int)((i >> 12) & 7);
        bool w = ((i >> 15) & 1) != 0;
        uint ea = (uint)((int)GPR[rA] + off);
        DequantLoad(rD, ea, gqrIdx, w);
        GPR[rA] = ea;
    }

    void PsqSt(uint i)
    {
        int rS = D(i); int rA = A(i); int off = (int)(short)(i & 0xFFF);
        if ((off & 0x800) != 0) off |= unchecked((int)0xFFFFF000);
        int gqrIdx = (int)((i >> 12) & 7);
        bool w = ((i >> 15) & 1) != 0;
        uint ea = (uint)((rA == 0 ? 0 : (int)GPR[rA]) + off);
        QuantStore(rS, ea, gqrIdx, w);
    }

    void PsqStu(uint i)
    {
        int rS = D(i); int rA = A(i); int off = (int)(short)(i & 0xFFF);
        if ((off & 0x800) != 0) off |= unchecked((int)0xFFFFF000);
        int gqrIdx = (int)((i >> 12) & 7);
        bool w = ((i >> 15) & 1) != 0;
        uint ea = (uint)((int)GPR[rA] + off);
        QuantStore(rS, ea, gqrIdx, w);
        GPR[rA] = ea;
    }

    void DequantLoad(int frD, uint ea, int gqrIdx, bool w)
    {
        uint gqr = GQR[gqrIdx];
        int ldType = (int)(gqr & 7);
        int ldScale = (int)((gqr >> 8) & 0x3F);
        float scale = DequantScale(ldScale);

        switch (ldType)
        {
            case 0:
                FPR[frD] = IntToFloat(_bus.Read32(ea));
                PS1[frD] = w ? 1.0 : IntToFloat(_bus.Read32(ea + 4));
                break;
            case 4:
                FPR[frD] = _bus.Read8(ea) * scale;
                PS1[frD] = w ? 1.0 : _bus.Read8(ea + 1) * scale;
                break;
            case 5:
                FPR[frD] = _bus.Read16(ea) * scale;
                PS1[frD] = w ? 1.0 : _bus.Read16(ea + 2) * scale;
                break;
            case 6:
                FPR[frD] = (sbyte)_bus.Read8(ea) * scale;
                PS1[frD] = w ? 1.0 : (sbyte)_bus.Read8(ea + 1) * scale;
                break;
            case 7:
                FPR[frD] = (short)_bus.Read16(ea) * scale;
                PS1[frD] = w ? 1.0 : (short)_bus.Read16(ea + 2) * scale;
                break;
            default:
                FPR[frD] = 0; PS1[frD] = w ? 1.0 : 0;
                break;
        }
    }

    void QuantStore(int frS, uint ea, int gqrIdx, bool w)
    {
        uint gqr = GQR[gqrIdx];
        int stType = (int)((gqr >> 16) & 7);
        int stScale = (int)((gqr >> 24) & 0x3F);
        float scale = QuantScale(stScale);

        switch (stType)
        {
            case 0:
                _bus.Write32(ea, FloatToInt((float)FPR[frS]));
                if (!w) _bus.Write32(ea + 4, FloatToInt((float)PS1[frS]));
                break;
            case 4:
                _bus.Write8(ea, (byte)Math.Clamp((int)(FPR[frS] * scale), 0, 255));
                if (!w) _bus.Write8(ea + 1, (byte)Math.Clamp((int)(PS1[frS] * scale), 0, 255));
                break;
            case 5:
                _bus.Write16(ea, (ushort)Math.Clamp((int)(FPR[frS] * scale), 0, 65535));
                if (!w) _bus.Write16(ea + 2, (ushort)Math.Clamp((int)(PS1[frS] * scale), 0, 65535));
                break;
            case 6:
                _bus.Write8(ea, (byte)(sbyte)Math.Clamp((int)(FPR[frS] * scale), -128, 127));
                if (!w) _bus.Write8(ea + 1, (byte)(sbyte)Math.Clamp((int)(PS1[frS] * scale), -128, 127));
                break;
            case 7:
                _bus.Write16(ea, (ushort)(short)Math.Clamp((int)(FPR[frS] * scale), -32768, 32767));
                if (!w) _bus.Write16(ea + 2, (ushort)(short)Math.Clamp((int)(PS1[frS] * scale), -32768, 32767));
                break;
        }
    }

    static float DequantScale(int s) => s == 0 ? 1f : MathF.Pow(2, -(s > 31 ? s - 64 : s));
    static float QuantScale(int s) => s == 0 ? 1f : MathF.Pow(2, s > 31 ? s - 64 : s);

    // ── Helpers ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int D(uint i) => (int)((i >> 21) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int A(uint i) => (int)((i >> 16) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int B(uint i) => (int)((i >> 11) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int C(uint i) => (int)((i >> 6) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int SIMM(uint i) => (short)(ushort)i;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint EaD(uint i) { int rA = A(i); int d = SIMM(i); return (uint)((rA == 0 ? 0 : (int)GPR[rA]) + d); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    uint EaX(uint i) { int rA = A(i); return (rA == 0 ? 0 : GPR[rA]) + GPR[B(i)]; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint RotL(uint v, int n) => (v << n) | (v >> (32 - n));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Mask(int mb, int me)
    {
        uint m = 0;
        if (mb <= me) { for (int i = mb; i <= me; i++) m |= 1u << (31 - i); }
        else { for (int i = 0; i <= me; i++) m |= 1u << (31 - i); for (int i = mb; i <= 31; i++) m |= 1u << (31 - i); }
        return m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void UpdateCR0(uint result)
    {
        int c = (int)result < 0 ? 8 : result > 0 ? 4 : 2;
        if ((XER & 0x80000000) != 0) c |= 1;
        CR = (CR & 0x0FFFFFFF) | ((uint)c << 28);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SetCRField(int field, int val) => CR = (CR & ~(0xFu << (28 - field * 4))) | ((uint)(val & 0xF) << (28 - field * 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int CompareCR(int a, int b) { int c = a < b ? 8 : a > b ? 4 : 2; return c; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int CompareCRu(uint a, uint b) { int c = a < b ? 8 : a > b ? 4 : 2; return c; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SetCA(bool set) { if (set) XER |= 0x20000000; else XER &= ~0x20000000u; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool GetCA() => (XER & 0x20000000) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double IntToFloat(uint v) { byte[] b = BitConverter.GetBytes(v); return BitConverter.ToSingle(b, 0); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static double IntToDouble(ulong v) { byte[] b = BitConverter.GetBytes(v); return BitConverter.ToDouble(b, 0); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint FloatToInt(float v) { byte[] b = BitConverter.GetBytes(v); return BitConverter.ToUInt32(b, 0); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong DoubleToLong(double v) { byte[] b = BitConverter.GetBytes(v); return BitConverter.ToUInt64(b, 0); }
}
