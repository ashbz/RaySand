using System.Runtime.CompilerServices;

namespace N64Emu;

sealed class Vr4300
{
    public readonly long[] GPR = new long[32];
    public long Hi, Lo;
    public ulong PC;
    public long LLBit;
    public long TotalCycles;

    public readonly uint[] COP0 = new uint[32];
    ulong _countInternal; // internal counter, incremented every cycle; COP0_COUNT = _countInternal >> 1

    public readonly long[] FPR = new long[32];
    public uint FCR0 = 0x00000B00;
    public uint FCR31;

    public readonly TlbEntry[] Tlb = new TlbEntry[32];

    // Delay slot state: when a branch executes, _nextIsDelay becomes true.
    // The NEXT instruction (delay slot) executes normally, then PC jumps to _delayTarget.
    bool _nextIsDelay;
    ulong _delayTarget;
    bool _inDelaySlot; // true while the delay slot instruction is executing

    bool _interruptPending;
    bool _exception; // set when exception fires, prevents delay slot target from being applied

    public N64Bus Bus;
    public string TtyOutput = "";
    public int ExceptionCount;
    public int EretCount;
    public int BadOpCount;
    public uint LastBadOp;
    public ulong LastBadPC;
    public int BreakExcCount;
    public int TimerFireCount;
    public int CompareWriteCount;
    public int RspTaskCount;
    ulong _timerTarget = ulong.MaxValue; // internal count at which timer fires

    const int COP0_INDEX    = 0;
    const int COP0_RANDOM   = 1;
    const int COP0_ENTRYLO0 = 2;
    const int COP0_ENTRYLO1 = 3;
    const int COP0_CONTEXT  = 4;
    const int COP0_PAGEMASK = 5;
    const int COP0_WIRED    = 6;
    const int COP0_BADVADDR = 8;
    const int COP0_COUNT    = 9;
    const int COP0_ENTRYHI  = 10;
    const int COP0_COMPARE  = 11;
    const int COP0_STATUS   = 12;
    const int COP0_CAUSE    = 13;
    const int COP0_EPC      = 14;
    const int COP0_PRID     = 15;
    const int COP0_CONFIG   = 16;
    const int COP0_LLADDR   = 17;
    const int COP0_TAGLO    = 28;
    const int COP0_TAGHI    = 29;
    const int COP0_ERROREPC = 30;

    public struct TlbEntry
    {
        public uint PageMask, EntryHi, EntryLo0, EntryLo1;
    }

    // Big-endian LWL/LWR/SWL/SWR lookup tables
    static readonly int[] _lwlShift = { 0, 8, 16, 24 };
    static readonly uint[] _lwlMask = { 0x00000000, 0x000000FF, 0x0000FFFF, 0x00FFFFFF };
    static readonly int[] _lwrShift = { 24, 16, 8, 0 };
    static readonly uint[] _lwrMask = { 0xFFFFFF00, 0xFFFF0000, 0xFF000000, 0x00000000 };
    static readonly int[] _swlShift = { 0, 8, 16, 24 };
    static readonly uint[] _swlMask = { 0x00000000, 0xFF000000, 0xFFFF0000, 0xFFFFFF00 };
    static readonly int[] _swrShift = { 24, 16, 8, 0 };
    static readonly uint[] _swrMask = { 0x00FFFFFF, 0x0000FFFF, 0x000000FF, 0x00000000 };

    static void WriteRdramSize(byte[] rdram, uint addr, uint size)
    {
        rdram[addr + 0] = unchecked((byte)(size >> 24));
        rdram[addr + 1] = unchecked((byte)(size >> 16));
        rdram[addr + 2] = unchecked((byte)(size >> 8));
        rdram[addr + 3] = unchecked((byte)size);
    }

    public Vr4300(N64Bus bus)
    {
        Bus = bus;
        Reset();
    }

    public void Reset()
    {
        Array.Clear(GPR);
        Hi = Lo = 0;
        PC = 0xBFC00000;
        LLBit = 0;
        TotalCycles = 0;
        Array.Clear(COP0);
        COP0[COP0_STATUS] = 0x34000000;
        COP0[COP0_CONFIG] = 0x7006E463;
        COP0[COP0_PRID]   = 0x00000B22;
        COP0[COP0_RANDOM] = 31;
        _nextIsDelay = false;
        _inDelaySlot = false;
        _interruptPending = false;
    }

