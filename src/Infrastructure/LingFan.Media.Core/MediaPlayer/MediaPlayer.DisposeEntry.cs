using Microsoft.Extensions.Logging;
using LingFan.Media.Core.Platform;
namespace LingFan.Media.Core;

/// <summary>
/// MediaPlayer 的释放入口职责段（部分类，按职责拆分，编译期等价）。
/// DisposeAsync 分步释放编排与同步 Dispose 兜底；Step_* 各步实现在 Dispose 主段。
/// </summary>
public sealed partial class MediaPlayer
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _positionTimer?.Dispose();

        // 分步释放（下方步骤 1~13），每步独立 try-catch 不中断。
        // 逐步耗时观测：退出缓慢（秒级）问题的定位手段——静默步骤（5~11）的耗时从此可见，
        // 任一步骤 >300ms 立即告警点名，收尾输出全明细（与 [SYNC]/[FREEZE] 诊断风格一致）。
        long disposeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var stepTimings = new List<string>(13);

        void LogStepTiming(string step, long startQpc)
        {
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(startQpc).TotalMilliseconds;
            stepTimings.Add($"{step}={ms:F0}ms");
            Console.Error.WriteLine($"[DISPOSE] ← {step} 完成 {ms:F0}ms");
            if (ms > 300)
                _logger.LogWarning(
                    "[DISPOSE] 步骤 {Step} 耗时 {Ms:F0}ms（>300ms，退出缓慢的定位线索）", step, ms);
        }

        // stderr 直写（不经日志队列）：步骤内若发生原生死亡，最后一条"→ 进入"行即崩溃步骤。
        void StepEnter(string step) =>
            Console.Error.WriteLine($"[DISPOSE] → {step}");

        // 1. 停管线线程 (cts.Cancel + join 5s 超时)
        long stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("停管线");
        await Step_StopPipelinesAsync();
        LogStepTiming("停管线", stepQpc);

        // 2. 清空帧队列 (归还到 FramePool)
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("清队列");
        Step_ClearFrameQueues();
        LogStepTiming("清队列", stepQpc);

        // 3. 刷新解码器 (FlushAsync 取剩余帧并 Dispose)
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("刷解码器");
        await Step_FlushDecodersAsync();
        LogStepTiming("刷解码器", stepQpc);

        // 4. 释放解码器
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("释放解码器");
        Step_DisposeDecoders();
        LogStepTiming("释放解码器", stepQpc);

        // 5. 释放渲染器 (Detach + 释放 SwapChain + GPU Flush)
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("渲染器");
        Step_DisposeRenderer();
        LogStepTiming("渲染器", stepQpc);

        // 6. 释放音频输出
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("音频输出");
        Step_DisposeAudioOutput();
        LogStepTiming("音频输出", stepQpc);

        // 7. 释放帧对象池（所有帧已归还）
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("帧池");
        Step_DisposeFramePools();
        LogStepTiming("帧池", stepQpc);

        // 8. 清空 BufferManager
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("缓冲");
        Step_ClearBufferManager();
        LogStepTiming("缓冲", stepQpc);

        // 9. 关闭 Demuxer
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("Demuxer");
        await Step_CloseDemuxerAsync();
        LogStepTiming("Demuxer", stepQpc);

        // 10. 关闭 MediaStream
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("流");
        Step_CloseStream();
        LogStepTiming("流", stepQpc);

        // 11. 重置 Clock
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("时钟");
        Step_ResetClock();
        LogStepTiming("时钟", stepQpc);

        // 12. 关闭 Session
        stepQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        StepEnter("会话");
        await Step_CloseSessionAsync();
        LogStepTiming("会话", stepQpc);

        // 13. 归还高精度定时器（与 PlayAsync 配对，避免整机定时器泄漏）
        ReleaseHighPrecisionTimer();

        var totalMs = System.Diagnostics.Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds;
        var detail = string.Join(" ", stepTimings);
        _logger.LogInformation("[DISPOSE] 释放完成：总 {Total:F0}ms ｜ {Detail}", totalMs, detail);
        Console.Error.WriteLine($"[DISPOSE] 释放完成：总 {totalMs:F0}ms ｜ {detail}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 同步快速释放兜底——做自己的同步清理路径
        // 硬性规定：禁止调用 DisposeAsync().GetAwaiter().GetResult()（伪异步）
        try
        {
            _positionTimer?.Dispose();

            // 同步停止管线（发 cts.Cancel）
            try { _bufferManager?.Stop(); } catch { }
            try { _videoPipeline?.Stop(); } catch { }
            try { _audioPipeline?.Stop(); } catch { }
            try { _subtitleProcessor?.Stop(); } catch { }

            // 等待管线线程退出（同步，2s 超时）
            // 确保 SemaphoreSlim.Dispose 不与正在进行的 WaitAsync/Release 并发
            try
            {
                var tasks = new List<Task>();
                if (_videoPipeline?.PipelineTask != null)
                    tasks.Add(_videoPipeline.PipelineTask);
                if (_audioPipeline?.PipelineTask != null)
                    tasks.Add(_audioPipeline.PipelineTask);
                if (_subtitleProcessor?.ProcessTask != null)
                    tasks.Add(_subtitleProcessor.ProcessTask);
                // 补齐 BufferManager 读取线程——原先不等它直接释放 Demuxer（跨线程 UAF 隐患）。
                if (_bufferManager?.ReaderTask != null)
                    tasks.Add(_bufferManager.ReaderTask);
                if (tasks.Count > 0)
                    Task.WaitAll(tasks.ToArray(), MediaPipelineTimeouts.PipelineTaskWait);
            }
            catch { } // AggregateException（线程异常）或 TimeoutException 均忽略

            // 线程已退出（或超时），安全释放管线内部资源（解码锁和 CTS）
            try { _videoPipeline?.Dispose(); } catch { }
            try { _audioPipeline?.Dispose(); } catch { }
            try { _subtitleProcessor?.Dispose(); } catch { }

            // 同步清空帧队列（归还到池）
            try { _frameQueue?.Clear(_videoFramePool); } catch { }
            try { _sampleQueue?.Clear(_audioFramePool); } catch { }

            // 同步释放原生资源（每步独立 try-catch）
            try { _videoDecoder?.Dispose(); } catch { }
            try { _audioDecoder?.Dispose(); } catch { }
            try { _subtitleDecoder?.Dispose(); } catch { }
            // 注意：共享单例渲染器（D3D11RendererFactory 缓存单例）不在此处置——
            // 其生命周期归工厂，SwapChain 由 UI Presenter.Detach 释放（缓存单例模式）。
            try { _audioOutput?.Dispose(); } catch { }
            try { _demuxer?.Close(); } catch { }
            try { _demuxer?.Dispose(); } catch { }
            try { _stream?.Close(); } catch { }
            try { _bufferManager?.Clear(); } catch { }

            // 归还高精度定时器（与 PlayAsync 配对，避免整机定时器泄漏）
            try { ReleaseHighPrecisionTimer(); } catch { }

            // 释放帧对象池
            try { _videoFramePool?.Dispose(); } catch { }
            try { _audioFramePool?.Dispose(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步 Dispose 兜底异常");
        }
    }
}
