namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频帧消费者（Sink）。所有视频末端——无头计算、Skia 软渲染、D3D11 零拷贝 GPU 呈现——均实现本接口，
/// 经统一 <see cref="IFrameChannel"/> 订阅消费。消除"有头 vs 无头"两条独立路径：它们同饮一条通道，
/// 差异仅在终端动作（上屏 / 喂算法）与能力（能否消费 GPU 纹理帧）。
/// </summary>
public interface IFrameSink
{
    /// <summary>消费一帧。管线线程同步调用；Sink 负责帧的所有权释放（归还对象池），且不应长时间阻塞。</summary>
    void OnFrame(VideoFrame frame);
}

/// <summary>
/// 统一视频帧投递通道：解码产出 → 通道 → 扇出至所有订阅的 Sink。
/// 有头与无头共用此通道；零拷贝是 Sink 能力差异（GPU 纹理帧 vs CPU 帧），而非独立分支。
/// </summary>
/// <remarks>
/// 公开事件 <c>IMediaPlayer.VideoFrameAvailable</c> 即本通道的 <see cref="Action{VideoFrame}"/> 适配外观；
/// 高级消费者（录制、缩略图等）可直接实现 <see cref="IFrameSink"/> 经本通道订阅，无需委托。
/// </remarks>
public interface IFrameChannel
{
    /// <summary>订阅一个 Sink，返回取消订阅的句柄（Dispose 即退订）。</summary>
    IDisposable Subscribe(IFrameSink sink);

    /// <summary>向所有订阅者投递一帧。</summary>
    void Emit(VideoFrame frame);
}
