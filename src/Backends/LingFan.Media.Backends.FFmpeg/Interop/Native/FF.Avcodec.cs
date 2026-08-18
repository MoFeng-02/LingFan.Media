using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

internal static partial class FF
{
    private const string LibAvcodec = "avcodec";

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial AVCodec* avcodec_find_decoder(AVCodecID id);

    [LibraryImport(LibAvcodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial AVCodec* avcodec_find_decoder_by_name(string name);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int avcodec_open2(AVCodecContext* avctx, AVCodec* codec, AVDictionary** options);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial void avcodec_free_context(AVCodecContext** avctx);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial void avcodec_flush_buffers(AVCodecContext* avctx);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int avcodec_decode_subtitle2(AVCodecContext* avctx, AVSubtitle* sub, int* got_sub_ptr, AVPacket* avpkt);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial AVFrame* av_frame_alloc();

    [LibraryImport(LibAvutil)]
    internal static unsafe partial void av_frame_free(AVFrame** frame);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial void av_frame_unref(AVFrame* frame);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial AVFrame* av_frame_clone(AVFrame* src);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_frame_get_buffer(AVFrame* frame, int align);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial AVBufferRef* av_hwdevice_ctx_alloc(AVHWDeviceType type);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_hwdevice_ctx_init(AVBufferRef* buf);

    [LibraryImport(LibAvcodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int av_hwdevice_ctx_create(AVBufferRef** device_ctx, AVHWDeviceType type, string? device, AVDictionary* opts, int flags);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_hwframe_transfer_data(AVFrame* dst, AVFrame* src, int flags);

    [LibraryImport(LibAvcodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial AVBitStreamFilter* av_bsf_get_by_name(string name);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int av_bsf_alloc(AVBitStreamFilter* filter, AVBSFContext** ctx);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int av_bsf_init(AVBSFContext* ctx);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial void av_bsf_free(AVBSFContext** ctx);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int av_bsf_send_packet(AVBSFContext* ctx, AVPacket* pkt);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int av_bsf_receive_packet(AVBSFContext* ctx, AVPacket* pkt);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial AVPacket* av_packet_alloc();

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial void av_packet_free(AVPacket** pkt);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial AVPacket* av_packet_clone(AVPacket* src);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial void av_packet_unref(AVPacket* pkt);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial int av_new_packet(AVPacket* pkt, int size);

    [LibraryImport(LibAvcodec)]
    internal static unsafe partial void avsubtitle_free(AVSubtitle* sub);
}
