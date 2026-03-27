using System.Runtime.CompilerServices;

namespace PspEmu;

/// <summary>
/// VFPU (Vector Floating Point Unit) implementation for the Allegrex.
/// 128 float registers organized as 8 matrices of 4x4.
/// Register naming: S (scalar), C/R (column/row pair), M (matrix).
/// </summary>
sealed partial class Allegrex
{
    // VFPU prefix state
    uint _vpfxs, _vpfxt, _vpfxd;
    bool _vpfxsActive, _vpfxtActive, _vpfxdActive;

    // ── VFPU register decoding ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int VfpuRegIndex(int mtx, int col, int row) => (mtx << 4) | (col << 2) | row;

    void GetVectorRegs(int reg, int size, Span<int> indices)
    {
        int mtx = (reg >> 2) & 7;
        int col = reg & 3;
        int row = (reg >> 5) & 3;
        bool transposed = (reg & 0x20) != 0;

        for (int i = 0; i < size; i++)
        {
            if (transposed)
                indices[i] = VfpuRegIndex(mtx, (col + i) & 3, row);
            else
                indices[i] = VfpuRegIndex(mtx, col, (row + i) & 3);
        }
    }

    float ReadVfpuWithPrefix(int idx, int lane, bool usePrefix, uint prefix)
    {
        float val = Vpr[idx & 127];

        if (!usePrefix) return val;

        int abs = (int)((prefix >> (8 + lane)) & 1);
        int cst = (int)((prefix >> (12 + lane)) & 1);
        int negate = (int)((prefix >> (16 + lane)) & 1);
        int swz = (int)((prefix >> (lane * 2)) & 3);

        if (cst != 0)
        {
            val = swz switch
            {
                0 => 0f,
                1 => 1f,
                2 => 2f,
                3 => 0.5f,
                _ => 0f,
            };
        }

        if (abs != 0) val = MathF.Abs(val);
        if (negate != 0) val = -val;

        return val;
    }

    void WriteVfpuWithPrefix(int idx, int lane, float val, bool usePrefix, uint prefix)
    {
        if (usePrefix)
        {
            int sat = (int)((prefix >> (lane * 2)) & 3);
            int mask = (int)((prefix >> (8 + lane)) & 1);

            val = sat switch
            {
                1 => Math.Clamp(val, 0f, 1f),
                3 => Math.Clamp(val, -1f, 1f),
                _ => val,
            };

            if (mask != 0) return; // masked out
        }

        Vpr[idx & 127] = val;
    }

    void ClearPrefixes()
    {
        _vpfxsActive = false;
        _vpfxtActive = false;
        _vpfxdActive = false;
    }

    // ── Main VFPU dispatch (COP2, opcode 0x12) ──

    void ExecVfpu(uint instr)
    {
        uint rs = Rs(instr);

        // MFVC/MTVC
        if (rs == 0x03) // MFVC
        {
            uint rt = Rt(instr);
            uint spec = (instr >> 1) & 0x7F;
            if (rt != 0) Gpr[rt] = spec switch
            {
                128 => VfpuCc,
                _ => 0,
            };
            return;
        }
        if (rs == 0x07) // MTVC
        {
            uint spec = (instr >> 1) & 0x7F;
            if (spec == 128) VfpuCc = Gpr[Rt(instr)];
            return;
        }

        // BVF/BVT (VFPU branch)
        if (rs == 0x08)
        {
            uint imm = Rt(instr);
            bool tf = (imm & 0x10) != 0;
            int cc = (int)(imm & 7);
            bool cond = ((VfpuCc >> cc) & 1) != 0;
            if (tf) cond = !cond;
            uint target = Pc + (uint)(Imm16(instr) << 2);
            bool likely = ((imm >> 1) & 1) != 0;
            if (likely) DoBranchLikely(cond, target);
            else if (cond) DoBranch(target);
            return;
        }

        // MFV/MTV (move VFPU scalar to/from GPR)
        if (rs == 0x00) // MFV
        {
            uint rt = Rt(instr);
            int vs = (int)(instr & 0x7F);
            if (rt != 0) Gpr[rt] = BitConverter.SingleToUInt32Bits(Vpr[vs & 127]);
            return;
        }
        if (rs == 0x04) // MTV
        {
            int vd = (int)(instr & 0x7F);
            Vpr[vd & 127] = BitConverter.UInt32BitsToSingle(Gpr[Rt(instr)]);
            return;
        }

        // Prefixes
        uint prefix_op = instr >> 24;
        if (prefix_op == 0xDC) { _vpfxs = instr & 0x00FFFFFF; _vpfxsActive = true; return; }
        if (prefix_op == 0xDD) { _vpfxt = instr & 0x00FFFFFF; _vpfxtActive = true; return; }
        if (prefix_op == 0xDE) { _vpfxd = instr & 0x00FFFFFF; _vpfxdActive = true; return; }

        // Dispatch VFPU arithmetic
        uint op = (instr >> 23) & 0x7;
        ExecVfpuArith(instr);
        ClearPrefixes();
    }

