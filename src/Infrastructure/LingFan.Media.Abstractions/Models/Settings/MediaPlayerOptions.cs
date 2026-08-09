namespace LingFan.Media.Abstractions;

/// <summary>
/// 播放器默认配置选项（契约层共享配置模型）。
/// </summary>
/// <remarks>
/// <para>由 Core 的 MediaPlayerFactory 读取，用于设置新创建播放器的默认值；
/// 由宿主 DI 经 <c>IOptions&lt;MediaPlayerOptions&gt;</c> 绑定（Extensions 层 <c>AddLingFanMedia</c> 中注册）。</para>
/// <para>置于 Abstractions 契约层：该配置同时被 Extensions（配置侧）与 Core（消费侧）使用，属多层共享的中立数据模型，
/// 符合契约层“纯数据模型、零外部引用”准则（与 <see cref="AudioSettings"/> / <see cref="VideoSettings"/> 同列）。</para>
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

    /// <summary>音频输出目标采样率（可选覆盖）。null=使用源媒体采样率。</summary>
    /// <remarks>经 <see cref="AudioSettings"/> 透传至 FFmpegAudioDecoder 重采样。若设置，
    /// 解码器输出与 WASAPI 设备均按此率工作，避免节奏/音高错乱。</remarks>
    public int? AudioOutputSampleRate { get; set; }

    /// <summary>音频输出目标声道数（可选覆盖）。null=使用源媒体声道数。</summary>
    public int? AudioOutputChannels { get; set; }

    /// <summary>音频输出目标采样格式（可选覆盖）。null=使用源媒体采样格式。</summary>
    public SampleFormat? AudioOutputSampleFormat { get; set; }
}
