namespace LingFan.Media.Abstractions;

/// <summary>
/// 帧对象池接口。供解码器等组件复用帧实例，降低分配与 GC 压力。
/// </summary>
/// <typeparam name="T">帧类型。</typeparam>
public interface IFramePool<T> where T : class
{
    /// <summary>租用一个帧实例。</summary>
    T Rent();

    /// <summary>归还帧实例到池中。</summary>
    void Return(T frame);
}
