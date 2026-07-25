namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// <see cref="ISubtitleDecoderFactory"/> 的 FFmpeg 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例。</para>
/// <para>工厂按 <see cref="MediaTrack.SubtitleCodec"/> 预置解码器，解码器无需参数化 Initialize。</para>
/// <para><b>异步策略</b>（与 Video/Audio 工厂对称）：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + <see cref="FFmpegSubtitleDecoder.BindStream"/>。</item>
/// <item><see cref="CreateAsync"/>：接口契约，V1 无 I/O，返回 <see cref="Task.FromResult"/>。</item>
/// </list>
/// </remarks>
public sealed class FFmpegSubtitleDecoderFactory : ISubtitleDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="FFmpegSubtitleDecoderFactory"/> 的新实例。
    /// </summary>
    public FFmpegSubtitleDecoderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public ISubtitleDecoder Create(MediaTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var decoder = new FFmpegSubtitleDecoder(_loggerFactory.CreateLogger<FFmpegSubtitleDecoder>());
        decoder.BindStream(track);
        return decoder;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：V1 无 I/O（手动 new + BindStream），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT，对称一致性 + 未来网络字幕加载 I/O）。
    /// </remarks>
    public Task<ISubtitleDecoder> CreateAsync(MediaTrack track, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ISubtitleDecoder>(Create(track));
    }
}
