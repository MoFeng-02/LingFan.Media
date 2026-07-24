namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体来源类型。
/// </summary>
public enum MediaSourceType : int
{
    /// <summary>本地文件。</summary>
    File,
    /// <summary>网络流。</summary>
    Network,
    /// <summary>自定义流。</summary>
    Stream
}