    void ExecVfpuArith(uint instr)
    {
        uint op2 = (instr >> 23) & 7;
        uint op3 = (instr >> 26) & 0x3F;

        // Decode size from the two-bit field
        int size = 1 + (int)((instr >> 7) & 1) + (int)((instr >> 15) & 1) * 2;
        if (size > 4) size = 4;

        int vd = (int)((instr >> 0) & 0x7F);
        int vs = (int)((instr >> 8) & 0x7F);
        int vt = (int)((instr >> 16) & 0x7F);

        Span<int> dRegs = stackalloc int[4];
        Span<int> sRegs = stackalloc int[4];
        Span<int> tRegs = stackalloc int[4];
        GetVectorRegs(vd, size, dRegs);
        GetVectorRegs(vs, size, sRegs);
        GetVectorRegs(vt, size, tRegs);

        Span<float> s = stackalloc float[4];
        Span<float> t = stackalloc float[4];
        Span<float> d = stackalloc float[4];

        for (int i = 0; i < size; i++)
        {
            s[i] = ReadVfpuWithPrefix(sRegs[i], i, _vpfxsActive, _vpfxs);
            t[i] = ReadVfpuWithPrefix(tRegs[i], i, _vpfxtActive, _vpfxt);
        }

        switch (op3)
        {
            case 0x18: // VADD
                for (int i = 0; i < size; i++) d[i] = s[i] + t[i];
                break;
            case 0x19: // VSUB
                for (int i = 0; i < size; i++) d[i] = s[i] - t[i];
                break;
            case 0x1A: // VSBN (?)
                for (int i = 0; i < size; i++) d[i] = s[i] * MathF.Pow(2, t[i]);
                break;
            case 0x1C: // VDIV (element-wise)
                for (int i = 0; i < size; i++) d[i] = t[i] != 0 ? s[i] / t[i] : 0;
                break;
            default:
                ExecVfpuArith2(instr, size, s, t, d, sRegs, tRegs, dRegs);
                break;
        }

        for (int i = 0; i < size; i++)
            WriteVfpuWithPrefix(dRegs[i], i, d[i], _vpfxdActive, _vpfxd);
    }

    void ExecVfpuArith2(uint instr, int size, Span<float> s, Span<float> t, Span<float> d,
        Span<int> sRegs, Span<int> tRegs, Span<int> dRegs)
    {
        uint op26 = instr >> 26;
        uint op23 = (instr >> 23) & 7;

        // Distinguish using full opcode + sub-fields
        if (op26 == 0x18 && op23 == 0) // VMUL
        {
            for (int i = 0; i < size; i++) d[i] = s[i] * t[i];
        }
        else if (op26 == 0x19 && op23 == 0) // VDOT
        {
            float dot = 0;
            for (int i = 0; i < size; i++) dot += s[i] * t[i];
            d[0] = dot;
            WriteVfpuWithPrefix(dRegs[0], 0, dot, _vpfxdActive, _vpfxd);
            return;
        }
        else if (op26 == 0x19 && op23 == 1) // VSCL
        {
            float scalar = t[0];
            for (int i = 0; i < size; i++) d[i] = s[i] * scalar;
        }
        else if (op26 == 0x19 && op23 == 4) // VCRS (cross product)
        {
            if (size >= 3)
            {
                d[0] = s[1] * t[2] - s[2] * t[1];
                d[1] = s[2] * t[0] - s[0] * t[2];
                d[2] = s[0] * t[1] - s[1] * t[0];
            }
        }
        else
        {
            ExecVfpuSingle(instr, size, s, t, d);
        }
    }

