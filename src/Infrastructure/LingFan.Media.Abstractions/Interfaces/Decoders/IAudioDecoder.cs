namespace LingFan.Media.Abstractions;

/// <summary>
/// 音频解码器接口。
/// </summary>
/// <remarks>单线程使用（管线线程），非线程安全。热路径方法无 CancellationToken。</remarks>
public interface IAudioDecoder : IMediaComponent
{
    /// <summary>参数化配置：查找编解码器并打开。</summary>
    void Initialize(AudioCodec codec, AudioSettings settings);

    /// <summary>解码一个数据包。无 CancellationToken（热路径）。</summary>
    ValueTask<AudioFrame?> DecodeAsync(MediaPacket packet);

    /// <summary>刷新内部缓冲，取出剩余帧。</summary>
    ValueTask<AudioFrame?> FlushAsync();

    /// <summary>重置解码器状态（Seek 后调用）。</summary>
    void Reset();

    /// <summary>当前编解码器。</summary>
    AudioCodec Codec { get; }
}
