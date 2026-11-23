using System;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

/// <summary>
/// AudioPipeline 的提交职责分部：采样提交与解码入队。
/// 音频不丢帧（丢帧致声音断裂），提交后经 Synchronizer.OnAudioFrameSubmitted 更新主时钟。
/// 字段与生命周期编排在主文件 AudioPipeline.cs。
/// </summary>
public sealed partial class AudioPipeline
{
    /// <summary>
    /// 提交阶段单步：从采样队列出队至多一小量子（~<see cref="MaxSubmitChunkMs"/>）并提交。
    /// 管线主循环与解码前瞻阶段共用——前瞻解码中穿插提交，保证设备在途缓冲持续补货
    /// （消除"解码阶段零提交 → 水库抽干 → 欠载"窗口）。
    /// </summary>
    /// <returns>false = 已取消且队列已清空（调用方应退出循环）。</returns>
    /// <param name="ct">本轮消费循环的令牌（循环入口固化）。用参数而不是现读 <see cref="_cts"/>，
    /// 否则旧循环在定位复位换掉 <see cref="_cts"/> 后会改用新令牌继续提交。</param>
    private bool TrySubmitChunk(CancellationToken ct)
    {
        // 关闭/停止：不再向渲染线程阻塞提交，直接归还剩余帧。
        // 避免 Stop/Dispose 时 SubmitBatch 卡在 WaitForBufferSpace 2s 超时累加 → 退出挂起 5s。
        if (ct.IsCancellationRequested)
        {
            while (_sampleQueue.TryDequeue(out var f) && f != null)
                ReturnFrame(f);
            return false;
        }

        // 先出首帧以确定小量子采样数上限（采样率跨流可能变化，故每流首帧懒初始化一次）
        if (!_sampleQueue.TryDequeue(out var head) || head is null)
            return true;
        if (_submitChunkSamples == 0)
        {
            _submitChunkSamples = Math.Max(MinChunkSamples, (int)(head.SampleRate * MaxSubmitChunkMs / 1000.0));
            _supplySampleRate = head.SampleRate;
        }

        var batch = new List<AudioFrame>(1) { head };
        int chunkSamples = head.FrameCount;
        while (_sampleQueue.TryDequeue(out var f) && f != null)
        {
            batch.Add(f);
            chunkSamples += f.FrameCount;
            if (chunkSamples >= _submitChunkSamples)
                break; // 达小量子上限即停，剩余帧留队列供下一轮循环提交
        }
        if (AudioDiagEnabled)
        {
            var subStart = Stopwatch.GetTimestamp();
            SubmitBatch(batch, ct);
            _lastSubmitEndTs = Stopwatch.GetTimestamp();
            var subMs = Stopwatch.GetElapsedTime(subStart).TotalMilliseconds;
            if (subMs > 80)
                _logger.LogWarning("[AUDIO-DIAG] SubmitBatch 阻塞 {Ms}ms（WaitForBufferSpace 设备节奏，属正常）", subMs);
        }
        else
        {
            SubmitBatch(batch, ct);
            _lastSubmitEndTs = Stopwatch.GetTimestamp();
        }

        // 供给速率仪表窗口（5s）：提交采样数 ÷ 墙钟 = 实时百分比。均匀吞吐亏损（调度延迟摊进
        // 每个提交周期）不产生离散缺口，唯此指标显形——速率 <100% → 本循环上游（包供给/解码）不足；
        // ≈100% 而设备仍欠载 → 消费/记账侧。与 [OPENSLES-pace] 欠载占比对表。
        _supplySubmittedSamples += chunkSamples;
        int sqNow = _sampleQueue.Count;
        if (sqNow < _supplyQueueMin) _supplyQueueMin = sqNow;
        if (sqNow > _supplyQueueMax) _supplyQueueMax = sqNow;
        long supplyNow = Stopwatch.GetTimestamp();
        if (_supplyWindowStartQpc == 0)
            _supplyWindowStartQpc = supplyNow;
        double supplyWinSec = Stopwatch.GetElapsedTime(_supplyWindowStartQpc, supplyNow).TotalSeconds;
        if (supplyWinSec >= 5.0 && _supplySampleRate > 0 && _supplySubmittedSamples > 0)
        {
            double supplyRate = _supplySubmittedSamples / (supplyWinSec * _supplySampleRate) * 100.0;
            int pktMinShown = _diagPacketDepthMin == int.MaxValue ? -1 : _diagPacketDepthMin;
            _logger.LogInformation(
                "[AUDIO-SUPPLY] 窗口={Sec:F1}s 提交采样={N}（速率 {Rate:F0}% 实时）解码包={P} 包通道最小={PkMin} 采样队列 min/max={Lo}/{Hi}",
                supplyWinSec, _supplySubmittedSamples, supplyRate, _supplyDecodedPackets, pktMinShown, _supplyQueueMin, _supplyQueueMax);
            _supplyWindowStartQpc = supplyNow;
            _supplySubmittedSamples = 0; _supplyDecodedPackets = 0;
            _diagPacketDepthMin = int.MaxValue;
            _supplyQueueMin = int.MaxValue; _supplyQueueMax = 0;
        }
        return true;
    }

