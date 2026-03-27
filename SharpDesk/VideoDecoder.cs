using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace SharpDesk;

sealed unsafe class VideoDecoder : IDisposable
{
    readonly AVCodecContext* _ctx;
    readonly AVFrame* _frame;
    readonly AVPacket* _pkt;
    SwsContext* _sws;
    readonly int _w, _h;

    public VideoDecoder(int w, int h)
    {
        _w = w; _h = h;
        VideoEncoder.EnsureFFmpegInit();
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null) throw new Exception("H.264 decoder not found");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->width = w;
        _ctx->height = h;

        if (ffmpeg.avcodec_open2(_ctx, codec, null) < 0)
            throw new Exception("Failed to open H.264 decoder");

        _frame = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
    }

    /// <summary>Decode H.264 packet into RGBA output buffer. Returns true on success.</summary>
    public bool Decode(byte[] data, byte[] rgbaOut)
    {
        fixed (byte* pData = data)
        {
            _pkt->data = pData;
            _pkt->size = data.Length;

            if (ffmpeg.avcodec_send_packet(_ctx, _pkt) < 0) return false;
            if (ffmpeg.avcodec_receive_frame(_ctx, _frame) < 0) return false;

            if (_sws == null)
            {
                _sws = ffmpeg.sws_getContext(
                    _frame->width, _frame->height, (AVPixelFormat)_frame->format,
                    _w, _h, AVPixelFormat.AV_PIX_FMT_RGBA,
                    (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
                if (_sws == null) return false;
            }

            fixed (byte* dst = rgbaOut)
            {
                var srcSlice = new byte*[] { _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3] };
                var srcStride = new int[] { _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], _frame->linesize[3] };
                var dstSlice = new byte*[] { dst };
                var dstStride = new int[] { _w * 4 };
                ffmpeg.sws_scale(_sws, srcSlice, srcStride, 0, _frame->height, dstSlice, dstStride);
            }
            return true;
        }
    }

    public void Dispose()
    {
        if (_sws != null) ffmpeg.sws_freeContext(_sws);
        var p = _pkt;  if (p != null) ffmpeg.av_packet_free(&p);
        var f = _frame; if (f != null) ffmpeg.av_frame_free(&f);
        var c = _ctx;   if (c != null) ffmpeg.avcodec_free_context(&c);
    }
}
