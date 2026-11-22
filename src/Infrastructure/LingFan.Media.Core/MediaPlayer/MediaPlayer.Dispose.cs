using Microsoft.Extensions.Logging;
using LingFan.Media.Core.Platform;

namespace LingFan.Media.Core;

// MediaPlayer 的 Dispose 职责段（部分类，由 MediaPlayer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/OpenAsync/PlayAsync/PauseAsync/StopAsync/SeekAsync/DisposeAsync/Dispose）在主文件；
// Session 级对象在 OpenAsync 延迟创建，DisposeAsync 11 步释放每步独立 try-catch。
public sealed partial class MediaPlayer
{
    private async Task Step_StopPipelinesAsync()
    {
        try
        {
            // 先停 BufferManager 读取线程
            _bufferManager?.Stop();

            _videoPipeline?.Stop();
            _audioPipeline?.Stop();
            _subtitleProcessor?.Stop();

            // 等待管线线程退出（5s 超时）
            var timeout = MediaPipelineTimeouts.PipelineJoin;
            var tasks = new List<Task>();

            if (_bufferManager?.ReaderTask != null)
                tasks.Add(_bufferManager.ReaderTask);

            if (_videoPipeline?.PipelineTask != null)
                tasks.Add(_videoPipeline.PipelineTask);

            if (_audioPipeline?.PipelineTask != null)
                tasks.Add(_audioPipeline.PipelineTask);

            if (_subtitleProcessor?.ProcessTask != null)
                tasks.Add(_subtitleProcessor.ProcessTask);

            if (tasks.Count > 0)
            {
                var allTasks = Task.WhenAll(tasks);
                try
                {
                    await allTasks.WaitAsync(timeout);
                }
                catch (TimeoutException)
                {
                    // 诊断：精确记录是哪个任务没退出，避免盲猜，便于复现时直接定位。
                    // 超时即潜在泄漏（管线线程未退出→原生资源未释放），按 NativeCallGate 协议以 LogError 上报。
                    var pending = new List<string>();
                    if (_bufferManager?.ReaderTask is { } bt && !bt.IsCompleted) pending.Add("BufferManager.ReaderTask");
                    if (_videoPipeline?.PipelineTask is { } vt && !vt.IsCompleted) pending.Add("VideoPipeline.PipelineTask");
                    if (_audioPipeline?.PipelineTask is { } at && !at.IsCompleted) pending.Add("AudioPipeline.PipelineTask");
                    if (_subtitleProcessor?.ProcessTask is { } st && !st.IsCompleted) pending.Add("SubtitleProcessor.ProcessTask");
                    var which = pending.Count > 0 ? string.Join(", ", pending) : "未知（可能已超时但状态竞态）";
                    _logger.LogError("管线线程退出超时（{TimeoutMs}ms），未完成任务 = {Pending}；继续释放，原生指针释放由后端 gate 保护，不会 UAF",
                        (int)timeout.TotalMilliseconds, which);
                }
            }

            // 线程已退出（或超时），安全释放管线内部资源（解码锁和 CTS）
            try { _videoPipeline?.Dispose(); } catch { }
            try { _audioPipeline?.Dispose(); } catch { }
            try { _subtitleProcessor?.Dispose(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤1: 停止管线异常");
        }
    }

    private void Step_ClearFrameQueues()
    {
        try
        {
            _frameQueue?.Clear(_videoFramePool);
            _sampleQueue?.Clear(_audioFramePool);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤2: 清空帧队列异常");
        }
    }

    private async ValueTask Step_FlushDecodersAsync()
    {
        try
        {
            if (_videoDecoder != null)
            {
                var frame = await _videoDecoder.FlushAsync();
                if (frame != null)
                {
                    if (_videoFramePool != null)
                        _videoFramePool.Return(frame);
                    else
                        frame.Dispose();
                }
            }

            if (_audioDecoder != null)
            {
                var frame = await _audioDecoder.FlushAsync();
                if (frame != null)
                {
                    if (_audioFramePool != null)
                        _audioFramePool.Return(frame);
                    else
                        frame.Dispose();
                }
            }

            if (_subtitleDecoder != null)
            {
                _ = await _subtitleDecoder.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤3: 刷新解码器异常");
        }
    }

    private void Step_DisposeDecoders()
    {
        try { _videoDecoder?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "释放视频解码器异常"); }
        try { _audioDecoder?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "释放音频解码器异常"); }
        try { _subtitleDecoder?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "释放字幕解码器异常"); }
    }

