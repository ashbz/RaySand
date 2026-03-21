using System;

namespace Pico8Emu
{
    // ── PICO-8 audio data structures ───────────────────────────────────────────

    public struct P8Note
    {
        public byte Pitch;   // 0-63  (PICO-8 "C0"=0 maps to standard C2=65.41 Hz)
        public byte Wave;    // 0-7   waveform
        public byte Vol;     // 0-7   volume
        public byte Fx;      // 0-7   effect
    }

    public class P8Sfx
    {
        public P8Note[] Notes     { get; } = new P8Note[32];
        public int      Speed;       // ticks per note; 0 treated as 1. 1 tick = 183 samples @ 22050 Hz
        public int      LoopStart;   // note index where loop begins
        public int      LoopEnd;     // note index where loop ends (0 = no loop)
        public bool     HasData;     // set if this slot contains a real SFX
    }

    public class P8Music
    {
        public int[]  SfxIdx  { get; } = new int[4] { -1, -1, -1, -1 };
        public bool   LoopBegin;
        public bool   LoopEnd;
        public bool   Stop;
        public bool   HasData => SfxIdx[0] >= 0 || SfxIdx[1] >= 0
                              || SfxIdx[2] >= 0 || SfxIdx[3] >= 0;
    }

    // ── Per-channel playback state ─────────────────────────────────────────────

    internal sealed class ChannelState
    {
        public bool   Active;
        public bool   IsMusic;
        public int    SfxIdx      = -1;
        public bool   CanLoop     = true;

        // Note position as a float: integer part = note index, fractional = within note
        public double Offset;        // 0..32 range; advances by 1/(183*speed) per sample
        public double Length;        // where to stop (0 = use 32)

        // Phase is CUMULATIVE (not wrapped) so phaser/noise work correctly
        public double Phase;
        public double PrevPhase;     // previous sample's phase for noise delta calculation
        public float  NoiseSample;   // filtered noise state

        // For slide effect
        public byte   PrevKey  = 24; // previous note's pitch (default C2)
        public float  PrevVol  = 0f;
    }

    // ── Music channel state ────────────────────────────────────────────────────

    internal sealed class MusicState
    {
        public int    Pattern   = -1;
        public int    LoopFrom  = 0;
        public int    Mask      = 0;    // channels reserved for music (sfx avoids these)
        public double Offset    = 0;    // in note-time units, advances by 1/183 per sample
        public double Length    = 0;    // pattern duration in note-time units
        public float  Volume    = 1f;
        public float  VolStep   = 0f;
    }

    // ── Audio synthesizer ─────────────────────────────────────────────────────

    public sealed class Pico8Audio
    {
        public const int SampleRate   = 22050;
        public const int NumChannels  = 4;
        public const int BufferFrames = 512;

        // 1 tick = 183 samples at 22050 Hz  (22050/183 ≈ 120.5 ticks/s = BPM reference)
        private const double InvTickRate = 1.0 / 183.0;   // ticks per sample at speed=1

        // Noise: tscale = SampleRate / key_to_freq(63) = 22050 / 2489 ≈ 8.859
        private const float NoiseTscale = 8.858923f;

        private readonly P8Sfx[]        _sfx;
        private readonly P8Music[]      _music;
        private readonly ChannelState[] _ch = new ChannelState[NumChannels];
        private readonly MusicState     _ms = new MusicState();

        public Pico8Audio(P8Sfx[] sfx, P8Music[] music)
        {
            _sfx   = sfx;
            _music = music;
            for (int i = 0; i < NumChannels; i++) _ch[i] = new ChannelState();
        }

        // ── Status queries (for stat(46..56)) ─────────────────────────────────

        public int ChannelSfx(int c)  => (c >= 0 && c < NumChannels && _ch[c].Active) ? _ch[c].SfxIdx  : -1;
        public int ChannelNote(int c) => (c >= 0 && c < NumChannels && _ch[c].Active) ? (int)_ch[c].Offset : -1;
        public int MusicPattern()     => _ms.Pattern;
        public int PatternTicks()     => (int)_ms.Offset;

