using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace SharpDesk;

sealed unsafe class VideoEncoder : IDisposable
{
    readonly AVCodecContext* _ctx;
    readonly AVFrame* _frame;
    readonly AVPacket* _pkt;
    readonly SwsContext* _sws;
    readonly int _w, _h;
    long _pts;

    public string Name { get; }
    public byte FrameType { get; }

    static readonly (string name, byte ft)[] CandidateEncoders =
    [
        ("h264_nvenc",  3), ("h264_amf",   3), ("h264_qsv",   3),
        ("hevc_nvenc",  4), ("hevc_amf",   4), ("hevc_qsv",   4),
        ("av1_nvenc",   5), ("av1_amf",    5), ("av1_qsv",    5),
        ("libx264",     3), ("libx265",    4), ("libsvtav1",   5),
    ];

    static bool _ffmpegInit;

    internal static void EnsureFFmpegInit()
    {
        if (_ffmpegInit) return;
        _ffmpegInit = true;
        string[] searchPaths = [
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
        ];
        foreach (var p in searchPaths)
        {
            if (!Directory.Exists(p) || Directory.GetFiles(p, "avcodec*.dll").Length == 0) continue;
            DynamicallyLoadedBindings.LibrariesPath = p;
            DynamicallyLoadedBindings.Initialize();
            return;
        }
        throw new Exception("FFmpeg DLLs not found");
    }

    public static bool TryCreate(int w, int h, int fps, out VideoEncoder? encoder, out string log)
    {
        encoder = null;
        try { EnsureFFmpegInit(); }
        catch { log = "FFmpeg DLLs not found \u2013 using LZ4 (run get-ffmpeg.ps1 to enable video codecs)"; return false; }

        foreach (var (name, ft) in CandidateEncoders)
        {
            try
            {
                encoder = new VideoEncoder(w, h, fps, name, ft);
                bool hw = !name.StartsWith("lib");
                string family = ft == 5 ? "AV1" : ft == 4 ? "HEVC" : "H.264";
                log = $"{family} encoder: {name} ({(hw ? "hardware" : "software")})";
                return true;
            }
            catch { }
        }
        log = "No video encoder available \u2013 using LZ4";
        return false;
    }

    VideoEncoder(int w, int h, int fps, string codecName, byte frameType)
    {
        _w = w; _h = h;
        FrameType = frameType;
        var codec = ffmpeg.avcodec_find_encoder_by_name(codecName);
        if (codec == null) throw new Exception($"{codecName} not found");
        Name = codecName;

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->width = w;
        _ctx->height = h;
        _ctx->time_base = new AVRational { num = 1, den = fps };
        _ctx->framerate = new AVRational { num = fps, den = 1 };
        _ctx->gop_size = fps * 5;
        _ctx->max_b_frames = 0;
        _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _ctx->thread_count = 2;

        if (codecName == "libx264")
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "preset", "ultrafast", 0);
            ffmpeg.av_opt_set(_ctx->priv_data, "tune", "zerolatency", 0);
            _ctx->bit_rate = 4_000_000;
        }
        else if (codecName == "libx265")
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "preset", "ultrafast", 0);
            ffmpeg.av_opt_set(_ctx->priv_data, "tune", "zerolatency", 0);
            _ctx->bit_rate = 4_000_000;
        }
        else if (codecName == "libsvtav1")
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "preset", "12", 0);
            _ctx->bit_rate = 4_000_000;
        }
        else if (codecName.Contains("nvenc"))
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "preset", "p1", 0);
            ffmpeg.av_opt_set(_ctx->priv_data, "tune", "ull", 0);
            _ctx->bit_rate = 6_000_000;
        }
        else if (codecName.Contains("amf"))
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "usage", "ultralowlatency", 0);
            _ctx->bit_rate = 6_000_000;
        }
        else if (codecName.Contains("qsv"))
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "preset", "veryfast", 0);
            _ctx->bit_rate = 6_000_000;
        }

        if (ffmpeg.avcodec_open2(_ctx, codec, null) < 0)
            throw new Exception($"Failed to open {codecName}");

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)_ctx->pix_fmt;
        _frame->width = w;
        _frame->height = h;
        ffmpeg.av_frame_get_buffer(_frame, 0);

        _pkt = ffmpeg.av_packet_alloc();

        _sws = ffmpeg.sws_getContext(
            w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
            w, h, _ctx->pix_fmt,
            (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
        if (_sws == null) throw new Exception("sws_getContext failed");
    }

    public byte[]? Encode(byte[] bgra)
    {
        ffmpeg.av_frame_make_writable(_frame);

        fixed (byte* src = bgra)
        {
            var srcSlice = new byte*[] { src };
            var srcStride = new int[] { _w * 4 };
            var dstSlice = new byte*[] { _frame->data[0], _frame->data[1], _frame->data[2] };
            var dstStride = new int[] { _frame->linesize[0], _frame->linesize[1], _frame->linesize[2] };
            ffmpeg.sws_scale(_sws, srcSlice, srcStride, 0, _h, dstSlice, dstStride);
        }

        _frame->pts = _pts++;
        if (ffmpeg.avcodec_send_frame(_ctx, _frame) < 0) return null;
        if (ffmpeg.avcodec_receive_packet(_ctx, _pkt) != 0) return null;

        var result = new byte[_pkt->size];
        new Span<byte>(_pkt->data, _pkt->size).CopyTo(result);
        ffmpeg.av_packet_unref(_pkt);
        return result;
    }

    public void Dispose()
    {
        if (_sws != null) ffmpeg.sws_freeContext(_sws);
        var p = _pkt;  if (p != null) ffmpeg.av_packet_free(&p);
        var f = _frame; if (f != null) ffmpeg.av_frame_free(&f);
        var c = _ctx;   if (c != null) ffmpeg.avcodec_free_context(&c);
    }
}