    private void Step_DisposeRenderer()
    {
    }

    private void Step_DisposeAudioOutput()
    {
        try { _audioOutput?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤6: 释放音频输出异常"); }
    }

    /// <summary>
    /// 释放帧对象池（步骤7，所有帧已归还后调用）。
    /// </summary>
    private void Step_DisposeFramePools()
    {
        try { _videoFramePool?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤7: 释放视频帧池异常"); }
        try { _audioFramePool?.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤7: 释放音频帧池异常"); }
    }

    private void Step_ClearBufferManager()
    {
        try { _bufferManager?.Clear(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤7: 清空缓冲异常"); }
    }

    private async ValueTask Step_CloseDemuxerAsync()
    {
        try { if (_demuxer != null) await _demuxer.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤9: 关闭 Demuxer 异常"); }
    }

    private void Step_CloseStream()
    {
        try { _stream?.Close(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤9: 关闭流异常"); }
    }

    private void Step_ResetClock()
    {
        try { _clock?.Reset(); }
        catch (Exception ex) { _logger.LogWarning(ex, "DisposeAsync 步骤10: 重置时钟异常"); }
    }

    private async ValueTask Step_CloseSessionAsync()
    {
        try
        {
            if (_session != null)
                await _session.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisposeAsync 步骤11: 关闭会话异常");
        }
    }

    private async Task CleanupAsync()
    {
        _positionTimer?.Dispose();
        try { _bufferManager?.Stop(); } catch { }
        try { _videoPipeline?.Stop(); } catch { }
        try { _audioPipeline?.Stop(); } catch { }
        try { _subtitleProcessor?.Stop(); } catch { }

        // 等待管线线程退出（异步，2s 超时）
        // OpenAsync 失败时管线通常未 Start（PipelineTask==null），但防御性处理
        var tasks = new List<Task>();
        if (_videoPipeline?.PipelineTask != null)
            tasks.Add(_videoPipeline.PipelineTask);
        if (_audioPipeline?.PipelineTask != null)
            tasks.Add(_audioPipeline.PipelineTask);
        if (_subtitleProcessor?.ProcessTask != null)
            tasks.Add(_subtitleProcessor.ProcessTask);
        // 补齐 BufferManager 读取线程（与上处对称）。
        if (_bufferManager?.ReaderTask != null)
            tasks.Add(_bufferManager.ReaderTask);
        if (tasks.Count > 0)
        {
            try { await Task.WhenAll(tasks).WaitAsync(MediaPipelineTimeouts.PipelineTaskWait); }
            catch { }
        }

        // 线程已退出（或超时），安全释放管线内部资源
        try { _videoPipeline?.Dispose(); } catch { }
        try { _audioPipeline?.Dispose(); } catch { }
        try { _subtitleProcessor?.Dispose(); } catch { }
        try { _frameQueue?.Clear(_videoFramePool); } catch { }
        try { _sampleQueue?.Clear(_audioFramePool); } catch { }
        try { _videoDecoder?.Dispose(); } catch { }
        try { _audioDecoder?.Dispose(); } catch { }
        try { _subtitleDecoder?.Dispose(); } catch { }
        // 共享单例渲染器不在此处置（生命周期归工厂，SwapChain 由 UI Presenter.Detach）——缓存单例模式。
        try { _audioOutput?.Dispose(); } catch { }
        try { _videoFramePool?.Dispose(); } catch { }
        try { _audioFramePool?.Dispose(); } catch { }
        try { _demuxer?.Close(); } catch { }
        try { _demuxer?.Dispose(); } catch { }
        try { _stream?.Close(); } catch { }
        try { _bufferManager?.Clear(); } catch { }
    }
}