        // ── Public API ────────────────────────────────────────────────────────

        public void SfxPlay(int n, int channel, int offset, int length)
        {
            if (n == -1)
            {
                if (channel >= 0 && channel < NumChannels)
                    _ch[channel].Active = false;
                else
                    for (int i = 0; i < NumChannels; i++)
                        if (!_ch[i].IsMusic) _ch[i].Active = false;
                return;
            }
            if (n == -2)
            {
                if (channel >= 0 && channel < NumChannels)
                { if (!_ch[channel].IsMusic) _ch[channel].CanLoop = false; }
                else
                    for (int i = 0; i < NumChannels; i++)
                        if (!_ch[i].IsMusic) _ch[i].CanLoop = false;
                return;
            }
            if (n < 0 || n >= 64) return;

            if (channel == -2)
            {
                for (int i = 0; i < NumChannels; i++)
                    if (_ch[i].Active && _ch[i].SfxIdx == n) _ch[i].Active = false;
                return;
            }

            var sfx = _sfx[n];
            if (!sfx.HasData) return;

            if (channel < 0) channel = FindFreeChannel();
            if (channel < 0 || channel >= NumChannels) return;

            // Stop any other channel already playing this SFX
            for (int i = 0; i < NumChannels; i++)
                if (i != channel && _ch[i].SfxIdx == n) _ch[i].Active = false;

            int noteOffset = Math.Clamp(offset, 0, 31);
            double noteEnd = length > 0 ? noteOffset + length : 0;
            LaunchSfx(channel, n, sfx, noteOffset, noteEnd, isMusic: false);
        }

        public void MusicPlay(int n, int fadeLenMs, int chMask)
        {
            if (n == -1)
            {
                if (fadeLenMs > 0)
                    _ms.VolStep = -_ms.Volume * (1000f / fadeLenMs);
                else
                {
                    _ms.Pattern = -1;
                    for (int i = 0; i < NumChannels; i++)
                        if (_ch[i].IsMusic) { _ch[i].Active = false; _ch[i].IsMusic = false; }
                }
                return;
            }
            if (n < 0 || n >= 64) return;

            _ms.Mask    = chMask & 0xF;
            _ms.Volume  = fadeLenMs > 0 ? 0f : 1f;
            _ms.VolStep = fadeLenMs > 0 ? 1000f / fadeLenMs : 0f;
            _ms.LoopFrom = n;

            SetMusicPattern(n);
        }

        // ── Sample generation ──────────────────────────────────────────────────