    void ExecVfpuSingle(uint instr, int size, Span<float> s, Span<float> t, Span<float> d)
    {
        uint op = (instr >> 16) & 0x1F;
        uint full = instr >> 26;

        // Single-operand functions (VFPU4)
        if ((full & 0x3C) == 0x34)
        {
            uint func = (instr >> 16) & 0x1F;
            switch (func)
            {
                case 0x00: // VMOV
                    for (int i = 0; i < size; i++) d[i] = s[i];
                    break;
                case 0x01: // VABS
                    for (int i = 0; i < size; i++) d[i] = MathF.Abs(s[i]);
                    break;
                case 0x02: // VNEG
                    for (int i = 0; i < size; i++) d[i] = -s[i];
                    break;
                case 0x04: // VSAT0 (clamp 0..1)
                    for (int i = 0; i < size; i++) d[i] = Math.Clamp(s[i], 0f, 1f);
                    break;
                case 0x05: // VSAT1 (clamp -1..1)
                    for (int i = 0; i < size; i++) d[i] = Math.Clamp(s[i], -1f, 1f);
                    break;
                case 0x10: // VRCP
                    for (int i = 0; i < size; i++) d[i] = s[i] != 0 ? 1f / s[i] : 0;
                    break;
                case 0x11: // VRSQ
                    for (int i = 0; i < size; i++) d[i] = s[i] > 0 ? 1f / MathF.Sqrt(s[i]) : 0;
                    break;
                case 0x12: // VSIN
                    for (int i = 0; i < size; i++) d[i] = MathF.Sin(s[i] * MathF.PI * 0.5f);
                    break;
                case 0x13: // VCOS
                    for (int i = 0; i < size; i++) d[i] = MathF.Cos(s[i] * MathF.PI * 0.5f);
                    break;
                case 0x14: // VEXP2
                    for (int i = 0; i < size; i++) d[i] = MathF.Pow(2f, s[i]);
                    break;
                case 0x15: // VLOG2
                    for (int i = 0; i < size; i++) d[i] = s[i] > 0 ? MathF.Log2(s[i]) : 0;
                    break;
                case 0x16: // VSQRT
                    for (int i = 0; i < size; i++) d[i] = s[i] >= 0 ? MathF.Sqrt(s[i]) : 0;
                    break;
                case 0x17: // VASIN
                    for (int i = 0; i < size; i++) d[i] = MathF.Asin(s[i]) / (MathF.PI * 0.5f);
                    break;
                case 0x1A: // VF2IN
                {
                    int imm = (int)((instr >> 8) & 0x1F);
                    float scale = (float)(1 << imm);
                    for (int i = 0; i < size; i++)
                        d[i] = BitConverter.UInt32BitsToSingle((uint)(int)(s[i] * scale));
                    break;
                }
                case 0x1B: // VI2F
                {
                    int imm = (int)((instr >> 8) & 0x1F);
                    float scale = 1f / (1 << imm);
                    for (int i = 0; i < size; i++)
                        d[i] = (int)BitConverter.SingleToUInt32Bits(s[i]) * scale;
                    break;
                }
                default:
                    Log.Warn(LogCat.VFPU, $"VFPU single func={func:X2} at {Pc - 4:X8}");
                    for (int i = 0; i < size; i++) d[i] = s[i];
                    break;
            }
            return;
        }

        // Comparison: VCMP
        if ((full & 0x3E) == 0x1E)
        {
            uint cond = instr & 0xF;
            uint cc = 0;
            for (int i = 0; i < size; i++)
            {
                bool result = cond switch
                {
                    0 => false,      // FL
                    1 => s[i] == t[i], // EQ
                    2 => s[i] < t[i],  // LT
                    3 => s[i] <= t[i], // LE
                    _ => false,
                };
                if (result) cc |= 1u << i;
            }
            bool any = cc != 0;
            bool all = cc == (uint)((1 << size) - 1);
            VfpuCc = (cc & 0x3F) | (any ? 0x10u : 0) | (all ? 0x20u : 0);
            return;
        }

        // Identity/Zero/One matrix load
        if ((full & 0x3E) == 0x3E)
        {
            uint func = (instr >> 16) & 0x1F;
            switch (func)
            {
                case 0x03: // VIDT
                {
                    int vd = (int)(instr & 0x7F);
                    Span<int> regs = stackalloc int[4];
                    GetVectorRegs(vd, size, regs);
                    for (int i = 0; i < size; i++)
                        Vpr[regs[i] & 127] = (i == (vd & 3)) ? 1f : 0f;
                    return;
                }
                case 0x06: // VZERO
                    for (int i = 0; i < size; i++) d[i] = 0f;
                    break;
                case 0x07: // VONE
                    for (int i = 0; i < size; i++) d[i] = 1f;
                    break;
            }
            return;
        }

        // Fallback
        for (int i = 0; i < size; i++) d[i] = s[i];
    }

    // ── VFPU Load/Store (lv.s, lv.q, sv.s, sv.q) ──

    void ExecLvVfpu(uint instr)
    {
        ExecVfpuLoadStore(instr);
    }

    void ExecSvVfpu(uint instr)
    {
        ExecVfpuLoadStore(instr);
    }

    void ExecVfpuLoadStore(uint instr)
    {
        uint op = instr >> 26;
        uint vt = (instr >> 16) & 0x7F;
        uint rs = Rs(instr);
        int offset = (int)(short)(instr & 0xFFFC);
        uint addr = (uint)((int)Gpr[rs] + offset);

        switch (op)
        {
            case 0x32: // LV.S (load single)
            {
                int idx = (int)(vt & 0x7F);
                Vpr[idx & 127] = BitConverter.UInt32BitsToSingle(Bus.Read32(addr));
                break;
            }
            case 0x36: // LV.Q (load quad, 4 floats)
            {
                Span<int> regs = stackalloc int[4];
                GetVectorRegs((int)vt, 4, regs);
                for (int i = 0; i < 4; i++)
                    Vpr[regs[i] & 127] = BitConverter.UInt32BitsToSingle(Bus.Read32(addr + (uint)(i * 4)));
                break;
            }
            case 0x3A: // SV.S (store single)
            {
                int idx = (int)(vt & 0x7F);
                Bus.Write32(addr, BitConverter.SingleToUInt32Bits(Vpr[idx & 127]));
                break;
            }
            case 0x3E: // SV.Q (store quad)
            {
                Span<int> regs = stackalloc int[4];
                GetVectorRegs((int)vt, 4, regs);
                for (int i = 0; i < 4; i++)
                    Bus.Write32(addr + (uint)(i * 4), BitConverter.SingleToUInt32Bits(Vpr[regs[i] & 127]));
                break;
            }
        }
    }
}
