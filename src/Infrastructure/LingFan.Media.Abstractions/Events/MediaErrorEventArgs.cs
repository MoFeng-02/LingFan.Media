namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体错误事件参数。
/// </summary>
public sealed class MediaErrorEventArgs : EventArgs
{
    /// <summary>错误码。</summary>
    public MediaErrorCode ErrorCode { get; }

    /// <summary>错误消息。</summary>
    public string Message { get; }

    /// <summary>原始异常（如果有）。</summary>
    public Exception? Exception { get; }

    /// <summary>是否致命（不可恢复）。</summary>
    public bool IsFatal { get; }

    /// <summary>是否可恢复。</summary>
    public bool Recoverable { get; }

    /// <summary>
    /// 初始化 <see cref="MediaErrorEventArgs"/> 的新实例。
    /// </summary>
    public MediaErrorEventArgs(
        MediaErrorCode errorCode,
        string message,
        Exception? exception = null,
        bool isFatal = false,
        bool recoverable = false)
    {
        ErrorCode = errorCode;
        Message = message;
        Exception = exception;
        IsFatal = isFatal;
        Recoverable = recoverable;
    }
}
