namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 音频输出配置选项。
/// </summary>
/// <remarks>
/// <para>纯数据配置类（config 分类），无 I/O。</para>
/// <para>由 <see cref="WasapiExtensions.AddWasapiOutput"/> 的 configure 委托设置。</para>
/// </remarks>
public sealed class WasapiOptions
{
    /// <summary>是否使用独占模式。默认 false（共享模式）。</summary>
    /// <remarks>
    /// 共享模式与其他应用共享音频设备，兼容性好但延迟略高。
    /// 独占模式独占设备，延迟最低但其他应用无法播放声音。
    /// V1 推荐使用共享模式。
    /// </remarks>
    public bool ExclusiveMode { get; set; } = false;

    /// <summary>缓冲时长。默认 50ms。</summary>
    /// <remarks>缓冲越大越稳定但延迟越高。共享模式建议 50ms，独占模式可低至 10ms。</remarks>
    public TimeSpan BufferDuration { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>期望的采样率（Hz）。默认 44100。</summary>
    /// <remarks>实际采样率由 <see cref="WasapiOutput.Initialize"/> 设置。</remarks>
    public int SampleRate { get; set; } = 44100;

    /// <summary>期望的声道数。默认 2（立体声）。</summary>
    /// <remarks>实际声道数由 <see cref="WasapiOutput.Initialize"/> 设置。</remarks>
    public int Channels { get; set; } = 2;
}
