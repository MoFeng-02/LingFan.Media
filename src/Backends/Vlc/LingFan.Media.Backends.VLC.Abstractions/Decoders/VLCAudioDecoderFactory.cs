namespace LingFan.Media.Backends.VLC.Abstractions.Decoders;

/// <summary>
/// <see cref="IAudioDecoderFactory"/> 的 VLC 实现（VLC 两后端共享）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para><b>异步策略</b>（与 VLCVideoDecoderFactory 对称）：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + <see cref="IAudioDecoder.Initialize"/>。</item>
/// <item><see cref="CreateAsync"/>：接口契约，返回 <see cref="Task.FromResult"/>。</item>
/// </list>
/// </remarks>
public sealed class VLCAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="VLCAudioDecoderFactory"/> 的新实例。
    /// </summary>
    public VLCAudioDecoderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IAudioDecoder Create(AudioCodec codec, AudioSettings settings)
    {
        var decoder = new VLCAudioDecoder(_loggerFactory.CreateLogger<VLCAudioDecoder>());
        decoder.Initialize(codec, settings);
        return decoder;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：无 I/O，返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT，对称一致性）。
    /// </remarks>
    public Task<IAudioDecoder> CreateAsync(AudioCodec codec, AudioSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IAudioDecoder>(Create(codec, settings));
    }
}
