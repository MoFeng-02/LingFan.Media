using System.Diagnostics.CodeAnalysis;
using LingFan.Media.Abstractions;
using MediaSourceProbe.Sources;

namespace MediaSourceProbe;

/// <summary>
/// 命令行选项。解析一次后只读，运行期各步骤据此取参数。
/// </summary>
internal sealed class ProbeOptions
{
    /// <summary>用法说明。参数缺失或取值非法时原样打印。</summary>
    public const string Usage =
        """
        用法: MediaSourceProbe <文件路径|URL> [选项]
              MediaSourceProbe --ssrf-selftest

        选项：
          --source file|stream|network  强制源类型（默认自动：URL 判 network，存在文件判 file；http 亦可写作 network）
          --nonseekable               （stream）包装为不可定位流，验证线性播放兜底
          --seek <秒>                 播放稳定后执行一次定位到指定秒（网络源实测 Range 定位）
          --open-only                 仅验证打开，不播放
          --headers "K:V;K2:V2"       （network）自定义请求头
          --cookie "n=v;n2=v2"        （network）Cookie
          --timeout <秒>              （network）连接超时，默认 30
          --read-timeout <秒>         （network）读超时，默认 10；传 0 关闭（对照实验用）
          --allow-private             （network）允许内网地址（默认拒绝）
          --insecure                  （network）放行无效/自签证书
          -v                          Debug 级日志
        """;

    /// <summary>源参数（文件路径或 URL）。未提供时回退到随工程复制的默认媒体文件。</summary>
    public required string SourceArgument { get; init; }

    /// <summary>源类型（可由 --source 强制，否则按 URL/文件自动判定）。</summary>
    public required MediaSourceType SourceType { get; init; }

    /// <summary>（stream）是否把底层流包装为不可定位流。</summary>
    public bool NonSeekable { get; init; }

    /// <summary>仅验证打开链路，不进入播放。</summary>
    public bool OpenOnly { get; init; }

    /// <summary>是否输出 Debug 级日志。</summary>
    public bool Verbose { get; init; }

    /// <summary>（network）是否允许内网地址。</summary>
    public bool AllowPrivate { get; init; }

    /// <summary>（network）是否放行无效/自签证书。</summary>
    public bool Insecure { get; init; }

    /// <summary>（network）自定义请求头参数原文。</summary>
    public string? HeadersArgument { get; init; }

    /// <summary>（network）Cookie 参数原文。</summary>
    public string? CookieArgument { get; init; }

    /// <summary>（network）连接超时参数原文。</summary>
    public string? TimeoutArgument { get; init; }

    /// <summary>（network）读超时参数原文。</summary>
    public string? ReadTimeoutArgument { get; init; }

    /// <summary>定位目标；null 表示本次不执行定位。</summary>
    public TimeSpan? SeekTarget { get; init; }

    /// <summary>
    /// 解析命令行。失败时返回 false 并给出可直接打印的错误/用法文本。
    /// </summary>
    public static bool TryParse(string[] args, [NotNullWhen(true)] out ProbeOptions? options, out string? error)
    {
        options = null;
        error = null;

        string? sourceArgument = GetFirstPositional(args);
        string? forcedSource = ParseOption(args, "--source");
        string? seekArgument = ParseOption(args, "--seek");

        TimeSpan? seekTarget = null;
        if (!string.IsNullOrWhiteSpace(seekArgument))
        {
            if (!double.TryParse(seekArgument, out double seekSeconds) || seekSeconds < 0)
            {
                error = $"--seek 需要非负秒数，实际: {seekArgument}";
                return false;
            }
            seekTarget = TimeSpan.FromSeconds(seekSeconds);
        }

        if (string.IsNullOrWhiteSpace(sourceArgument))
        {
            sourceArgument = SourceFactory.ResolveDefaultMedia();
            if (sourceArgument is null)
            {
                error = Usage;
                return false;
            }
        }

        bool isUrl = sourceArgument.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || sourceArgument.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        MediaSourceType? sourceType = forcedSource?.ToLowerInvariant() switch
        {
            "file" => MediaSourceType.File,
            "stream" => MediaSourceType.Stream,
            // network 与 http 同义：前者与「文件 / 流 / 网络」三路说法一致，后者是旧写法。
            "network" or "http" => MediaSourceType.Network,
            null => isUrl ? MediaSourceType.Network : MediaSourceType.File,
            _ => null,
        };

        if (sourceType is null)
        {
            error = $"--source 取值无效: {forcedSource}（可选 file|stream|network）";
            return false;
        }
        if (sourceType == MediaSourceType.Network && !isUrl)
        {
            error = "--source network 需要提供 http(s) URL。";
            return false;
        }

        options = new ProbeOptions
        {
            SourceArgument = sourceArgument,
            SourceType = sourceType.Value,
            NonSeekable = args.Contains("--nonseekable"),
            OpenOnly = args.Contains("--open-only"),
            Verbose = args.Contains("-v") || args.Contains("--verbose"),
            AllowPrivate = args.Contains("--allow-private"),
            Insecure = args.Contains("--insecure"),
            HeadersArgument = ParseOption(args, "--headers"),
            CookieArgument = ParseOption(args, "--cookie"),
            TimeoutArgument = ParseOption(args, "--timeout"),
            ReadTimeoutArgument = ParseOption(args, "--read-timeout"),
            SeekTarget = seekTarget,
        };
        return true;
    }

    /// <summary>取选项值（选项名后紧跟的那一项）。</summary>
    private static string? ParseOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>取第一个位置参数（跳过全部选项与选项值，避免把 "--seek 5" 的值误当源参数）。</summary>
    private static string? GetFirstPositional(string[] args)
    {
        string[] valueOptions = { "--source", "--seek", "--headers", "--cookie", "--timeout", "--read-timeout" };
        for (int i = 0; i < args.Length; i++)
        {
            if (valueOptions.Contains(args[i], StringComparer.Ordinal))
            {
                i++; // 跳过选项值
                continue;
            }
            if (args[i].StartsWith('-'))
                continue; // 布尔开关
            return args[i];
        }
        return null;
    }
}
