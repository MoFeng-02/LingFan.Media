using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 音频输出（公开适配层）。Phase 1 起，全部 WASAPI COM 逻辑已提取至 <see cref="WasapiRenderLoop"/>：
/// 本类仅实现 <see cref="IAudioOutput"/> / <see cref="IBatchAudioSubmit"/> 契约，并把每个调用转发给内部的
/// <see cref="_loop"/>（常驻 STA 渲染线程）。公开 API 签名、行为与线程语义与 v1 完全一致。
/// </summary>
/// <remarks>
/// <para><b>为什么要这层适配</b>：保留 <c>IAudioOutput</c> / <c>IBatchAudioSubmit</c> 公共契约不变（Phase 1 不删接口），
/// 同时把易出 AV 的 WASAPI COM 细节隔离在 <see cref="WasapiRenderLoop"/> 内。后续 Phase 2 引入
/// <c>AudioSampleRing</c> 时，只改 <see cref="WasapiRenderLoop"/> 内部消费模型，本适配层签名不变。</para>
/// <para><b>异步策略 / 线程安全 / AOT / 资源所有权 / Submit 所有权</b> 等契约语义全部由 <see cref="WasapiRenderLoop"/> 承担，
/// 见其 XML 文档。本类不持有任何 COM 状态。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiOutput : IAudioOutput, IBatchAudioSubmit
{
    private readonly WasapiOptions _options;
    private readonly ILogger<WasapiOutput> _logger;
    private readonly WasapiRenderLoop _loop;

    /// <summary>
    /// 初始化 <see cref="WasapiOutput"/> 的新实例。
    /// </summary>
    /// <param name="options">WASAPI 配置选项。</param>
    /// <param name="logger">日志器。</param>
    internal WasapiOutput(WasapiOptions options, ILogger<WasapiOutput> logger)
    {
        _options = options;
        _logger = logger;
        _loop = new WasapiRenderLoop(options, logger);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default) => _loop.InitializeAsync(ct);

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels) => _loop.Initialize(sampleRate, channels);

    /// <inheritdoc/>
    public void Submit(AudioFrame frame) => _loop.Submit(frame);

    /// <inheritdoc/>
    public void SubmitBatch(IEnumerable<AudioFrame> frames) => _loop.SubmitBatch(frames);

    /// <inheritdoc cref="IBatchAudioSubmit.SubmitBatch(IEnumerable{AudioFrame},CancellationToken)"/>
    public void SubmitBatch(IEnumerable<AudioFrame> frames, CancellationToken ct) => _loop.SubmitBatch(frames, ct);

    /// <inheritdoc/>
    public void Pause() => _loop.Pause();

    /// <inheritdoc/>
    public void Resume() => _loop.Resume();

    /// <inheritdoc/>
    public void Flush() => _loop.Flush();

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPosition() => _loop.GetPlaybackPosition();

    /// <inheritdoc/>
    public TimeSpan GetPlaybackPositionDirect() => _loop.GetPlaybackPositionDirect();

    /// <inheritdoc/>
    public TimeSpan Latency => _loop.Latency;

    /// <inheritdoc/>
    public float Volume
    {
        get => _loop.Volume;
        set => _loop.Volume = value;
    }

    /// <inheritdoc/>
    public int BufferSize => _loop.BufferSize;

    /// <inheritdoc/>
    public bool ExclusiveMode => _loop.ExclusiveMode;

    /// <inheritdoc/>
    public bool EventDrivenMode => _loop.EventDrivenMode;

    /// <inheritdoc/>
    public SampleFormat DeviceSampleFormat => _loop.DeviceSampleFormat;

    /// <inheritdoc/>
    public void Dispose() => _loop.Dispose();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _loop.DisposeAsync();
}
