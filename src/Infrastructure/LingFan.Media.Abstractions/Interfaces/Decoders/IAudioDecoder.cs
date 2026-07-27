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

    /// <summary>解码器实际输出采样率（源采样率，或重采样目标采样率）。</summary>
    /// <remarks>Initialize 后可用。MediaPlayer 据此初始化音频输出设备（WASAPI 以固定率开设备）。</remarks>
    int OutputSampleRate { get; }

    /// <summary>解码器实际输出声道数（源声道数，或重采样目标声道数）。</summary>
    int OutputChannels { get; }
}
