using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PsxEmu;

/// <summary>
/// PSX Sound Processing Unit — 24 ADPCM voices, ADSR envelopes,
/// Gaussian interpolation, noise generator, reverb, DMA, IRQ9.
/// Architecture ported from ProjectPSX (BluestormDNA).
/// </summary>
unsafe class PsxSpu
{
    // ── Gaussian interpolation table (512 entries from psx-spx) ──────────
    static readonly short[] GaussTable = {
        -0x001,-0x001,-0x001,-0x001,-0x001,-0x001,-0x001,-0x001,
        -0x001,-0x001,-0x001,-0x001,-0x001,-0x001,-0x001,-0x001,
        0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0000,0x0001,
        0x0001,0x0001,0x0001,0x0002,0x0002,0x0002,0x0003,0x0003,
        0x0003,0x0004,0x0004,0x0005,0x0005,0x0006,0x0007,0x0007,
        0x0008,0x0009,0x0009,0x000A,0x000B,0x000C,0x000D,0x000E,
        0x000F,0x0010,0x0011,0x0012,0x0013,0x0015,0x0016,0x0018,
        0x0019,0x001B,0x001C,0x001E,0x0020,0x0021,0x0023,0x0025,
        0x0027,0x0029,0x002C,0x002E,0x0030,0x0033,0x0035,0x0038,
        0x003A,0x003D,0x0040,0x0043,0x0046,0x0049,0x004D,0x0050,
        0x0054,0x0057,0x005B,0x005F,0x0063,0x0067,0x006B,0x006F,
        0x0074,0x0078,0x007D,0x0082,0x0087,0x008C,0x0091,0x0096,
        0x009C,0x00A1,0x00A7,0x00AD,0x00B3,0x00BA,0x00C0,0x00C7,
        0x00CD,0x00D4,0x00DB,0x00E3,0x00EA,0x00F2,0x00FA,0x0101,
        0x010A,0x0112,0x011B,0x0123,0x012C,0x0135,0x013F,0x0148,
        0x0152,0x015C,0x0166,0x0171,0x017B,0x0186,0x0191,0x019C,
        0x01A8,0x01B4,0x01C0,0x01CC,0x01D9,0x01E5,0x01F2,0x0200,
        0x020D,0x021B,0x0229,0x0237,0x0246,0x0255,0x0264,0x0273,
        0x0283,0x0293,0x02A3,0x02B4,0x02C4,0x02D6,0x02E7,0x02F9,
        0x030B,0x031D,0x0330,0x0343,0x0356,0x036A,0x037E,0x0392,
        0x03A7,0x03BC,0x03D1,0x03E7,0x03FC,0x0413,0x042A,0x0441,
        0x0458,0x0470,0x0488,0x04A0,0x04B9,0x04D2,0x04EC,0x0506,
        0x0520,0x053B,0x0556,0x0572,0x058E,0x05AA,0x05C7,0x05E4,
        0x0601,0x061F,0x063E,0x065C,0x067C,0x069B,0x06BB,0x06DC,
        0x06FD,0x071E,0x0740,0x0762,0x0784,0x07A7,0x07CB,0x07EF,
        0x0813,0x0838,0x085D,0x0883,0x08A9,0x08D0,0x08F7,0x091E,
        0x0946,0x096F,0x0998,0x09C1,0x09EB,0x0A16,0x0A40,0x0A6C,
        0x0A98,0x0AC4,0x0AF1,0x0B1E,0x0B4C,0x0B7A,0x0BA9,0x0BD8,
        0x0C07,0x0C38,0x0C68,0x0C99,0x0CCB,0x0CFD,0x0D30,0x0D63,
        0x0D97,0x0DCB,0x0E00,0x0E35,0x0E6B,0x0EA1,0x0ED7,0x0F0F,
        0x0F46,0x0F7F,0x0FB7,0x0FF1,0x102A,0x1065,0x109F,0x10DB,
        0x1116,0x1153,0x118F,0x11CD,0x120B,0x1249,0x1288,0x12C7,
        0x1307,0x1347,0x1388,0x13C9,0x140B,0x144D,0x1490,0x14D4,
        0x1517,0x155C,0x15A0,0x15E6,0x162C,0x1672,0x16B9,0x1700,
        0x1747,0x1790,0x17D8,0x1821,0x186B,0x18B5,0x1900,0x194B,
        0x1996,0x19E2,0x1A2E,0x1A7B,0x1AC8,0x1B16,0x1B64,0x1BB3,
        0x1C02,0x1C51,0x1CA1,0x1CF1,0x1D42,0x1D93,0x1DE5,0x1E37,
        0x1E89,0x1EDC,0x1F2F,0x1F82,0x1FD6,0x202A,0x207F,0x20D4,
        0x2129,0x217F,0x21D5,0x222C,0x2282,0x22DA,0x2331,0x2389,
        0x23E1,0x2439,0x2492,0x24EB,0x2545,0x259E,0x25F8,0x2653,
        0x26AD,0x2708,0x2763,0x27BE,0x281A,0x2876,0x28D2,0x292E,
        0x298B,0x29E7,0x2A44,0x2AA1,0x2AFF,0x2B5C,0x2BBA,0x2C18,
        0x2C76,0x2CD4,0x2D33,0x2D91,0x2DF0,0x2E4F,0x2EAE,0x2F0D,
        0x2F6C,0x2FCC,0x302B,0x308B,0x30EA,0x314A,0x31AA,0x3209,
        0x3269,0x32C9,0x3329,0x3389,0x33E9,0x3449,0x34A9,0x3509,
        0x3569,0x35C9,0x3629,0x3689,0x36E8,0x3748,0x37A8,0x3807,
        0x3867,0x38C6,0x3926,0x3985,0x39E4,0x3A43,0x3AA2,0x3B00,
        0x3B5F,0x3BBD,0x3C1B,0x3C79,0x3CD7,0x3D35,0x3D92,0x3DEF,
        0x3E4C,0x3EA9,0x3F05,0x3F62,0x3FBD,0x4019,0x4074,0x40D0,
        0x412A,0x4185,0x41DF,0x4239,0x4292,0x42EB,0x4344,0x439C,
        0x43F4,0x444C,0x44A3,0x44FA,0x4550,0x45A6,0x45FC,0x4651,
        0x46A6,0x46FA,0x474E,0x47A1,0x47F4,0x4846,0x4898,0x48E9,
        0x493A,0x498A,0x49D9,0x4A29,0x4A77,0x4AC5,0x4B13,0x4B5F,
        0x4BAC,0x4BF7,0x4C42,0x4C8D,0x4CD7,0x4D20,0x4D68,0x4DB0,
        0x4DF7,0x4E3E,0x4E84,0x4EC9,0x4F0E,0x4F52,0x4F95,0x4FD7,
        0x5019,0x505A,0x509A,0x50DA,0x5118,0x5156,0x5194,0x51D0,
        0x520C,0x5247,0x5281,0x52BA,0x52F3,0x532A,0x5361,0x5397,
        0x53CC,0x5401,0x5434,0x5467,0x5499,0x54CA,0x54FA,0x5529,
        0x5558,0x5585,0x55B2,0x55DE,0x5609,0x5632,0x565B,0x5684,
        0x56AB,0x56D1,0x56F6,0x571B,0x573E,0x5761,0x5782,0x57A3,
        0x57C3,0x57E2,0x57FF,0x581C,0x5838,0x5853,0x586D,0x5886,
        0x589E,0x58B5,0x58CB,0x58E0,0x58F4,0x5907,0x5919,0x592A,
        0x593A,0x5949,0x5958,0x5965,0x5971,0x597C,0x5986,0x598F,
        0x5997,0x599E,0x59A4,0x59A9,0x59AD,0x59B0,0x59B2,0x59B3,
    };

