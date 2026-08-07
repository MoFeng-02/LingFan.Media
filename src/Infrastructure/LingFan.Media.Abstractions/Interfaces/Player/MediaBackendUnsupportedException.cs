namespace LingFan.Media.Abstractions;

/// <summary>
/// 当所有已注册后端均无法打开给定媒体源时抛出（运行时回退穷尽）。
/// </summary>
public sealed class MediaBackendUnsupportedException : Exception
{
    /// <summary>构造异常，source 为媒体源标识（<see cref="IMediaSource.Identifier"/>）。</summary>
    public MediaBackendUnsupportedException(string source)
        : base($"没有已注册的后端能够打开该媒体源：{source}") { }

    /// <summary>构造异常并携带导致穷尽回退的内部异常。</summary>
    public MediaBackendUnsupportedException(string source, Exception innerException)
        : base($"没有已注册的后端能够打开该媒体源：{source}", innerException) { }
}
