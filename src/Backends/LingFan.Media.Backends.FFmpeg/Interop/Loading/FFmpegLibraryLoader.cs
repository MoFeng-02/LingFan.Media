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
///        这是针对结构体偏移跨主版本敏感的布局损坏防护。</item>
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

    /// <summary>
    /// 核心组件（必载），按依赖闭包序加载：被依赖者先载，后续库的 DT_NEEDED 即可命中已加载对象（无需搜索路径兜底）。
    /// 依赖关系：avcodec 依赖 avutil+swresample；avformat 依赖 avcodec+avutil；swresample/swscale 依赖 avutil。
    /// avfilter 预留但可选（当前未绑定其函数），单独 best-effort。
    /// </summary>
    private static readonly string[] CoreComponents = { "avutil", "swresample", "swscale", "avcodec", "avformat" };

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
        PreloadLinuxVaLibraries(baseDir);   // Linux：libva（VAAPI）解析——系统优先，bin 平级，递归扫描兜底（见方法注释）

        foreach (var rel in Releases)
        {
            int MajorOf(string simple) => simple switch
            {
                "avutil" => rel.util,
                "avcodec" => rel.codec,
                "avformat" => rel.format,
                "swscale" => rel.swscale,
                "swresample" => rel.swresample,
                _ => rel.avfilter,
            };
            var loaded = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            bool ok = true;

            for (int i = 0; i < CoreComponents.Length; i++)
            {
                string simple = CoreComponents[i];
                if (!TryLoadVersioned(baseDir, simple, MajorOf(simple), out IntPtr h))
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

        // 兜底：递归扫描应用目录树（任意布局：平级 / ffmpeg/ 等子目录）——按文件名解析组件、
        // 同组件取版本最高者，依赖闭包序绝对路径加载并登记句柄；成功则版本门禁/导出/镜像自检照常把关。
        if (!string.IsNullOrEmpty(baseDir) && TryLoadByRecursiveScan(baseDir))
            return;

        throw new InvalidOperationException(
            "未能加载 FFmpeg 原生库（avutil/avcodec/avformat/swscale/swresample）。" +
            "请确认原生 DLL 已部署到运行目录或 FFmpegLibraryPath 所指目录。");
    }

    /// <summary>
    /// Linux libva（VAAPI）解析：①系统库若已提供 vaMapBuffer2（libva ≥2.21）直接复用（不遮蔽发行版）；
    /// ②应用目录树解析 bin 内副本：平级优先，其次递归扫描任意子目录（同组件取版本最高者）。
    /// 绝对路径预载后，同 soname 的后续 dlopen 复用本副本（不依赖 DT_RUNPATH——glibc 的 dlopen
    /// 不消费 ffmpeg 库的 RUNPATH，BtbN 构建的 VAAPI 蹦床会因此命中系统旧版并 assert 崩溃）。
    /// </summary>
    private static void PreloadLinuxVaLibraries(string? baseDir)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(baseDir))
            return;

        if (NativeLibrary.TryLoad("libva.so.2", out var systemVa))
        {
            if (NativeLibrary.TryGetExport(systemVa, "vaMapBuffer2", out _))
                return;   // 系统库可用：不预载任何副本
            NativeLibrary.Free(systemVa);   // 系统库过旧：归还探针引用
        }

        foreach (var simple in new[] { "libva", "libva-drm" })
        {
            string? path = ResolveBundledLibrary(baseDir, simple);
            if (path != null)
                NativeLibrary.TryLoad(path, out _);   // best-effort：缺文件/加载失败不阻断
        }
    }

    /// <summary>在应用目录树内解析组件库：bin 平级优先，其次递归扫描任意子目录；同组件取版本最高者。</summary>
    private static string? ResolveBundledLibrary(string baseDir, string simple)
    {
        try
        {
            string flat = Path.Combine(baseDir, $"lib{simple}.so.2");
            if (File.Exists(flat))
                return flat;

            string? best = null;
            ulong bestRank = 0;
            foreach (var f in Directory.EnumerateFiles(baseDir, $"lib{simple}.so*", SearchOption.AllDirectories))
            {
                ulong rank = VersionRank(Path.GetFileName(f));
                if (rank >= bestRank) { best = f; bestRank = rank; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>兜底：递归扫描应用目录树加载 FFmpeg 组件（任意布局：平级 / ffmpeg/ 等子目录）。
    /// 按文件名解析组件（libavutil.so.60.26.102 → avutil），同组件取版本最高者，
    /// 依赖闭包序绝对路径加载并登记句柄；全组件齐备返回 true（版本门禁/导出/镜像自检照常把关）。</summary>
    private static bool TryLoadByRecursiveScan(string? baseDir)
    {
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return false;
        try
        {
            var best = new Dictionary<string, (string Path, ulong Rank)>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(baseDir, "lib*.so*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var m = System.Text.RegularExpressions.Regex.Match(name, @"^lib([a-z]+)\.so");
                if (!m.Success) continue;
                string comp = m.Groups[1].Value;
                ulong rank = VersionRank(name);
                if (!best.TryGetValue(comp, out var cur) || rank > cur.Rank)
                    best[comp] = (file, rank);
            }

            var loaded = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            foreach (var simple in CoreComponents)
            {
                if (!best.TryGetValue(simple, out var c) || !NativeLibrary.TryLoad(c.Path, out var h))
                {
                    foreach (var kv in loaded) NativeLibrary.Free(kv.Value);
                    return false;
                }
                loaded[simple] = h;
            }

            foreach (var kv in loaded) _handles[kv.Key] = kv.Value;
            return true;
        }
        catch { return false; }
    }

    /// <summary>按文件名版本段计算可比序数（libavutil.so.60.26.102 → 60/26/102 三段加权）。</summary>
    private static ulong VersionRank(string fileName)
    {
        int i = fileName.IndexOf(".so.", StringComparison.Ordinal);
        if (i < 0) return 0;
        string[] seg = fileName.Substring(i + 4).Split('.');
        ulong v = 0;
        for (int k = 0; k < 3; k++)
            v = (v << 10) | (uint)(k < seg.Length && ulong.TryParse(seg[k], out var n) ? n & 0x3FF : 0ul);
        return v;
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
