using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// FFmpeg 原生库加载器（自绑定，替代 FFmpeg.AutoGen 的 RootPath + EnsureUnversionedAliases）。
/// <list type="bullet">
///   <item>按平台（Windows/Linux/macOS/iOS/Android）构造带版本号的文件名（如 avutil-61.dll / libavutil.61.dylib / libavutil.so.61）。</item>
///   <item>按已知发布版本（9.0→4.x）成组探测，保证同一发布内各组件主版本一致（avutil 61 ↔ avcodec 63 …），杜绝混装错配。</item>
///   <item>通过 <see cref="NativeLibrary.SetDllImportResolver"/> 把 [LibraryImport("avutil")] 等解析到已加载句柄，无需文件系统别名 hack。</item>
///   <item>加载后调用 avutil_version() 做版本门禁（仅支持 4.x–9.0，avutil 56–61），不支持版本快速失败。</item>
///   <item>加载后做<b>结构体镜像运行时自测</b>（VerifyStructLayout）：分配真实原生结构体，用 av_opt 在多个深度写哨兵再经镜像读回，
///        整体校验布局一致性。任一已加载版本（含 4.x）只要镜像与真实库不符即 fail-fast 报出具体字段，而非静默内存损坏。
///        这是结构体偏移跨主版本敏感的根因防护。</item>
///   <item>iOS 静态链接（libavcodec.a 等链入 App 主镜像）兜底：动态 .dylib 探测全失败后，加载 App 可执行文件主镜像复用其符号。</item>
///   <item>Android 额外命名形态兜底（libavcodec-61.so 等），与无版本 libavcodec.so 并列尝试。</item>
/// </list>
/// 幂等：多次调用安全，仅首次真正加载。
/// </summary>
internal static partial class FF
{
    private static readonly object _initLock = new();
    private static int _initState; // 0=未初始化 1=已初始化 2=失败
    private static readonly Dictionary<string, IntPtr> _handles = new(StringComparer.Ordinal);
    private static bool _resolverRegistered;

    /// <summary>调用任意 FFmpeg P/Invoke 前必须调用一次（即使 path 为 null，也须建立解析器）。</summary>
    /// <param name="libraryPath">原生库目录（可为 null，依赖默认搜索路径）。</param>
    internal static void Initialize(string? libraryPath)
    {
        if (_initState != 0) return;
        lock (_initLock)
        {
            if (_initState != 0) return;
            try
            {
                LoadNativeLibraries(libraryPath);
                RegisterResolver();
                VerifyVersion();
                VerifyExports();
                VerifyStructLayout();
                _initState = 1;
            }
            catch
            {
                _initState = 2;
                throw;
            }
        }
    }

    /// <summary>核心组件（必载）；avfilter 预留但可选（当前未绑定其函数）。</summary>
    private static readonly string[] CoreComponents = { "avutil", "avcodec", "avformat", "swscale", "swresample" };

    /// <summary>已知发布版本 → 各组件主版本号（新→旧，优先最新兼容版本）。</summary>
    private static readonly (int util, int codec, int format, int swscale, int swresample, int avfilter)[] Releases =
    {
        (61, 63, 63, 10, 7, 12), // FFmpeg 9.0
        (60, 62, 62, 9, 6, 11),  // FFmpeg 8.0
        (59, 61, 61, 8, 5, 10),  // FFmpeg 7.x
        (58, 60, 60, 7, 4, 9),   // FFmpeg 6.x
        (57, 59, 59, 6, 4, 8),   // FFmpeg 5.x
        (56, 58, 58, 5, 3, 7),   // FFmpeg 4.x
    };

    private static void LoadNativeLibraries(string? baseDir)
    {
        foreach (var rel in Releases)
        {
            int[] majors = { rel.util, rel.codec, rel.format, rel.swscale, rel.swresample, rel.avfilter };
            var loaded = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            bool ok = true;

            for (int i = 0; i < CoreComponents.Length; i++)
            {
                string simple = CoreComponents[i];
                if (!TryLoadVersioned(baseDir, simple, majors[i], out IntPtr h))
                {
                    ok = false;
                    break;
                }
                loaded[simple] = h;
            }

            if (!ok)
            {
                Rollback(loaded);
                continue;
            }

            // avfilter 预留：能载则载（best-effort），载不到也不阻断。
            if (TryLoadVersioned(baseDir, "avfilter", rel.avfilter, out IntPtr hf))
                loaded["avfilter"] = hf;

            foreach (var kv in loaded) _handles[kv.Key] = kv.Value;
            return;
        }

        // 动态探测全部失败：iOS 静态链接（符号在 App 主镜像）兜底。
        if (OperatingSystem.IsIOS() && TryLoadStaticMainImage())
            return;

        throw new InvalidOperationException(
            "未能加载 FFmpeg 原生库（avutil/avcodec/avformat/swscale/swresample）。" +
            "请确认原生 DLL 已部署到运行目录或 FFmpegLibraryPath 所指目录。");
    }

