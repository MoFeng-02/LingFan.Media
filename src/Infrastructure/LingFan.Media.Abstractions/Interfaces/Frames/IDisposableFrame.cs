namespace LingFan.Media.Abstractions;

/// <summary>
/// 帧释放接口。VideoFrame / AudioFrame 实现此接口。
/// </summary>
/// <remarks>
/// 帧所有权转移语义：Decoder → FrameQueue → Renderer，任何时刻只有一个组件持有所有权。
/// 丢帧必须 Dispose，否则 GPU 资源泄漏。
/// 必须继承 <see cref="IDisposable"/>：FramePool 满池分支经 <c>is IDisposable</c> 判定后 Dispose 帧
/// （不继承时判定恒 false、帧静默泄漏——真机实证：AHB 帧 880 次满池归还被跳过、
/// Graphics 内存每遍播放 +51MB 直至进程耗尽）。
/// </remarks>
public interface IDisposableFrame : IDisposable
{
    /// <summary>是否已释放。</summary>
    bool IsDisposed { get; }

    // Dispose 由 IDisposable 继承提供（同签名重复声明会触发 CS0108）。
}