    // ── ADPCM filter tables ──────────────────────────────────────────────
    static readonly int[] PosAdpcmTable = { 0, 60, 115, 98, 122 };
    static readonly int[] NegAdpcmTable = { 0, 0, -52, -55, -60 };

    // ── SPU RAM (512 KB) ─────────────────────────────────────────────────
    readonly byte[] _ram = new byte[512 * 1024];
    readonly byte* _ramPtr;
    readonly GCHandle _ramH;

    // ── 24 Voices ────────────────────────────────────────────────────────
    const int NumVoices = 24;

    enum Phase : byte { Attack, Decay, Sustain, Release, Off }

    struct Voice
    {
        // Registers
        public ushort VolLeftReg, VolRightReg;
        public ushort Pitch;
        public ushort StartAddress;
        public ushort AdsrLo, AdsrHi;
        public ushort AdsrVolume;
        public ushort RepeatAddress;

        // Internal state
        public ushort CurrentAddress;
        public uint Counter;       // pitch counter (bits 12+ = sample index, bits 3-11 = interp)
        public Phase AdsrPhase;
        public int AdsrCounter;
        public short Old, Older;
        public short Latest;
        public bool HasSamples;
        public bool ReadRamIrq;

        // Decoded sample buffer: 3 trailing + 28 current
        public fixed short Decoded[31];
        public fixed byte AdpcmBlock[16];

        public void KeyOn()
        {
            HasSamples = false;
            Old = 0;
            Older = 0;
            CurrentAddress = StartAddress;
            AdsrCounter = 0;
            AdsrVolume = 0;
            AdsrPhase = Phase.Attack;
            Counter = 0;
        }

        public void KeyOff()
        {
            AdsrCounter = 0;
            AdsrPhase = Phase.Release;
        }

        public short ProcessVolume(ushort reg)
        {
            if ((reg & 0x8000) == 0)
                return (short)(reg << 1);
            // Sweep mode: extract the effective volume from bits 0-14 treated as signed
            // This is an approximation; full sweep envelope tracking not implemented
            short raw = (short)((reg & 0x7FFF) << 1);
            return raw == 0 ? (short)0x3FFF : raw;
        }

