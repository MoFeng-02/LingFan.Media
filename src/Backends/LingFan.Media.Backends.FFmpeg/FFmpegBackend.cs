namespace LingFan.Media.Backends.FFmpeg;

/// <summary>
/// FFmpeg 后端入口。持有 FFmpeg 全局初始化状态。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton。只持有 <c>avformat_network_init()</c> 等全局初始化状态，
/// 不持有任何媒体流/解码上下文，多播放器共享安全。</para>
/// <para>构造函数和 Dispose 均为同步——avformat_network_init/deinit 是快速原生调用，无 I/O 阻塞。</para>
/// <para>AOT 兼容：无反射，sealed 类。</para>
/// </remarks>
public sealed class FFmpegBackend : IDisposable
{
    private readonly ILogger<FFmpegBackend> _logger;
    private bool _disposed;
    private bool _initialized;

    /// <summary>
    /// 初始化 <see cref="FFmpegBackend"/> 的新实例。
    /// </summary>
    /// <remarks>
    /// <para>🔴 原生初始化刻意放在此处（Singleton 首次被 FFmpeg 工厂解析时构造），而非 <c>AddFFmpeg()</c> 注册期。
    /// 这样注册阶段保持纯 DI、绝不触碰原生库；只有真正用到 FFmpeg 后端（如其他后端不支持某源而回退）时才需要
    /// ffmpeg 原生 DLL 在场。注册一个后端 ≠ 马上要它的 native 库——这是“开箱即用 + 不侵入”的硬约束。</para>
    /// </remarks>
    /// <param name="logger">日志器。</param>
    /// <param name="options">FFmpeg 配置（含库路径与日志级别）。</param>
    public FFmpegBackend(ILogger<FFmpegBackend> logger, FFmpegOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            // 设置原生库搜索路径（若宿主显式指定）。须在首个 ffmpeg.* 调用前设置，使 AutoGen 按 RootPath 加载 DLL。
            if (!string.IsNullOrEmpty(options.FFmpegLibraryPath))
            {
                SetLibraryPath(options.FFmpegLibraryPath!);
            }

            // 设置 FFmpeg 日志级别（首个 ffmpeg.* 调用，触发原生绑定加载；此时 RootPath 已就绪）
            ffmpeg.av_log_set_level(options.LogLevel);

            // FFmpeg 全局网络初始化（幂等调用，多次调用安全）
            ffmpeg.avformat_network_init();
            _initialized = true;
            _logger.LogDebug("FFmpeg 全局初始化完成（日志级别={LogLevel}）", options.LogLevel);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "FFmpeg 全局初始化失败");
            throw;
        }
    }

    /// <summary>
    /// 设置 FFmpeg 原生库搜索路径。
    /// </summary>
    /// <param name="path">原生库目录路径。</param>
    internal static void SetLibraryPath(string path)
    {
        // 🔴 FFmpeg.AutoGen 8.1 的 DynamicallyLoaded 绑定按「无版本号」名（avutil/avcodec/...）加载原生库，
        // 但 BtbN lgpl-shared 等常见共享构建只提供 avutil-60.dll 等带版本文件。若目录下仅存在带版本名，
        // AutoGen 加载失败 → 首个 ffmpeg.* 调用崩溃（静默退出码 127，无诊断）。
        // 此处 best-effort 补一份「无版本号别名」（硬链接优先、失败退化为复制），使本库对 BtbN 构建开箱即用；
        // 失败不抛（宿主应自行保证 DLL 可被 AutoGen 加载，构建期复制别名为首选方案）。
        EnsureUnversionedAliases(path);

        // FFmpeg.AutoGen 通过 ffmpeg.RootPath 设置原生库搜索路径
        ffmpeg.RootPath = path;
    }

    /// <summary>
    /// best-effort：为目录下「av&lt;name&gt;-&lt;digits&gt;.dll」创建同名无版本别名（av&lt;name&gt;.dll），
    /// 使 FFmpeg.AutoGen 的 DynamicallyLoaded 绑定能找到它。幂等；任何异常静默忽略。
    /// </summary>
    private static void EnsureUnversionedAliases(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "av*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string unversioned = System.Text.RegularExpressions.Regex.Replace(name, @"-\d+$", "");
                if (unversioned == name) continue; // 已是无版本名
                string alias = Path.Combine(dir, unversioned + ".dll");
                if (File.Exists(alias)) continue;
                // 复制出无版本别名（best-effort；硬链接 API 在部分 SDK 表面不可用，复制最稳）。
                File.Copy(file, alias);
            }
        }
        catch
        {
            // best-effort：忽略。宿主应在构建期产出无版本别名（见各 Host 的 CopyFFmpegNative）。
        }
    }

    /// <summary>
    /// 释放 FFmpeg 全局资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialized)
        {
            try
            {
                ffmpeg.avformat_network_deinit();
                _logger.LogDebug("FFmpeg 全局网络清理完成");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "FFmpeg 全局网络清理异常");
            }
            _initialized = false;
        }
    }
}
