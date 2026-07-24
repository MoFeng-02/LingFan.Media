namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频解码器工厂接口。
/// </summary>
/// <remarks>Singleton 工厂，无状态。每次 Create() 返回新实例。</remarks>
public interface IAudioDecoderFactory
{
    /// <summary>根据编解码器和设置创建解码器实例。</summary>
    IAudioDecoder Create(AudioCodec codec, AudioSettings settings);
}
