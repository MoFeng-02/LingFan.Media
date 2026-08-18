using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

internal static partial class FF
{
    private const string LibAvformat = "avformat";

    [LibraryImport(LibAvformat)]
    internal static unsafe partial int avformat_network_init();

    [LibraryImport(LibAvformat)]
    internal static unsafe partial int avformat_network_deinit();

    // 结构镜像运行时自测用（AVStream / AVFormatContext 偏移校验）。
    [LibraryImport(LibAvformat)]
    internal static unsafe partial AVStream* avformat_new_stream(AVFormatContext* s, AVCodec* c);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial void avformat_free_context(AVFormatContext* s);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial AVFormatContext* avformat_alloc_context();

    [LibraryImport(LibAvformat, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int avformat_open_input(AVFormatContext** ps, string? url, AVInputFormat* fmt, AVDictionary** options);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial int avformat_find_stream_info(AVFormatContext* ic, AVDictionary** options);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial void avio_closep(AVIOContext** s);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial void avformat_close_input(AVFormatContext** ctx);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial AVIOContext* avio_alloc_context(
        byte* buffer, int buffer_size, int write_flag, void* opaque,
        AVIOReadFunc read_packet, AVIOWriteFunc? write_packet, AVIOSeekFunc? seek);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial int av_read_frame(AVFormatContext* s, AVPacket* pkt);

    [LibraryImport(LibAvformat)]
    internal static unsafe partial int av_seek_frame(AVFormatContext* s, int stream_index, long timestamp, int flags);
}
