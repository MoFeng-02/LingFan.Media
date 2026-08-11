using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Consumers;

/// <summary>
/// 无头 / 服务端场景的空音频输出：不打开音频设备、不初始化 WASAPI、<see cref="Submit"/> 为 no-op。
/// 供无 <c>VideoView</c> / 无音频设备场景替代具体音频输出，使 <see cref="MediaPlayer"/> 在无头下正常初始化与运行
/// （C-9.4 对称件：<see cref="NoOpVideoRenderer"/> 解决“无 GPU 设备”，本类解决“无音频设备”）。
/// </summary>
/// <remarks>
/// <para>无头 A 形态下，视频帧经 <see cref="IMediaPlayer.VideoFrameAvailable"/> 流向计算 sink，
/// 音频帧被本输出静默丢弃——完全不依赖音频硬件，可在无音频端点 / CI 环境运行。</para>
/// <para>实现 <see cref="IAudioOutput"/>（: <see cref="IMediaComponent"/> = <see cref="IDisposable"/> + <see cref="IAsyncDisposable"/>），生命周期闭环无原生资源。</para>
/// <para>AOT 兼容：<see langword="sealed"/>、无反射、无 P/Invoke。</para>
/// </remarks>
public sealed class NoOpAudioOutput : IAudioOutput, IRealtimePacedOutput
{
    private float _volume = 1.0f;
    private bool _paceRealTime = true;

    // 无头实时节流锚点：以「首帧提交时刻(wall)」为基准，按「已提交采样数 / 采样率」推算真实播放进度。
    // 不依赖帧 Timestamp/Duration（MF 解封装/解码器给出的时间戳可能不可靠，全 0 或基于压缩包），
    // 否则 delay<=0 不 sleep → 音频瞬间提交完 → SyncTo 把主时钟瞬间拉到片尾 → Position 立即到顶、管线瞬间 EOF。
    private DateTime _anchorWall = default;
    private bool _anchored;
    private long _submittedSamples;
    private int _sampleRate;

    /// <inheritdoc />
    public void Initialize(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _submittedSamples = 0;
        _anchored = false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 模拟真实音频输出的实时背压：以首帧时间戳为锚点按真实节奏阻塞提交，使音频管线
    /// （单线程 decode→submit 循环）被实时限速，主时钟随真实时间推进而非瞬间跑完。
    /// 主时钟由音频管线 <c>OnAudioFrameSubmitted</c> 驱动（基准 = 帧结束时间戳）；
    /// 若无背压，无头下音频会瞬间提交完、时钟飙到片尾，视频帧全被判“落后→Drop”，sink 收不到帧。
    /// 阻塞语义与 <see cref="IAudioOutput"/> 契约一致（缓冲满时同步阻塞背压是正常机制，非伪异步）。
    /// </remarks>
    public void Submit(AudioFrame frame)
    {
        // 最快模式：不实时节流，瞬时返回，尽快处理完（转码 / 离线 ML）。
        if (!_paceRealTime)
            return;

        // 兜底：若初始化时采样率未就绪（如 AAC 在 avcodec_open2 后 ctx->sample_rate 仍为 0，
        // 解码器 OutputSampleRate 透传为 0），改用帧自带的采样率（解码后 avFrame->sample_rate 恒正确）
        // 做实时背压，否则 played 恒为 0 → 不 sleep → 音频瞬间提交完 → SyncTo 把主时钟飙到片尾。
        if (_sampleRate <= 0 && frame.SampleRate > 0)
            _sampleRate = frame.SampleRate;

        // 锚定首帧：记录“首帧在此 wall 时刻开始消费”，后续以「累计提交采样数 / 采样率」推算真实播放进度。
        // 关键：MF 解封装/解码器给出的帧 Timestamp/Duration 可能不可靠（全 0 或基于压缩包大小），
        // 若以其为锚点则 delay 恒 <=0 → 不 sleep → 音频瞬间提交完 → Synchronizer 用 SyncTo 把主时钟
        // 瞬间拉到片尾 → Position 立即到顶、管线立即 EOF（表现为“几秒播完 21 秒视频”的假完成）。
        // 改用「累计采样 / 采样率」与采样率强绑定，与帧时间戳无关，实时节奏恒定可靠，
        // 与真实 WASAPI 由硬件节奏限速语义一致。
        if (!_anchored)
        {
            _anchored = true;
            _anchorWall = DateTime.UtcNow;
            _submittedSamples = 0;
            return;
        }

        _submittedSamples += frame.FrameCount;
        var played = _sampleRate > 0 ? (double)_submittedSamples / _sampleRate : 0d;
        var target = _anchorWall + TimeSpan.FromSeconds(played);
        var delay = target - DateTime.UtcNow;
        if (delay > TimeSpan.Zero)
            Thread.Sleep(delay);
    }

    /// <inheritdoc />
    public void Pause() { }

    /// <inheritdoc />
    public void Resume() { }

    /// <inheritdoc />
    public void Flush()
    {
        _anchored = false;
        _submittedSamples = 0;
    }

    /// <inheritdoc />
    public TimeSpan GetPlaybackPosition() => TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Latency => TimeSpan.Zero;

    /// <inheritdoc />
    public float Volume
    {
        get => _volume;
        set => _volume = value;
    }

    /// <inheritdoc />
    public bool PaceRealTime
    {
        set => _paceRealTime = value;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
