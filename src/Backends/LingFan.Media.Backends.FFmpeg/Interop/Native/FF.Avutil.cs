using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

internal static partial class FF
{
    private const string LibAvutil = "avutil";

    [LibraryImport(LibAvutil)]
    internal static unsafe partial void av_log_set_level(int level);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial void* av_malloc(UIntPtr size);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial AVBufferRef* av_buffer_ref(AVBufferRef* buf);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial void av_buffer_unref(AVBufferRef** buf);

    /// <summary>
    /// AVRational → double。等价于 FFmpeg 头文件（libavutil/rational.h）中的
    /// <c>static inline double av_q2d(AVRational a) { return a.den ? a.num / (double)a.den : 0.0; }</c>。
    /// <para><b>注意</b>：该函数在 FFmpeg 中是 <c>static inline</c>，<u>不导出</u>到 DLL 符号表，
    /// 绝不能以 <c>[LibraryImport]</c> 声明（否则首次调用抛 EntryPointNotFoundException）。
    /// 此处用托管实现与 FFmpeg 源码严格一致。同理 av_inv_q/av_make_q/av_cmp_q/av_gcd 均为 inline，须托管实现或避免声明。</para>
    /// </summary>
    internal static double av_q2d(AVRational a) => a.den != 0 ? a.num / (double)a.den : 0.0;

    [LibraryImport(LibAvutil, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial AVDictionaryEntry* av_dict_get(AVDictionary* m, string key, AVDictionaryEntry* prev, int flags);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_sample_fmt_is_planar(AVSampleFormat sample_fmt);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_get_bytes_per_sample(AVSampleFormat sample_fmt);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial void av_channel_layout_default(AVChannelLayout* ch_layout, int nb_channels);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial uint avutil_version();

    // 结构镜像运行时自测用：在【原生真实偏移】写字段，供自绑定镜像读回比对（偏移一致性校验）。
    [LibraryImport(LibAvutil, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int av_opt_set_int(void* obj, string name, long val, int search_flags);

    [LibraryImport(LibAvutil)]
    internal static unsafe partial int av_strerror(int errnum, byte* errbuf, UIntPtr errbuf_size);
}
