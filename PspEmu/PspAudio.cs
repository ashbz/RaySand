namespace PspEmu;

/// <summary>
/// PSP audio output: manages up to 8 audio channels.
/// Collects samples for the audio thread to consume.
/// </summary>
sealed class PspAudio
{
    public const int MaxChannels = 8;
    public const int SampleRate = 44100;

    public sealed class AudioChannel
    {
        public bool Reserved;
        public int SampleCount;
        public int Format; // 0 = stereo, 0x10 = mono
        public int LeftVolume = 0x8000;
        public int RightVolume = 0x8000;
    }

    public readonly AudioChannel[] Channels = new AudioChannel[MaxChannels];

    // Ring buffer for mixed output
    const int RingSize = 44100 * 2; // 1 second stereo
    readonly short[] _ring = new short[RingSize];
    int _writePos;
    int _readPos;
    readonly object _lock = new();

    public PspAudio()
    {
        for (int i = 0; i < MaxChannels; i++)
            Channels[i] = new AudioChannel();
    }

    public int ReserveChannel(int channel, int sampleCount, int format)
    {
        if (channel < 0)
        {
            for (int i = 0; i < MaxChannels; i++)
            {
                if (!Channels[i].Reserved)
                { channel = i; break; }
            }
            if (channel < 0) return -1;
        }

        if (channel >= MaxChannels) return -1;

        Channels[channel].Reserved = true;
        Channels[channel].SampleCount = sampleCount > 0 ? sampleCount : 1024;
        Channels[channel].Format = format;

        Log.Write(LogCat.Audio, $"ReserveChannel ch={channel} samples={sampleCount} fmt={format}");
        return channel;
    }

    public int ReleaseChannel(int channel)
    {
        if (channel < 0 || channel >= MaxChannels) return -1;
        Channels[channel].Reserved = false;
        return 0;
    }

    public int OutputBlocking(int channel, int leftVol, int rightVol, uint bufAddr, PspBus bus)
    {
        if (channel < 0 || channel >= MaxChannels) return -1;
        var ch = Channels[channel];
        if (!ch.Reserved) return -1;

        ch.LeftVolume = leftVol;
        ch.RightVolume = rightVol;

        int count = ch.SampleCount;
        bool stereo = ch.Format != 0x10;

        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                short left, right;
                if (stereo)
                {
                    uint off = (uint)(i * 4);
                    left = (short)bus.Read16(bufAddr + off);
                    right = (short)bus.Read16(bufAddr + off + 2);
                }
                else
                {
                    short sample = (short)bus.Read16(bufAddr + (uint)(i * 2));
                    left = right = sample;
                }

                // Apply volume (PSP volume range 0-0x8000)
                left = (short)(left * leftVol / 0x8000);
                right = (short)(right * rightVol / 0x8000);

                _ring[_writePos] = left;
                _ring[(_writePos + 1) % RingSize] = right;
                _writePos = (_writePos + 2) % RingSize;
            }
        }

        return count;
    }

    public int GetChannelRestLen(int channel)
    {
        if (channel < 0 || channel >= MaxChannels) return 0;
        return 0; // immediate output
    }

    /// <summary>Read samples for Raylib audio stream callback.</summary>
    public int ReadSamples(Span<short> dest)
    {
        lock (_lock)
        {
            int available = (_writePos - _readPos + RingSize) % RingSize;
            int toRead = Math.Min(available, dest.Length);
            toRead &= ~1; // ensure stereo pairs

            for (int i = 0; i < toRead; i++)
            {
                dest[i] = _ring[_readPos];
                _readPos = (_readPos + 1) % RingSize;
            }

            // Fill remainder with silence
            for (int i = toRead; i < dest.Length; i++)
                dest[i] = 0;

            return toRead;
        }
    }
}
