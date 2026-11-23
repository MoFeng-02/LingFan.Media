using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

// FF 原生库加载器的加载期自测段（部分类，由 FFmpegLibraryLoader.cs 再拆，编译期等价）。
// 版本门禁 + 导出符号校验 + 结构体镜像哨兵自测；fail-fast 防静默内存损坏。
internal static partial class FF
{
    private static void VerifyVersion()
    {
        uint v = avutil_version();
        int major = (int)((v >> 16) & 0xFF);
        if (major < 56 || major > 61)
            throw new InvalidOperationException(
                $"不支持的 FFmpeg 主版本 {major}（avutil_version=0x{v:X}）。本后端支持 4.x–9.0（avutil 56–61）。");
    }

    /// <summary>
    /// 布局损坏防护：FFmpeg 头文件中的 <c>static inline</c> 函数（如 av_q2d/av_inv_q/av_make_q/av_cmp_q/av_gcd）
    /// 不导出到 DLL 符号表，若被误以 <c>[LibraryImport]</c> 声明，只有运行到该调用才会抛
    /// <see cref="EntryPointNotFoundException"/>（且栈很深、难定位）。这里在加载期一次性校验关键导出符号在其【应属库】中确实存在，
    /// 任一缺失立即 fail-fast 并明确提示；同时顺带发现「FFmpeg 原生库装残 / 版本错配」的部署问题。
    /// 无反射、AOT 安全（用 NativeLibrary.TryGetExport）。
    /// <para>注：源码中将符号声明到【错误库】（如 av_frame_alloc 写成 LibraryImport("avcodec")）运行时无法自检，
    /// 由 facade 源码扫描门禁（对照真实 DLL 导出表）兜底——本方法覆盖「符号确实存在但组件缺失/被挪库」的部署层错误。</para>
    /// </summary>
    private static void VerifyExports()
    {
        // (符号, 应属库) —— 权威映射，随 facade 同步维护；用于组件/版本错配的 fail-fast。
        (string Sym, string Lib)[] required =
        {
            ("avutil_version", "avutil"), ("av_malloc", "avutil"), ("av_dict_get", "avutil"), ("av_opt_set_int", "avutil"),
            ("av_frame_alloc", "avutil"), ("av_frame_get_buffer", "avutil"), ("av_buffer_ref", "avutil"),
            ("av_channel_layout_default", "avutil"), ("av_hwdevice_ctx_alloc", "avutil"),
            ("av_image_get_buffer_size", "avutil"), ("av_image_copy_to_buffer", "avutil"),
            ("avcodec_alloc_context3", "avcodec"), ("avcodec_open2", "avcodec"), ("avcodec_send_packet", "avcodec"),
            ("avcodec_receive_frame", "avcodec"), ("av_packet_alloc", "avcodec"), ("av_bsf_alloc", "avcodec"),
            ("avformat_open_input", "avformat"), ("avformat_find_stream_info", "avformat"), ("av_read_frame", "avformat"),
            ("avformat_new_stream", "avformat"), ("avformat_close_input", "avformat"), ("avio_alloc_context", "avformat"),
            ("sws_getContext", "swscale"), ("sws_scale", "swscale"), ("sws_freeContext", "swscale"),
            ("swr_init", "swresample"), ("swr_convert", "swresample"), ("swr_free", "swresample"), ("swr_alloc_set_opts2", "swresample"),
        };
        var missing = new List<string>(required.Length);
        foreach (var (sym, lib) in required)
        {
            if (!_handles.TryGetValue(lib, out var h) || h == IntPtr.Zero || !NativeLibrary.TryGetExport(h, sym, out _))
                missing.Add($"{sym} (应属 {lib})");
        }
        if (missing.Count != 0)
            throw new InvalidOperationException(
                "FFmpeg 原生库缺失以下导出符号：" + string.Join(", ", missing) +
                "。可能原因：(1) 该函数是 FFmpeg 头文件中的 static inline（不导出），被误以 [LibraryImport] 声明——请改托管实现；" +
                "(2) 原生库版本错配或部署残缺（缺少对应组件 DLL）。");
    }

    /// <summary>
    /// 布局损坏防护：结构体镜像偏移跨 FFmpeg 主版本敏感（hw_device_ctx 等中段字段在 4.x 与 9.0 间可能不同）。
    /// 这里在加载期分配真实原生结构体，用 av_opt 在多个深度写哨兵、再经镜像读回，整体校验布局一致性。
    /// 任一已加载版本（含 4.x）只要镜像与真实库不符，立即 fail-fast 报出具体字段，而非静默内存损坏。
    /// </summary>
    private static void VerifyStructLayout()
    {
        VerifyCodecContextLayout();
        VerifyStreamLayout();
    }