        // ── ADSR ─────────────────────────────────────────────────────
        public void TickAdsr()
        {
            if (AdsrPhase == Phase.Off) { AdsrVolume = 0; return; }

            int target, shift, step;
            bool decreasing, exponential;

            switch (AdsrPhase)
            {
                case Phase.Attack:
                    target = 0x7FFF;
                    shift = (AdsrLo >> 10) & 0x1F;
                    step = 7 - ((AdsrLo >> 8) & 3);
                    decreasing = false;
                    exponential = ((AdsrLo >> 15) & 1) != 0;
                    break;
                case Phase.Decay:
                    target = ((AdsrLo & 0xF) + 1) * 0x800;
                    shift = (AdsrLo >> 4) & 0xF;
                    step = -8;
                    decreasing = true;
                    exponential = true;
                    break;
                case Phase.Sustain:
                    target = 0;
                    shift = (AdsrHi >> 8) & 0x1F;
                    bool susDecr = ((AdsrHi >> 14) & 1) != 0;
                    step = susDecr ? -8 + ((AdsrHi >> 6) & 3) : 7 - ((AdsrHi >> 6) & 3);
                    decreasing = susDecr;
                    exponential = ((AdsrHi >> 15) & 1) != 0;
                    break;
                case Phase.Release:
                    target = 0;
                    shift = AdsrHi & 0x1F;
                    step = -8;
                    decreasing = true;
                    exponential = ((AdsrHi >> 5) & 1) != 0;
                    break;
                default:
                    return;
            }

            if (AdsrCounter > 0) { AdsrCounter--; return; }

            int cycles = 1 << Math.Max(0, shift - 11);
            int envStep = step << Math.Max(0, 11 - shift);
            if (exponential && !decreasing && AdsrVolume > 0x6000) cycles *= 4;
            if (exponential && decreasing) envStep = (envStep * AdsrVolume) >> 15;

            AdsrVolume = (ushort)Math.Clamp(AdsrVolume + envStep, 0, 0x7FFF);
            AdsrCounter = cycles;

            bool next = decreasing ? (AdsrVolume <= target) : (AdsrVolume >= target);
            if (next && AdsrPhase != Phase.Sustain)
            {
                AdsrPhase++;
                AdsrCounter = 0;
            }
        }
    }

    Voice[] _voices = new Voice[NumVoices];

    // ── Control registers ────────────────────────────────────────────────
    short _mainVolLeft, _mainVolRight;
    short _reverbVolLeft, _reverbVolRight;
    uint _keyOn, _keyOff;
    uint _pitchMod, _noiseMode, _reverbMode, _endx;
    ushort _unknownA0;
    uint _reverbStartAddr, _reverbInternalAddr;
    ushort _irqAddress;
    ushort _transferAddress;
    uint _transferAddrInternal;
    ushort _transferFifo;
    ushort _transferControl;
    ushort _controlReg;
    ushort _statusReg;
    ushort _cdVolLeft, _cdVolRight;
    ushort _extVolLeft, _extVolRight;
    ushort _curVolLeft, _curVolRight;
    uint _unknownBC;

    // Reverb registers
    uint _dAPF1, _dAPF2;
    short _vIIR, _vCOMB1, _vCOMB2, _vCOMB3, _vCOMB4, _vWALL, _vAPF1, _vAPF2;
    uint _mLSAME, _mRSAME, _mLCOMB1, _mRCOMB1, _mLCOMB2, _mRCOMB2;
    uint _dLSAME, _dRSAME, _mLDIFF, _mRDIFF, _mLCOMB3, _mRCOMB3, _mLCOMB4, _mRCOMB4;
    uint _dLDIFF, _dRDIFF, _mLAPF1, _mRAPF1, _mLAPF2, _mRAPF2;
    short _vLIN, _vRIN;

    // Control helpers
    bool SpuEnabled => ((_controlReg >> 15) & 1) != 0;
    bool SpuUnmuted => ((_controlReg >> 14) & 1) != 0;
    int NoiseShift => (_controlReg >> 10) & 0xF;
    int NoiseStep => ((_controlReg >> 8) & 3) + 4;
    bool ReverbEnabled => ((_controlReg >> 7) & 1) != 0;
    bool Irq9Enabled => ((_controlReg >> 6) & 1) != 0;
    int TransferMode => (_controlReg >> 4) & 3;

    // ── Noise generator ──────────────────────────────────────────────────
    int _noiseTimer, _noiseLevel;

    // ── Timing ───────────────────────────────────────────────────────────
    int _counter;
    const int CyclesPerSample = 0x300; // 33868800 / 44100
    int _reverbCounter;
    int _capturePos;

    // ── Audio output ring buffer (lock-free SPSC) ────────────────────────
    const int RingSize = 8192; // power of 2, in stereo sample pairs
    readonly short[] _ring = new short[RingSize * 2];
    volatile int _ringWrite;
    volatile int _ringRead;

    public PsxSpu()
    {
        _ramH = GCHandle.Alloc(_ram, GCHandleType.Pinned);
        _ramPtr = (byte*)_ramH.AddrOfPinnedObject();
    }

    ~PsxSpu()
    {
        if (_ramH.IsAllocated) _ramH.Free();
    }

    public void Reset()
    {
        Array.Clear(_ram);
        _voices = new Voice[NumVoices];
        _keyOn = _keyOff = _endx = 0;
        _controlReg = 0;
        _statusReg = 0;
        _counter = 0;
        _reverbCounter = 0;
        _capturePos = 0;
        _noiseTimer = 0;
        _noiseLevel = 0;
        _ringWrite = 0;
        _ringRead = 0;
        _reverbStartAddr = 0;
        _reverbInternalAddr = 0;
        _transferAddrInternal = 0;
    }

    // ── Ring buffer access for audio thread ──────────────────────────────

    public int AvailableSamples
    {
        get
        {
            int w = _ringWrite, r = _ringRead;
            return ((w - r) + RingSize) % RingSize;
        }
    }

