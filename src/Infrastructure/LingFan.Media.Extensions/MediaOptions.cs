namespace LingFan.Media.Extensions;

/// <summary>
/// 媒体全局配置选项。
/// </summary>
/// <remarks>
/// <para>由 <see cref="ServiceCollectionExtensions.AddLingFanMedia"/> 读取并注册到
/// <c>IOptions&lt;MediaOptions&gt;</c>。</para>
/// <para>日志配置（<see cref="EnableLogging"/> / <see cref="LogLevel"/>）存储在此对象中，
/// 由消费方应用的 Host 读取后配置实际日志基础设施（<c>AddLogging()</c>）。
/// Extensions 层只依赖 <c>Logging.Abstractions</c>，不引用 <c>Logging</c> 具体实现包。</para>
/// </remarks>
public sealed class MediaOptions
{
    /// <summary>默认视频渲染器类型（null = 自动选择）。</summary>
    public Type? DefaultVideoRenderer { get; set; }

    /// <summary>默认音频输出类型（null = 自动选择）。</summary>
    public Type? DefaultAudioOutput { get; set; }

    /// <summary>首选后端名称（null = 自动选择，如 "FFmpeg"）。</summary>
    public string? PreferredBackend { get; set; }

    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool EnableHardwareDecode { get; set; } = true;

    /// <summary>目标缓冲时长（默认 5 秒）。</summary>
    public TimeSpan BufferTargetDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>是否启用日志（默认 true）。</summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>日志级别（默认 Information）。</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    /// <summary>默认音量（0.0~1.0，默认 1.0）。</summary>
    public float DefaultVolume { get; set; } = 1.0f;

    /// <summary>
    /// 将当前实例的属性复制到目标实例。
    /// </summary>
    /// <param name="target">目标实例。</param>
    internal void CopyTo(MediaOptions target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.DefaultVideoRenderer = DefaultVideoRenderer;
        target.DefaultAudioOutput = DefaultAudioOutput;
        target.PreferredBackend = PreferredBackend;
        target.EnableHardwareDecode = EnableHardwareDecode;
        target.BufferTargetDuration = BufferTargetDuration;
        target.EnableLogging = EnableLogging;
        target.LogLevel = LogLevel;
        target.DefaultVolume = DefaultVolume;
    }
}
