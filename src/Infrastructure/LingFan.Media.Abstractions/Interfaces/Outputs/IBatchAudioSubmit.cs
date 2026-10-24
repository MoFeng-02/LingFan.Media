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
    /// 只有<b>背压超时</b>（<see cref="TimeoutException"/>：缓冲区长时间无法腾空）才降级为「丢弃该帧、继续后续帧」；
    /// 其余异常（声道数/采样率/格式不匹配等参数类错误）必须原样冒泡、中断该批——静默吞掉会把配置错误
    /// 变成持续丢帧或杂音，且调用方永远看不到根因。
    /// <b>取消不属于上述范畴</b>：仅当存在可取消重载、且由 <see cref="CancellationToken"/> 触发的中止
    /// （<see cref="OperationCanceledException"/>，Stop/Dispose 的正常退出路径）时，实现应放弃本批剩余帧
    /// 并尽快返回，<b>不得</b>当作错误冒泡；调用方收到正常返回即表示本批已被放弃，按正常退出处理，
    /// 不得据此判定失败或记 fail 日志。
    /// 不接管帧所有权，调用方负责提交后释放帧。
    /// </summary>
    /// <param name="frames">待提交音频帧集合（可能含 null，实现需跳过）。</param>
    void SubmitBatch(IEnumerable<AudioFrame> frames);

    /// <summary>
    /// 批量提交音频帧（可感知取消令牌）。语义同 <see cref="SubmitBatch(IEnumerable{AudioFrame})"/>，
    /// 但当 <paramref name="ct"/> 触发取消时，实现应尽快放弃阻塞等待（背压/渲染线程握手）并返回，
    /// 使调用方（音频管线）能在 Stop/Dispose 时立即退出，避免退出挂起。
    /// </summary>
    /// <remarks>
    /// <para>取消触发后实现应<b>尽快正常返回</b>（内部吞掉 <see cref="OperationCanceledException"/>），
    /// 不向调用方抛异常：未写入的帧（含已入队未确认的帧）由调用方按「本批已放弃」统一处理，
    /// 正常返回<b>不</b>携带「本批已全部写入」的保证。参数类错误仍按
    /// <see cref="SubmitBatch(IEnumerable{AudioFrame})"/> 的规则原样冒泡。</para>
    /// </remarks>
    /// <param name="frames">待提交音频帧集合（可能含 null，实现需跳过）。</param>
    /// <param name="ct">取消令牌（关闭/停止时触发）。</param>
    void SubmitBatch(IEnumerable<AudioFrame> frames, CancellationToken ct);
}
