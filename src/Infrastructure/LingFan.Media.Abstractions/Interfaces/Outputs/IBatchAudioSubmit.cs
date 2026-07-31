namespace LingFan.Media.Abstractions;

/// <summary>
/// 可选音频批量提交能力（性能优化特性接口）。
/// </summary>
/// <remarks>
/// <para>用于把多帧音频在「一次 STA 跨线程往返」内连续提交，消除逐帧 <c>RunOnSta</c> 阻塞往返带来的固定开销，
/// 从而让音频供给速率达到实时（修复听感卡顿/掉速）。</para>
/// <para>属于 <see cref="IAudioOutput"/> 的可选增强：实现者若支持批量提交则实现本接口，
/// <c>AudioPipeline</c> 通过 <c>is</c> 探测，不支持时退回逐帧 <see cref="IAudioOutput.Submit"/>（其他平台行为不变）。</para>
/// <para>契约层增补签名（未来基准：契约层可动态演进，只增不改优先）。零外部引用，仅依赖本命名空间类型。</para>
/// </remarks>
public interface IBatchAudioSubmit
{
    /// <summary>
    /// 批量提交音频帧。实现应在一个原生/STA 上下文内连续写入所有帧（含必要的背压等待），
    /// 而非为每帧各做一次跨线程往返。
    /// 单帧提交失败（如缓冲区超时）应仅丢弃该帧并继续后续帧，不得中断整批。
    /// 不接管帧所有权，调用方负责提交后释放帧。
    /// </summary>
    /// <param name="frames">待提交音频帧集合（可能含 null，实现需跳过）。</param>
    void SubmitBatch(IEnumerable<AudioFrame> frames);
}
