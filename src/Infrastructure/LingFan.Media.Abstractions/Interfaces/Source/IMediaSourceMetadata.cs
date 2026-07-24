namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体源元数据接口。
/// </summary>
public interface IMediaSourceMetadata
{
    /// <summary>名称（可能为 null）。</summary>
    string? Name { get; }

    /// <summary>MIME 类型（可能为 null）。</summary>
    string? ContentType { get; }

    /// <summary>是否直播流。</summary>
    bool IsLive { get; }

    /// <summary>自定义字段。</summary>
    IReadOnlyDictionary<string, string> ExtraFields { get; }
}
