using System.Reflection;
using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.VLCNative.Interop;

/// <summary>
/// libvlc 引擎实例封装（对应旧 <c>VLCBackend</c> 持有的 LibVLC，但走自写 Apache-2.0 P/Invoke）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。只持有 libvlc 引擎实例，不持媒体/播放上下文；多播放器共享安全。</para>
/// <para>原生 libvlc 是 LGPL 许可证的原生库，但<b>不打包进库本体</b>：运行时由 <c>VideoLAN.LibVLC.*</c>
/// 或系统/下游提供，作为外部运行时依赖。</para>
/// <para>原生库定位采用<b>规则驱动 + 递归查找</b>，不硬编码任何单一 RID 路径：
/// 从 <c>AppContext.BaseDirectory</c>（及 <c>libvlc/</c> 子目录）出发，按当前 OS 的原生库命名规则
/// （Windows <c>libvlc.dll</c> / macOS·iOS <c>libvlc.dylib</c> / Linux·Android <c>libvlc.so*</c>）
/// 有界递归（深度 ≤ 6）搜索；命中后按「是否含 libvlccore + plugins 同级目录」与「是否与当前进程架构匹配」
/// 综合评分，优先选用<b>架构匹配且完整</b>的运行时。Windows / macOS / Linux / Android / iOS 走同一套规则。</para>
/// <para>关键：VideoLAN 包在 <c>libvlc/</c> 下平铺 <c>win-x64 / win-arm64 / win-x86</c> 三套镜像，
/// 递归发现必须按<b>当前进程架构</b>过滤——否则 x64 进程会误加载 arm64/x86 镜像触发
/// <see cref="BadImageFormatException"/>（0x8007000B）。评分对架构匹配项加权 +1000，且 <c>NativeLibrary.Load</c>
/// 对所有候选做容错遍历：单个候选架构不符即跳过试下一个。</para>
/// <para>加载后通过 <see cref="NativeLibrary.SetDllImportResolver"/> 把逻辑名 <c>"libvlc"</c> 钉死到已加载模块句柄，
/// 不依赖各 OS loader 的「同基名复用」隐式行为，解析确定性强。</para>
/// <para>plugins 目录不做特殊处理：libvlc 通过 libvlccore 自身模块路径推导同级 <c>plugins/</c>，
/// 这是官方主路径；<c>VLC_PLUGIN_PATH</c> 环境变量在 .NET 下写入进程环境块后
/// UCRT <c>getenv</c> 未必可见，故不采用。</para>
/// <para>当前版本仅验证 Windows 运行时；其他平台的发现规则已就绪，待对应 VideoLAN 包接入后验证。</para>
/// </remarks>
public sealed class LibVlcInstance : IDisposable
{
    /// <summary>P/Invoke 使用的原生库逻辑名，须与 <c>[LibraryImport("libvlc")]</c> 中的名字逐字一致。</summary>
    internal const string NativeLibraryName = "libvlc";

    // 递归搜索最大深度（从 AppContext.BaseDirectory 起算）。VideoLAN 包原生平铺在
    // <out>/libvlc/<rid>/ 下（深度 2），留足余量覆盖自包含发布/嵌套布局。
    private const int MaxSearchDepth = 6;

    // 架构匹配的评分权重（远高于 core/plugins 权重，确保同架构优先于异架构）。
    private const int ArchMatchScore = 1000;

    private static readonly object LoadLock = new();
    private static nint _nativeModule;
    private static bool _resolverInstalled;
    private static bool _initialized;

    private nint _handle;
    private bool _disposed;

    /// <summary>以默认选项初始化 libvlc 引擎实例（<c>libvlc_new(0, NULL)</c>）。</summary>
    public LibVlcInstance()
        : this(null)
    {
    }

