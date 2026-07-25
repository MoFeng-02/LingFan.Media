namespace LingFan.Media.Backends.FFmpeg;

/// <summary>
/// FFmpeg 后端配置选项。
/// </summary>
public sealed class FFmpegOptions
{
    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool HardwareAcceleration { get; set; } = true;

    /// <summary>
    /// FFmpeg 原生库路径（自定义路径时设置，null 表示使用系统默认搜索路径）。
    /// </summary>
    public string? FFmpegLibraryPath { get; set; }

    /// <summary>
    /// FFmpeg 内部日志级别（默认 AV_LOG_ERROR = 16）。
    /// </summary>
    public int LogLevel { get; set; } = 16;

    /// <summary>是否启用多线程解码（默认 true）。</summary>
    public bool EnableMultiThread { get; set; } = true;

    /// <summary>解码线程数（0 = 自动选择，默认 0）。</summary>
    public int ThreadCount { get; set; } = 0;
}
