namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体来源描述。
/// </summary>
public interface IMediaSource
{
    /// <summary>来源类型。</summary>
    MediaSourceType Type { get; }

    /// <summary>标识符（文件路径或 URL）。</summary>
    string Identifier { get; }
}
