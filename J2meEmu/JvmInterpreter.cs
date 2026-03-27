namespace J2meEmu;

static class JvmInterpreter
{
    public static JValue Execute(JvmThread thread, JvmFrame f)
    {
        var loader = thread.Loader;
        var code = f.Code;
        if (code.Length == 0) return JValue.Null;

        while (true)
        {
            if (f.PC >= code.Length) return JValue.Null;
            if (++thread.InstructionCount > thread.MaxInstructions) return JValue.Null;
            if (thread.Name != "main" && (thread.InstructionCount & 0x7FFF) == 0)
                Thread.Sleep(1);
            int pc = f.PC;
            byte op;
            try
            {
            op = code[f.PC++];

            switch (op)
            {
                case 0x00: break; // nop
                case 0x01: f.Push(JValue.Null); break; // aconst_null
                case 0x02: f.Push(JValue.OfInt(-1)); break; // iconst_m1
                case 0x03: f.Push(JValue.OfInt(0)); break; // iconst_0
                case 0x04: f.Push(JValue.OfInt(1)); break; // iconst_1
                case 0x05: f.Push(JValue.OfInt(2)); break; // iconst_2
                case 0x06: f.Push(JValue.OfInt(3)); break; // iconst_3
                case 0x07: f.Push(JValue.OfInt(4)); break; // iconst_4
                case 0x08: f.Push(JValue.OfInt(5)); break; // iconst_5
                case 0x09: f.Push(JValue.OfLong(0)); break; // lconst_0
                case 0x0A: f.Push(JValue.OfLong(1)); break; // lconst_1
                case 0x0B: f.Push(JValue.OfFloat(0f)); break; // fconst_0
                case 0x0C: f.Push(JValue.OfFloat(1f)); break; // fconst_1
                case 0x0D: f.Push(JValue.OfFloat(2f)); break; // fconst_2
                case 0x0E: f.Push(JValue.OfDouble(0.0)); break; // dconst_0
                case 0x0F: f.Push(JValue.OfDouble(1.0)); break; // dconst_1
                case 0x10: f.Push(JValue.OfInt((sbyte)code[f.PC++])); break; // bipush
                case 0x11: f.Push(JValue.OfInt((short)(code[f.PC] << 8 | code[f.PC + 1]))); f.PC += 2; break; // sipush

                case 0x12: // ldc
                {
                    int idx = code[f.PC++];
                    LoadConstant(f, idx);
                    break;
                }
                case 0x13: // ldc_w
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    LoadConstant(f, idx);
                    break;
                }
                case 0x14: // ldc2_w
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    LoadConstant2(f, idx);
                    break;
                }

                case 0x15: f.Push(f.Locals[code[f.PC++]]); break; // iload
                case 0x16: f.Push(f.Locals[code[f.PC++]]); break; // lload
                case 0x17: f.Push(f.Locals[code[f.PC++]]); break; // fload
                case 0x18: f.Push(f.Locals[code[f.PC++]]); break; // dload
                case 0x19: f.Push(f.Locals[code[f.PC++]]); break; // aload

                case 0x1A: f.Push(f.Locals[0]); break; // iload_0
                case 0x1B: f.Push(f.Locals[1]); break; // iload_1
                case 0x1C: f.Push(f.Locals[2]); break; // iload_2
                case 0x1D: f.Push(f.Locals[3]); break; // iload_3
                case 0x1E: f.Push(f.Locals[0]); break; // lload_0
                case 0x1F: f.Push(f.Locals[1]); break; // lload_1
                case 0x20: f.Push(f.Locals[2]); break; // lload_2
                case 0x21: f.Push(f.Locals[3]); break; // lload_3
                case 0x22: f.Push(f.Locals[0]); break; // fload_0
                case 0x23: f.Push(f.Locals[1]); break; // fload_1
                case 0x24: f.Push(f.Locals[2]); break; // fload_2
                case 0x25: f.Push(f.Locals[3]); break; // fload_3
                case 0x26: f.Push(f.Locals[0]); break; // dload_0
                case 0x27: f.Push(f.Locals[1]); break; // dload_1
                case 0x28: f.Push(f.Locals[2]); break; // dload_2
                case 0x29: f.Push(f.Locals[3]); break; // dload_3
                case 0x2A: f.Push(f.Locals[0]); break; // aload_0
                case 0x2B: f.Push(f.Locals[1]); break; // aload_1
                case 0x2C: f.Push(f.Locals[2]); break; // aload_2
                case 0x2D: f.Push(f.Locals[3]); break; // aload_3

                case 0x2E: // iaload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.IntData.Length)
                        f.Push(JValue.OfInt(arr.IntData[idx]));
                    else f.Push(JValue.OfInt(0));
                    break;
                }
                case 0x2F: // laload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.LongData.Length)
                        f.Push(JValue.OfLong(arr.LongData[idx]));
                    else f.Push(JValue.OfLong(0));
                    break;
                }
                case 0x30: // faload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.FloatData.Length)
                        f.Push(JValue.OfFloat(arr.FloatData[idx]));
                    else f.Push(JValue.OfFloat(0));
                    break;
                }
                case 0x31: // daload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.DoubleData.Length)
                        f.Push(JValue.OfDouble(arr.DoubleData[idx]));
                    else f.Push(JValue.OfDouble(0));
                    break;
                }
                case 0x32: // aaload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.RefData.Length)
                        f.Push(JValue.OfRef(arr.RefData[idx]));
                    else f.Push(JValue.Null);
                    break;
                }
                case 0x33: // baload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.ByteData.Length)
                        f.Push(JValue.OfInt((sbyte)arr.ByteData[idx]));
                    else f.Push(JValue.OfInt(0));
                    break;
                }
                case 0x34: // caload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.CharData.Length)
                        f.Push(JValue.OfInt(arr.CharData[idx]));
                    else f.Push(JValue.OfInt(0));
                    break;
                }
                case 0x35: // saload
                {
                    int idx = f.Pop().Int;
                    var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.ShortData.Length)
                        f.Push(JValue.OfInt(arr.ShortData[idx]));
                    else f.Push(JValue.OfInt(0));
                    break;
                }

                case 0x36: f.Locals[code[f.PC++]] = f.Pop(); break; // istore
                case 0x37: f.Locals[code[f.PC++]] = f.Pop(); break; // lstore
                case 0x38: f.Locals[code[f.PC++]] = f.Pop(); break; // fstore
                case 0x39: f.Locals[code[f.PC++]] = f.Pop(); break; // dstore
                case 0x3A: f.Locals[code[f.PC++]] = f.Pop(); break; // astore

                case 0x3B: f.Locals[0] = f.Pop(); break; // istore_0
                case 0x3C: f.Locals[1] = f.Pop(); break; // istore_1
                case 0x3D: f.Locals[2] = f.Pop(); break; // istore_2
                case 0x3E: f.Locals[3] = f.Pop(); break; // istore_3
                case 0x3F: f.Locals[0] = f.Pop(); break; // lstore_0
                case 0x40: f.Locals[1] = f.Pop(); break; // lstore_1
                case 0x41: f.Locals[2] = f.Pop(); break; // lstore_2
                case 0x42: f.Locals[3] = f.Pop(); break; // lstore_3
                case 0x43: f.Locals[0] = f.Pop(); break; // fstore_0
                case 0x44: f.Locals[1] = f.Pop(); break; // fstore_1
                case 0x45: f.Locals[2] = f.Pop(); break; // fstore_2
                case 0x46: f.Locals[3] = f.Pop(); break; // fstore_3
                case 0x47: f.Locals[0] = f.Pop(); break; // dstore_0
                case 0x48: f.Locals[1] = f.Pop(); break; // dstore_1
                case 0x49: f.Locals[2] = f.Pop(); break; // dstore_2
                case 0x4A: f.Locals[3] = f.Pop(); break; // dstore_3
                case 0x4B: f.Locals[0] = f.Pop(); break; // astore_0
                case 0x4C: f.Locals[1] = f.Pop(); break; // astore_1
                case 0x4D: f.Locals[2] = f.Pop(); break; // astore_2
                case 0x4E: f.Locals[3] = f.Pop(); break; // astore_3

                case 0x4F: // iastore
                {
                    int val = f.Pop().Int; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.IntData.Length) arr.IntData[idx] = val;
                    break;
                }
                case 0x50: // lastore
                {
                    long val = f.Pop().Long; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.LongData.Length) arr.LongData[idx] = val;
                    break;
                }
                case 0x51: // fastore
                {
                    float val = f.Pop().Float; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.FloatData.Length) arr.FloatData[idx] = val;
                    break;
                }
                case 0x52: // dastore
                {
                    double val = f.Pop().Double; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.DoubleData.Length) arr.DoubleData[idx] = val;
                    break;
                }
                case 0x53: // aastore
                {
                    var val = f.Pop().Ref; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.RefData.Length) arr.RefData[idx] = val;
                    break;
                }
                case 0x54: // bastore
                {
                    int val = f.Pop().Int; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.ByteData.Length) arr.ByteData[idx] = (byte)val;
                    break;
                }
                case 0x55: // castore
                {
                    int val = f.Pop().Int; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.CharData.Length) arr.CharData[idx] = (char)val;
                    break;
                }
                case 0x56: // sastore
                {
                    int val = f.Pop().Int; int idx = f.Pop().Int; var aref = f.Pop().Ref;
                    if (aref is JavaArray arr && (uint)idx < (uint)arr.ShortData.Length) arr.ShortData[idx] = (short)val;
                    break;
                }

                case 0x57: f.Pop(); break; // pop
                case 0x58: f.Pop(); f.Pop(); break; // pop2

                case 0x59: // dup
                {
                    var v = f.Peek();
                    f.Push(v);
                    break;
                }
                case 0x5A: // dup_x1
                {
                    var v1 = f.Pop();
                    var v2 = f.Pop();
                    f.Push(v1); f.Push(v2); f.Push(v1);
                    break;
                }
                case 0x5B: // dup_x2
                {
                    var v1 = f.Pop();
                    var v2 = f.Pop();
                    var v3 = f.Pop();
                    f.Push(v1); f.Push(v3); f.Push(v2); f.Push(v1);
                    break;
                }
                case 0x5C: // dup2
                {
                    var v1 = f.Pop();
                    var v2 = f.Peek();
                    f.Push(v1); f.Push(v2); f.Push(v1);
                    break;
                }
                case 0x5D: // dup2_x1
                {
                    var v1 = f.Pop();
                    var v2 = f.Pop();
                    var v3 = f.Pop();
                    f.Push(v2); f.Push(v1); f.Push(v3); f.Push(v2); f.Push(v1);
                    break;
                }
                case 0x5E: // dup2_x2
                {
                    var v1 = f.Pop();
                    var v2 = f.Pop();
                    var v3 = f.Pop();
                    var v4 = f.Pop();
                    f.Push(v2); f.Push(v1); f.Push(v4); f.Push(v3); f.Push(v2); f.Push(v1);
                    break;
                }
                case 0x5F: // swap
                {
                    var v1 = f.Pop();
                    var v2 = f.Pop();
                    f.Push(v1); f.Push(v2);
                    break;
                }

                // Arithmetic
                case 0x60: { int b = f.Pop().Int; f.Push(JValue.OfInt(f.Pop().Int + b)); break; } // iadd
                case 0x61: { long b = f.Pop().Long; f.Push(JValue.OfLong(f.Pop().Long + b)); break; } // ladd
                case 0x62: { float b = f.Pop().Float; f.Push(JValue.OfFloat(f.Pop().Float + b)); break; } // fadd
                case 0x63: { double b = f.Pop().Double; f.Push(JValue.OfDouble(f.Pop().Double + b)); break; } // dadd
                case 0x64: { int b = f.Pop().Int; f.Push(JValue.OfInt(f.Pop().Int - b)); break; } // isub
                case 0x65: { long b = f.Pop().Long; f.Push(JValue.OfLong(f.Pop().Long - b)); break; } // lsub
                case 0x66: { float b = f.Pop().Float; f.Push(JValue.OfFloat(f.Pop().Float - b)); break; } // fsub
                case 0x67: { double b = f.Pop().Double; f.Push(JValue.OfDouble(f.Pop().Double - b)); break; } // dsub
                case 0x68: { int b = f.Pop().Int; f.Push(JValue.OfInt(f.Pop().Int * b)); break; } // imul
                case 0x69: { long b = f.Pop().Long; f.Push(JValue.OfLong(f.Pop().Long * b)); break; } // lmul
                case 0x6A: { float b = f.Pop().Float; f.Push(JValue.OfFloat(f.Pop().Float * b)); break; } // fmul
                case 0x6B: { double b = f.Pop().Double; f.Push(JValue.OfDouble(f.Pop().Double * b)); break; } // dmul
                case 0x6C: { int b = f.Pop().Int; f.Push(JValue.OfInt(b != 0 ? f.Pop().Int / b : 0)); break; } // idiv
                case 0x6D: { long b = f.Pop().Long; f.Push(JValue.OfLong(b != 0 ? f.Pop().Long / b : 0)); break; } // ldiv
                case 0x6E: { float b = f.Pop().Float; f.Push(JValue.OfFloat(f.Pop().Float / b)); break; } // fdiv
                case 0x6F: { double b = f.Pop().Double; f.Push(JValue.OfDouble(f.Pop().Double / b)); break; } // ddiv
                case 0x70: { int b = f.Pop().Int; f.Push(JValue.OfInt(b != 0 ? f.Pop().Int % b : 0)); break; } // irem
                case 0x71: { long b = f.Pop().Long; f.Push(JValue.OfLong(b != 0 ? f.Pop().Long % b : 0)); break; } // lrem
                case 0x72: { float b = f.Pop().Float; f.Push(JValue.OfFloat(f.Pop().Float % b)); break; } // frem
                case 0x73: { double b = f.Pop().Double; f.Push(JValue.OfDouble(f.Pop().Double % b)); break; } // drem
                case 0x74: f.Push(JValue.OfInt(-f.Pop().Int)); break; // ineg
                case 0x75: f.Push(JValue.OfLong(-f.Pop().Long)); break; // lneg
                case 0x76: f.Push(JValue.OfFloat(-f.Pop().Float)); break; // fneg
                case 0x77: f.Push(JValue.OfDouble(-f.Pop().Double)); break; // dneg

                // Shifts & bitwise
                case 0x78: { int s = f.Pop().Int & 0x1F; f.Push(JValue.OfInt(f.Pop().Int << s)); break; } // ishl
                case 0x79: { int s = f.Pop().Int & 0x3F; f.Push(JValue.OfLong(f.Pop().Long << s)); break; } // lshl
                case 0x7A: { int s = f.Pop().Int & 0x1F; f.Push(JValue.OfInt(f.Pop().Int >> s)); break; } // ishr
                case 0x7B: { int s = f.Pop().Int & 0x3F; f.Push(JValue.OfLong(f.Pop().Long >> s)); break; } // lshr
                case 0x7C: { int s = f.Pop().Int & 0x1F; f.Push(JValue.OfInt((int)((uint)f.Pop().Int >> s))); break; } // iushr
                case 0x7D: { int s = f.Pop().Int & 0x3F; f.Push(JValue.OfLong((long)((ulong)f.Pop().Long >> s))); break; } // lushr
                case 0x7E: { int b = f.Pop().Int; f.Push(JValue.OfInt(f.Pop().Int & b)); break; } // iand
                case 0x7F: { long b = f.Pop().Long; f.Push(JValue.OfLong(f.Pop().Long & b)); break; } // land
                case 0x80: { int b = f.Pop().Int; f.Push(JValue.OfInt(f.Pop().Int | b)); break; } // ior
                case 0x81: { long b = f.Pop().Long; f.Push(JValue.OfLong(f.Pop().Long | b)); break; } // lor
                case 0x82: { int b = f.Pop().Int; f.Push(JValue.OfInt(f.Pop().Int ^ b)); break; } // ixor
                case 0x83: { long b = f.Pop().Long; f.Push(JValue.OfLong(f.Pop().Long ^ b)); break; } // lxor

                case 0x84: // iinc
                {
                    int idx = code[f.PC++];
                    int inc = (sbyte)code[f.PC++];
                    f.Locals[idx] = JValue.OfInt(f.Locals[idx].Int + inc);
                    break;
                }

                // Conversions
                case 0x85: f.Push(JValue.OfLong(f.Pop().Int)); break; // i2l
                case 0x86: f.Push(JValue.OfFloat(f.Pop().Int)); break; // i2f
                case 0x87: f.Push(JValue.OfDouble(f.Pop().Int)); break; // i2d
                case 0x88: f.Push(JValue.OfInt((int)f.Pop().Long)); break; // l2i
                case 0x89: f.Push(JValue.OfFloat(f.Pop().Long)); break; // l2f
                case 0x8A: f.Push(JValue.OfDouble(f.Pop().Long)); break; // l2d
                case 0x8B: f.Push(JValue.OfInt((int)f.Pop().Float)); break; // f2i
                case 0x8C: f.Push(JValue.OfLong((long)f.Pop().Float)); break; // f2l
                case 0x8D: f.Push(JValue.OfDouble(f.Pop().Float)); break; // f2d
                case 0x8E: f.Push(JValue.OfInt((int)f.Pop().Double)); break; // d2i
                case 0x8F: f.Push(JValue.OfLong((long)f.Pop().Double)); break; // d2l
                case 0x90: f.Push(JValue.OfFloat((float)f.Pop().Double)); break; // d2f
                case 0x91: f.Push(JValue.OfInt((sbyte)f.Pop().Int)); break; // i2b
                case 0x92: f.Push(JValue.OfInt((char)f.Pop().Int)); break; // i2c
                case 0x93: f.Push(JValue.OfInt((short)f.Pop().Int)); break; // i2s

                // Comparisons
                case 0x94: // lcmp
                {
                    long b = f.Pop().Long, a = f.Pop().Long;
                    f.Push(JValue.OfInt(a > b ? 1 : a < b ? -1 : 0));
                    break;
                }
                case 0x95: // fcmpl
                {
                    float b = f.Pop().Float, a = f.Pop().Float;
                    f.Push(JValue.OfInt(float.IsNaN(a) || float.IsNaN(b) ? -1 : a > b ? 1 : a < b ? -1 : 0));
                    break;
                }
                case 0x96: // fcmpg
                {
                    float b = f.Pop().Float, a = f.Pop().Float;
                    f.Push(JValue.OfInt(float.IsNaN(a) || float.IsNaN(b) ? 1 : a > b ? 1 : a < b ? -1 : 0));
                    break;
                }
                case 0x97: // dcmpl
                {
                    double b = f.Pop().Double, a = f.Pop().Double;
                    f.Push(JValue.OfInt(double.IsNaN(a) || double.IsNaN(b) ? -1 : a > b ? 1 : a < b ? -1 : 0));
                    break;
                }
                case 0x98: // dcmpg
                {
                    double b = f.Pop().Double, a = f.Pop().Double;
                    f.Push(JValue.OfInt(double.IsNaN(a) || double.IsNaN(b) ? 1 : a > b ? 1 : a < b ? -1 : 0));
                    break;
                }

                // Branches
                case 0x99: BranchIf(f, f.Pop().Int == 0); break; // ifeq
                case 0x9A: BranchIf(f, f.Pop().Int != 0); break; // ifne
                case 0x9B: BranchIf(f, f.Pop().Int < 0); break; // iflt
                case 0x9C: BranchIf(f, f.Pop().Int >= 0); break; // ifge
                case 0x9D: BranchIf(f, f.Pop().Int > 0); break; // ifgt
                case 0x9E: BranchIf(f, f.Pop().Int <= 0); break; // ifle

                case 0x9F: { int b = f.Pop().Int, a = f.Pop().Int; BranchIf(f, a == b); break; } // if_icmpeq
                case 0xA0: { int b = f.Pop().Int, a = f.Pop().Int; BranchIf(f, a != b); break; } // if_icmpne
                case 0xA1: { int b = f.Pop().Int, a = f.Pop().Int; BranchIf(f, a < b); break; } // if_icmplt
                case 0xA2: { int b = f.Pop().Int, a = f.Pop().Int; BranchIf(f, a >= b); break; } // if_icmpge
                case 0xA3: { int b = f.Pop().Int, a = f.Pop().Int; BranchIf(f, a > b); break; } // if_icmpgt
                case 0xA4: { int b = f.Pop().Int, a = f.Pop().Int; BranchIf(f, a <= b); break; } // if_icmple
                case 0xA5: { var b = f.Pop().Ref; var a = f.Pop().Ref; BranchIf(f, a == b); break; } // if_acmpeq
                case 0xA6: { var b = f.Pop().Ref; var a = f.Pop().Ref; BranchIf(f, a != b); break; } // if_acmpne

                case 0xA7: // goto
                {
                    short offset = (short)(code[f.PC] << 8 | code[f.PC + 1]);
                    f.PC = pc + offset;
                    break;
                }
                case 0xA8: // jsr
                {
                    short offset = (short)(code[f.PC] << 8 | code[f.PC + 1]);
                    f.Push(JValue.OfInt(f.PC + 2));
                    f.PC = pc + offset;
                    break;
                }
                case 0xA9: // ret
                    f.PC = f.Locals[code[f.PC]].Int;
                    break;

                case 0xAA: // tableswitch
                {
                    int basePc = pc;
                    int pad = (4 - (f.PC % 4)) % 4;
                    f.PC += pad;
                    int def = ReadI4(code, ref f.PC);
                    int low = ReadI4(code, ref f.PC);
                    int high = ReadI4(code, ref f.PC);
                    int key = f.Pop().Int;
                    if (key >= low && key <= high)
                    {
                        int offsetIdx = (key - low) * 4;
                        f.PC += offsetIdx;
                        int target = ReadI4(code, ref f.PC);
                        f.PC = basePc + target;
                    }
                    else
                    {
                        f.PC = basePc + def;
                    }
                    break;
                }
                case 0xAB: // lookupswitch
                {
                    int basePc = pc;
                    int pad = (4 - (f.PC % 4)) % 4;
                    f.PC += pad;
                    int def = ReadI4(code, ref f.PC);
                    int npairs = ReadI4(code, ref f.PC);
                    int key = f.Pop().Int;
                    bool found = false;
                    for (int i = 0; i < npairs; i++)
                    {
                        int match = ReadI4(code, ref f.PC);
                        int target = ReadI4(code, ref f.PC);
                        if (key == match)
                        {
                            f.PC = basePc + target;
                            found = true;
                            break;
                        }
                    }
                    if (!found) f.PC = basePc + def;
                    break;
                }

                case 0xAC: return f.Pop(); // ireturn
                case 0xAD: return f.Pop(); // lreturn
                case 0xAE: return f.Pop(); // freturn
                case 0xAF: return f.Pop(); // dreturn
                case 0xB0: return f.Pop(); // areturn
                case 0xB1: return JValue.Null; // return (void)

                case 0xB2: // getstatic
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    var (cls, name, desc) = f.ClassFile!.GetMemberRef(idx);
                    var targetClass = loader.LoadClass(cls);
                    loader.InitializeClass(targetClass, thread);
                    var field = targetClass.FindField(name, desc);
                    if (field != null && field.IsStatic)
                        f.Push(targetClass.StaticFields[field.Index]);
                    else
                        f.Push(JValue.Null);
                    break;
                }
                case 0xB3: // putstatic
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    var val = f.Pop();
                    var (cls, name, desc) = f.ClassFile!.GetMemberRef(idx);
                    var targetClass = loader.LoadClass(cls);
                    loader.InitializeClass(targetClass, thread);
                    var field = targetClass.FindField(name, desc);
                    if (field != null && field.IsStatic)
                        targetClass.StaticFields[field.Index] = val;
                    break;
                }
                case 0xB4: // getfield
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    var obj = f.Pop().Ref as JavaObject;
                    if (obj == null) { f.Push(JValue.Null); break; }
                    var (cls, name, desc) = f.ClassFile!.GetMemberRef(idx);
                    var fieldClass = loader.LoadClass(cls);
                    var field = fieldClass.FindField(name, desc) ?? obj.Class.FindField(name, desc);
                    if (field != null && !field.IsStatic && field.Index < obj.Fields.Length)
                        f.Push(obj.Fields[field.Index]);
                    else
                        f.Push(JValue.Null);
                    break;
                }
                case 0xB5: // putfield
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    var val = f.Pop();
                    var obj = f.Pop().Ref as JavaObject;
                    if (obj == null) break;
                    var (cls, name, desc) = f.ClassFile!.GetMemberRef(idx);
                    var fieldClass = loader.LoadClass(cls);
                    var field = fieldClass.FindField(name, desc) ?? obj.Class.FindField(name, desc);
                    if (field != null && !field.IsStatic && field.Index < obj.Fields.Length)
                        obj.Fields[field.Index] = val;
                    break;
                }

                case 0xB6: // invokevirtual
                case 0xB7: // invokespecial
                case 0xB8: // invokestatic
                case 0xB9: // invokeinterface
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    if (op == 0xB9) f.PC += 2; // skip count + zero

                    var (cls, mname, mdesc) = f.ClassFile!.GetMemberRef(idx);
                    bool isStatic = (op == 0xB8);

                    var method = ResolveMethod(loader, thread, cls, mname, mdesc, isStatic);
                    if (method == null)
                    {
                        string sig = $"{cls}.{mname}{mdesc}";
                        loader.MissingMethods.Add(sig);
                        Log.Error($"Missing method: {sig}");
                        int pSlots = CountParameterSlots(mdesc);
                        for (int i = 0; i < pSlots; i++) f.Pop();
                        if (!isStatic) f.Pop();
                        if (!mdesc.EndsWith(")V"))
                            f.Push(JValue.Null);
                        break;
                    }

                    int paramSlots = method.ParameterSlots();
                    int totalArgs = paramSlots + (isStatic ? 0 : 1);
                    var args = new JValue[totalArgs];
                    for (int i = totalArgs - 1; i >= 0; i--)
                        args[i] = f.Pop();

                    if ((op == 0xB6 || op == 0xB9) && args.Length > 0)
                    {
                        var receiver = args[0].Ref as JavaObject;
                        if (receiver != null)
                        {
                            var vMethod = receiver.Class.FindMethod(mname, mdesc);
                            if (vMethod != null && !vMethod.IsAbstract) method = vMethod;
                            else if (vMethod != null && vMethod.IsAbstract)
                            {
                                var concrete = FindConcreteMethod(receiver.Class, mname, mdesc);
                                if (concrete != null) method = concrete;
                            }
                        }
                    }

                    loader.MethodsInvoked++;
                    var result = thread.Invoke(method, args);
                    if (!mdesc.EndsWith(")V"))
                        f.Push(result);
                    break;
                }

                case 0xBB: // new
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    string cls = f.ClassFile!.GetClassName(idx);
                    var targetClass = loader.LoadClass(cls);
                    loader.InitializeClass(targetClass, thread);
                    var obj = new JavaObject(targetClass);
                    f.Push(JValue.OfRef(obj));
                    break;
                }
                case 0xBC: // newarray
                {
                    int atype = code[f.PC++];
                    int count = f.Pop().Int;
                    var kind = atype switch
                    {
                        4 => JavaArray.ArrayKind.Boolean,
                        5 => JavaArray.ArrayKind.Char,
                        6 => JavaArray.ArrayKind.Float,
                        7 => JavaArray.ArrayKind.Double,
                        8 => JavaArray.ArrayKind.Byte,
                        9 => JavaArray.ArrayKind.Short,
                        10 => JavaArray.ArrayKind.Int,
                        11 => JavaArray.ArrayKind.Long,
                        _ => JavaArray.ArrayKind.Int
                    };
                    f.Push(JValue.OfRef(loader.CreateArray(kind, count)));
                    break;
                }
                case 0xBD: // anewarray
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    int count = f.Pop().Int;
                    string cls = f.ClassFile!.GetClassName(idx);
                    f.Push(JValue.OfRef(loader.CreateRefArray(cls, count)));
                    break;
                }
                case 0xBE: // arraylength
                {
                    var arr = f.Pop().Ref as JavaArray;
                    f.Push(JValue.OfInt(arr?.Length ?? 0));
                    break;
                }
                case 0xBF: // athrow
                {
                    var ex = f.Pop().Ref as JavaObject;
                    if (ex != null) throw new JvmException(ex);
                    throw new JvmException(loader.CreateException("java/lang/NullPointerException", "null throw"));
                }
                case 0xC0: // checkcast
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    // Permissive: don't throw ClassCastException, just leave the ref
                    break;
                }
                case 0xC1: // instanceof
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    var obj = f.Pop().Ref as JavaObject;
                    if (obj == null) { f.Push(JValue.OfInt(0)); break; }
                    string cls = f.ClassFile!.GetClassName(idx);
                    var targetClass = loader.LoadClass(cls);
                    f.Push(JValue.OfInt(obj.Class.IsAssignableTo(targetClass) ? 1 : 0));
                    break;
                }
                case 0xC2: // monitorenter
                {
                    var obj = f.Pop().Ref as JavaObject;
                    if (obj != null) Monitor.Enter(obj.Monitor);
                    break;
                }
                case 0xC3: // monitorexit
                {
                    var obj = f.Pop().Ref as JavaObject;
                    if (obj != null) Monitor.Exit(obj.Monitor);
                    break;
                }
                case 0xC4: // wide
                {
                    byte wideOp = code[f.PC++];
                    int wideIdx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    f.EnsureLocal(wideIdx);
                    switch (wideOp)
                    {
                        case 0x15: case 0x16: case 0x17: case 0x18: case 0x19:
                            f.Push(f.Locals[wideIdx]);
                            break;
                        case 0x36: case 0x37: case 0x38: case 0x39: case 0x3A:
                            f.Locals[wideIdx] = f.Pop();
                            break;
                        case 0x84: // wide iinc
                        {
                            short inc = (short)(code[f.PC] << 8 | code[f.PC + 1]); f.PC += 2;
                            f.Locals[wideIdx] = JValue.OfInt(f.Locals[wideIdx].Int + inc);
                            break;
                        }
                        case 0xA9: // wide ret
                            f.PC = f.Locals[wideIdx].Int;
                            break;
                    }
                    break;
                }
                case 0xC5: // multianewarray
                {
                    int idx = code[f.PC] << 8 | code[f.PC + 1]; f.PC += 2;
                    int dims = code[f.PC++];
                    var counts = new int[dims];
                    for (int i = dims - 1; i >= 0; i--) counts[i] = f.Pop().Int;
                    var arr = CreateMultiArray(loader, counts, 0);
                    f.Push(JValue.OfRef(arr));
                    break;
                }
                case 0xC6: BranchIf(f, f.Pop().Ref == null); break; // ifnull
                case 0xC7: BranchIf(f, f.Pop().Ref != null); break; // ifnonnull
                case 0xC8: // goto_w
                {
                    int offset = ReadI4(code, ref f.PC);
                    f.PC = pc + offset;
                    break;
                }
                case 0xC9: // jsr_w
                {
                    int offset = ReadI4(code, ref f.PC);
                    f.Push(JValue.OfInt(f.PC));
                    f.PC = pc + offset;
                    break;
                }

                default:
                    Log.Error($"Unimplemented opcode: 0x{op:X2} at PC={pc} in {f.Method.Owner.Name}.{f.Method.Name}");
                    return JValue.Null;
            }
            }
            catch (JvmException jex)
            {
                if (TryHandleException(thread, f, jex.JavaEx, pc))
                    continue;
                throw;
            }
        }
    }

    static bool TryHandleException(JvmThread thread, JvmFrame f, JavaObject exObj, int pc)
    {
        foreach (var entry in f.Method.ExceptionTable)
        {
            if (pc < entry.StartPc || pc >= entry.EndPc) continue;
            if (entry.CatchType == 0)
            {
                f.SP = 0;
                f.Push(JValue.OfRef(exObj));
                f.PC = entry.HandlerPc;
                return true;
            }
            if (f.ClassFile != null && entry.CatchType < f.ClassFile.ConstantPool.Length)
            {
                string catchName = f.ClassFile.GetClassName(entry.CatchType);
                if (IsAssignable(thread.Loader, exObj.Class, catchName))
                {
                    f.SP = 0;
                    f.Push(JValue.OfRef(exObj));
                    f.PC = entry.HandlerPc;
                    return true;
                }
            }
        }
        return false;
    }

    static bool IsAssignable(JvmClassLoader loader, JavaClass cls, string targetName)
    {
        var cur = cls;
        while (cur != null)
        {
            if (cur.Name == targetName) return true;
            if (cur.Super == null) break;
            cur = cur.Super;
        }
        return false;
    }

    static void LoadConstant(JvmFrame f, int idx)
    {
        if (f.ClassFile == null || idx <= 0 || idx >= f.ClassFile.ConstantPool.Length) { f.Push(JValue.Null); return; }
        ref var cp = ref f.ClassFile.ConstantPool[idx];
        switch (cp.Tag)
        {
            case CpTag.Integer: f.Push(JValue.OfInt(cp.IntVal)); break;
            case CpTag.Float: f.Push(JValue.OfFloat(cp.FloatVal)); break;
            case CpTag.String:
                var str = f.ClassFile.GetUtf8(cp.Index1);
                f.Push(JValue.OfRef(new JavaObject(new JavaClass("java/lang/String"), str)));
                break;
            case CpTag.Class:
                f.Push(JValue.OfRef(null));
                break;
            default: f.Push(JValue.Null); break;
        }
    }

    static void LoadConstant2(JvmFrame f, int idx)
    {
        if (f.ClassFile == null || idx <= 0 || idx >= f.ClassFile.ConstantPool.Length) { f.Push(JValue.Null); return; }
        ref var cp = ref f.ClassFile.ConstantPool[idx];
        switch (cp.Tag)
        {
            case CpTag.Long: f.Push(JValue.OfLong(cp.LongVal)); break;
            case CpTag.Double: f.Push(JValue.OfDouble(cp.DoubleVal)); break;
            default: f.Push(JValue.Null); break;
        }
    }

    static void BranchIf(JvmFrame f, bool condition)
    {
        int pcBefore = f.PC - 1;
        short offset = (short)(f.Code[f.PC] << 8 | f.Code[f.PC + 1]);
        f.PC += 2;
        if (condition) f.PC = pcBefore + offset;
    }

    static int ReadI4(byte[] code, ref int pc)
    {
        int v = (code[pc] << 24) | (code[pc + 1] << 16) | (code[pc + 2] << 8) | code[pc + 3];
        pc += 4;
        return v;
    }

    static JavaMethod? ResolveMethod(JvmClassLoader loader, JvmThread thread, string cls, string name, string desc, bool isStatic)
    {
        var targetClass = loader.LoadClass(cls);
        loader.InitializeClass(targetClass, thread);
        return targetClass.FindMethod(name, desc);
    }

    static JavaMethod? FindConcreteMethod(JavaClass cls, string name, string desc)
    {
        string key = $"{name}{desc}";
        for (var c = cls; c != null; c = c.Super)
        {
            if (c.Methods.TryGetValue(key, out var m) && !m.IsAbstract && (m.Code != null || m.NativeHandler != null))
                return m;
        }
        return null;
    }

    static int CountParameterSlots(string desc)
    {
        int slots = 0;
        int i = 1;
        while (i < desc.Length && desc[i] != ')')
        {
            char c = desc[i];
            if (c == 'J' || c == 'D') { slots += 2; i++; }
            else if (c == 'L') { slots++; i = desc.IndexOf(';', i) + 1; }
            else if (c == '[') { i++; }
            else { slots++; i++; }
        }
        return slots;
    }

    static JavaArray CreateMultiArray(JvmClassLoader loader, int[] counts, int dim)
    {
        int length = counts[dim];
        if (dim == counts.Length - 1)
            return loader.CreateArray(JavaArray.ArrayKind.Ref, length);

        var arr = loader.CreateRefArray("java/lang/Object", length);
        for (int i = 0; i < length; i++)
            arr.RefData[i] = CreateMultiArray(loader, counts, dim + 1);
        return arr;
    }
}
