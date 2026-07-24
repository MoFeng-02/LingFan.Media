namespace LingFan.Media.Core;

/// <summary>
/// 播放器默认配置选项。
/// </summary>
/// <remarks>
/// <para>由 <see cref="MediaPlayerFactory"/> 读取，用于设置新创建播放器的默认值。</para>
/// <para>在 Extensions 层可通过 <c>IOptions&lt;MediaPlayerOptions&gt;</c> 绑定配置。</para>
/// </remarks>
public sealed class MediaPlayerOptions
{
    /// <summary>默认音量 (0.0~1.0)。</summary>
    public float DefaultVolume { get; set; } = 1.0f;

    /// <summary>默认是否静音。</summary>
    public bool DefaultMuted { get; set; }

    /// <summary>默认播放速率。</summary>
    public float DefaultPlaybackRate { get; set; } = 1.0f;

    /// <summary>是否启用硬件加速解码。</summary>
    public bool EnableHardwareAcceleration { get; set; } = true;

    /// <summary>视频帧队列容量。</summary>
    public int VideoFrameQueueCapacity { get; set; } = 30;

    /// <summary>音频帧队列容量。</summary>
    public int AudioSampleQueueCapacity { get; set; } = 60;

    /// <summary>缓冲目标时长（本地文件）。</summary>
    public TimeSpan LocalBufferTarget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>缓冲目标时长（网络流）。</summary>
    public TimeSpan NetworkBufferTarget { get; set; } = TimeSpan.FromSeconds(30);
}