    public void SetupHleBoot()
    {
        if (Bus.Cart.Rom.Length < 0x1000) return;

        int cicType = Bus.Cart.CicType;
        uint cicSeed = Bus.Cart.CicSeed;

        // Write CIC seed to PIF RAM at offset 0x24 (big-endian)
        uint pifSeedWord = (cicSeed << 8) | 0x3F;
        Bus.Pif.Ram[0x24] = (byte)(pifSeedWord >> 24);
        Bus.Pif.Ram[0x25] = (byte)(pifSeedWord >> 16);
        Bus.Pif.Ram[0x26] = (byte)(pifSeedWord >> 8);
        Bus.Pif.Ram[0x27] = (byte)(pifSeedWord);

        // Write RDRAM size to the address the CIC type expects
        uint rdramSizeAddr = cicType == 6105 ? 0x3F0u : 0x318u;
        WriteRdramSize(Bus.Rdram, rdramSizeAddr, (uint)N64Bus.RdramSize);

        // Write MI_VERSION
        Bus.Mi.MiVersion = 0x01010101;

        // Copy first 0x1000 bytes of ROM to SP DMEM (IPL3)
        Array.Copy(Bus.Cart.Rom, 0, Bus.Rsp.Dmem, 0, Math.Min(0x1000, Bus.Cart.Rom.Length));

        // COP0 setup (same for all CIC types)
        COP0[COP0_STATUS] = 0x34000000;
        COP0[COP0_CONFIG] = 0x7006E463;
        COP0[COP0_PRID]   = 0x00000B22;
        COP0[COP0_RANDOM] = 0x0000001F;

        // GPR[22] = CIC seed bits 8-15 for all CIC types
        GPR[22] = (cicSeed >> 8) & 0xFF;

        // Set GPRs based on CIC type (values from Dillonb's n64 emulator)
        switch (cicType)
        {
            case 6101:
                GPR[1]  = 0;
                GPR[2]  = unchecked((long)0xFFFFFFFFDF6445CC);
                GPR[3]  = unchecked((long)0xFFFFFFFFDF6445CC);
                GPR[4]  = 0x000045CC;
                GPR[5]  = 0x73EE317A;
                GPR[6]  = unchecked((long)0xFFFFFFFFA4001F0C);
                GPR[7]  = unchecked((long)0xFFFFFFFFA4001F08);
                GPR[8]  = 0xC0;
                GPR[10] = 0x40;
                GPR[11] = unchecked((long)0xFFFFFFFFA4000040);
                GPR[12] = unchecked((long)0xFFFFFFFFC7601FAC);
                GPR[13] = unchecked((long)0xFFFFFFFFC7601FAC);
                GPR[14] = unchecked((long)0xFFFFFFFFB48E2ED6);
                GPR[15] = unchecked((long)0xFFFFFFFFBA1A7D4B);
                GPR[20] = 1;
                GPR[23] = 1;
                GPR[24] = 2;
                GPR[25] = unchecked((long)0xFFFFFFFF905F4718);
                GPR[29] = unchecked((long)0xFFFFFFFFA4001FF0);
                GPR[31] = unchecked((long)0xFFFFFFFFA4001550);
                Lo = unchecked((long)0xFFFFFFFFBA1A7D4B);
                Hi = unchecked((long)0xFFFFFFFF997EC317);
                break;

            default: // CIC-6102 (SM64 and 88% of games)
                GPR[1]  = 1;
                GPR[2]  = unchecked((long)0x000000000EBDA536);
                GPR[3]  = unchecked((long)0x000000000EBDA536);
                GPR[4]  = 0x0000A536;
                GPR[5]  = unchecked((long)0xFFFFFFFFC0F1D859);
                GPR[6]  = unchecked((long)0xFFFFFFFFA4001F0C);
                GPR[7]  = unchecked((long)0xFFFFFFFFA4001F08);
                GPR[8]  = 0xC0;
                GPR[10] = 0x40;
                GPR[11] = unchecked((long)0xFFFFFFFFA4000040);
                GPR[12] = unchecked((long)0xFFFFFFFFED10D0B3);
                GPR[13] = 0x1402A4CC;
                GPR[14] = 0x2DE108EA;
                GPR[15] = 0x3103E121;
                GPR[20] = 1;
                GPR[25] = unchecked((long)0xFFFFFFFF9DEBB54F);
                GPR[29] = unchecked((long)0xFFFFFFFFA4001FF0);
                GPR[31] = unchecked((long)0xFFFFFFFFA4001550);
                Lo = 0x3103E121;
                Hi = 0x3FC18657;
                break;

            case 6103:
                GPR[1]  = 1;
                GPR[2]  = 0x49A5EE96;
                GPR[3]  = 0x49A5EE96;
                GPR[4]  = 0x0000EE96;
                GPR[5]  = unchecked((long)0xFFFFFFFFD4646273);
                GPR[6]  = unchecked((long)0xFFFFFFFFA4001F0C);
                GPR[7]  = unchecked((long)0xFFFFFFFFA4001F08);
                GPR[8]  = 0xC0;
                GPR[10] = 0x40;
                GPR[11] = unchecked((long)0xFFFFFFFFA4000040);
                GPR[12] = unchecked((long)0xFFFFFFFFCE9DFBF7);
                GPR[13] = unchecked((long)0xFFFFFFFFCE9DFBF7);
                GPR[14] = 0x1AF99984;
                GPR[15] = 0x18B63D28;
                GPR[20] = 1;
                GPR[25] = unchecked((long)0xFFFFFFFF825B21C9);
                GPR[29] = unchecked((long)0xFFFFFFFFA4001FF0);
                GPR[31] = unchecked((long)0xFFFFFFFFA4001550);
                Lo = 0x18B63D28;
                Hi = 0x625C2BBE;
                break;

            case 6105:
                GPR[1]  = 0;
                GPR[2]  = unchecked((long)0xFFFFFFFFF58B0FBF);
                GPR[3]  = unchecked((long)0xFFFFFFFFF58B0FBF);
                GPR[4]  = 0x0000FBF;
                GPR[5]  = unchecked((long)0xFFFFFFFFDECAAAD1);
                GPR[6]  = unchecked((long)0xFFFFFFFFA4001F0C);
                GPR[7]  = unchecked((long)0xFFFFFFFFA4001F08);
                GPR[8]  = 0xC0;
                GPR[10] = 0x40;
                GPR[11] = unchecked((long)0xFFFFFFFFA4000040);
                GPR[12] = unchecked((long)0xFFFFFFFF9651F81E);
                GPR[13] = 0x2D42AAC5;
                GPR[14] = 0x489B52CF;
                GPR[15] = 0x56584D60;
                GPR[20] = 1;
                GPR[24] = 2;
                GPR[25] = unchecked((long)0xFFFFFFFFCDCE565F);
                GPR[29] = unchecked((long)0xFFFFFFFFA4001FF0);
                GPR[31] = unchecked((long)0xFFFFFFFFA4001550);
                Lo = 0x56584D60;
                Hi = 0x4BE35D1F;
                // CIC-6105 needs IMEM stub
                Bus.Write32(0x04001000, 0x3C0DBFC0);
                Bus.Write32(0x04001004, 0x8DA807FC);
                Bus.Write32(0x04001008, 0x25AD07C0);
                Bus.Write32(0x0400100C, 0x31080080);
                Bus.Write32(0x04001010, 0x5500FFFC);
                Bus.Write32(0x04001014, 0x3C0DBFC0);
                Bus.Write32(0x04001018, 0x8DA80024);
                Bus.Write32(0x0400101C, 0x3C0BB000);
                break;

            case 6106:
                GPR[1]  = 0;
                GPR[2]  = unchecked((long)0xFFFFFFFFA95930A4);
                GPR[3]  = unchecked((long)0xFFFFFFFFA95930A4);
                GPR[4]  = 0x000030A4;
                GPR[5]  = unchecked((long)0xFFFFFFFFB04DC903);
                GPR[6]  = unchecked((long)0xFFFFFFFFA4001F0C);
                GPR[7]  = unchecked((long)0xFFFFFFFFA4001F08);
                GPR[8]  = 0xC0;
                GPR[10] = 0x40;
                GPR[11] = unchecked((long)0xFFFFFFFFA4000040);
                GPR[12] = unchecked((long)0xFFFFFFFFBCB59510);
                GPR[13] = unchecked((long)0xFFFFFFFFBCB59510);
                GPR[14] = 0x0CF85C13;
                GPR[15] = 0x7A3C07F4;
                GPR[20] = 1;
                GPR[24] = 2;
                GPR[25] = 0x465E3F72;
                GPR[29] = unchecked((long)0xFFFFFFFFA4001FF0);
                GPR[31] = unchecked((long)0xFFFFFFFFA4001550);
                Lo = 0x7A3C07F4;
                Hi = 0x23953898;
                break;
        }

        // Start executing IPL3 from DMEM
        PC = 0xA4000040;
    }

