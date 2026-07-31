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

    /// <summary>缓冲时长。默认 100ms。</summary>
    /// <remarks>缓冲越大越稳定但延迟越高。共享模式建议 100ms（兼顾稳定与 A/V 同步），独占模式可低至 10ms。</remarks>
    public TimeSpan BufferDuration { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>是否使用事件驱动模式。默认 true（V2）。</summary>
    /// <remarks>
    /// <para>V2 新增。事件驱动模式下 WASAPI 通过内核事件通知缓冲区可写，替代 V1 的 Thread.Sleep 轮询。</para>
    /// <para>优势：降低延迟（事件触发即唤醒 vs 轮询间隔）、减少 CPU 占用（无空转轮询）。</para>
    /// <para>事件驱动为 sync 原生边界（EventWaitHandle.WaitOne），与 V1 轮询同属 COM 背压机制，非伪异步。</para>
    /// <para>V1 行为（轮询）可通过设为 false 恢复。</para>
    /// </remarks>
    public bool EventDrivenMode { get; set; } = true;

    /// <summary>期望的采样格式。null = 自动检测设备原生格式（推荐）。</summary>
    /// <remarks>
    /// <para>V2 新增。控制 WASAPI 设备初始化时使用的采样格式。</para>
    /// <para>null（默认）：共享模式使用 GetMixFormat 获取设备原生格式；独占模式优先尝试 F32。</para>
    /// <para>指定格式：尝试以指定格式初始化设备，若不支持则回退到设备原生格式。</para>
    /// <para>当帧格式与设备格式匹配时，Submit 零转换直拷（O9 多格式直出）。</para>
    /// </remarks>
    public SampleFormat? PreferredSampleFormat { get; set; } = null;

    /// <summary>期望的采样率（Hz）。默认 44100。</summary>
    /// <remarks>实际采样率由 <see cref="WasapiOutput.Initialize"/> 设置。</remarks>
    public int SampleRate { get; set; } = 44100;

    /// <summary>期望的声道数。默认 2（立体声）。</summary>
    /// <remarks>实际声道数由 <see cref="WasapiOutput.Initialize"/> 设置。</remarks>
    public int Channels { get; set; } = 2;
}
