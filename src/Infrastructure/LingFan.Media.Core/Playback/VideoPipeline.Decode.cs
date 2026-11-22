using System;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Core;

// VideoPipeline 的 Decode 职责段（部分类，由 VideoPipeline.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/StartCore/PipelineLoop/Dispose）在主文件；
// 本段为解码生产者循环（独立于呈现消费者），共享 _runId/_cts 会话状态，线程模型不变。
public sealed partial class VideoPipeline
{
    /// <summary>
    /// 解码生产者循环（独立于呈现消费者）。持续从包队列读取、解码、入队，将帧队列维持在
    /// <see cref="TargetDepth"/> 帧的前向缓冲；背压到位即让出，使呈现侧永远有帧可取，
    /// 从而把「解码延迟」与「呈现节拍」彻底解耦。
    /// </summary>
    private async Task DecodeLoop()
    {
        // 解码循环同样绑定运行代号：包通道被复位重建后，旧循环的收尾不得改动新一轮状态。
        int runId = Volatile.Read(ref _runId);
        try
        {
            // 解码生产者同样提升优先级，避免被 OS 调度抢占导致前向缓冲断供、呈现侧饿死。
            // 跨平台生效（Thread.Priority 为 BCL API），设置失败静默降级。
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; }
            catch { }
            while (!_cts.IsCancellationRequested)
            {
                // 复位后旧轮作废：线程尚未退出也不得再从包通道取包。
                if (Volatile.Read(ref _runId) != runId)
                    break;

                // 解码循环心跳（冻结看门狗配套）：证明解码线程是否仍在循环。
                Interlocked.Exchange(ref _decodeHeartbeatQpc, System.Diagnostics.Stopwatch.GetTimestamp());
                _decodePhase = 0;

                if (_isPaused)
                {
                    await Task.Delay(10, _cts.Token);
                    continue;
                }

                // 背压：维持固定前向缓冲，避免无限超前占用内存；同时确保呈现侧永不被饿死。
                if (_frameQueue.Count >= TargetDepth)
                {
                    await Task.Delay(1, _cts.Token);
                    continue;
                }

                MediaPacket? packet;
                try
                {
                    _decodePhase = DecodePhaseReadPacket;
                    packet = await _packetQueue.Reader.ReadAsync(_cts.Token);
                }
                    catch (ChannelClosedException)
                    {
                        // 包通道在复位重建时会被替换：旧循环读到的「通道已关闭」是更换通道的结果，
                        // 不是流末。此时直接退场交给新一轮循环，避免把通道更换误判成播放完毕。
                        if (Volatile.Read(ref _runId) != runId)
                            break;

                        // EOS 意图已到达：提前置位，使下方 DRAIN 全窗口内 PipelineLoop 的
                        // Drop→Present 保护生效（否则 DRAIN 期间 eos 未置位，尾帧走真丢分支）。
                        _eosReached = true;
                        // 包源结束：必须先 DRAIN 解码器把末尾 B 帧重排缓冲（末段 GOP）全部取出入队，
                    // 再 Complete 帧队列 —— 顺序颠倒会让呈现侧提前收尾，末段 GOP 整批丢失。
                    // 修复：原实现只 Complete 不 DRAIN，导致 H.264/H.265 尾部
                    // 重排帧滞留 MFT 内部缓冲永不吐出 → 最后呈现帧冻结（画面尾帧不动）。
                    // 音频无 B 帧（无 ctts）不受影响，故仅视频侧需此 EOS 排空。
                    try
                    {
                        VideoFrame? drained;
                        while ((drained = await _decoder.FlushAsync()) != null)
                        {
                            await _frameQueue.EnqueueAsync(drained, _cts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "视频解码器 EOS 排空异常（末段帧可能丢失）");
                    }
                    _frameQueue.Complete();
                    break;
                }

                // 解码 + 后处理（加锁防止与 定位复位/Reset 竞态）
                _decodePhase = DecodePhaseLock;
                try
                {
                    await _decodeLock.WaitAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 等锁期间被取消（定位复位会取消令牌，属常规路径）：包已取得但尚未交给解码器，
                    // 此处释放，否则 MediaPacket 持有的原生缓冲随每次定位泄漏。
                    // 锁未取得，因此不 Release（超发会让后续锁计数错乱）。
                    packet.Dispose();
                    throw;
                }
                VideoFrame? decodedFrame = null;
                try
                {
                    // 隐患B修复：解码锁获取超时期间 定位复位 可能跳过 Reset，此处补做
                    if (_pendingDecoderReset)
                    {
                        _decoder.Reset();
                        _pendingDecoderReset = false;
                    }

                    // 解码锁超时期间 定位复位 可能跳过处理器重置，此处补做
                    if (_pendingProcessorReset)
                    {
                        _processorReset?.Invoke();
                        _pendingProcessorReset = false;
                    }

                    // 双重检查：获取锁后确认未暂停（防止在等待锁期间被 定位复位 暂停）
                    if (_isPaused)
                    {
                        packet.Dispose();
                        continue; // finally 会释放锁
                    }

                    try
                    {
                        _decodePhase = DecodePhaseDecode;
                        decodedFrame = await _decoder.DecodeAsync(packet);
                    }
                    finally
                    {
                        packet.Dispose();
                    }

                    if (decodedFrame != null && _processors != null)
                    {
                        // 经过视频后处理链（所有权转移）
                        foreach (var processor in _processors)
                        {
                            decodedFrame = processor(decodedFrame);
                            if (decodedFrame == null)
                                break; // 处理器丢弃帧（已 Dispose 输入帧）
                        }
                    }
                }
                finally
                {
                    _decodeLock.Release();
                }

                // 入队在解码锁之外：避免持锁阻塞于满队列（与 定位复位 死锁）；
                // 满时 BoundedChannelFullMode.Wait 自动背压。
                if (decodedFrame != null)
                {
                    try
                    {
                        _decodePhase = DecodePhaseEnqueue;
                        await _frameQueue.EnqueueAsync(decodedFrame, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        ReturnFrame(decodedFrame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出（Stop 调用了 cts.Cancel）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视频解码生产者异常");
        }
        finally
        {
            // 只有当轮解码循环才能置这两个位：复位后旧循环退出时既不得让新一轮看起来已经解码完，
            // 也不得把新一轮的存活标志落下。
            if (Volatile.Read(ref _runId) == runId)
            {
                _decodeLoopAlive = false;
                _decodeDone = true;
            }
        }
    }
}
