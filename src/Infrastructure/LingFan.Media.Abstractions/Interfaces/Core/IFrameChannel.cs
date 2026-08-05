namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频帧消费者（Sink）。所有视频末端——无头计算、Skia 软渲染、D3D11 零拷贝 GPU 呈现——均实现本接口，
/// 经统一 <see cref="IFrameChannel"/> 订阅消费。消除"有头 vs 无头"两条独立路径：它们同饮一条通道，
/// 差异仅在终端动作（上屏 / 喂算法）与能力（能否消费 GPU 纹理帧）。
/// </summary>
public interface IFrameSink
{
    /// <summary>
    /// 消费一帧。管线线程同步调用。
    /// <b>只读借用契约</b>：Sink 可在本次调用内读取/呈现帧，但<b>不得 Dispose</b>（所有权归管线，
    /// 投递后由管线 <c>ReturnFrame</c> 统一释放），也<b>不得在调用返回后持有帧引用</b>
    /// （多播下后续订阅方会读到已释放帧，use-after-free）。
    /// </summary>
    void OnFrame(VideoFrame frame);
}

/// <summary>
/// 统一视频帧投递通道：解码产出 → 通道 → 扇出至所有订阅的 Sink。
/// 有头与无头共用此通道；零拷贝是 Sink 能力差异（GPU 纹理帧 vs CPU 帧），而非独立分支。
/// </summary>
/// <remarks>
/// 公开事件 <c>IMediaPlayer.VideoFrameAvailable</c> 即本通道的 <see cref="Action{VideoFrame}"/> 适配外观；
/// 高级消费者（录制、缩略图等）可直接实现 <see cref="IFrameSink"/> 经本通道订阅，无需委托。
/// <para><b>帧所有权</b>：<see cref="IFrameChannel.Emit"/> 由管线在 <c>try</c> 内调用，
/// 其后 <c>finally</c> 中由管线 <c>ReturnFrame</c> 释放帧——<b>通道与所有 Sink 均为只读借用，绝不 Dispose</b>。
/// 任一 Sink 在 <see cref="OnFrame"/> 内 Dispose 会在多播下破坏后续订阅方，属硬违例。</para>
/// </remarks>
public interface IFrameChannel
{
    /// <summary>订阅一个 Sink，返回取消订阅的句柄（Dispose 即退订）。</summary>
    IDisposable Subscribe(IFrameSink sink);

    /// <summary>退订一个 Sink（按引用/相等性匹配）。</summary>
    void Unsubscribe(IFrameSink sink);

    /// <summary>向所有订阅者投递一帧。</summary>
    void Emit(VideoFrame frame);
}
