using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

internal static partial class FF
{
    private const string LibSwresample = "swresample";

    [LibraryImport(LibSwresample)]
    internal static unsafe partial int swr_alloc_set_opts2(
        SwrContext** ps, AVChannelLayout* out_ch_layout, AVSampleFormat out_sample_fmt, int out_sample_rate,
        AVChannelLayout* in_ch_layout, AVSampleFormat in_sample_fmt, int in_sample_rate, int log_offset, void* log_ctx);

    [LibraryImport(LibSwresample)]
    internal static unsafe partial int swr_init(SwrContext* s);

    [LibraryImport(LibSwresample)]
    internal static unsafe partial int swr_get_out_samples(SwrContext* s, int in_samples);

    [LibraryImport(LibSwresample)]
    internal static unsafe partial int swr_convert_frame(SwrContext* swr, AVFrame* output, AVFrame* input);

    [LibraryImport(LibSwresample)]
    internal static unsafe partial void swr_free(SwrContext** s);
}
