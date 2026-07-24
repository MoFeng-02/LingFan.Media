namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频解码器接口。
/// </summary>
/// <remarks>
/// <para>线程安全：单线程使用（管线线程），非线程安全。</para>
/// <para>热路径方法（DecodeAsync/FlushAsync）无 CancellationToken。</para>
/// </remarks>
public interface IVideoDecoder : IMediaComponent
{
    /// <summary>参数化配置：查找编解码器并打开。非生命周期方法。</summary>
    void Initialize(VideoCodec codec, VideoSettings settings);

    /// <summary>
    /// 解码一个数据包。无 CancellationToken（热路径）。
    /// 返回 null 表示需要更多数据（如 B 帧延迟）。
    /// </summary>
    ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet);

    /// <summary>刷新内部缓冲，取出剩余帧。</summary>
    ValueTask<VideoFrame?> FlushAsync();

    /// <summary>重置解码器状态（Seek 后调用）。</summary>
    void Reset();

    /// <summary>当前编解码器。</summary>
    VideoCodec Codec { get; }

    /// <summary>是否使用硬件加速。</summary>
    bool IsHardwareAccelerated { get; }
}
