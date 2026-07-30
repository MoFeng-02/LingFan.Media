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

    // 无头实时节流锚点：以首帧时间戳为基准，使音频主时钟按真实节奏推进。
    private DateTime _anchorWall = default;
    private TimeSpan _anchorTs = default;
    private bool _anchored;

    /// <inheritdoc />
    public void Initialize(int sampleRate, int channels)
    {
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

        // 锚定首帧：记下来“首帧应在此时刻(wall)被消费”，其时间戳为锚点。
        // 后续帧的目标消费时刻 = 锚点 wall + (frame.Timestamp - 锚点 ts)，
        // 若尚未到时刻则阻塞至该时刻。这样管线线程被实时限速（1x），
        // 与真实 WASAPI 由硬件节奏限速语义一致，且不依赖 frame.Duration / frameCount
        // （MF 解封装器不设置 Duration、解码器可能给出压缩包大小，二者均不可靠）。
        if (!_anchored)
        {
            _anchored = true;
            _anchorWall = DateTime.UtcNow;
            _anchorTs = frame.Timestamp;
            return;
        }

        var target = _anchorWall + (frame.Timestamp - _anchorTs);
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