    private static bool TryLoadVersioned(string? baseDir, string simple, int major, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        // 先试带版本号名（如 avutil-61.dll），再退化为无版本名（兼容旧别名副本）。
        if (TryLoad(baseDir, VersionedName(simple, major), out handle)) return true;
        if (TryLoad(baseDir, UnversionedName(simple), out handle)) return true;
        // Android 额外命名形态：部分构建仅提供 libavcodec-61.so 风格。
        if (OperatingSystem.IsAndroid() && TryLoad(baseDir, $"lib{simple}-{major}.so", out handle)) return true;
        return false;
    }

    private static bool TryLoad(string? baseDir, string name, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        string path = baseDir != null ? Path.Combine(baseDir, name) : name;
        return NativeLibrary.TryLoad(path, out handle);
    }

    private static string VersionedName(string simple, int major)
    {
        if (OperatingSystem.IsWindows()) return $"{simple}-{major}.dll";
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) return $"lib{simple}.{major}.dylib";
        return $"lib{simple}.so.{major}"; // Linux / Android
    }

    private static string UnversionedName(string simple)
    {
        if (OperatingSystem.IsWindows()) return $"{simple}.dll";
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()) return $"lib{simple}.dylib";
        return $"lib{simple}.so";
    }

    private static void Rollback(Dictionary<string, IntPtr> loaded)
    {
        foreach (var kv in loaded)
        {
            try { if (kv.Value != IntPtr.Zero) NativeLibrary.Free(kv.Value); } catch { }
        }
    }

    private static void RegisterResolver()
    {
        if (_resolverRegistered) return;
        NativeLibrary.SetDllImportResolver(typeof(FF).Assembly, (name, _, _) =>
            _handles.TryGetValue(name, out var h) ? h : IntPtr.Zero);
        _resolverRegistered = true;
    }

    private static void VerifyVersion()
    {
        uint v = avutil_version();
        int major = (int)((v >> 16) & 0xFF);
        if (major < 56 || major > 61)
            throw new InvalidOperationException(
                $"不支持的 FFmpeg 主版本 {major}（avutil_version=0x{v:X}）。本后端支持 4.x–9.0（avutil 56–61）。");
    }

    /// <summary>
    /// 根因防护：FFmpeg 头文件中的 <c>static inline</c> 函数（如 av_q2d/av_inv_q/av_make_q/av_cmp_q/av_gcd）
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
                "。可能原因：① 该函数是 FFmpeg 头文件中的 static inline（不导出），被误以 [LibraryImport] 声明——请改托管实现；" +
                "② 原生库版本错配或部署残缺（缺少对应组件 DLL）。");
    }

    /// <summary>
    /// 根因防护：结构体镜像偏移跨 FFmpeg 主版本敏感（hw_device_ctx 等中段字段在 4.x 与 9.0 间可能不同）。
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
            // 在结构体不同深度布点（覆盖 hw_device_ctx@560 前后区域）。
            // av_opt_set_int 写入原生真实偏移；按镜像字段偏移读回；不一致即镜像偏移错误。
            CheckAvOptField(ctx, "strict_std_compliance", 0x62B);
            CheckAvOptField(ctx, "err_recognition", 0x73C);
            CheckAvOptField(ctx, "hwaccel_flags", 0x84D);
            CheckAvOptField(ctx, "extra_hw_frames", 0x95E);
            CheckAvOptField(ctx, "width", 0xA6F);
            CheckAvOptField(ctx, "height", 0xB70);
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

    /// <summary>
    /// iOS 静态链接兜底：libavcodec.a 等被链入 App 主镜像，动态 .dylib 路径不存在。
    /// 加载 App 可执行文件主镜像，把各组件简单名映射到该句柄（符号即在其内）。
    /// </summary>
    private static bool TryLoadStaticMainImage()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return false;
        if (!NativeLibrary.TryLoad(processPath, out IntPtr mainHandle) || mainHandle == IntPtr.Zero) return false;
        foreach (var simple in CoreComponents) _handles[simple] = mainHandle;
        return true;
    }
}
