using System.Text;
using Raylib_CsLo;
using static Raylib_CsLo.Raylib;

namespace J2meEmu;

static class J2meAudio
{
    static bool _initialized;
    static readonly object _lock = new();
    static AudioStream _toneStream;
    static bool _toneStreamReady;
    static short[]? _toneBuf;
    static int _tonePos;
    static int _toneSamples;
    const int ToneSampleRate = 22050;

    public static bool Headless;

    public static void Init()
    {
        if (Headless) return;
        lock (_lock)
        {
            if (_initialized) return;
            InitAudioDevice();
            _toneStream = LoadAudioStream(ToneSampleRate, 16, 1);
            PlayAudioStream(_toneStream);
            _toneStreamReady = true;
            _initialized = true;
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized) return;
            if (_toneStreamReady) { StopAudioStream(_toneStream); UnloadAudioStream(_toneStream); _toneStreamReady = false; }
            CloseAudioDevice();
            _initialized = false;
        }
    }

    public static void Update()
    {
        if (!_initialized) return;
        lock (_lock)
        {
            if (_toneStreamReady && _toneBuf != null && IsAudioStreamProcessed(_toneStream))
            {
                int remaining = _toneSamples - _tonePos;
                if (remaining > 0)
                {
                    int frames = Math.Min(remaining, 1024);
                    unsafe { fixed (short* p = &_toneBuf[_tonePos]) UpdateAudioStream(_toneStream, p, frames); }
                    _tonePos += frames;
                }
            }
        }
    }

    public class PlayerData
    {
        public byte[]? RawData;
        public string? ContentType;
        public Sound RlSound;
        public bool HasSound;
        public bool Playing;
        public int LoopCount = 1;
        public float Volume = 1.0f;
        public int State = 100; // UNREALIZED=100, REALIZED=200, PREFETCHED=300, STARTED=400, CLOSED=0
    }

    public static PlayerData CreatePlayer(Stream? stream, string? contentType)
    {
        var pd = new PlayerData { ContentType = contentType };
        if (stream != null)
        {
            try
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                pd.RawData = ms.ToArray();
            }
            catch { }
        }
        return pd;
    }

    public static PlayerData CreatePlayer(byte[] data, string? contentType)
    {
        return new PlayerData { RawData = data, ContentType = contentType };
    }

    public static void Realize(PlayerData pd)
    {
        if (pd.State >= 200) return;
        pd.State = 200;
        if (!_initialized || pd.RawData == null || pd.RawData.Length == 0) return;
        LoadAudioData(pd);
    }

    public static void Prefetch(PlayerData pd)
    {
        if (pd.State < 200) Realize(pd);
        pd.State = Math.Max(pd.State, 300);
    }

    public static void Start(PlayerData pd)
    {
        if (pd.State < 300) Prefetch(pd);
        pd.State = 400;
        pd.Playing = true;
        if (!_initialized || !pd.HasSound) return;
        SetSoundVolume(pd.RlSound, pd.Volume);
        PlaySound(pd.RlSound);
    }

    public static void Stop(PlayerData pd)
    {
        pd.State = 300;
        pd.Playing = false;
        if (!_initialized || !pd.HasSound) return;
        StopSound(pd.RlSound);
    }

    public static void Close(PlayerData pd)
    {
        if (pd.HasSound && _initialized)
        {
            StopSound(pd.RlSound);
            UnloadSound(pd.RlSound);
        }
        pd.HasSound = false;
        pd.Playing = false;
        pd.State = 0;
    }

    public static void SetVolume(PlayerData pd, int level)
    {
        pd.Volume = Math.Clamp(level / 100f, 0f, 1f);
        if (_initialized && pd.HasSound)
            SetSoundVolume(pd.RlSound, pd.Volume);
    }

    static void LoadAudioData(PlayerData pd)
    {
        if (pd.RawData == null) return;
        string? ext = GuessExtension(pd.ContentType, pd.RawData);
        if (ext == null) return;

        try
        {
            unsafe
            {
                fixed (byte* ptr = pd.RawData)
                {
                    sbyte* extPtr = stackalloc sbyte[ext.Length + 1];
                    for (int i = 0; i < ext.Length; i++) extPtr[i] = (sbyte)ext[i];
                    extPtr[ext.Length] = 0;

                    var wave = LoadWaveFromMemory(extPtr, ptr, pd.RawData.Length);
                    if (wave.frameCount > 0)
                    {
                        pd.RlSound = LoadSoundFromWave(wave);
                        pd.HasSound = true;
                        UnloadWave(wave);
                    }
                }
            }
        }
        catch (Exception ex) { Log.Error($"Audio load error: {ex.Message}"); }
    }

    static string? GuessExtension(string? contentType, byte[] data)
    {
        if (contentType != null)
        {
            if (contentType.Contains("wav")) return ".wav";
            if (contentType.Contains("ogg")) return ".ogg";
            if (contentType.Contains("mp3") || contentType.Contains("mpeg")) return ".mp3";
            if (contentType.Contains("midi") || contentType.Contains("mid")) return null;
        }
        if (data.Length >= 4)
        {
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F') return ".wav";
            if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S') return ".ogg";
            if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) return ".mp3";
            if (data[0] == 'I' && data[1] == 'D' && data[2] == '3') return ".mp3";
            if (data[0] == 'f' && data[1] == 'L' && data[2] == 'a' && data[3] == 'C') return ".flac";
        }
        return null;
    }

    public static void PlayTone(int note, int durationMs, int volume)
    {
        if (!_initialized) return;
        double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
        int samples = (int)(ToneSampleRate * durationMs / 1000.0);
        var buf = new short[samples];
        float vol = Math.Clamp(volume / 100f, 0f, 1f);
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / ToneSampleRate;
            double envelope = 1.0;
            if (i < 200) envelope = i / 200.0;
            if (i > samples - 200) envelope = (samples - i) / 200.0;
            buf[i] = (short)(Math.Sin(2 * Math.PI * freq * t) * 16000 * vol * envelope);
        }
        lock (_lock)
        {
            _toneBuf = buf;
            _tonePos = 0;
            _toneSamples = samples;
        }
    }

    public static PlayerData CreateTonePlayer(byte[] sequence)
    {
        var pd = new PlayerData { ContentType = "audio/x-tone-seq", RawData = sequence, State = 200 };
        return pd;
    }
}