    private static unsafe void VerifyCodecContextLayout()
    {
        AVCodecContext* ctx = FF.avcodec_alloc_context3(null);
        if (ctx == null) return; // 极端 OOM：跳过自测，不阻断加载（正常必非 null）
        try
        {
            // 在结构体不同深度布点（覆盖 hwaccel/hw_frames_ctx/hw_device_ctx@536-560 关键区前后）。
            // 布点字段须同时满足：是 AVCodecContext 的 AVOption（经 av_opt_set_int 写入原生真实偏移）
            // 且镜像有同名字段（按 Marshal.OffsetOf 读回比对）。
            // 选用 FFmpeg 4.x–9.0 选项表全程存在的字段：8.0 大版本从选项表移除了
            // strict_std_compliance / err_recognition / workaround_bugs / error_concealment 等
            // （结构体字段仍在，但不可再经 av_opt 写），width/height 则从来不是 AVCodecContext 选项。
            // 哨兵值取各选项 min/max 合法区间内的值（越界会被 av_opt 以 ERANGE 拒绝）。
            CheckAvOptField(ctx, "qmin", 0x2B);
            CheckAvOptField(ctx, "trellis", 0x73C);
            CheckAvOptField(ctx, "debug", 0x84D);
            CheckAvOptField(ctx, "hwaccel_flags", 0x95E);
            CheckAvOptField(ctx, "extra_hw_frames", 0xA6F);
        }
        finally
        {
            FF.avcodec_free_context(&ctx);
        }
    }

    private static unsafe void CheckAvOptField(AVCodecContext* ctx, string fieldName, int sentinel)
    {
        // search_flags=0：这些字段均为 AVCodecContext 直接 AVOption，无需子对象搜索。
        int ret = FF.av_opt_set_int(ctx, fieldName, sentinel, 0);
        if (ret < 0)
            throw new InvalidOperationException($"结构自测失败：av_opt_set_int(\"{fieldName}\") 返回 {ret}。");

        // 按【自绑定镜像】的字段偏移读回 4 字节（int）。若镜像偏移与真实原生布局不符，
        // 此处读到的将是相邻字段的字节 → 与哨兵不符 → 立即 fail-fast，而非解码期静默内存损坏。
        int offset = (int)Marshal.OffsetOf<AVCodecContext>(fieldName);
        int actual = Marshal.ReadInt32((IntPtr)ctx + offset);
        if (actual != sentinel)
            throw new InvalidOperationException(
                $"FFmpeg 结构镜像偏移错误：字段 '{fieldName}' 经 av_opt 写入 {sentinel}，" +
                $"但自绑定镜像（偏移 {offset}）读回 {actual}。当前镜像基于 avutil 60（FFmpeg 8.x）校验；" +
                $"实际加载的 FFmpeg 版本结构体布局不一致，请调整 FFmpegStructures.cs 中对应字段偏移或收窄支持版本范围。");
    }

    private static unsafe void VerifyStreamLayout()
    {
        AVFormatContext* fmt = FF.avformat_alloc_context();
        if (fmt == null) return;
        try
        {
            AVStream* stream = FF.avformat_new_stream(fmt, null);
            if (stream == null) return;

            // codecpar 由 ffmpeg 在 new_stream 时真实分配；若镜像的 codecpar 偏移错误，
            // 读到的将是垃圾指针（0 或未对齐）→ fail-fast。
            if ((IntPtr)stream->codecpar == IntPtr.Zero || ((long)(IntPtr)stream->codecpar & 7) != 0)
                throw new InvalidOperationException(
                    "FFmpeg 结构镜像偏移错误：AVStream.codecpar 经镜像读回无效指针。" +
                    "当前镜像基于 avutil 60（FFmpeg 8.x）；实际加载版本结构体布局不一致，请调整 FFmpegStructures.cs。");

            // streams[0] 应等于刚分配的 stream（验证 streams 偏移 + 解引用语义）。
            if (fmt->streams == null || fmt->streams[0] != stream)
                throw new InvalidOperationException(
                    "FFmpeg 结构镜像偏移错误：AVFormatContext.streams[0] 与 avformat_new_stream 返回值不一致。");
        }
        finally
        {
            FF.avformat_free_context(fmt);
        }
    }
}
