using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SharpDesk;

/// <summary>
/// Captures system audio via WASAPI loopback (records whatever the speakers play).
/// Converts 32-bit float → 16-bit PCM to halve bandwidth before sending.
/// </summary>
sealed class AudioCapture : IDisposable
{
    readonly WasapiLoopbackCapture _capture;
    volatile bool _muted;

    public int SampleRate { get; }
    public int Channels   { get; }
    public int BitsPerSample => 16;
    public bool Muted { get => _muted; set => _muted = value; }

    public event Action<byte[], int>? OnData;

    public AudioCapture()
    {
        _capture = new WasapiLoopbackCapture();
        SampleRate = _capture.WaveFormat.SampleRate;
        Channels   = _capture.WaveFormat.Channels;

        _capture.DataAvailable += (_, e) =>
        {
            if (_muted || e.BytesRecorded == 0 || OnData == null) return;

            int floatSamples = e.BytesRecorded / 4;
            var pcm = new byte[floatSamples * 2];
            for (int i = 0; i < floatSamples; i++)
            {
                float sample = BitConverter.ToSingle(e.Buffer, i * 4);
                short s16 = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), s16);
            }
            OnData(pcm, pcm.Length);
        };
    }

    public void Start() => _capture.StartRecording();
    public void Stop()  { try { _capture.StopRecording(); } catch { } }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }
}
