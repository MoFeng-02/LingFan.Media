namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体元数据。
/// </summary>
public sealed class MediaMetadata
{
    /// <summary>标题。</summary>
    public string? Title { get; init; }

    /// <summary>艺术家。</summary>
    public string? Artist { get; init; }

    /// <summary>专辑。</summary>
    public string? Album { get; init; }

    /// <summary>年份。</summary>
    public int? Year { get; init; }

    /// <summary>类型/流派。</summary>
    public string? Genre { get; init; }

    /// <summary>总时长。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>容器格式。</summary>
    public ContainerFormat ContainerFormat { get; init; }

    /// <summary>自定义字段。</summary>
    public IReadOnlyDictionary<string, string> ExtraFields { get; init; } = new Dictionary<string, string>();
}