    /// <summary>
    /// Full HLE boot: skip IPL3 entirely, copy game code to RDRAM and jump to entry point.
    /// Use this when IPL3 execution has issues.
    /// </summary>
    public void SetupDirectBoot()
    {
        if (Bus.Cart.Rom.Length < 0x1000) return;

        int cicType = Bus.Cart.CicType;
        uint cicSeed = Bus.Cart.CicSeed;

        // Write CIC seed to PIF RAM at offset 0x24
        uint pifSeedWord = (cicSeed << 8) | 0x3F;
        Bus.Pif.Ram[0x24] = (byte)(pifSeedWord >> 24);
        Bus.Pif.Ram[0x25] = (byte)(pifSeedWord >> 16);
        Bus.Pif.Ram[0x26] = (byte)(pifSeedWord >> 8);
        Bus.Pif.Ram[0x27] = (byte)(pifSeedWord);

        // Write RDRAM size (CIC-6105 uses 0x3F0, others use 0x318)
        uint rdramSizeAddr = cicType == 6105 ? 0x3F0u : 0x318u;
        WriteRdramSize(Bus.Rdram, rdramSizeAddr, (uint)N64Bus.RdramSize);

        // Write OS info block at RDRAM 0x300 (read by osInitialize)
        WriteRdramSize(Bus.Rdram, 0x300, 1);                       // osTvType: 1=NTSC
        WriteRdramSize(Bus.Rdram, 0x304, 0);                       // osRomType: 0=cartridge
        WriteRdramSize(Bus.Rdram, 0x308, 0x10000000);              // osRomBase
        WriteRdramSize(Bus.Rdram, 0x30C, 0);                       // osResetType: 0=cold
        WriteRdramSize(Bus.Rdram, 0x314, (uint)N64Bus.RdramSize);  // osMemSize

        // Write MI_VERSION
        Bus.Mi.MiVersion = 0x01010101;

        // PIF RAM boot completion flag
        Bus.Pif.Ram[63] = 0x80;

        // Copy first 0x1000 bytes of ROM to SP DMEM (IPL3 area)
        Array.Copy(Bus.Cart.Rom, 0, Bus.Rsp.Dmem, 0, Math.Min(0x1000, Bus.Cart.Rom.Length));

        // Copy 1 MiB of game code from ROM offset 0x1000 to RDRAM at the entry point
        uint entryPhys = Bus.Cart.EntryPoint & 0x1FFF_FFFF;
        int copyLen = Math.Min(0x10_0000, Bus.Cart.Rom.Length - 0x1000);
        if (copyLen > 0 && entryPhys + copyLen <= Bus.Rdram.Length)
            Array.Copy(Bus.Cart.Rom, 0x1000, Bus.Rdram, (int)entryPhys, copyLen);

        N64Machine.DiagWrite($"[DirectBoot] entry=0x{Bus.Cart.EntryPoint:X8} copied {copyLen} bytes to RDRAM@0x{entryPhys:X8}");

        // COP0 setup
        COP0[COP0_STATUS] = 0x34000000;
        COP0[COP0_CONFIG] = 0x7006E463;
        COP0[COP0_PRID]   = 0x00000B22;
        COP0[COP0_RANDOM] = 0x0000001F;
        COP0[COP0_COMPARE] = 0xFFFFFFFF;
        _timerTarget = _countInternal + ((ulong)uint.MaxValue << 1); // far future

        // GPR setup matching post-IPL3 state for CIC-6102
        GPR[1]  = 1;
        GPR[2]  = unchecked((long)0x000000000EBDA536);
        GPR[3]  = unchecked((long)0x000000000EBDA536);
        GPR[4]  = 0x0000A536;
        GPR[5]  = unchecked((long)0xFFFFFFFFC0F1D859);
        GPR[6]  = unchecked((long)0xFFFFFFFFA4001F0C);
        GPR[7]  = unchecked((long)0xFFFFFFFFA4001F08);
        GPR[8]  = 0xC0;
        GPR[10] = 0x40;
        GPR[11] = unchecked((long)0xFFFFFFFFA4000040);
        GPR[12] = unchecked((long)0xFFFFFFFFED10D0B3);
        GPR[13] = 0x1402A4CC;
        GPR[14] = 0x2DE108EA;
        GPR[15] = 0x3103E121;
        GPR[20] = 1; // NTSC
        GPR[22] = (long)((cicSeed >> 8) & 0xFF);
        GPR[25] = unchecked((long)0xFFFFFFFF9DEBB54F);
        GPR[29] = unchecked((long)0xFFFFFFFFA4001FF0);
        GPR[31] = unchecked((long)0xFFFFFFFFA4001550);
        Lo = 0x3103E121;
        Hi = 0x3FC18657;

        // Initialize RSP to halted state (as IPL3 leaves it)
        Bus.Rsp.SpStatus = 0x0001; // halt

        // Jump directly to the game entry point
        PC = Bus.Cart.EntryPoint;
        N64Machine.DiagWrite($"[DirectBoot] Jumping to entry point 0x{PC:X8}, " +
            $"copied {copyLen} bytes to RDRAM at 0x{entryPhys:X8}");
    }

    public void CheckInterrupts()
    {
        uint status = COP0[COP0_STATUS];
        uint cause = COP0[COP0_CAUSE];

        if (Bus.Mi.InterruptPending)
            cause |= (1u << 10);
        else
            cause &= ~(1u << 10);

        // IP7 (timer) is set ONLY in Step() when COUNT==COMPARE matches,
        // matching the reference emulator behavior. Do NOT re-check here.

        COP0[COP0_CAUSE] = cause;

        bool ie = (status & 1) != 0;
        bool exl = (status & 2) != 0;
        bool erl = (status & 4) != 0;
        uint im = (status >> 8) & 0xFF;
        uint ip = (cause >> 8) & 0xFF;

        _interruptPending = ie && !exl && !erl && (im & ip) != 0;
    }

    void RaiseException(uint exCode, int copError = -1)
    {
        COP0[COP0_CAUSE] = (COP0[COP0_CAUSE] & ~0x3000007Cu) | ((exCode & 0x1F) << 2);
        if (copError >= 0)
            COP0[COP0_CAUSE] = (COP0[COP0_CAUSE] & ~0x30000000u) | ((uint)(copError & 3) << 28);

        bool oldExl = (COP0[COP0_STATUS] & 2) != 0;
        if (!oldExl)
        {
            if (_inDelaySlot)
            {
                COP0[COP0_CAUSE] |= 0x80000000u;
                COP0[COP0_EPC] = (uint)(PC - 8);
            }
            else
            {
                COP0[COP0_CAUSE] &= ~0x80000000u;
                COP0[COP0_EPC] = (uint)(PC - 4);
            }
        }

        ExceptionCount++;
        COP0[COP0_STATUS] |= 2; // set EXL
        _exception = true;
        _nextIsDelay = false;
        _inDelaySlot = false;

        // Exception vector selection (matching reference exactly)
        if (exCode == 2 || exCode == 3) // TLB miss
        {
            // TLB refill uses different vector when !oldExl
            if (!oldExl)
                PC = 0x80000000;
            else
                PC = 0x80000180;
        }
        else
        {
            PC = 0x80000180;
        }
        CheckInterrupts();
    }

