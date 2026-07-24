namespace LingFan.Media.Core;

/// <summary>
/// 帧对象池。V2 预留，V1 直接 new + Dispose。
/// </summary>
/// <remarks>
/// <para>当前为 V1 占位实现——直接 new 实例，不池化。</para>
/// <para>V2 可改为 ArrayPool/MemoryPool 复用帧底层 buffer，减少 GC 压力。</para>
/// </remarks>
/// <typeparam name="T">帧类型。</typeparam>
public sealed class FramePool<T> : IFramePool<T> where T : class
{
    /// <inheritdoc />
    public T Rent()
    {
        // V1: 直接 new（由调用方通过工厂创建）
        // V2: 从池中租用
        throw new NotSupportedException(
            "V1 直接 new 帧实例，不使用对象池。V2 将实现池化。");
    }

    /// <inheritdoc />
    public void Return(T frame)
    {
        // V1: 直接 Dispose（如果帧实现了 IDisposable）
        if (frame is IDisposable disposable)
        {
            disposable.Dispose();
        }
        // V2: 归还到池中
    }
}