        public void FillBuffer(short[] buf, int start, int frameCount)
        {
            for (int i = 0; i < frameCount; i++)
            {
                // Advance music timer (based on channel 0's inv_fps = 1/22050)
                AdvanceMusic();

                float mix = 0f;
                for (int c = 0; c < NumChannels; c++)
                {
                    if (!_ch[c].Active) continue;
                    float samp = GetChannelSample(c);
                    mix += samp;
                }

                // Clamp to [-1, 1] before converting, matching PICO-8's behaviour
                buf[start + i] = (short)Math.Clamp((int)(mix * 32767f), -32767, 32767);
            }
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private void AdvanceMusic()
        {
            if (_ms.Pattern < 0) return;

            // Advance fade volume
            if (_ms.VolStep != 0)
            {
                _ms.Volume += _ms.VolStep / SampleRate;
                _ms.Volume = Math.Clamp(_ms.Volume, 0f, 1f);
                if (_ms.VolStep < 0 && _ms.Volume <= 0f)
                {
                    SetMusicPattern(-1);
                    return;
                }
            }

            // Music offset advances at 1/183 per sample (speed-independent global clock)
            _ms.Offset += InvTickRate;

            if (_ms.Offset >= _ms.Length)
            {
                // Pattern finished — find next
                int next = _ms.Pattern + 1;
                if (_music[_ms.Pattern].Stop)
                {
                    SetMusicPattern(-1);
                    return;
                }
                if (_music[_ms.Pattern].LoopEnd)
                {
                    next = _ms.LoopFrom;
                }
                else if (next >= 64 || !_music[next].HasData)
                {
                    next = _ms.LoopFrom;
                }
                SetMusicPattern(next);
            }
        }

        private void SetMusicPattern(int pattern)
        {
            // Stop all currently active music channels
            for (int n = 0; n < NumChannels; n++)
                if (_ch[n].IsMusic) { _ch[n].Active = false; _ch[n].IsMusic = false; }

            if (pattern < 0 || pattern >= 64)
            {
                _ms.Pattern = -1;
                return;
            }

            var pat = _music[pattern];
            if (pat.LoopBegin) _ms.LoopFrom = pattern;
            _ms.Pattern = pattern;
            _ms.Offset  = 0;
            _ms.Length  = ComputePatternLength(pat);

            for (int c = 0; c < NumChannels; c++)
            {
                int si = pat.SfxIdx[c];
                if (si < 0 || si >= 64 || !_sfx[si].HasData) continue;
                LaunchSfx(c, si, _sfx[si], 0, 0, isMusic: true);
            }
        }

        private double ComputePatternLength(P8Music pat)
        {
            // Pattern length = duration of first non-looping channel.
            // If all channels loop, use the longest duration.
            double loopDuration   = -1;
            double noLoopDuration = -1;

            for (int c = 0; c < 4; c++)
            {
                int si = pat.SfxIdx[c];
                if (si < 0 || si >= 64 || !_sfx[si].HasData) continue;
                var sfx = _sfx[si];
                int speed = Math.Max(sfx.Speed, 1);
                bool hasLoop = sfx.LoopEnd > sfx.LoopStart && sfx.LoopEnd > 0;

                if (hasLoop)
                {
                    loopDuration = Math.Max(loopDuration, 32.0 * speed);
                }
                else
                {
                    double end = 32.0;
                    if (sfx.LoopEnd == 0 && sfx.LoopStart > 0)
                        end = Math.Min(end, sfx.LoopStart);
                    noLoopDuration = end * speed;
                    break; // use first non-looping channel
                }
            }

            double dur = noLoopDuration > 0 ? noLoopDuration
                       : loopDuration    > 0 ? loopDuration
                       : 32.0;
            return dur; // in note-time units (1 unit = 183 samples * speed)
        }

        private int FindFreeChannel()
        {
            // Prefer idle, non-music-reserved channels
            for (int i = 0; i < NumChannels; i++)
                if (!_ch[i].Active && (_ms.Mask & (1 << i)) == 0) return i;
            // Then any idle channel
            for (int i = 0; i < NumChannels; i++)
                if (!_ch[i].Active) return i;
            // Then a non-music, non-masked channel
            for (int i = 0; i < NumChannels; i++)
                if (!_ch[i].IsMusic && (_ms.Mask & (1 << i)) == 0) return i;
            // Last resort: any non-music channel
            for (int i = 0; i < NumChannels; i++)
                if (!_ch[i].IsMusic) return i;
            return -1;
        }

        private void LaunchSfx(int c, int sfxIdx, P8Sfx sfx, double offset, double length, bool isMusic)
        {
            var ch = _ch[c];
            ch.SfxIdx     = sfxIdx;
            ch.Offset     = offset;
            ch.Length     = length;
            ch.IsMusic    = isMusic;
            ch.Active     = true;
            ch.CanLoop    = true;
            ch.Phase      = 0;
            ch.PrevPhase  = 0;
            ch.NoiseSample= 0f;
            ch.PrevKey    = 24;  // default: C2 standard
            ch.PrevVol    = 0f;
        }

        private float GetChannelSample(int chanIdx)
        {
            var ch  = _ch[chanIdx];
            var sfx = _sfx[ch.SfxIdx];
            int speed = Math.Max(sfx.Speed, 1);

            // How much offset advances per sample: 1 / (183 * speed)
            double offsetPerSample = InvTickRate / speed;

            if (ch.Offset >= 32.0)
            {
                ch.Active = false;
                return 0f;
            }

            int noteId     = (int)ch.Offset;
            int nextNoteId = (int)(ch.Offset + offsetPerSample);

            ref P8Note note = ref sfx.Notes[noteId];
            byte  key    = note.Pitch;
            float vol    = note.Vol / 7f;
            float freq   = KeyToFreq(key);

            if (vol > 0f)
            {
                float t = (float)(ch.Offset - noteId); // fractional position within note [0..1)

                ApplyEffect(ref freq, ref vol, note.Fx, t, ch, sfx, speed);

                double phInc = freq / SampleRate;
                float  samp  = Waveform(note.Wave, note.Pitch, ch);

                if (ch.IsMusic) vol *= _ms.Volume;

                // Update phase (cumulative, no wrap)
                ch.PrevPhase = ch.Phase;
                ch.Phase    += phInc;

                float output = samp * vol;

                // Note transition: update prev_key/vol
                if (nextNoteId != noteId)
                {
                    ch.PrevKey = key;
                    ch.PrevVol = vol;
                }

                // Advance offset
                ch.Offset += offsetPerSample;

                // Handle SFX note loop
                if (sfx.LoopEnd > sfx.LoopStart && sfx.LoopEnd > 0
                    && ch.Offset >= sfx.LoopEnd && ch.CanLoop)
                {
                    double range = sfx.LoopEnd - sfx.LoopStart;
                    ch.Offset = sfx.LoopStart + (ch.Offset - sfx.LoopStart) % range;
                }
                else if (ch.Offset >= (ch.Length > 0 ? ch.Length : 32.0))
                {
                    ch.Active  = false;
                    ch.IsMusic = false;
                }

                return output;
            }
            else
            {
                // Silent note — still advance
                ch.PrevPhase = ch.Phase;
                ch.Phase    += (double)freq / SampleRate;
                ch.Offset   += offsetPerSample;

                if (sfx.LoopEnd > sfx.LoopStart && sfx.LoopEnd > 0
                    && ch.Offset >= sfx.LoopEnd && ch.CanLoop)
                {
                    double range = sfx.LoopEnd - sfx.LoopStart;
                    ch.Offset = sfx.LoopStart + (ch.Offset - sfx.LoopStart) % range;
                }
                else if (ch.Offset >= (ch.Length > 0 ? ch.Length : 32.0))
                {
                    ch.Active  = false;
                    ch.IsMusic = false;
                }

                return 0f;
            }
        }

        // ── Effects ───────────────────────────────────────────────────────────

        private static void ApplyEffect(ref float freq, ref float vol, byte fx,
                                        float t, ChannelState ch, P8Sfx sfx, int speed)
        {
            switch (fx)
            {
                case 1: // Slide — glide from previous note's pitch and volume
                    freq = Lerp(KeyToFreq(ch.PrevKey), freq, t);
                    if (ch.PrevVol > 0f)
                        vol = Lerp(ch.PrevVol, vol, t);
                    break;

                case 2: // Vibrato — oscillates half a semitone
                {
                    // Rate: 7.5 cycles per offset_per_second (matches zepto8 empirical values)
                    double offsetPerSecond = SampleRate / (183.0 * speed);
                    double vt = Math.Abs((7.5 * ch.Offset / offsetPerSecond) % 1.0 - 0.5) - 0.25;
                    freq = Lerp(freq, freq * 1.059463094359f, (float)vt);
                    break;
                }

                case 3: // Drop — pitch falls to zero over note duration
                    freq *= 1f - t;
                    break;

                case 4: // Fade in
                    vol *= t;
                    break;

                case 5: // Fade out
                    vol *= 1f - t;
                    break;

                case 6: // Arpeggio fast
                case 7: // Arpeggio slow
                {
                    // Number of arp steps per note time unit
                    // Fast=4 steps/note-unit, Slow=8; halved when speed<=8
                    int noteId = (int)ch.Offset;
                    double offsetPerSecond = SampleRate / (183.0 * speed);
                    int m = (speed <= 8 ? 32 : 16) / (fx == 6 ? 4 : 8);
                    int n = (int)(m * 7.5 * ch.Offset / offsetPerSecond);
                    int arpNote = (noteId & ~3) | (n & 3);
                    arpNote = Math.Clamp(arpNote, 0, 31);
                    freq = KeyToFreq(sfx.Notes[arpNote].Pitch);
                    break;
                }
            }
        }

        // ── Pitch ─────────────────────────────────────────────────────────────
        // PICO-8 "C0" = standard C2 = 65.41 Hz
        // Formula matches zepto8: 440 * exp2((key - 33) / 12)
        //   key=0  → C2  = 65.41 Hz
        //   key=24 → C4  = 261.6 Hz (middle C)
        //   key=33 → A4  = 440 Hz
        //   key=63 → D#6 ≈ 2489 Hz

        private static float KeyToFreq(int key)
            => (float)(440.0 * Math.Pow(2.0, (key - 33.0) / 12.0));

        private static float Lerp(float a, float b, float t)
            => a + (b - a) * Math.Clamp(t, 0f, 1f);

        // ── Waveforms (from zepto8/fake-08 reference) ─────────────────────────
        // Amplitudes calibrated to match PICO-8 WAV exports.
        // Phase is cumulative (not wrapped) for phaser/noise correctness.

        private float Waveform(int wave, int key, ChannelState ch)
        {
            float advance = (float)ch.Phase;
            float t = advance - (float)Math.Floor(advance);  // fmod(advance, 1), always ≥ 0

            switch (wave)
            {
                case 0: // Triangle — peak ±0.5
                {
                    float r = 1f - Math.Abs(4f * t - 2f);
                    return r * 0.5f;
                }
                case 1: // Tilted saw — peak ±0.5
                {
                    const float a = 0.875f;
                    float r = t < a ? 2f * t / a - 1f
                                    : 2f * (1f - t) / (1f - a) - 1f;
                    return r * 0.5f;
                }
                case 2: // Sawtooth — zepto8 shape, peak ±0.327
                {
                    float r = t < 0.5f ? t : t - 1f;
                    return 0.653f * r;
                }
                case 3: // Square 50% — peak ±0.25
                    return t < 0.5f ? 0.25f : -0.25f;

                case 4: // Pulse ~31.6% — peak ±0.25
                    return t < 0.316f ? 0.25f : -0.25f;

                case 5: // Organ — two piecewise triangles / 9
                {
                    float r = t < 0.5f
                        ? 3f - Math.Abs(24f * t - 6f)
                        : 1f - Math.Abs(16f * t - 12f);
                    return r / 9f;
                }
                case 6: // Noise — 1st-order IIR-filtered random samples
                {
                    float scale = (float)((ch.Phase - ch.PrevPhase) * NoiseTscale);
                    if (scale < 0f) scale = 0f;
                    float ns = (ch.NoiseSample + scale * NextRand()) / (1f + scale);
                    ch.NoiseSample = ns;
                    float factor = 1f - key / 63f;
                    return ns * 1.5f * (1f + factor * factor);
                }
                case 7: // Phaser — two triangles at slightly different freqs / 6
                {
                    // Second oscillator runs at freq * 109/110
                    float t2 = (float)(advance * 109.0 / 110.0);
                    t2 = t2 - (float)Math.Floor(t2);
                    float r  = 2f - Math.Abs(8f * t - 4f);
                    float r2 = 1f - Math.Abs(4f * t2 - 2f);
                    return (r + r2) / 6f;
                }
                default: return 0f;
            }
        }

        // Simple xorshift PRNG for noise
        private uint _noiseRng = 0xACE1u;
        private float NextRand()
        {
            _noiseRng ^= _noiseRng << 13;
            _noiseRng ^= _noiseRng >> 17;
            _noiseRng ^= _noiseRng << 5;
            return (_noiseRng & 0xFFFF) / 32768f - 1f;
        }
    }
}
