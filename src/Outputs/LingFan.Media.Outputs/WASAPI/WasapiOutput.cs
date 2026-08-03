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
internal sealed class WasapiOutput : IAudioOutput, IBatchAudioSubmit, IAudioOutputWarmup
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

    /// <inheritdoc cref="IAudioOutputWarmup.WarmupAsync"/>
    /// <remarks>
    /// <para>一次性 throwaway 渲染循环：触发 OS 音频引擎（audiodg.exe）首次拉起。WASAPI 的
    /// <c>IAudioClient.Initialize</c> 首调用会冷启动音频子系统，产生 2~3s 一次性开销；预热后 Dispose 释放 COM
    /// （引擎在进程内保持热态），使正式 <c>OpenAsync</c> 中的 <c>IAudioClient.Initialize</c> 几乎瞬时。</para>
    /// <para>失败一律忽略：正式 <c>OpenAsync</c> 仍会完整初始化音频，预热只是把冷启动成本前移到 host 加载界面期。</para>
    /// </remarks>
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // WasapiRenderLoop 持有原始 Dispose()（未声明 IDisposable 接口），故显式在 finally 释放以免 STA 线程/COM 泄漏。
        WasapiRenderLoop? warm = null;
        try
        {
            warm = new WasapiRenderLoop(_options, _logger);
            await warm.InitializeAsync(ct).ConfigureAwait(false);
            warm.Initialize(44100, 2); // 默认格式即可触发引擎路径；AUTOCONVERTPCM 保证任何格式都被接受
        }
        catch
        {
            // 预热失败不影响后续播放：正式 OpenAsync 仍会完整初始化音频。
        }
        finally
        {
            warm?.Dispose();
        }
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