    bool Cop1Usable => (COP0[COP0_STATUS] & (1u << 29)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Step()
    {
        GPR[0] = 0;

        bool isDelay = _nextIsDelay;
        _nextIsDelay = false;
        _inDelaySlot = isDelay;

        uint paddr = Bus.TranslateVirtual(PC);
        uint instr = Bus.Read32(paddr);
        PC += 4;
        TotalCycles++;

        if (_interruptPending)
        {
            RaiseException(0);
            _interruptPending = false;
            return;
        }

        uint wired = COP0[COP0_WIRED] & 31;
        COP0[COP0_RANDOM] = (COP0[COP0_RANDOM] > wired) ? COP0[COP0_RANDOM] - 1 : 31;

        Execute(instr);
        GPR[0] = 0;

        if (isDelay && !_exception)
            PC = _delayTarget;
        _exception = false;

        _countInternal++;
        COP0[COP0_COUNT] = (uint)(_countInternal >> 1);

        if (_countInternal >= _timerTarget)
        {
            _timerTarget = _countInternal + ((ulong)uint.MaxValue << 1);
            TimerFireCount++;
            COP0[COP0_CAUSE] |= (1u << 15);
            CheckInterrupts();
            if (TimerFireCount <= 20)
                N64Machine.DiagWrite($"[TMR] Fire #{TimerFireCount}: COUNT=0x{COP0[COP0_COUNT]:X8} COMPARE=0x{COP0[COP0_COMPARE]:X8} SR=0x{COP0[COP0_STATUS]:X8} intPend={_interruptPending}");
        }
    }

    void Execute(uint instr)
    {
        if (instr == 0) return;

        int op = (int)(instr >> 26);
        switch (op)
        {
            case 0x00: ExecSpecial(instr); break;
            case 0x01: ExecRegimm(instr); break;
            case 0x02: ExecJ(instr); break;
            case 0x03: ExecJal(instr); break;
            case 0x04: ExecBeq(instr); break;
            case 0x05: ExecBne(instr); break;
            case 0x06: ExecBlez(instr); break;
            case 0x07: ExecBgtz(instr); break;
            case 0x08: ExecAddi(instr); break;
            case 0x09: ExecAddiu(instr); break;
            case 0x0A: ExecSlti(instr); break;
            case 0x0B: ExecSltiu(instr); break;
            case 0x0C: ExecAndi(instr); break;
            case 0x0D: ExecOri(instr); break;
            case 0x0E: ExecXori(instr); break;
            case 0x0F: ExecLui(instr); break;
            case 0x10: ExecCop0(instr); break;
            case 0x11:
                if (!Cop1Usable) { RaiseException(11, 1); break; }
                ExecCop1(instr); break;
            case 0x14: ExecBeql(instr); break;
            case 0x15: ExecBnel(instr); break;
            case 0x16: ExecBlezl(instr); break;
            case 0x17: ExecBgtzl(instr); break;
            case 0x18: ExecDaddi(instr); break;
            case 0x19: ExecDaddiu(instr); break;
            case 0x1A: ExecLdl(instr); break;
            case 0x1B: ExecLdr(instr); break;
            case 0x20: ExecLb(instr); break;
            case 0x21: ExecLh(instr); break;
            case 0x22: ExecLwl(instr); break;
            case 0x23: ExecLw(instr); break;
            case 0x24: ExecLbu(instr); break;
            case 0x25: ExecLhu(instr); break;
            case 0x26: ExecLwr(instr); break;
            case 0x27: ExecLwu(instr); break;
            case 0x28: ExecSb(instr); break;
            case 0x29: ExecSh(instr); break;
            case 0x2A: ExecSwl(instr); break;
            case 0x2B: ExecSw(instr); break;
            case 0x2C: ExecSdl(instr); break;
            case 0x2D: ExecSdr(instr); break;
            case 0x2E: ExecSwr(instr); break;
            case 0x2F: break; // CACHE
            case 0x30: ExecLl(instr); break;
            case 0x31:
                if (!Cop1Usable) { RaiseException(11, 1); break; }
                ExecLwc1(instr); break;
            case 0x34: ExecLld(instr); break;
            case 0x35:
                if (!Cop1Usable) { RaiseException(11, 1); break; }
                ExecLdc1(instr); break;
            case 0x37: ExecLd(instr); break;
            case 0x38: ExecSc(instr); break;
            case 0x39:
                if (!Cop1Usable) { RaiseException(11, 1); break; }
                ExecSwc1(instr); break;
            case 0x3C: ExecScd(instr); break;
            case 0x3D:
                if (!Cop1Usable) { RaiseException(11, 1); break; }
                ExecSdc1(instr); break;
            case 0x3F: ExecSd(instr); break;
            default:
                BadOpCount++;
                LastBadOp = instr;
                LastBadPC = PC - 4;
                if (BadOpCount <= 5)
                    N64Machine.DiagWrite($"[BADOP] PC=0x{PC - 4:X8} instr=0x{instr:X8} opcode={op}");
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Rs(uint i) => (int)((i >> 21) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Rt(uint i) => (int)((i >> 16) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Rd(uint i) => (int)((i >> 11) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Sa(uint i) => (int)((i >> 6) & 0x1F);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short Imm(uint i) => (short)(i & 0xFFFF);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Target(uint i) => i & 0x03FF_FFFF;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long SE32(long val) => (int)val;

    void DoBranch(ulong target)
    {
        _delayTarget = target;
        _nextIsDelay = true;
    }

    void DoBranchLikely(ulong target, bool condition)
    {
        if (condition)
            DoBranch(target);
        else
            PC += 4; // skip delay slot
    }

    uint VAddr(uint instr) => (uint)(GPR[Rs(instr)] + Imm(instr));
    uint PAddr(uint instr) => Bus.TranslateVirtual(VAddr(instr));

    // ========== SPECIAL (opcode 0) ==========
    void ExecSpecial(uint instr)
    {
        int func = (int)(instr & 0x3F);
        switch (func)
        {
            case 0x00: GPR[Rd(instr)] = SE32((long)(uint)((int)GPR[Rt(instr)] << Sa(instr))); break;
            case 0x02: GPR[Rd(instr)] = SE32((long)((uint)(int)GPR[Rt(instr)] >> Sa(instr))); break;
            case 0x03: GPR[Rd(instr)] = SE32((long)((int)GPR[Rt(instr)] >> Sa(instr))); break;
            case 0x04: GPR[Rd(instr)] = SE32((long)(uint)((int)GPR[Rt(instr)] << ((int)GPR[Rs(instr)] & 31))); break;
            case 0x06: GPR[Rd(instr)] = SE32((long)((uint)(int)GPR[Rt(instr)] >> ((int)GPR[Rs(instr)] & 31))); break;
            case 0x07: GPR[Rd(instr)] = SE32((long)((int)GPR[Rt(instr)] >> ((int)GPR[Rs(instr)] & 31))); break;
            case 0x08: DoBranch((ulong)GPR[Rs(instr)]); break; // JR
            case 0x09: // JALR
            {
                ulong tgt = (ulong)GPR[Rs(instr)];
                GPR[Rd(instr)] = (long)(PC + 4);
                DoBranch(tgt);
                break;
            }
            case 0x0C: RaiseException(8); break; // SYSCALL
            case 0x0D: BreakExcCount++; RaiseException(9); break; // BREAK
            case 0x0F: break; // SYNC
            case 0x10: GPR[Rd(instr)] = Hi; break;
            case 0x11: Hi = GPR[Rs(instr)]; break;
            case 0x12: GPR[Rd(instr)] = Lo; break;
            case 0x13: Lo = GPR[Rs(instr)]; break;
            case 0x14: GPR[Rd(instr)] = GPR[Rt(instr)] << ((int)GPR[Rs(instr)] & 63); break;
            case 0x16: GPR[Rd(instr)] = (long)((ulong)GPR[Rt(instr)] >> ((int)GPR[Rs(instr)] & 63)); break;
            case 0x17: GPR[Rd(instr)] = GPR[Rt(instr)] >> ((int)GPR[Rs(instr)] & 63); break;
            case 0x18: { long r = (long)(int)GPR[Rs(instr)] * (int)GPR[Rt(instr)]; Lo = SE32(r); Hi = SE32(r >> 32); break; }
            case 0x19: { ulong r = (ulong)(uint)GPR[Rs(instr)] * (uint)GPR[Rt(instr)]; Lo = SE32((long)r); Hi = SE32((long)(r >> 32)); break; }
            case 0x1A: // DIV
            {
                int n = (int)GPR[Rs(instr)], d = (int)GPR[Rt(instr)];
                if (d == 0) { Lo = n >= 0 ? -1 : 1; Hi = SE32(n); }
                else if (n == int.MinValue && d == -1) { Lo = SE32(n); Hi = 0; }
                else { Lo = SE32(n / d); Hi = SE32(n % d); }
                break;
            }
            case 0x1B: // DIVU
            {
                uint n = (uint)GPR[Rs(instr)], d = (uint)GPR[Rt(instr)];
                if (d == 0) { Lo = SE32(unchecked((long)(int)0xFFFFFFFF)); Hi = SE32((long)(int)n); }
                else { Lo = SE32((long)(int)(n / d)); Hi = SE32((long)(int)(n % d)); }
                break;
            }
            case 0x1C: { long lo = Math.BigMul(GPR[Rs(instr)], GPR[Rt(instr)], out long hi); Lo = lo; Hi = hi; break; }
            case 0x1D: { ulong lo = Math.BigMul((ulong)GPR[Rs(instr)], (ulong)GPR[Rt(instr)], out ulong hi); Lo = (long)lo; Hi = (long)hi; break; }
            case 0x1E: { long n = GPR[Rs(instr)], d = GPR[Rt(instr)]; if (d == 0) { Lo = n >= 0 ? -1 : 1; Hi = n; } else if (n == long.MinValue && d == -1) { Lo = n; Hi = 0; } else { Lo = n / d; Hi = n % d; } break; }
            case 0x1F: { ulong n = (ulong)GPR[Rs(instr)], d = (ulong)GPR[Rt(instr)]; if (d == 0) { Lo = -1; Hi = (long)n; } else { Lo = (long)(n / d); Hi = (long)(n % d); } break; }
            case 0x20: case 0x21: GPR[Rd(instr)] = SE32((long)((int)GPR[Rs(instr)] + (int)GPR[Rt(instr)])); break;
            case 0x22: case 0x23: GPR[Rd(instr)] = SE32((long)((int)GPR[Rs(instr)] - (int)GPR[Rt(instr)])); break;
            case 0x24: GPR[Rd(instr)] = GPR[Rs(instr)] & GPR[Rt(instr)]; break;
            case 0x25: GPR[Rd(instr)] = GPR[Rs(instr)] | GPR[Rt(instr)]; break;
            case 0x26: GPR[Rd(instr)] = GPR[Rs(instr)] ^ GPR[Rt(instr)]; break;
            case 0x27: GPR[Rd(instr)] = ~(GPR[Rs(instr)] | GPR[Rt(instr)]); break;
            case 0x2A: GPR[Rd(instr)] = GPR[Rs(instr)] < GPR[Rt(instr)] ? 1 : 0; break;
            case 0x2B: GPR[Rd(instr)] = (ulong)GPR[Rs(instr)] < (ulong)GPR[Rt(instr)] ? 1 : 0; break;
            case 0x2C: case 0x2D: GPR[Rd(instr)] = GPR[Rs(instr)] + GPR[Rt(instr)]; break;
            case 0x2E: case 0x2F: GPR[Rd(instr)] = GPR[Rs(instr)] - GPR[Rt(instr)]; break;
            case 0x30: if (GPR[Rs(instr)] >= GPR[Rt(instr)]) RaiseException(13); break;
            case 0x31: if ((ulong)GPR[Rs(instr)] >= (ulong)GPR[Rt(instr)]) RaiseException(13); break;
            case 0x32: if (GPR[Rs(instr)] < GPR[Rt(instr)]) RaiseException(13); break;
            case 0x33: if ((ulong)GPR[Rs(instr)] < (ulong)GPR[Rt(instr)]) RaiseException(13); break;
            case 0x34: if (GPR[Rs(instr)] == GPR[Rt(instr)]) RaiseException(13); break;
            case 0x36: if (GPR[Rs(instr)] != GPR[Rt(instr)]) RaiseException(13); break;
            case 0x38: GPR[Rd(instr)] = GPR[Rt(instr)] << Sa(instr); break;
            case 0x3A: GPR[Rd(instr)] = (long)((ulong)GPR[Rt(instr)] >> Sa(instr)); break;
            case 0x3B: GPR[Rd(instr)] = GPR[Rt(instr)] >> Sa(instr); break;
            case 0x3C: GPR[Rd(instr)] = GPR[Rt(instr)] << (Sa(instr) + 32); break;
            case 0x3E: GPR[Rd(instr)] = (long)((ulong)GPR[Rt(instr)] >> (Sa(instr) + 32)); break;
            case 0x3F: GPR[Rd(instr)] = GPR[Rt(instr)] >> (Sa(instr) + 32); break;
        }
    }

    // ========== REGIMM (opcode 1) ==========
    void ExecRegimm(uint instr)
    {
        int rt = Rt(instr);
        ulong target = (ulong)((long)PC + ((long)Imm(instr) << 2));

        switch (rt)
        {
            case 0x00: if (GPR[Rs(instr)] < 0) DoBranch(target); break; // BLTZ
            case 0x01: if (GPR[Rs(instr)] >= 0) DoBranch(target); break; // BGEZ
            case 0x02: DoBranchLikely(target, GPR[Rs(instr)] < 0); break;
            case 0x03: DoBranchLikely(target, GPR[Rs(instr)] >= 0); break;
            case 0x10: GPR[31] = (long)(PC + 4); if (GPR[Rs(instr)] < 0) DoBranch(target); break;
            case 0x11: GPR[31] = (long)(PC + 4); if (GPR[Rs(instr)] >= 0) DoBranch(target); break;
            case 0x12: GPR[31] = (long)(PC + 4); DoBranchLikely(target, GPR[Rs(instr)] < 0); break;
            case 0x13: GPR[31] = (long)(PC + 4); DoBranchLikely(target, GPR[Rs(instr)] >= 0); break;
        }
    }

    // ========== Jumps ==========
    void ExecJ(uint instr)
    {
        DoBranch(((PC - 4) & 0xFFFF_FFFF_F000_0000) | ((ulong)Target(instr) << 2));
    }

    void ExecJal(uint instr)
    {
        GPR[31] = (long)(PC + 4);
        DoBranch(((PC - 4) & 0xFFFF_FFFF_F000_0000) | ((ulong)Target(instr) << 2));
    }

    // ========== Branches (non-taken: delay slot still executes, no DoBranch) ==========
    void ExecBeq(uint instr)
    {
        ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2));
        if (GPR[Rs(instr)] == GPR[Rt(instr)]) DoBranch(t);
    }

    void ExecBne(uint instr)
    {
        ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2));
        if (GPR[Rs(instr)] != GPR[Rt(instr)]) DoBranch(t);
    }

    void ExecBlez(uint instr)
    {
        ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2));
        if (GPR[Rs(instr)] <= 0) DoBranch(t);
    }

