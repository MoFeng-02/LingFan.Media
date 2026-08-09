namespace LingFan.Media.Video;

/// <summary>
/// 视频后处理器接口。对 <see cref="VideoFrame"/> 执行单帧变换。
/// </summary>
/// <remarks>
/// <para><b>所有权转移语义</b>：<see cref="Process"/> 接收输入帧的所有权，
/// 调用后输入帧被 Dispose（释放 GPU/CPU 资源），返回新的 <see cref="VideoFrame"/>。</para>
/// <para>当 <see cref="IsEnabled"/> 为 false 时，<see cref="Process"/> 直接返回输入帧，
/// 不 Dispose、不创建新帧（透传）。</para>
/// <para>实现应为 sealed 类以保证 AOT 友好。</para>
/// </remarks>
public interface IVideoProcessor
{
    /// <summary>处理器名称。</summary>
    string Name { get; }

    /// <summary>是否启用。禁用时 <see cref="Process"/> 透传输入帧。</summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// 重置处理器内部状态（Seek/Flush 后调用）。
    /// </summary>
    /// <remarks>
    /// <para>所有权：有状态处理器（如 <see cref="FrameRateConverter"/> 持有的上一帧副本 _held）
    /// 须在此释放，避免 Seek 后返回陈旧帧或跨播放会话滞留。</para>
    /// <para>幂等、线程安全（由调用方在管线解码锁内调用）。</para>
    /// </remarks>
    void Reset();

    /// <summary>
    /// 处理一帧视频。
    /// </summary>
    /// <param name="frame">输入帧（所有权转移：处理后输入帧被 Dispose）。</param>
    /// <returns>处理后的新帧（或禁用时返回原帧）。</returns>
    /// <remarks>
    /// 所有权转移：输入 frame 被 Dispose，返回新 frame 传入下一个处理器。
    /// 当 <see cref="IsEnabled"/> 为 false 时直接返回输入帧（不 Dispose，不创建新帧）。
    /// </remarks>
    VideoFrame? Process(VideoFrame frame);
}
