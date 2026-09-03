using System;
using System.Collections.Concurrent;

namespace LingFan.Media.Core;

/// <summary>
/// 帧对象池。复用帧实例减少 GC 压力。
/// </summary>
/// <remarks>
/// <para>线程安全：使用 <see cref="ConcurrentStack{T}"/> 天然线程安全。</para>
/// <para>生命周期：Session 级（每个 <see cref="MediaPlayer"/> 拥有独立池）。</para>
/// <para>工作流程：</para>
/// <list type="number">
/// <item><see cref="Rent"/>：从池中弹出一个帧壳（或通过工厂创建新壳）</item>
/// <item>解码器调用 <c>VideoFrame.Reset(...)</c> 填充帧数据</item>
/// <item>管线消费帧（Present/Submit）</item>
/// <item><see cref="Return"/>：重置帧状态（释放 Resource）后推回池中</item>
/// </list>
/// <para><b>异步策略</b>：sync（纯内存操作，无 I/O）。<see cref="Rent"/> 和 <see cref="Return"/> 均为同步。</para>
/// <para><b>AOT 兼容</b>：sealed 类，泛型 + ConcurrentStack，无反射。</para>
/// </remarks>
/// <typeparam name="T">帧类型（VideoFrame / AudioFrame）。</typeparam>
public sealed class FramePool<T> : IFramePool<T>, IDisposable where T : class
{
    private readonly ConcurrentStack<T> _pool = new();
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private readonly int _maxSize;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="FramePool{T}"/> 的新实例。
    /// </summary>
    /// <param name="factory">创建新帧的工厂（池为空时调用）。</param>
    /// <param name="reset">重置帧状态的回调（Return 时调用，释放 Resource 等）。可为 null。</param>
    /// <param name="maxSize">最大池大小，防内存泄漏（默认 16）。</param>
    public FramePool(Func<T> factory, Action<T>? reset = null, int maxSize = 16)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _reset = reset;
        _maxSize = maxSize > 0 ? maxSize : 16;
    }

    /// <summary>
    /// 租用一个帧实例。池中有可用帧时弹出，否则通过工厂创建。
    /// </summary>
    /// <returns>帧实例（需调用方通过 Reset 填充数据）。</returns>
    public T Rent()
    {
        if (_pool.TryPop(out var frame))
            return frame;
        return _factory();
    }

    /// <summary>
    /// 归还帧实例到池中。池满时 Dispose 帧而非入池。
    /// </summary>
    /// <param name="frame">要归还的帧。</param>
    public void Return(T frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed)
        {
            System.Threading.Interlocked.Increment(ref _retDisposedBranch);
            if (frame is IDisposable d) d.Dispose();
            return;
        }

        if (_pool.Count >= _maxSize)
        {
            // 池满，释放帧
            System.Threading.Interlocked.Increment(ref _retFullBranch);
            if (frame is IDisposable d) d.Dispose();
            return;
        }

        // 泄漏对账（诊断期）：确认 reset 被调且 Resource 类型符合预期。
        if (frame is VideoFrame vf && vf.Resource != null)
            Console.WriteLine($"[POOL-RET] resource={vf.Resource.GetType().Name}");
        System.Threading.Interlocked.Increment(ref _retEnqueueBranch);
        _reset?.Invoke(frame);
        _pool.Push(frame);
    }

    // 泄漏对账（诊断期）：三分支命中计数（disposed/满池/入池）。
    internal static long _retDisposedBranch, _retFullBranch, _retEnqueueBranch;

    /// <summary>
    /// 释放池中所有帧。池 Dispose 后 Return 的帧也会被 Dispose。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_pool.TryPop(out var frame))
        {
            if (frame is IDisposable d) d.Dispose();
        }
    }
}
