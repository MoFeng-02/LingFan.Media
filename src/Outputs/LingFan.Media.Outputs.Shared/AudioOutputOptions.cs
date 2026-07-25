namespace LingFan.Media.Outputs.Shared;

/// <summary>
/// 通用音频输出选项。各平台输出可继承或参考此类扩展自己的选项。
/// </summary>
/// <remarks>
/// <para>纯数据配置类（config 分类），无 I/O。</para>
/// <para>WASAPI 使用 <see cref="WasapiOptions"/>，其他平台桩暂不使用。</para>
/// </remarks>
public sealed class AudioOutputOptions
{
    /// <summary>期望的采样率（Hz）。0 表示由音频流自动决定。</summary>
    public int SampleRate { get; set; }

    /// <summary>期望的声道数。0 表示由音频流自动决定。</summary>
    public int Channels { get; set; }

    /// <summary>缓冲时长。默认 50ms。</summary>
    public TimeSpan BufferDuration { get; set; } = TimeSpan.FromMilliseconds(50);
}