    /// <summary>
    /// Read stereo samples into dest. Returns number of stereo frames read.
    /// </summary>
    public int ReadSamples(short[] dest, int offset, int maxFrames)
    {
        int avail = AvailableSamples;
        int frames = Math.Min(avail, maxFrames);
        int r = _ringRead;
        for (int i = 0; i < frames; i++)
        {
            dest[offset + i * 2] = _ring[r * 2];
            dest[offset + i * 2 + 1] = _ring[r * 2 + 1];
            r = (r + 1) % RingSize;
        }
        _ringRead = r;
        return frames;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void PushSample(short left, short right)
    {
        int w = _ringWrite;
        int next = (w + 1) % RingSize;
        if (next == _ringRead) return; // full, drop
        _ring[w * 2] = left;
        _ring[w * 2 + 1] = right;
        _ringWrite = next;
    }

    // ── Main tick — returns true if IRQ9 should fire ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Tick(int cycles)
    {
        _counter += cycles;
        bool irq = false;
        while (_counter >= CyclesPerSample)
        {
            _counter -= CyclesPerSample;
            irq |= ProcessSample();
        }
        return irq;
    }

    bool ProcessSample()
    {
        bool edgeTrigger = false;
        int sumL = 0, sumR = 0;
        int sumRevL = 0, sumRevR = 0;

        uint edgeOn = _keyOn; _keyOn = 0;
        uint edgeOff = _keyOff; _keyOff = 0;

        TickNoise();

        for (int i = 0; i < NumVoices; i++)
        {
            ref var v = ref _voices[i];

            if ((edgeOff & (1u << i)) != 0) v.KeyOff();
            if ((edgeOn & (1u << i)) != 0)
            {
                _endx &= ~(1u << i);
                v.KeyOn();
            }

            if (v.AdsrPhase == Phase.Off) { v.Latest = 0; continue; }

            short sample;
            if ((_noiseMode & (1u << i)) != 0)
            {
                sample = (short)_noiseLevel;
            }
            else
            {
                sample = SampleVoice(i);
                edgeTrigger |= Irq9Enabled && v.ReadRamIrq;
                v.ReadRamIrq = false;
            }

            sample = (short)((sample * v.AdsrVolume) >> 15);
            v.TickAdsr();
            v.Latest = sample;

            int volL = v.ProcessVolume(v.VolLeftReg);
            int volR = v.ProcessVolume(v.VolRightReg);
            sumL += (sample * volL) >> 15;
            sumR += (sample * volR) >> 15;

            if ((_reverbMode & (1u << i)) != 0)
            {
                sumRevL += (sample * volL) >> 15;
                sumRevR += (sample * volR) >> 15;
            }
        }

        if (!SpuUnmuted) { sumL = 0; sumR = 0; }

        // Reverb (at 22050 Hz = every other sample)
        if (_reverbCounter == 0)
        {
            var (revL, revR) = ProcessReverb(sumRevL, sumRevR);
            sumL += revL;
            sumR += revR;
        }
        _reverbCounter = (_reverbCounter + 1) & 1;

        // Capture buffers
        edgeTrigger |= WriteCaptureBuffer(0 * 1024 + _capturePos, 0);
        edgeTrigger |= WriteCaptureBuffer(1 * 1024 + _capturePos, 0);
        edgeTrigger |= WriteCaptureBuffer(2 * 1024 + _capturePos, NumVoices > 1 ? _voices[1].Latest : (short)0);
        edgeTrigger |= WriteCaptureBuffer(3 * 1024 + _capturePos, NumVoices > 3 ? _voices[3].Latest : (short)0);
        _capturePos = (_capturePos + 2) & 0x3FF;

        // Final mix with main volume (handle sweep mode: use curVol if sweep bit set)
        int mVolL = ((_mainVolLeft & 0x8000) != 0)
            ? (short)(_curVolLeft << 1) : (_mainVolLeft << 1);
        int mVolR = ((_mainVolRight & 0x8000) != 0)
            ? (short)(_curVolRight << 1) : (_mainVolRight << 1);
        if (mVolL == 0 && (_mainVolLeft & 0x8000) != 0) mVolL = 0x7FFE;
        if (mVolR == 0 && (_mainVolRight & 0x8000) != 0) mVolR = 0x7FFE;
        sumL = (Math.Clamp(sumL, -0x8000, 0x7FFF) * mVolL) >> 15;
        sumR = (Math.Clamp(sumR, -0x8000, 0x7FFF) * mVolR) >> 15;

        PushSample((short)Math.Clamp(sumL, -0x8000, 0x7FFF),
                    (short)Math.Clamp(sumR, -0x8000, 0x7FFF));

        if (Irq9Enabled && edgeTrigger)
            _statusReg |= 1 << 6; // IRQ9 flag

        return Irq9Enabled && edgeTrigger;
    }

    bool WriteCaptureBuffer(int addr, short sample)
    {
        *(short*)(_ramPtr + (addr & 0x7_FFFE)) = sample;
        return (addr >> 3) == _irqAddress;
    }

    // ── Noise generator ──────────────────────────────────────────────────
    void TickNoise()
    {
        int step = NoiseStep;
        int shift = NoiseShift;
        _noiseTimer -= step;
        int parity = ((_noiseLevel >> 15) & 1) ^ ((_noiseLevel >> 12) & 1) ^
                     ((_noiseLevel >> 11) & 1) ^ ((_noiseLevel >> 10) & 1) ^ 1;
        if (_noiseTimer < 0) _noiseLevel = _noiseLevel * 2 + parity;
        if (_noiseTimer < 0) _noiseTimer += 0x20000 >> shift;
        if (_noiseTimer < 0) _noiseTimer += 0x20000 >> shift;
    }

    // ── Voice sampling with ADPCM decode + Gaussian interpolation ────────
    short SampleVoice(int idx)
    {
        ref var v = ref _voices[idx];

        if (!v.HasSamples)
        {
            DecodeSamples(ref v);
            v.HasSamples = true;
            byte flags = v.AdpcmBlock[1];
            if ((flags & 0x4) != 0) // Loop Start
                v.RepeatAddress = v.CurrentAddress;
        }

        uint interpIdx = (v.Counter >> 3) & 0xFF;
        uint sampleIdx = (v.Counter >> 12) & 0x1F;

        int interp = GaussTable[0x0FF - interpIdx] * v.Decoded[sampleIdx + 0];
        interp += GaussTable[0x1FF - interpIdx] * v.Decoded[sampleIdx + 1];
        interp += GaussTable[0x100 + interpIdx] * v.Decoded[sampleIdx + 2];
        interp += GaussTable[0x000 + interpIdx] * v.Decoded[sampleIdx + 3];
        interp >>= 15;

        // Pitch with modulation
        int step = v.Pitch;
        if ((_pitchMod & (1u << idx)) != 0 && idx > 0)
        {
            int factor = _voices[idx - 1].Latest + 0x8000;
            step = (step * factor) >> 15;
            step &= 0xFFFF;
        }
        if (step > 0x3FFF) step = 0x4000;

        v.Counter += (uint)step;

        if (((v.Counter >> 12) & 0x1F) >= 28)
        {
            v.Counter -= 28u << 12;
            v.CurrentAddress += 2;
            v.HasSamples = false;

            byte flags = v.AdpcmBlock[1];
            bool loopEnd = (flags & 1) != 0;
            bool loopRepeat = (flags & 2) != 0;

            if (loopEnd)
            {
                _endx |= 1u << idx;
                if (loopRepeat)
                    v.CurrentAddress = v.RepeatAddress;
                else
                {
                    v.AdsrPhase = Phase.Off;
                    v.AdsrVolume = 0;
                }
            }
        }

        return (short)interp;
    }

    void DecodeSamples(ref Voice v)
    {
        // Save last 3 samples for interpolation
        v.Decoded[2] = v.Decoded[30];
        v.Decoded[1] = v.Decoded[29];
        v.Decoded[0] = v.Decoded[28];

        int ramAddr = v.CurrentAddress * 8;
        for (int b = 0; b < 16; b++)
            v.AdpcmBlock[b] = *(_ramPtr + ((ramAddr + b) & 0x7_FFFF));

        v.ReadRamIrq |= v.CurrentAddress == _irqAddress || (ushort)(v.CurrentAddress + 1) == _irqAddress;

        int headerShift = v.AdpcmBlock[0] & 0xF;
        if (headerShift > 12) headerShift = 9;
        int shift = 12 - headerShift;
        int filter = (v.AdpcmBlock[0] >> 4) & 7;
        if (filter > 4) filter = 4;

        int f0 = PosAdpcmTable[filter];
        int f1 = NegAdpcmTable[filter];

        int pos = 2;
        int nibble = 1;
        for (int i = 0; i < 28; i++)
        {
            nibble = (nibble + 1) & 1;
            int t = (v.AdpcmBlock[pos] >> (nibble * 4)) & 0xF;
            t = (t << 28) >> 28; // sign extend 4-bit
            int s = (t << shift) + ((v.Old * f0 + v.Older * f1 + 32) / 64);
            short sample = (short)Math.Clamp(s, -0x8000, 0x7FFF);
            v.Decoded[3 + i] = sample;
            v.Older = v.Old;
            v.Old = sample;
            pos += nibble;
        }
    }

    // ── Reverb ───────────────────────────────────────────────────────────
    (short, short) ProcessReverb(int lInput, int rInput)
    {
        if (!ReverbEnabled || _reverbStartAddr == 0)
            return (0, 0);

        int Lin = (_vLIN * lInput) >> 15;
        int Rin = (_vRIN * rInput) >> 15;

        short mlSame = Sat(Lin + ((LoadReverb(_dLSAME) * _vWALL) >> 15) - ((LoadReverb(_mLSAME - 2) * _vIIR) >> 15) + LoadReverb(_mLSAME - 2));
        short mrSame = Sat(Rin + ((LoadReverb(_dRSAME) * _vWALL) >> 15) - ((LoadReverb(_mRSAME - 2) * _vIIR) >> 15) + LoadReverb(_mRSAME - 2));
        WriteReverb(_mLSAME, mlSame);
        WriteReverb(_mRSAME, mrSame);

        short mlDiff = Sat(Lin + ((LoadReverb(_dRDIFF) * _vWALL) >> 15) - ((LoadReverb(_mLDIFF - 2) * _vIIR) >> 15) + LoadReverb(_mLDIFF - 2));
        short mrDiff = Sat(Rin + ((LoadReverb(_dLDIFF) * _vWALL) >> 15) - ((LoadReverb(_mRDIFF - 2) * _vIIR) >> 15) + LoadReverb(_mRDIFF - 2));
        WriteReverb(_mLDIFF, mlDiff);
        WriteReverb(_mRDIFF, mrDiff);

        short l = Sat((_vCOMB1 * LoadReverb(_mLCOMB1) >> 15) + (_vCOMB2 * LoadReverb(_mLCOMB2) >> 15) + (_vCOMB3 * LoadReverb(_mLCOMB3) >> 15) + (_vCOMB4 * LoadReverb(_mLCOMB4) >> 15));
        short r = Sat((_vCOMB1 * LoadReverb(_mRCOMB1) >> 15) + (_vCOMB2 * LoadReverb(_mRCOMB2) >> 15) + (_vCOMB3 * LoadReverb(_mRCOMB3) >> 15) + (_vCOMB4 * LoadReverb(_mRCOMB4) >> 15));

        l = Sat(l - Sat((_vAPF1 * LoadReverb(_mLAPF1 - _dAPF1)) >> 15));
        r = Sat(r - Sat((_vAPF1 * LoadReverb(_mRAPF1 - _dAPF1)) >> 15));
        WriteReverb(_mLAPF1, l);
        WriteReverb(_mRAPF1, r);
        l = Sat((l * _vAPF1 >> 15) + LoadReverb(_mLAPF1 - _dAPF1));
        r = Sat((r * _vAPF1 >> 15) + LoadReverb(_mRAPF1 - _dAPF1));

        l = Sat(l - Sat((_vAPF2 * LoadReverb(_mLAPF2 - _dAPF2)) >> 15));
        r = Sat(r - Sat((_vAPF2 * LoadReverb(_mRAPF2 - _dAPF2)) >> 15));
        WriteReverb(_mLAPF2, l);
        WriteReverb(_mRAPF2, r);
        l = Sat((l * _vAPF2 >> 15) + LoadReverb(_mLAPF2 - _dAPF2));
        r = Sat((r * _vAPF2 >> 15) + LoadReverb(_mRAPF2 - _dAPF2));

        l = Sat(l * _reverbVolLeft >> 15);
        r = Sat(r * _reverbVolRight >> 15);

        _reverbInternalAddr = Math.Max(_reverbStartAddr, (_reverbInternalAddr + 2) & 0x7_FFFE);
        return (l, r);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short Sat(int v) => (short)Math.Clamp(v, -0x8000, 0x7FFF);

    short LoadReverb(uint addr)
    {
        uint rel = (addr + _reverbInternalAddr - _reverbStartAddr) % (0x8_0000 - _reverbStartAddr);
        uint wrapped = (_reverbStartAddr + rel) & 0x7_FFFE;
        return *(short*)(_ramPtr + wrapped);
    }

    void WriteReverb(uint addr, short val)
    {
        if (!ReverbEnabled) return;
        uint rel = (addr + _reverbInternalAddr - _reverbStartAddr) % (0x8_0000 - _reverbStartAddr);
        uint wrapped = (_reverbStartAddr + rel) & 0x7_FFFE;
        *(short*)(_ramPtr + wrapped) = val;
    }

    // ── DMA interface ────────────────────────────────────────────────────

    public void DmaWrite(Span<uint> data)
    {
        _statusReg |= 1 << 10; // transfer busy
        var bytes = MemoryMarshal.Cast<uint, byte>(data);
        int dest = (int)_transferAddrInternal;
        for (int i = 0; i < bytes.Length; i++)
        {
            *(_ramPtr + (dest & 0x7_FFFF)) = bytes[i];
            dest++;
        }
        uint irqAddr32 = (uint)_irqAddress << 3;
        if (irqAddr32 >= _transferAddrInternal && irqAddr32 < (uint)dest)
        {
            if (Irq9Enabled) _statusReg |= 1 << 6;
        }
        _transferAddrInternal = (uint)(dest & 0x7_FFFF);
        _statusReg &= unchecked((ushort)~(1 << 10)); // transfer done
    }

    public Span<uint> DmaRead(int wordCount)
    {
        _statusReg |= 1 << 10; // transfer busy
        var result = new uint[wordCount];
        int src = (int)_transferAddrInternal;
        for (int i = 0; i < wordCount; i++)
        {
            result[i] = *(uint*)(_ramPtr + (src & 0x7_FFFC));
            src += 4;
        }
        uint irqAddr32 = (uint)_irqAddress << 3;
        if (irqAddr32 >= _transferAddrInternal && irqAddr32 < (uint)src)
        {
            if (Irq9Enabled) _statusReg |= 1 << 6;
        }
        _transferAddrInternal = (uint)(src & 0x7_FFFF);
        _statusReg &= unchecked((ushort)~(1 << 10)); // transfer done
        return result;
    }

    // ── Register read/write ──────────────────────────────────────────────

    public ushort Read(uint addr)
    {
        if (addr >= 0x1F801C00 && addr <= 0x1F801D7F)
        {
            int idx = (int)(((addr & 0xFF0) >> 4) - 0xC0);
            if (idx < 0 || idx >= NumVoices) return 0;
            ref var v = ref _voices[idx];
            return (addr & 0xF) switch
            {
                0x0 => v.VolLeftReg,
                0x2 => v.VolRightReg,
                0x4 => v.Pitch,
                0x6 => v.StartAddress,
                0x8 => v.AdsrLo,
                0xA => v.AdsrHi,
                0xC => v.AdsrVolume,
                0xE => v.RepeatAddress,
                _ => 0,
            };
        }

        return addr switch
        {
            0x1F801D80 => (ushort)_mainVolLeft,
            0x1F801D82 => (ushort)_mainVolRight,
            0x1F801D84 => (ushort)_reverbVolLeft,
            0x1F801D86 => (ushort)_reverbVolRight,
            0x1F801D88 => (ushort)_keyOn,
            0x1F801D8A => (ushort)(_keyOn >> 16),
            0x1F801D8C => (ushort)_keyOff,
            0x1F801D8E => (ushort)(_keyOff >> 16),
            0x1F801D90 => (ushort)_pitchMod,
            0x1F801D92 => (ushort)(_pitchMod >> 16),
            0x1F801D94 => (ushort)_noiseMode,
            0x1F801D96 => (ushort)(_noiseMode >> 16),
            0x1F801D98 => (ushort)_reverbMode,
            0x1F801D9A => (ushort)(_reverbMode >> 16),
            0x1F801D9C => (ushort)_endx,
            0x1F801D9E => (ushort)(_endx >> 16),
            0x1F801DA0 => _unknownA0,
            0x1F801DA2 => (ushort)(_reverbStartAddr >> 3),
            0x1F801DA4 => _irqAddress,
            0x1F801DA6 => _transferAddress,
            0x1F801DA8 => _transferFifo,
            0x1F801DAA => _controlReg,
            0x1F801DAC => _transferControl,
            0x1F801DAE => _statusReg,
            0x1F801DB0 => _cdVolLeft,
            0x1F801DB2 => _cdVolRight,
            0x1F801DB4 => _extVolLeft,
            0x1F801DB6 => _extVolRight,
            0x1F801DB8 => _curVolLeft,
            0x1F801DBA => _curVolRight,
            0x1F801DBC => (ushort)_unknownBC,
            0x1F801DBE => (ushort)(_unknownBC >> 16),
            0x1F801DC0 => (ushort)(_dAPF1 >> 3),
            0x1F801DC2 => (ushort)(_dAPF2 >> 3),
            0x1F801DC4 => (ushort)_vIIR,
            0x1F801DC6 => (ushort)_vCOMB1,
            0x1F801DC8 => (ushort)_vCOMB2,
            0x1F801DCA => (ushort)_vCOMB3,
            0x1F801DCC => (ushort)_vCOMB4,
            0x1F801DCE => (ushort)_vWALL,
            0x1F801DD0 => (ushort)_vAPF1,
            0x1F801DD2 => (ushort)_vAPF2,
            0x1F801DD4 => (ushort)(_mLSAME >> 3),
            0x1F801DD6 => (ushort)(_mRSAME >> 3),
            0x1F801DD8 => (ushort)(_mLCOMB1 >> 3),
            0x1F801DDA => (ushort)(_mRCOMB1 >> 3),
            0x1F801DDC => (ushort)(_mLCOMB2 >> 3),
            0x1F801DDE => (ushort)(_mRCOMB2 >> 3),
            0x1F801DE0 => (ushort)(_dLSAME >> 3),
            0x1F801DE2 => (ushort)(_dRSAME >> 3),
            0x1F801DE4 => (ushort)(_mLDIFF >> 3),
            0x1F801DE6 => (ushort)(_mRDIFF >> 3),
            0x1F801DE8 => (ushort)(_mLCOMB3 >> 3),
            0x1F801DEA => (ushort)(_mRCOMB3 >> 3),
            0x1F801DEC => (ushort)(_mLCOMB4 >> 3),
            0x1F801DEE => (ushort)(_mRCOMB4 >> 3),
            0x1F801DF0 => (ushort)(_dLDIFF >> 3),
            0x1F801DF2 => (ushort)(_dRDIFF >> 3),
            0x1F801DF4 => (ushort)(_mLAPF1 >> 3),
            0x1F801DF6 => (ushort)(_mRAPF1 >> 3),
            0x1F801DF8 => (ushort)(_mLAPF2 >> 3),
            0x1F801DFA => (ushort)(_mRAPF2 >> 3),
            0x1F801DFC => (ushort)_vLIN,
            0x1F801DFE => (ushort)_vRIN,
            // Voice internal (read-only) — return ADSR volume for the voice
            >= 0x1F801E00 and <= 0x1F801E5F =>
                ReadVoiceInternal(addr),
            _ => 0,
        };
    }

    ushort ReadVoiceInternal(uint addr)
    {
        int voiceIdx = (int)((addr - 0x1F801E00) / 4);
        bool isHigh = ((addr - 0x1F801E00) & 2) != 0;
        if (voiceIdx < 0 || voiceIdx >= NumVoices) return 0;
        if (isHigh) return 0; // high word: current volume right (not tracked in detail)
        return _voices[voiceIdx].AdsrVolume;
    }

    public void Write(uint addr, ushort val)
    {
        if (addr >= 0x1F801C00 && addr <= 0x1F801D7F)
        {
            int idx = (int)(((addr & 0xFF0) >> 4) - 0xC0);
            if (idx < 0 || idx >= NumVoices) return;
            ref var v = ref _voices[idx];
            switch (addr & 0xF)
            {
                case 0x0: v.VolLeftReg = val; break;
                case 0x2: v.VolRightReg = val; break;
                case 0x4: v.Pitch = val; break;
                case 0x6: v.StartAddress = val; break;
                case 0x8: v.AdsrLo = val; break;
                case 0xA: v.AdsrHi = val; break;
                case 0xC: v.AdsrVolume = val; break;
                case 0xE: v.RepeatAddress = val; break;
            }
            return;
        }

        switch (addr)
        {
            case 0x1F801D80: _mainVolLeft = (short)val; break;
            case 0x1F801D82: _mainVolRight = (short)val; break;
            case 0x1F801D84: _reverbVolLeft = (short)val; break;
            case 0x1F801D86: _reverbVolRight = (short)val; break;
            case 0x1F801D88: _keyOn = (_keyOn & 0xFFFF0000) | val; break;
            case 0x1F801D8A: _keyOn = (_keyOn & 0xFFFF) | ((uint)val << 16); break;
            case 0x1F801D8C: _keyOff = (_keyOff & 0xFFFF0000) | val; break;
            case 0x1F801D8E: _keyOff = (_keyOff & 0xFFFF) | ((uint)val << 16); break;
            case 0x1F801D90: _pitchMod = (_pitchMod & 0xFFFF0000) | val; break;
            case 0x1F801D92: _pitchMod = (_pitchMod & 0xFFFF) | ((uint)val << 16); break;
            case 0x1F801D94: _noiseMode = (_noiseMode & 0xFFFF0000) | val; break;
            case 0x1F801D96: _noiseMode = (_noiseMode & 0xFFFF) | ((uint)val << 16); break;
            case 0x1F801D98: _reverbMode = (_reverbMode & 0xFFFF0000) | val; break;
            case 0x1F801D9A: _reverbMode = (_reverbMode & 0xFFFF) | ((uint)val << 16); break;
            case 0x1F801D9C: _endx = (_endx & 0xFFFF0000) | val; break;
            case 0x1F801D9E: _endx = (_endx & 0xFFFF) | ((uint)val << 16); break;
            case 0x1F801DA0: _unknownA0 = val; break;
            case 0x1F801DA2:
                _reverbStartAddr = (uint)(val << 3);
                _reverbInternalAddr = _reverbStartAddr;
                break;
            case 0x1F801DA4: _irqAddress = val; break;
            case 0x1F801DA6:
                _transferAddress = val;
                _transferAddrInternal = (uint)(val * 8);
                break;
            case 0x1F801DA8:
                _transferFifo = val;
                *(short*)(_ramPtr + (_transferAddrInternal & 0x7_FFFE)) = (short)val;
                _transferAddrInternal = (_transferAddrInternal + 2) & 0x7_FFFF;
                break;
            case 0x1F801DAA:
                _controlReg = val;
                if (!SpuEnabled)
                    for (int i = 0; i < NumVoices; i++)
                    {
                        _voices[i].AdsrPhase = Phase.Off;
                        _voices[i].AdsrVolume = 0;
                    }
                if (!Irq9Enabled)
                    _statusReg &= unchecked((ushort)~(1 << 6));
                // Mirror bits 0-5 from control to status
                _statusReg = (ushort)((_statusReg & 0xFFC0) | (val & 0x3F));
                // Set DMA request bits based on transfer mode
                int xferMode = (val >> 4) & 3;
                _statusReg &= unchecked((ushort)~((0x3 << 8) | (1 << 7))); // clear bits 7-9
                if (xferMode >= 2) _statusReg |= 1 << 7;       // bit 7: DMA R/W request
                if (xferMode == 2) _statusReg |= 1 << 8;       // DMA write request
                else if (xferMode == 3) _statusReg |= 1 << 9;  // DMA read request
                break;
            case 0x1F801DAC: _transferControl = val; break;
            case 0x1F801DAE: _statusReg = val; break;
            case 0x1F801DB0: _cdVolLeft = val; break;
            case 0x1F801DB2: _cdVolRight = val; break;
            case 0x1F801DB4: _extVolLeft = val; break;
            case 0x1F801DB6: _extVolRight = val; break;
            case 0x1F801DB8: _curVolLeft = val; break;
            case 0x1F801DBA: _curVolRight = val; break;
            case 0x1F801DBC: _unknownBC = (_unknownBC & 0xFFFF0000) | val; break;
            case 0x1F801DBE: _unknownBC = (_unknownBC & 0xFFFF) | ((uint)val << 16); break;
            case 0x1F801DC0: _dAPF1 = (uint)(val << 3); break;
            case 0x1F801DC2: _dAPF2 = (uint)(val << 3); break;
            case 0x1F801DC4: _vIIR = (short)val; break;
            case 0x1F801DC6: _vCOMB1 = (short)val; break;
            case 0x1F801DC8: _vCOMB2 = (short)val; break;
            case 0x1F801DCA: _vCOMB3 = (short)val; break;
            case 0x1F801DCC: _vCOMB4 = (short)val; break;
            case 0x1F801DCE: _vWALL = (short)val; break;
            case 0x1F801DD0: _vAPF1 = (short)val; break;
            case 0x1F801DD2: _vAPF2 = (short)val; break;
            case 0x1F801DD4: _mLSAME = (uint)(val << 3); break;
            case 0x1F801DD6: _mRSAME = (uint)(val << 3); break;
            case 0x1F801DD8: _mLCOMB1 = (uint)(val << 3); break;
            case 0x1F801DDA: _mRCOMB1 = (uint)(val << 3); break;
            case 0x1F801DDC: _mLCOMB2 = (uint)(val << 3); break;
            case 0x1F801DDE: _mRCOMB2 = (uint)(val << 3); break;
            case 0x1F801DE0: _dLSAME = (uint)(val << 3); break;
            case 0x1F801DE2: _dRSAME = (uint)(val << 3); break;
            case 0x1F801DE4: _mLDIFF = (uint)(val << 3); break;
            case 0x1F801DE6: _mRDIFF = (uint)(val << 3); break;
            case 0x1F801DE8: _mLCOMB3 = (uint)(val << 3); break;
            case 0x1F801DEA: _mRCOMB3 = (uint)(val << 3); break;
            case 0x1F801DEC: _mLCOMB4 = (uint)(val << 3); break;
            case 0x1F801DEE: _mRCOMB4 = (uint)(val << 3); break;
            case 0x1F801DF0: _dLDIFF = (uint)(val << 3); break;
            case 0x1F801DF2: _dRDIFF = (uint)(val << 3); break;
            case 0x1F801DF4: _mLAPF1 = (uint)(val << 3); break;
            case 0x1F801DF6: _mRAPF1 = (uint)(val << 3); break;
            case 0x1F801DF8: _mLAPF2 = (uint)(val << 3); break;
            case 0x1F801DFA: _mRAPF2 = (uint)(val << 3); break;
            case 0x1F801DFC: _vLIN = (short)val; break;
            case 0x1F801DFE: _vRIN = (short)val; break;
        }
    }

    /// <summary>Returns true if SPU status has IRQ9 flag set.</summary>
    public bool Irq9Pending => (_statusReg & (1 << 6)) != 0;
}
