using NAudio.Wave;

namespace SharpDesk;

sealed class AudioPlayer : IDisposable
{
    readonly BufferedWaveProvider _buffer;
    readonly WaveOutEvent _out;

    public float Volume { get => _out.Volume; set => _out.Volume = Math.Clamp(value, 0f, 1f); }

    public AudioPlayer(int sampleRate, int channels, int bitsPerSample)
    {
        var format = new WaveFormat(sampleRate, 16, channels);

        _buffer = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferLength = sampleRate * channels * 2 * 2
        };

        _out = new WaveOutEvent { DesiredLatency = 100 };
        _out.Init(_buffer);
        _out.Play();
    }

    public void Feed(byte[] data, int offset, int count)
    {
        _buffer.AddSamples(data, offset, count);
    }

    public void Dispose()
    {
        _out.Stop();
        _out.Dispose();
    }
}
