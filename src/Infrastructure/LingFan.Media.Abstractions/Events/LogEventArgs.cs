using Microsoft.Extensions.Logging;

namespace LingFan.Media.Abstractions;

/// <summary>
/// 日志事件参数。
/// </summary>
public sealed class LogEventArgs : EventArgs
{
    /// <summary>日志级别。</summary>
    public LogLevel Level { get; }

    /// <summary>日志消息。</summary>
    public string Message { get; }

    /// <summary>来源模块（可能为 null）。</summary>
    public string? Source { get; }

    /// <summary>关联异常（如果有）。</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// 初始化 <see cref="LogEventArgs"/> 的新实例。
    /// </summary>
    /// <param name="level">日志级别。</param>
    /// <param name="message">日志消息。</param>
    /// <param name="source">来源模块（可为 null）。</param>
    /// <param name="exception">关联异常（可为 null）。</param>
    public LogEventArgs(LogLevel level, string message, string? source = null, Exception? exception = null)
    {
        Level = level;
        Message = message;
        Source = source;
        Exception = exception;
    }
}
