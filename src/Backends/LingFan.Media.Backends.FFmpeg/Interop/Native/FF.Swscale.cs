using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

internal static partial class FF
{
    private const string LibSwscale = "swscale";

    [LibraryImport(LibSwscale)]
    internal static unsafe partial void sws_freeContext(SwsContext* swsContext);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_image_get_buffer_size(AVPixelFormat pix_fmt, int width, int height, int align);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_image_copy_to_buffer(
        byte* dst, int dst_size, byte** src_data, int* src_linesize,
        AVPixelFormat pix_fmt, int width, int height, int align);
}
