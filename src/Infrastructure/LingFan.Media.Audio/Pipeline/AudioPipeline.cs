namespace LingFan.Media.Audio;

/// <summary>
/// 音频管线**配置**。管理效果链、音频设置和混音器设置。
/// </summary>
/// <remarks>
/// <para>本类仅为<b>配置</b>，不持有解码/输出执行逻辑；实际执行由
/// <c>Core/Playback/AudioPipeline.cs</c> 完成。</para>
/// <para>使用 <see cref="BuildConfig"/> 生成不可变配置快照
/// (<see cref="AudioPipelineConfig"/>)，供 MediaPlayer 创建 Core 执行器时消费。</para>
/// <para>非线程安全（配置在播放启动前设置，运行时不可修改）。</para>
/// </remarks>
public sealed class AudioPipeline
{
    private readonly List<IAudioEffect> _effects = [];
    private AudioSettings _settings = new();

    /// <summary>效果链（只读视图）。</summary>
    public IReadOnlyList<IAudioEffect> Effects => _effects;

    /// <summary>音频解码与输出设置。</summary>
    public AudioSettings Settings => _settings;

    /// <summary>混音器设置（null 表示不使用混音器）。</summary>
    public MixerSettings? MixerSettings { get; set; }

    /// <summary>
    /// 添加音频效果到链末尾。
    /// </summary>
    /// <param name="effect">音频效果。</param>
    /// <exception cref="ArgumentNullException">effect 为 null。</exception>
    public void AddEffect(IAudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        _effects.Add(effect);
    }

    /// <summary>
    /// 从链中移除效果。
    /// </summary>
    /// <param name="effect">要移除的效果。</param>
    public void RemoveEffect(IAudioEffect effect)
    {
        _effects.Remove(effect);
    }

    /// <summary>
    /// 应用管线配置。
    /// </summary>
    /// <param name="settings">音频设置。</param>
    /// <exception cref="ArgumentNullException">settings 为 null。</exception>
    public void Configure(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// 生成供 Core <c>AudioPipeline</c> 执行器使用的不可变配置快照。
    /// </summary>
    /// <returns>配置快照。</returns>
    public AudioPipelineConfig BuildConfig()
    {
        return new AudioPipelineConfig
        {
            OutputSampleRate = _settings.OutputSampleRate,
            OutputChannels = _settings.OutputChannels,
            OutputSampleFormat = _settings.OutputSampleFormat,
            Effects = _effects.AsReadOnly(),
            MixerSettings = MixerSettings,
        };
    }
}
