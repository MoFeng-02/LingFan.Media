namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频解码器工厂接口。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态。每次 Create() 返回新实例。</para>
/// <para>优先使用 <see cref="CreateAsync"/>（对称一致性，支持 CT）。</para>
/// </remarks>
public interface IAudioDecoderFactory
{
    /// <summary>根据编解码器和设置创建解码器实例。</summary>
    IAudioDecoder Create(AudioCodec codec, AudioSettings settings);

    /// <summary>异步根据编解码器和设置创建解码器实例。</summary>
    /// <param name="codec">音频编解码器。</param>
    /// <param name="settings">音频设置。</param>
    /// <param name="ct">取消令牌。</param>
    Task<IAudioDecoder> CreateAsync(AudioCodec codec, AudioSettings settings, CancellationToken ct = default);
}
