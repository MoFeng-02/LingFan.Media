using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Outputs.OpenSLES;

// OpenSlesOutput 的帧提交职责段（部分类，按职责拆分，编译期等价）。
// PCM 入队（背压/格式重协商/首帧锚点）与缓冲完成回调（回收/主时钟记账/节拍仪表）；
// 初始化与控制编排在主文件，播放器创建在 Player 段。
internal sealed unsafe partial class OpenSlesOutput
{
    /// <inheritdoc/>
    public void Submit(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _bqItf == IntPtr.Zero)
            throw new InvalidOperationException("OpenSL ES 输出尚未初始化，无法 Submit。");

        // 运行时格式重协商：帧采样率/声道数与当前播放器配置不一致时重建播放器。
        // MediaCodec 真实输出格式仅首帧 FORMAT_CHANGED 后可知（configure 初始值不可信），
        // 首帧到达即触发重建（HE-AAC v2 等场景 1ch→2ch、采样率翻倍）。
        if (frame.Channels != _channels || frame.SampleRate != _sampleRate)
        {
            if (frame.SampleRate <= 0 || frame.Channels <= 0)
                throw new ArgumentException($"音频帧采样率/声道数非法：{frame.SampleRate}Hz/{frame.Channels}ch。", nameof(frame));
            RecreatePlayer(frame.SampleRate, frame.Channels);
        }

        // 仅支持 S16（OpenSL ES 输出固定 S16；上游需保证一致，否则由宿主/管线转换）
        if (frame.SampleFormat != SampleFormat.S16)
            throw new NotSupportedException($"OpenSL ES 输出仅支持 S16，收到 {frame.SampleFormat}。");

        int byteLength = frame.FrameCount * frame.Channels * 2; // S16
        if (frame.Data.Length < byteLength)
            throw new ArgumentException($"音频帧数据不足：期望 {byteLength} 字节，实际 {frame.Data.Length} 字节。", nameof(frame));

        // 背压：等待有空闲缓冲（最多 MaxInFlightBuffers 个在途）
        _backpressure.Wait();

        // 取一个空闲原生缓冲（无则分配）
        if (!_freeBuffers.TryDequeue(out IntPtr buffer))
            buffer = Marshal.AllocHGlobal(byteLength);

        try
        {
            // 拷贝 PCM（S16）到原生缓冲
            var src = frame.Data.Span[..byteLength];
            var dst = new Span<byte>((void*)buffer, byteLength);
            src.CopyTo(dst);

            _inFlightBuffers.Enqueue(buffer);
            _inFlightSamples.Enqueue(frame.FrameCount);
            // 首帧锚点（一次性）：主时钟 = 锚点 PTS + 已消费采样数/采样率 = 当前「可闻」媒体时间。
            // 用首帧 PTS 而非 0 作锚：音频管线不等 PTS 直接提交（如本样例音频首包 pts=1.13s 提交于
            // 起播瞬间），可闻媒体时间从首帧 PTS 起算才能与实际听到的内容对齐。
            Interlocked.CompareExchange(ref _anchorMediaTicks, frame.Timestamp.Ticks, long.MinValue);

            var enqueue = GetVTable<SLResult_EnqueueDelegate>(_bqItf, SLOT_BQ_Enqueue);
            int ret = enqueue(_bqItf, buffer, (uint)byteLength);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"BufferQueue Enqueue 失败，code={ret}。");
        }
        catch
        {
            // Enqueue 失败：回收缓冲 + 释放背压
            if (buffer != IntPtr.Zero)
            {
                _freeBuffers.Enqueue(buffer);
                _backpressure.Release();
            }
            throw;
        }
    }

    private void OnBufferCompleted(IntPtr bq, IntPtr context)
    {
        // 一个缓冲播放完成：回收一个在途缓冲到空闲池，并释放背压信号。
        // 锁 _gate：重建窗口（STOPPED→清池→Destroy）内可能收到迟到回调；
        // TryDequeue 失败（池已被清空）则不 Release，防止信号量计数溢出（SemaphoreFullException）。
        lock (_gate)
        {
            if (_inFlightBuffers.TryDequeue(out IntPtr buffer))
            {
                _freeBuffers.Enqueue(buffer);
                _backpressure.Release();
                // 主时钟记账：该缓冲的采样已实际播完（可闻）。同时记录步进时刻与缓冲时长，
                // 供 GetPlaybackPosition 在两次步进之间做 1× 实时插值（消除阶梯量化）。
                if (_inFlightSamples.TryDequeue(out int samples))
                {
                    Interlocked.Add(ref _framesConsumed, samples);
                    long nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                    Volatile.Write(ref _lastStepQpc, nowQpc);
                    _lastBufferSamples = samples;

                    // 节拍仪表：完成回调间隔 = 该缓冲实际播放时长（健康 ≈ capMs）+ 设备排空时间。
                    double capMs = samples * 1000.0 / _sampleRate;
                    int inFlightNow = _inFlightBuffers.Count;
                    if (inFlightNow < _paceInFlightMin) _paceInFlightMin = inFlightNow;
                    if (_pacePrevStepQpc != 0)
                    {
                        double intervalMs = System.Diagnostics.Stopwatch.GetElapsedTime(_pacePrevStepQpc, nowQpc).TotalMilliseconds;
                        _paceStepCount++;
                        _paceIntervalSumMs += intervalMs;
                        if (intervalMs > _paceIntervalPeakMs) _paceIntervalPeakMs = intervalMs;
                        if (intervalMs > capMs * 1.25)
                        {
                            _underrunCount++;
                            _underrunDebtMs += intervalMs - capMs;
                        }
                        if (_paceWindowStartQpc == 0)
                            _paceWindowStartQpc = nowQpc;
                        double windowSec = System.Diagnostics.Stopwatch.GetElapsedTime(_paceWindowStartQpc, nowQpc).TotalSeconds;
                        if (windowSec >= 5.0 && _paceStepCount >= 2)
                        {
                            double avg = _paceIntervalSumMs / _paceStepCount;
                            double debtRatio = _underrunDebtMs / (windowSec * 1000.0);
                            int minShown = _paceInFlightMin == int.MaxValue ? -1 : _paceInFlightMin;
                            _logger.LogInformation(
                                "[OPENSLES-pace] 窗口={Sec:F1}s 欠载={Count} 次/累计 {Debt:F0} ms（占比 {Ratio:P0}）回调间隔均/峰={Avg:F1}/{Peak:F1} ms 在途最小={Min} 单缓冲={Cap:F1} ms",
                                windowSec, _underrunCount, _underrunDebtMs, debtRatio, avg, _paceIntervalPeakMs, minShown, capMs);
                            _paceWindowStartQpc = nowQpc;
                            _paceStepCount = 0; _paceIntervalSumMs = 0; _paceIntervalPeakMs = 0;
                            _underrunCount = 0; _underrunDebtMs = 0;
                            _paceInFlightMin = int.MaxValue;
                        }
                    }
                    _pacePrevStepQpc = nowQpc;
                }
            }
        }
    }
}
