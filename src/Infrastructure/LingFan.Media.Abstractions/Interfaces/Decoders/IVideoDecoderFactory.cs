namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频解码器工厂接口。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂，无状态。每次 Create() 返回新实例。</para>
/// <para>优先使用 <see cref="CreateAsync"/>（硬解 GPU 设备初始化可能 I/O，支持 CT）。</para>
/// </remarks>
public interface IVideoDecoderFactory
{
    /// <summary>根据编解码器和设置创建解码器实例。</summary>
    IVideoDecoder Create(VideoCodec codec, VideoSettings settings);

    /// <summary>异步根据编解码器和设置创建解码器实例。</summary>
    /// <param name="codec">视频编解码器。</param>
    /// <param name="settings">视频设置。</param>
    /// <param name="ct">取消令牌。</param>
    Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default);
}
