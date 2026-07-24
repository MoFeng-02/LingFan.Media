namespace LingFan.Media.Abstractions;

/// <summary>
/// 帧释放接口。VideoFrame / AudioFrame 实现此接口。
/// </summary>
/// <remarks>
/// 帧所有权转移语义：Decoder → FrameQueue → Renderer，任何时刻只有一个组件持有所有权。
/// 丢帧必须 Dispose，否则 GPU 资源泄漏。
/// </remarks>
public interface IDisposableFrame
{
    /// <summary>是否已释放。</summary>
    bool IsDisposed { get; }

    /// <summary>释放帧资源（级联释放 IFrameResource）。</summary>
    void Dispose();
}
