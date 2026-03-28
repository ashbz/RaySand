using FFmpeg.AutoGen.Abstractions;

namespace SharpDesk;

sealed unsafe class VideoDecoder : IDisposable
{
    readonly AVCodecContext* _ctx;
    readonly AVFrame* _frame;
    readonly AVPacket* _pkt;
    SwsContext* _sws;
    readonly int _w, _h;

    public VideoDecoder(int w, int h, byte frameType)
    {
        _w = w; _h = h;
        VideoEncoder.EnsureFFmpegInit();

        var codecId = frameType switch
        {
            5 => AVCodecID.AV_CODEC_ID_AV1,
            4 => AVCodecID.AV_CODEC_ID_HEVC,
            _ => AVCodecID.AV_CODEC_ID_H264,
        };
        var codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null) throw new Exception($"Decoder for frame type {frameType} not found");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->width = w;
        _ctx->height = h;
        if (ffmpeg.avcodec_open2(_ctx, codec, null) < 0)
            throw new Exception($"Failed to open decoder for frame type {frameType}");

        _frame = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
    }

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