    /// <summary>
    /// 整批提交解码帧。先逐帧做变换链 + 主时钟 + 数据事件（逐帧语义，批量化只折叠输出往返），
    /// 再一次性交给输出（支持 <see cref="IBatchAudioSubmit"/> 时折叠为单次 STA 往返，否则退回逐帧 Submit）。
    /// 帧所有权始终在本方法（pipeline 线程）内归还到池，绝不跨线程归还（帧池非线程安全）。
    /// </summary>
    private void SubmitBatch(List<AudioFrame> batch, CancellationToken ct = default)
    {
        if (batch.Count == 0) return;

        // 预处理：变换链 + 主时钟 + 数据事件（逐帧执行，不随批量化改变语义）
        var prepared = new List<AudioFrame>(batch.Count);
        foreach (var frame in batch)
        {
            var f = frame;
            if (_transforms != null)
            {
                foreach (var transform in _transforms)
                {
                    f = transform(f);
                    if (f == null)
                    {
                        _logger.LogWarning("音频变换链丢弃帧（返回 null），跳过提交");
                        goto nextFrame; // 变换已 Dispose 输入帧，不归还（与历史行为一致）
                    }
                }
            }

            _synchronizer.OnAudioFrameSubmitted(f);
            _audioDataSink?.Invoke(f);
            prepared.Add(f);
        nextFrame: ;
        }

        try
        {
            if (_output is IBatchAudioSubmit batched)
                batched.SubmitBatch(prepared, ct);
            else
                foreach (var f in prepared)
                    _output.Submit(f);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取消（Stop/Dispose）时 WasapiRenderLoop 已静默中断提交，正常路径不记错误（回归双保险）。
            _logger.LogError(ex, "音频批提交失败");
        }
        finally
        {
            // 帧所有权始终在 pipeline 线程归还（帧池非线程安全，严禁跨线程归还）
            foreach (var f in prepared)
                ReturnFrame(f);
        }
    }

    /// <summary>
    /// 解码单个包并入队（与 定位复位/Reset 加锁，防止竞态）。供 PipelineLoop 主解码与前瞻预解码共用。
    /// </summary>
    /// <param name="packet">待解码的音频包。所有权归本方法：无论解码成功、取消还是暂停跳过，均由本方法负责 Dispose。</param>
    /// <param name="ct">本轮消费循环的令牌（循环入口固化）。用参数而不是现读 <see cref="_cts"/>，
    /// 否则旧循环在定位复位换掉 <see cref="_cts"/> 后会改用新令牌继续解码。</param>
    private async Task DecodeAndEnqueueAsync(MediaPacket packet, CancellationToken ct)
    {
        // 包的所有权在本方法内转移：只要没能把包交给解码器，就必须由这里归还。
        // 等待锁期间循环被取消（定位复位取消令牌）时不会有任何一方接手该包，故显式归还。
        try
        {
            await _decodeLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            packet.Dispose();
            throw;
        }

        try
        {
            // 解码锁获取超时期间 定位复位 可能跳过 Reset，此处补做，确保解码器内部状态必然复位
            if (_pendingDecoderReset)
            {
                _decoder.Reset();
                _pendingDecoderReset = false;
            }

           // 解码锁超时期间 定位复位 可能跳过效果重置，此处补做，
            // 确保有状态效果（均衡器 biquad / 混响延迟线 / 压缩器包络）必然复位
            if (_pendingEffectReset)
            {
                _effectReset?.Invoke();
                _pendingEffectReset = false;
            }

            // 双重检查：获取锁后确认未暂停（防止在等待锁期间被 定位复位 暂停）
            if (_isPaused)
            {
                packet.Dispose();
                return; // finally 会释放锁
            }

            AudioFrame? decodedFrame;
            try
            {
                decodedFrame = await _decoder.DecodeAsync(packet);
            }
            finally
            {
                packet.Dispose();
            }

            if (decodedFrame != null)
            {
                if (!_sampleQueue.TryEnqueue(decodedFrame))
                    ReturnFrame(decodedFrame); // 队列满，归还帧到池
            }
        }
        finally
        {
            _decodeLock.Release();
        }
    }
}