    /// <summary>
    /// 以指定命令行选项初始化 libvlc 引擎实例。
    /// </summary>
    /// <param name="options">libvlc 启动选项，如 <c>--vout=dummy</c>、<c>--avcodec-hw=any</c>；为 null 或空则等价于无参构造。</param>
    /// <exception cref="DllNotFoundException">未找到/无法加载原生 libvlc。</exception>
    /// <exception cref="InvalidOperationException"><c>libvlc_new</c> 返回 NULL。</exception>
    public LibVlcInstance(IReadOnlyList<string>? options)
    {
        EnsureNativeLoaded();

        _handle = CreateCore(options);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"libvlc_new 失败: {LastErrorMessage() ?? "unknown error"}（已加载模块: {NativeModulePath}）");
        }
    }

    /// <summary>libvlc 引擎实例句柄（供 demuxer 创建 media / mediaplayer 使用）。</summary>
    public nint Handle => _handle;

    /// <summary>本次实际加载的原生 libvlc 完整路径（诊断用）。</summary>
    public static string NativeModulePath { get; private set; } = string.Empty;

    /// <summary>
    /// libvlc 版本字符串（仅供诊断）。
    /// 静态成员：<c>libvlc_get_version</c> 不需要引擎实例，可用于在 <c>libvlc_new</c> 之前单独验证原生加载与 P/Invoke 解析。
    /// </summary>
    public static string NativeVersion
    {
        get
        {
            EnsureNativeLoaded();
            var v = LibVlcNative.libvlc_get_version();
            return v != IntPtr.Zero ? (Marshal.PtrToStringUTF8(v) ?? string.Empty) : string.Empty;
        }
    }

    /// <summary>libvlc 版本字符串（实例转发到 <see cref="NativeVersion"/>）。</summary>
    public string Version => NativeVersion;

    /// <summary>读取 libvlc 最近一次错误信息；返回的字符串由 libvlc 内部持有（线程局部），不得释放。</summary>
    /// <returns>错误信息；无错误时为 null。</returns>
    public static string? LastErrorMessage()
    {
        var err = LibVlcNative.libvlc_errmsg();
        return err != IntPtr.Zero ? Marshal.PtrToStringUTF8(err) : null;
    }

    /// <summary>
    /// 调用 <c>libvlc_new</c>，按需把托管字符串数组封送为 <c>const char* const*</c>。
    /// </summary>
    /// <remarks>
    /// libvlc 在 <c>libvlc_new</c> 内部把选项解析并复制进自身 config，返回后调用方即可释放 argv 与各字符串
    /// （与 libvlc 标准用法一致）。这里用 <c>StringToCoTaskMemUTF8</c> / <c>FreeCoTaskMem</c> 严格配对。
    /// </remarks>
    private static nint CreateCore(IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
            return LibVlcNative.libvlc_new(0, IntPtr.Zero);

        var count = options.Count;
        var utf8 = new IntPtr[count];
        var argv = Marshal.AllocHGlobal(IntPtr.Size * count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                utf8[i] = Marshal.StringToCoTaskMemUTF8(options[i]);
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, utf8[i]);
            }

            return LibVlcNative.libvlc_new(count, argv);
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (utf8[i] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(utf8[i]);
            }

            Marshal.FreeHGlobal(argv);
        }
    }

    /// <summary>
    /// 定位并加载原生 libvlc，随后注册 DllImport 解析器。幂等，线程安全。
    /// </summary>
    private static void EnsureNativeLoaded()
    {
        // 快速路径用独立的完成标志而非句柄本身：句柄必须在解析器注册完成「之后」才对外可见，
        // 否则并发线程可能在解析器就位前发起 P/Invoke，退化为默认探测。
        if (Volatile.Read(ref _initialized))
            return;

        lock (LoadLock)
        {
            if (_initialized)
                return;

            var candidates = LocateNativeCandidates(); // 规则驱动 + 递归查找，按架构/完整性评分排序
            if (candidates.Count == 0)
            {
                throw new DllNotFoundException(
                    $"未找到原生 libvlc（OS={DetectOs()}）。请通过 VideoLAN.LibVLC.* 提供原生运行时（不打包进库本体）。" +
                    $"搜索根: {string.Join("; ", GetSearchRoots().Where(r => !string.IsNullOrEmpty(r)))}");
            }

            // 容错遍历：单个候选架构不符（BadImageFormatException）或缺依赖（IOException/DllNotFound）即跳过，试下一个。
            nint module = IntPtr.Zero;
            string? loadedPath = null;
            foreach (var (dllPath, _) in candidates)
            {
                try
                {
                    // libvlc 的导入表依赖同目录 libvlccore（Windows）/ libvlccore.so（Linux）/ libvlccore.dylib（macOS）。
                    // 先预加载加固；失败不致命，交给后续 Load 报真实错误。
                    var corePath = FindSiblingCore(dllPath);
                    if (corePath is not null)
                        _ = NativeLibrary.TryLoad(corePath, out _);

                    module = NativeLibrary.Load(dllPath);
                    loadedPath = dllPath;
                    break;
                }
                catch (BadImageFormatException) { /* 架构不符，试下一个候选 */ }
                catch (DllNotFoundException) { }
                catch (IOException) { }
            }

            if (module == IntPtr.Zero)
            {
                throw new DllNotFoundException(
                    $"未能加载任何候选 libvlc（共 {candidates.Count} 个匹配，但全部加载失败：架构不符或缺失依赖）。" +
                    $"搜索根: {string.Join("; ", GetSearchRoots().Where(r => !string.IsNullOrEmpty(r)))}");
            }

            _nativeModule = module;
            NativeModulePath = loadedPath!;

            // 解析器必须在首次 P/Invoke 之前注册；同一程序集只允许注册一次，重复注册会抛 InvalidOperationException。
            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(LibVlcInstance).Assembly, ResolveNativeLibrary);
                _resolverInstalled = true;
            }

            Volatile.Write(ref _initialized, true);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 原生库定位：规则驱动 + 有界递归（Windows / macOS / Linux / Android / iOS 通用）
    // ─────────────────────────────────────────────────────────────

    /// <summary>返回按评分降序排列的候选清单（评分 = 架构匹配 + 含 libvlccore + 含 plugins）。</summary>
    private static List<(string Path, int Score)> LocateNativeCandidates()
    {
        var os = DetectOs();
        var roots = GetSearchRoots();
        var matches = new List<(string Path, int Score)>();

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                continue;
            CollectCandidates(root, os, 0, matches);
        }

        if (matches.Count == 0)
            return matches;

        // 评分高者优先：架构匹配 > 含 libvlccore + plugins 的完整运行时 > 仅 libvlccore > 仅同名文件；
        // 同级时路径更浅优先（前置 Collect 已按 BFS 顺序，Sort 稳定保留浅层在前）。
        matches.Sort((a, b) => b.Score.CompareTo(a.Score));
        return matches;
    }

    private static void CollectCandidates(string dir, OsKind os, int depth, List<(string Path, int Score)> outMatches)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (IsLibVlcName(Path.GetFileName(file), os))
                    outMatches.Add((file, ScoreCandidate(file)));
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (IOException) { return; }

        if (depth >= MaxSearchDepth)
            return;

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
                CollectCandidates(sub, os, depth + 1, outMatches);
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private static int ScoreCandidate(string libPath)
    {
        var dir = Path.GetDirectoryName(libPath) ?? string.Empty;
        var os = DetectOs();
        var hasCore = false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (IsLibVlcCoreName(Path.GetFileName(f), os))
                {
                    hasCore = true;
                    break;
                }
            }
        }
        catch (IOException) { }

        var hasPlugins = Directory.Exists(Path.Combine(dir, "plugins"));
        var score = 0;
        if (PathMatchesCurrentArch(libPath)) score += ArchMatchScore; // 关键：同架构优先，避免 x64 误加载 arm64/x86
        if (hasCore) score += 50;
        if (hasPlugins) score += 100;
        return score;
    }

    private static string? FindSiblingCore(string libPath)
    {
        var dir = Path.GetDirectoryName(libPath);
        if (dir is null)
            return null;
        var os = DetectOs();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (IsLibVlcCoreName(Path.GetFileName(f), os))
                    return f;
            }
        }
        catch (IOException) { }
        return null;
    }

    // ─── 当前进程架构识别（用于跨平台 RID 匹配） ───

    /// <summary>当前进程的架构 token：x64 / x86 / arm64 / arm（<see cref="RuntimeInformation.OSArchitecture"/>）。</summary>
    private static string ArchToken => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        _ => string.Empty
    };

    /// <summary>
    /// 判断路径是否命中<b>当前进程架构</b>。规则：路径中任一段整体等于架构 token（如 <c>x64</c>），
    /// 或按 <c>-</c> 拆分后的子 token 精确等于架构 token（如 <c>win-x64</c> → <c>x64</c>、
    /// <c>osx-arm64</c> → <c>arm64</c>）。
    /// 用「拆分后精确匹配」而非子串包含，避免 <c>arm</c> 误中 <c>arm64</c> 之类的跨架构误判。
    /// </summary>
    private static bool PathMatchesCurrentArch(string path)
    {
        var token = ArchToken;
        if (string.IsNullOrEmpty(token))
            return false;

        foreach (var seg in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var part in seg.Split('-'))
            {
                if (part.Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static bool IsLibVlcName(string? fileName, OsKind os)
    {
        fileName = fileName ?? string.Empty;
        return os switch
        {
            OsKind.Windows => fileName.Equals("libvlc.dll", StringComparison.OrdinalIgnoreCase),
            OsKind.Mac => fileName.Equals("libvlc.dylib", StringComparison.OrdinalIgnoreCase),
            _ => fileName.StartsWith("libvlc.so", StringComparison.OrdinalIgnoreCase) // Linux / Android：libvlc.so、libvlc.so.5 …
        };
    }

    private static bool IsLibVlcCoreName(string? fileName, OsKind os)
    {
        fileName = fileName ?? string.Empty;
        return os switch
        {
            OsKind.Windows => fileName.Equals("libvlccore.dll", StringComparison.OrdinalIgnoreCase),
            OsKind.Mac => fileName.Equals("libvlccore.dylib", StringComparison.OrdinalIgnoreCase),
            _ => fileName.StartsWith("libvlccore.so", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static OsKind DetectOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return OsKind.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return OsKind.Mac; // macOS + iOS 同为 .dylib
        return OsKind.Linux;   // Linux + Android 同为 .so
    }

    private static string[] GetSearchRoots()
    {
        var baseDir = AppContext.BaseDirectory;
        return new[]
        {
            baseDir,                              // 自包含发布 / 扁平布局（AppContext.BaseDirectory/libvlc.*）
            Path.Combine(baseDir, "libvlc"),     // VideoLAN.* 包原生平铺布局（libvlc/<rid>/libvlc.*）
        };
    }

    private enum OsKind { Windows, Mac, Linux }

    private static nint ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        => string.Equals(libraryName, NativeLibraryName, StringComparison.Ordinal)
            ? _nativeModule
            : IntPtr.Zero;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            LibVlcNative.libvlc_release(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
