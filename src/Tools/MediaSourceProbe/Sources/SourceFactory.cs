using LingFan.Media.Abstractions;
using LingFan.Media.Sources;

namespace MediaSourceProbe.Sources;

/// <summary>
/// 探针构造的媒体源及其所有权。
/// </summary>
/// <remarks>
/// <para>所有权约定：探针创建的资源（如 stream 源打开的文件流）由探针释放；
/// 交给播放会话的 <see cref="StreamMediaSource"/> 一律以 <c>ownsStream: false</c> 构造，
/// 会话关闭时只关闭自身的透传包装、不释放底层流。这样一次运行内可安全重试打开
/// （后端回退会先释放上一个后端 player），也不会出现“会话关掉用户流”的连带释放。</para>
/// <para>用法：<c>using var probeSource = SourceFactory.Create(options);</c> 运行结束即释放。</para>
/// </remarks>
internal sealed class ProbeSource(IMediaSource source, Stream? ownedStream) : IDisposable
{
    /// <summary>交给播放器的媒体源。</summary>
    public IMediaSource Source { get; } = source;

    /// <summary>探针持有所有权的底层流；null 表示本次源没有由探针创建的资源。</summary>
    public Stream? OwnedStream { get; } = ownedStream;

    /// <summary>释放探针创建的资源（播放会话不负责释放）。</summary>
    public void Dispose()
    {
        if (OwnedStream is null)
            return;

        try
        {
            OwnedStream.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SOURCE] 释放自有流异常（不影响判定）: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>按命令行选项构造媒体源。</summary>
internal static class SourceFactory
{
    /// <summary>构造源。参数非法时抛 <see cref="ArgumentException"/>，调用方转成用法错误。</summary>
    public static ProbeSource Create(ProbeOptions options) => options.SourceType switch
    {
        MediaSourceType.File => new ProbeSource(new FileMediaSource(options.SourceArgument), null),
        MediaSourceType.Stream => CreateStreamSource(options.SourceArgument, options.NonSeekable),
        MediaSourceType.Network => new ProbeSource(CreateNetworkSource(options), null),
        _ => throw new InvalidOperationException("不可达的源类型"),
    };

    /// <summary>
    /// 由文件路径构造 stream 源。
    /// </summary>
    /// <remarks>
    /// 底层文件流由探针创建并持有（<see cref="ProbeSource.OwnedStream"/>），
    /// <see cref="StreamMediaSource.OwnsStream"/> 显式为 false——播放会话关闭时不得释放它。
    /// 不可定位包装只隐藏 Seek 能力、自身不持有句柄，故所有权直接记在内层文件流上：
    /// 包装器由 GC 回收即可，文件句柄必须由探针显式释放。
    /// </remarks>
    private static ProbeSource CreateStreamSource(string path, bool nonSeekable)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"--source stream 需要已存在的文件路径: {path}", nameof(path));

        Stream inner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream wrapped = nonSeekable ? new NonSeekableStream(inner) : inner;
        var source = new StreamMediaSource(
            wrapped,
            identifier: Path.GetFileName(path) + (nonSeekable ? "-nonseekable" : "-passthrough"),
            ownsStream: false);
        return new ProbeSource(source, inner);
    }

    private static NetworkMediaSource CreateNetworkSource(ProbeOptions options)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.HeadersArgument))
        {
            foreach (var pair in options.HeadersArgument.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int idx = pair.IndexOf(':');
                if (idx <= 0)
                    throw new ArgumentException($"--headers 项格式应为 \"Key: Value\"，实际: {pair}");
                headers[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
            }
        }

        List<MediaCookie>? cookies = null;
        if (!string.IsNullOrWhiteSpace(options.CookieArgument))
        {
            cookies = new List<MediaCookie>();
            foreach (var pair in options.CookieArgument.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int idx = pair.IndexOf('=');
                if (idx <= 0)
                    throw new ArgumentException($"--cookie 项格式应为 \"name=value\"，实际: {pair}");
                cookies.Add(new MediaCookie(pair[..idx].Trim(), pair[(idx + 1)..].Trim(), null, null, false, false));
            }
        }

        TimeSpan? timeout = double.TryParse(options.TimeoutArgument, out var t) ? TimeSpan.FromSeconds(t) : null;
        TimeSpan? readTimeout = double.TryParse(options.ReadTimeoutArgument, out var rt)
            ? (rt <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(rt))
            : null;

        return new NetworkMediaSource(
            options.SourceArgument,
            headers: headers.Count > 0 ? headers : null,
            timeout: timeout,
            allowInsecureHttps: options.Insecure,
            cookies: cookies,
            allowPrivateAddresses: options.AllowPrivate,
            readTimeout: readTimeout);
    }

    /// <summary>优先用输出目录下随工程复制的 Resources\Video\m1.mp4。</summary>
    public static string? ResolveDefaultMedia()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "Resources", "Video", "m1.mp4");
        if (File.Exists(local))
            return local;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Video", "m1.mp4");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
