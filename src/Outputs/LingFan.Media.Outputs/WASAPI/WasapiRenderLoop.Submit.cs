using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

// WasapiRenderLoop 的帧提交职责段（部分类，按职责拆分，编译期等价）。
// 单帧/批量提交（逐帧投递 + 等待渲染线程写入确认）；仅背压超时丢帧继续，参数错误冒泡（契约口径）。
// 不接管帧所有权；字段与控制编排在主文件。
internal sealed partial class WasapiRenderLoop
{
    /// <inheritdoc cref="WasapiOutput.Submit"/>
    /// <remarks>
    /// 语义保持：Submit 不接管帧所有权。将帧投递给渲染线程并阻塞等待其写入完成（COM 背压在渲染线程内），
    /// 故调用方在 Submit 返回后可安全归还帧；不跨越调用方线程做 COM 封送。
    /// </remarks>
    public void Submit(AudioFrame frame) => Submit(frame, CancellationToken.None);

    /// <summary>
    /// 提交单帧并阻塞等待渲染线程写入完成（可感知取消令牌）。
    /// </summary>
    public void Submit(AudioFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var item = new RenderItem(ItemKind.Frame, frame);
        _queue.Enqueue(item);
        _workAvailable.Set();
        item.Done.Wait(ct);
        if (item.Exception is not null)
            ExceptionDispatchInfo.Throw(item.Exception);
    }

    /// <summary>
    /// 批量提交：把多帧音频投递给渲染线程（在渲染线程内连续写入，消除逐帧跨线程往返开销）。
    /// 仅<b>背压超时</b>（<see cref="TimeoutException"/>）降级为丢弃该帧并继续后续帧；
    /// 参数类错误（声道/尺寸不匹配等 <see cref="ArgumentException"/>）原样冒泡、中断该批（契约要求，不得吞掉）。
    /// 不接管帧所有权。
    /// </summary>
    public void SubmitBatch(IEnumerable<AudioFrame> frames) => SubmitBatch(frames, CancellationToken.None);

    /// <summary>
    /// 批量提交（可感知取消令牌）。语义同 <see cref="SubmitBatch(IEnumerable{AudioFrame})"/>，
    /// 但 <paramref name="ct"/> 触发取消时立即放弃对渲染线程的阻塞等待，使调用方（音频管线）在 Stop/Dispose 时快速退出。
    /// </summary>
    public void SubmitBatch(IEnumerable<AudioFrame> frames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frames);

        // Phase 1：逐帧投递 + 等待（保持阻塞语义与异常可观测性）。Phase 2 将改由 AudioSampleRing 非阻塞接管所有权。
        var pending = new List<RenderItem>();
        foreach (var frame in frames)
        {
            if (frame is null) continue;
            var item = new RenderItem(ItemKind.Frame, frame);
            _queue.Enqueue(item);
            _workAvailable.Set();
            pending.Add(item);
        }

        foreach (var item in pending)
        {
            try
            {
                item.Done.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                // 取消（Stop/Dispose）是正常退出路径：立即放弃剩余帧提交，使音频管线快速退出，
                // 不再冒泡成 fail 日志。
                break;
            }
            if (item.Exception is TimeoutException tex)
            {
                // 背压超时（缓冲区暂无可写空间）：仅丢弃该帧并继续后续帧。
                // 声道/尺寸不匹配等 ArgumentException 不在此吞掉，让其冒泡以暴露真实管线 bug。
                _logger.LogWarning("WASAPI 批量提交跳过单帧（背压超时）：{Msg}", tex.Message);
            }
            else if (item.Exception is not null)
            {
                ExceptionDispatchInfo.Throw(item.Exception);
            }
        }
    }
}