    void ExecBgtz(uint instr)
    {
        ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2));
        if (GPR[Rs(instr)] > 0) DoBranch(t);
    }

    void ExecBeql(uint instr) { ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2)); DoBranchLikely(t, GPR[Rs(instr)] == GPR[Rt(instr)]); }
    void ExecBnel(uint instr) { ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2)); DoBranchLikely(t, GPR[Rs(instr)] != GPR[Rt(instr)]); }
    void ExecBlezl(uint instr) { ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2)); DoBranchLikely(t, GPR[Rs(instr)] <= 0); }
    void ExecBgtzl(uint instr) { ulong t = (ulong)((long)PC + ((long)Imm(instr) << 2)); DoBranchLikely(t, GPR[Rs(instr)] > 0); }

    // ========== Immediate ALU ==========
    void ExecAddi(uint instr) => GPR[Rt(instr)] = SE32((long)((int)GPR[Rs(instr)] + Imm(instr)));
    void ExecAddiu(uint instr) => GPR[Rt(instr)] = SE32((long)((int)GPR[Rs(instr)] + Imm(instr)));
    void ExecSlti(uint instr) => GPR[Rt(instr)] = GPR[Rs(instr)] < (long)Imm(instr) ? 1 : 0;
    void ExecSltiu(uint instr) => GPR[Rt(instr)] = (ulong)GPR[Rs(instr)] < (ulong)(long)Imm(instr) ? 1 : 0;
    void ExecAndi(uint instr) => GPR[Rt(instr)] = GPR[Rs(instr)] & (ushort)Imm(instr);
    void ExecOri(uint instr) => GPR[Rt(instr)] = GPR[Rs(instr)] | (ushort)Imm(instr);
    void ExecXori(uint instr) => GPR[Rt(instr)] = GPR[Rs(instr)] ^ (ushort)Imm(instr);
    void ExecLui(uint instr) => GPR[Rt(instr)] = SE32((long)((int)Imm(instr) << 16));
    void ExecDaddi(uint instr) => GPR[Rt(instr)] = GPR[Rs(instr)] + (long)Imm(instr);
    void ExecDaddiu(uint instr) => GPR[Rt(instr)] = GPR[Rs(instr)] + (long)Imm(instr);

    // ========== Loads ==========
    void ExecLb(uint instr) { GPR[Rt(instr)] = (sbyte)Bus.Read8(PAddr(instr)); }
    void ExecLbu(uint instr) { GPR[Rt(instr)] = Bus.Read8(PAddr(instr)); }
    void ExecLh(uint instr) { GPR[Rt(instr)] = (short)Bus.Read16(PAddr(instr)); }
    void ExecLhu(uint instr) { GPR[Rt(instr)] = Bus.Read16(PAddr(instr)); }
    void ExecLw(uint instr) { GPR[Rt(instr)] = (int)Bus.Read32(PAddr(instr)); }
    void ExecLwu(uint instr) { GPR[Rt(instr)] = Bus.Read32(PAddr(instr)); }
    void ExecLd(uint instr) { GPR[Rt(instr)] = (long)Bus.Read64(PAddr(instr)); }

    void ExecLwl(uint instr)
    {
        uint va = VAddr(instr);
        uint w = Bus.Read32(Bus.TranslateVirtual(va & ~3u));
        int b = (int)(va & 3);
        uint old = (uint)(int)GPR[Rt(instr)];
        GPR[Rt(instr)] = SE32((long)((w << _lwlShift[b]) | (old & _lwlMask[b])));
    }

    void ExecLwr(uint instr)
    {
        uint va = VAddr(instr);
        uint w = Bus.Read32(Bus.TranslateVirtual(va & ~3u));
        int b = (int)(va & 3);
        uint old = (uint)(int)GPR[Rt(instr)];
        GPR[Rt(instr)] = SE32((long)((w >> _lwrShift[b]) | (old & _lwrMask[b])));
    }

    void ExecLdl(uint instr)
    {
        uint va = VAddr(instr);
        ulong w = Bus.Read64(Bus.TranslateVirtual(va & ~7u));
        int b = (int)(va & 7);
        ulong old = (ulong)GPR[Rt(instr)];
        int shift = b * 8;
        ulong mask = shift == 0 ? 0UL : (ulong.MaxValue >> (64 - shift));
        GPR[Rt(instr)] = (long)((w << shift) | (old & mask));
    }

    void ExecLdr(uint instr)
    {
        uint va = VAddr(instr);
        ulong w = Bus.Read64(Bus.TranslateVirtual(va & ~7u));
        int b = (int)(va & 7);
        ulong old = (ulong)GPR[Rt(instr)];
        int shift = (7 - b) * 8;
        ulong mask = shift == 0 ? 0UL : (ulong.MaxValue << (64 - shift));
        GPR[Rt(instr)] = (long)((w >> shift) | (old & mask));
    }

    void ExecLl(uint instr) { ExecLw(instr); LLBit = 1; COP0[COP0_LLADDR] = PAddr(instr) >> 4; }
    void ExecLld(uint instr) { ExecLd(instr); LLBit = 1; }

    // ========== Stores ==========
    void ExecSb(uint instr) { Bus.Write8(PAddr(instr), (byte)GPR[Rt(instr)]); }
    void ExecSh(uint instr) { Bus.Write16(PAddr(instr), (ushort)GPR[Rt(instr)]); }
    void ExecSw(uint instr) { Bus.Write32(PAddr(instr), (uint)GPR[Rt(instr)]); }
    void ExecSd(uint instr) { Bus.Write64(PAddr(instr), (ulong)GPR[Rt(instr)]); }

    void ExecSwl(uint instr)
    {
        uint va = VAddr(instr);
        uint pa = Bus.TranslateVirtual(va & ~3u);
        uint old = Bus.Read32(pa);
        int b = (int)(va & 3);
        uint val = (uint)(int)GPR[Rt(instr)];
        Bus.Write32(pa, (old & _swlMask[b]) | (val >> _swlShift[b]));
    }

    void ExecSwr(uint instr)
    {
        uint va = VAddr(instr);
        uint pa = Bus.TranslateVirtual(va & ~3u);
        uint old = Bus.Read32(pa);
        int b = (int)(va & 3);
        uint val = (uint)(int)GPR[Rt(instr)];
        Bus.Write32(pa, (old & _swrMask[b]) | (val << _swrShift[b]));
    }

    void ExecSdl(uint instr)
    {
        uint va = VAddr(instr);
        uint pa = Bus.TranslateVirtual(va & ~7u);
        ulong old = Bus.Read64(pa);
        int b = (int)(va & 7);
        ulong val = (ulong)GPR[Rt(instr)];
        int shift = b * 8;
        ulong mask = shift == 0 ? 0UL : (ulong.MaxValue << (64 - shift));
        Bus.Write64(pa, (old & mask) | (val >> shift));
    }

    void ExecSdr(uint instr)
    {
        uint va = VAddr(instr);
        uint pa = Bus.TranslateVirtual(va & ~7u);
        ulong old = Bus.Read64(pa);
        int b = (int)(va & 7);
        ulong val = (ulong)GPR[Rt(instr)];
        int shift = (7 - b) * 8;
        ulong mask = shift == 0 ? 0UL : (ulong.MaxValue >> (64 - shift));
        Bus.Write64(pa, (old & mask) | (val << shift));
    }

    void ExecSc(uint instr) { if (LLBit != 0) { Bus.Write32(PAddr(instr), (uint)GPR[Rt(instr)]); GPR[Rt(instr)] = 1; } else GPR[Rt(instr)] = 0; LLBit = 0; }
    void ExecScd(uint instr) { if (LLBit != 0) { Bus.Write64(PAddr(instr), (ulong)GPR[Rt(instr)]); GPR[Rt(instr)] = 1; } else GPR[Rt(instr)] = 0; LLBit = 0; }

    // ========== COP0 ==========
    void ExecCop0(uint instr)
    {
        int rs = Rs(instr);
        switch (rs)
        {
            case 0x00: GPR[Rt(instr)] = SE32(COP0[Rd(instr)]); break;
            case 0x01: GPR[Rt(instr)] = (long)COP0[Rd(instr)]; break;
            case 0x04: WriteCop0(Rd(instr), (uint)GPR[Rt(instr)]); break;
            case 0x05: WriteCop0(Rd(instr), (uint)GPR[Rt(instr)]); break;
            case 0x10: ExecCop0Co(instr); break;
        }
    }

    void WriteCop0(int reg, uint val)
    {
        switch (reg)
        {
            case COP0_INDEX: COP0[reg] = val & 0x8000003F; break;
            case COP0_ENTRYLO0: COP0[reg] = val & 0x3FFFFFFF; break;
            case COP0_ENTRYLO1: COP0[reg] = val & 0x3FFFFFFF; break;
            case COP0_CONTEXT: COP0[reg] = (COP0[reg] & 0x007FFFF0) | (val & 0xFF800000); break;
            case COP0_PAGEMASK: COP0[reg] = val & 0x01FFE000; break;
            case COP0_WIRED: COP0[reg] = val & 0x3F; COP0[COP0_RANDOM] = 31; break;
            case COP0_COUNT:
                {
                    ulong oldInternal = _countInternal;
                    _countInternal = (ulong)val << 1;
                    COP0[reg] = val;
                    // Translate timer target to new count base (like gopher64)
                    if (_timerTarget != ulong.MaxValue)
                    {
                        long delta = (long)(_timerTarget - oldInternal);
                        _timerTarget = _countInternal + (ulong)delta;
                    }
                }
                break;
            case COP0_ENTRYHI: COP0[reg] = val & 0xFFFFE0FF; break;
            case COP0_COMPARE:
                COP0[reg] = val;
                CompareWriteCount++;
                {
                    uint currentCount = (uint)(_countInternal >> 1);
                    uint diff = val - currentCount; // wrapping subtraction (like gopher64)
                    if (diff == 0) diff = uint.MaxValue;
                    if (diff > 0x80000000u)
                        _timerTarget = _countInternal + 50000; // behind: fire after short delay
                    else
                        _timerTarget = _countInternal + ((ulong)diff << 1);
                }
                COP0[COP0_CAUSE] &= ~(1u << 15); // clear IP7
                CheckInterrupts();
                if (CompareWriteCount <= 20)
                    N64Machine.DiagWrite($"[TMR] COMPARE write #{CompareWriteCount}: new=0x{val:X8} COUNT=0x{COP0[COP0_COUNT]:X8} SR=0x{COP0[COP0_STATUS]:X8} target=0x{_timerTarget:X}");
                break;
            case COP0_STATUS:
                COP0[reg] = (COP0[reg] & ~0xFF57FFFFu) | (val & 0xFF57FFFFu);
                CheckInterrupts();
                break;
            case COP0_CAUSE: COP0[reg] = (COP0[reg] & ~0x300u) | (val & 0x300u); CheckInterrupts(); break;
            default: COP0[reg] = val; break;
        }
    }

    void ExecCop0Co(uint instr)
    {
        switch ((int)(instr & 0x3F))
        {
            case 0x01: // TLBR
            {
                ref var e = ref Tlb[(int)(COP0[COP0_INDEX] & 31)];
                COP0[COP0_PAGEMASK] = e.PageMask; COP0[COP0_ENTRYHI] = e.EntryHi;
                COP0[COP0_ENTRYLO0] = e.EntryLo0; COP0[COP0_ENTRYLO1] = e.EntryLo1;
                break;
            }
            case 0x02: // TLBWI
            {
                ref var e = ref Tlb[(int)(COP0[COP0_INDEX] & 31)];
                e.PageMask = COP0[COP0_PAGEMASK]; e.EntryHi = COP0[COP0_ENTRYHI];
                e.EntryLo0 = COP0[COP0_ENTRYLO0]; e.EntryLo1 = COP0[COP0_ENTRYLO1];
                break;
            }
            case 0x06: // TLBWR
            {
                ref var e = ref Tlb[(int)(COP0[COP0_RANDOM] & 31)];
                e.PageMask = COP0[COP0_PAGEMASK]; e.EntryHi = COP0[COP0_ENTRYHI];
                e.EntryLo0 = COP0[COP0_ENTRYLO0]; e.EntryLo1 = COP0[COP0_ENTRYLO1];
                break;
            }
            case 0x08: // TLBP
            {
                uint hi = COP0[COP0_ENTRYHI];
                COP0[COP0_INDEX] = 0x80000000;
                for (int i = 0; i < 32; i++)
                {
                    uint mask = ~Tlb[i].PageMask & 0xFFFFE000;
                    if ((Tlb[i].EntryHi & mask) == (hi & mask) &&
                        ((Tlb[i].EntryHi & 0xFF) == (hi & 0xFF) || ((Tlb[i].EntryLo0 | Tlb[i].EntryLo1) & 1) != 0))
                    { COP0[COP0_INDEX] = (uint)i; break; }
                }
                break;
            }
            case 0x18: // ERET
                EretCount++;
                if ((COP0[COP0_STATUS] & 4) != 0)
                { PC = COP0[COP0_ERROREPC]; COP0[COP0_STATUS] &= ~4u; }
                else
                { PC = COP0[COP0_EPC]; COP0[COP0_STATUS] &= ~2u; }
                LLBit = 0;
                _nextIsDelay = false;
                _inDelaySlot = false;
                CheckInterrupts();
                break;
        }
    }

    // ========== COP1 (FPU) ==========
    void ExecCop1(uint instr)
    {
        int rs = Rs(instr);
        switch (rs)
        {
            case 0x00: GPR[Rt(instr)] = SE32(GetFpr32(Rd(instr))); break;
            case 0x01: GPR[Rt(instr)] = FPR[Rd(instr)]; break;
            case 0x02: GPR[Rt(instr)] = Rd(instr) == 0 ? (long)FCR0 : Rd(instr) == 31 ? (long)FCR31 : 0; break;
            case 0x04: SetFpr32(Rd(instr), (uint)GPR[Rt(instr)]); break;
            case 0x05: FPR[Rd(instr)] = GPR[Rt(instr)]; break;
            case 0x06: if (Rd(instr) == 31) FCR31 = (uint)GPR[Rt(instr)] & 0x0183FFFF; break;
            case 0x08: ExecBc1(instr); break;
            case 0x10: ExecFpuS(instr); break;
            case 0x11: ExecFpuD(instr); break;
            case 0x14: ExecFpuW(instr); break;
            case 0x15: ExecFpuL(instr); break;
        }
    }

    void ExecBc1(uint instr)
    {
        bool cond = (FCR31 & (1u << 23)) != 0;
        ulong target = (ulong)((long)PC + ((long)Imm(instr) << 2));
        int nd = (int)((instr >> 17) & 1);
        int tf = (int)((instr >> 16) & 1);
        bool take = tf == 1 ? cond : !cond;

        if (nd == 0)
        { if (take) DoBranch(target); }
        else
            DoBranchLikely(target, take);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] uint GetFpr32(int r) => (uint)FPR[r];
    [MethodImpl(MethodImplOptions.AggressiveInlining)] void SetFpr32(int r, uint v) { FPR[r] = (int)v; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] float GetFprS(int r) => BitConverter.Int32BitsToSingle((int)GetFpr32(r));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] void SetFprS(int r, float v) { SetFpr32(r, (uint)BitConverter.SingleToInt32Bits(v)); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] double GetFprD(int r) => BitConverter.Int64BitsToDouble(FPR[r]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] void SetFprD(int r, double v) { FPR[r] = BitConverter.DoubleToInt64Bits(v); }

    void ExecFpuS(uint instr)
    {
        int func = (int)(instr & 0x3F), fd = Sa(instr), fs = Rd(instr), ft = Rt(instr);
        switch (func)
        {
            case 0x00: SetFprS(fd, GetFprS(fs) + GetFprS(ft)); break;
            case 0x01: SetFprS(fd, GetFprS(fs) - GetFprS(ft)); break;
            case 0x02: SetFprS(fd, GetFprS(fs) * GetFprS(ft)); break;
            case 0x03: SetFprS(fd, GetFprS(fs) / GetFprS(ft)); break;
            case 0x04: SetFprS(fd, MathF.Sqrt(GetFprS(fs))); break;
            case 0x05: SetFprS(fd, MathF.Abs(GetFprS(fs))); break;
            case 0x06: SetFprS(fd, GetFprS(fs)); break;
            case 0x07: SetFprS(fd, -GetFprS(fs)); break;
            case 0x08: FPR[fd] = (long)MathF.Round(GetFprS(fs)); break;
            case 0x09: FPR[fd] = (long)MathF.Truncate(GetFprS(fs)); break;
            case 0x0A: FPR[fd] = (long)MathF.Ceiling(GetFprS(fs)); break;
            case 0x0B: FPR[fd] = (long)MathF.Floor(GetFprS(fs)); break;
            case 0x0C: SetFpr32(fd, (uint)(int)MathF.Round(GetFprS(fs))); break;
            case 0x0D: SetFpr32(fd, (uint)(int)MathF.Truncate(GetFprS(fs))); break;
            case 0x0E: SetFpr32(fd, (uint)(int)MathF.Ceiling(GetFprS(fs))); break;
            case 0x0F: SetFpr32(fd, (uint)(int)MathF.Floor(GetFprS(fs))); break;
            case 0x21: SetFprD(fd, (double)GetFprS(fs)); break;
            case 0x24: SetFpr32(fd, (uint)(int)GetFprS(fs)); break;
            case 0x25: FPR[fd] = (long)GetFprS(fs); break;
            default: if (func >= 0x30) FpuCompare(GetFprS(fs), GetFprS(ft), func); break;
        }
    }

    void ExecFpuD(uint instr)
    {
        int func = (int)(instr & 0x3F), fd = Sa(instr), fs = Rd(instr), ft = Rt(instr);
        switch (func)
        {
            case 0x00: SetFprD(fd, GetFprD(fs) + GetFprD(ft)); break;
            case 0x01: SetFprD(fd, GetFprD(fs) - GetFprD(ft)); break;
            case 0x02: SetFprD(fd, GetFprD(fs) * GetFprD(ft)); break;
            case 0x03: SetFprD(fd, GetFprD(fs) / GetFprD(ft)); break;
            case 0x04: SetFprD(fd, Math.Sqrt(GetFprD(fs))); break;
            case 0x05: SetFprD(fd, Math.Abs(GetFprD(fs))); break;
            case 0x06: SetFprD(fd, GetFprD(fs)); break;
            case 0x07: SetFprD(fd, -GetFprD(fs)); break;
            case 0x0C: SetFpr32(fd, (uint)(int)Math.Round(GetFprD(fs))); break;
            case 0x0D: SetFpr32(fd, (uint)(int)Math.Truncate(GetFprD(fs))); break;
            case 0x0E: SetFpr32(fd, (uint)(int)Math.Ceiling(GetFprD(fs))); break;
            case 0x0F: SetFpr32(fd, (uint)(int)Math.Floor(GetFprD(fs))); break;
            case 0x20: SetFprS(fd, (float)GetFprD(fs)); break;
            case 0x24: SetFpr32(fd, (uint)(int)GetFprD(fs)); break;
            case 0x25: FPR[fd] = (long)GetFprD(fs); break;
            default: if (func >= 0x30) FpuCompare(GetFprD(fs), GetFprD(ft), func); break;
        }
    }

    void ExecFpuW(uint instr) { int f = (int)(instr & 0x3F); if (f == 0x20) SetFprS(Sa(instr), (float)(int)GetFpr32(Rd(instr))); else if (f == 0x21) SetFprD(Sa(instr), (double)(int)GetFpr32(Rd(instr))); }
    void ExecFpuL(uint instr) { int f = (int)(instr & 0x3F); if (f == 0x20) SetFprS(Sa(instr), (float)FPR[Rd(instr)]); else if (f == 0x21) SetFprD(Sa(instr), (double)FPR[Rd(instr)]); }

    void FpuCompare(double a, double b, int func)
    {
        bool less = a < b, equal = a == b, unord = double.IsNaN(a) || double.IsNaN(b);
        bool cond = ((func & 4) != 0 && less) || ((func & 2) != 0 && equal) || ((func & 1) != 0 && unord);
        if (cond) FCR31 |= (1u << 23); else FCR31 &= ~(1u << 23);
    }

    void ExecLwc1(uint instr) { SetFpr32(Rt(instr), Bus.Read32(PAddr(instr))); }
    void ExecSwc1(uint instr) { Bus.Write32(PAddr(instr), GetFpr32(Rt(instr))); }
    void ExecLdc1(uint instr) { FPR[Rt(instr)] = (long)Bus.Read64(PAddr(instr)); }
    void ExecSdc1(uint instr) { Bus.Write64(PAddr(instr), (ulong)FPR[Rt(instr)]); }
}
