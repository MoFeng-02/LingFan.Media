namespace LingFan.Media.Abstractions;

/// <summary>
/// 无头帧消费契约（后端数据处理）。<see cref="VideoFrame"/> 经此交给下游计算，不渲染、不显示。
/// 简单场景可直接用 <see cref="Action{T}"/> 订阅 <see cref="IMediaPlayer.VideoFrameAvailable"/>，无需本接口；
/// 本接口用于需要结构化生命周期 / 批量汇聚的场景（配合 <c>LingFan.Media.Consumers.ProcessingFrameSink</c>）。
/// </summary>
/// <remarks>
/// <para>零外部引用：仅依赖契约层中立类型（<see cref="VideoFrame"/>）。</para>
/// <para>只读借用模型：实现方在 <see cref="Consume"/> 中仅可读、可同步拷贝所需数据，
/// <b>不得 Dispose、不得跨线程持有传入的 <see cref="VideoFrame"/></b>——帧所有权归管线，回调返回后即释放。</para>
/// <para>对称生命周期：实现 <see cref="IDisposable"/> + <see cref="IAsyncDisposable"/>。</para>
/// <para>AOT 兼容：纯接口契约，无反射、无 P/Invoke。</para>
/// </remarks>
public interface IHeadlessFrameConsumer : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 消费一帧。视频管线在调用后释放该帧（只读借用）。
    /// 若需跨调用持有数据，须在方法内同步拷贝
    /// （CPU: <see cref="SoftwareFrameResource.Data"/> 的 <see cref="ReadOnlySpan{T}"/> → 自有 buffer；
    /// GPU: <see cref="IGpuTextureResource.ReadbackToCpu"/> 取托管快照交下游）。
    /// </summary>
    /// <param name="frame">当前帧（只读借用，回调返回即失效）。</param>
    void Consume(VideoFrame frame);
}
