using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// 输出排空分部：MFT 就绪输出循环取帧、STREAM_CHANGE 重协商重试与积压队列管理。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class MFVideoDecoder
{
    // MFT 输出积压队列（防丢包缓冲）
    // H.264 解码 MFT 是 **N 进 M 出**：受 B 帧重排/DPB 影响，单次 ProcessInput 后可能
    //    产出 0 帧、也可能产出多帧；且在**输出未被取空前会以 MF_E_NOTACCEPTING 拒收新输入**。
    //    IVideoDecoder 契约是「一次调用最多返回一帧」，故多产出的帧暂存于此队列，由后续调用
    //    依次取走 —— 保证「入包数 == 出帧数」，绝不丢帧。
    //    须排空 MFT 的全部输出并重投被拒输入，否则按比例静默丢弃压缩包：
    //    表现为帧率下降（参考帧缺失 ⇒ 花屏）+ PTS 缺口（卡顿/回弹）。
    private readonly Queue<VideoFrame> _pendingOutputs = new();
    private bool _warnedPendingBacklog;   // 积压异常告警只打一次
    private int _pendingBacklogOverCount;  // 连续超过高水位的 DrainAvailableOutputs 次数（区分瞬态突发 vs 持续积压）
    private const int PendingBacklogHighWater = 24;     // 积压高水位（> 即异常；正常仅 DPB 深度 ≤16）
    private const int PendingBacklogSustainedLimit = 4;  // 连续超标次数上限，超过才视为「持续积压」真缺陷
    // (b)(2) 架构补短：稳态 _pendingOutputs 硬上限（EOS/DRAIN 模式豁免）。
    // 仅限稳态 decode-ahead，防止个别大 GOP/重排瞬间把内部缓冲无界堆高（内存尖峰）；
    // 超限即停止继续从 MFT 取帧，未取走的帧留在 MFT 内部（绝不丢），下一次 DecodeAsync 借
    // NOTACCEPTING→Drain 路径自然取回。EOS 必须排空 DPB 残留以救尾帧，故 DRAIN 路径不受此限。
    private const int PendingOutputHardCeiling = 32;    // 高于正常 DPB 深度(≤16)，低于 EOS 豁免突发的 39 帧
    private const int MaxOutputsPerDrain = 64;     // 单轮最多取帧数，防异常 MFT 无限吐帧

    /// <summary>
    /// 排空 MFT **当前所有就绪输出**，全部压入 <see cref="_pendingOutputs"/>，返回本轮产出帧数。
    /// </summary>
    /// <remarks>
    /// 每次 ProcessInput 之后必须循环 ProcessOutput 直到 <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>。
    /// 只取一帧会让输出在 MFT 内部积压，进而使后续 ProcessInput 持续返回
    /// <c>MF_E_NOTACCEPTING</c> ⇒ 输入包被拒 ⇒ 丢帧。这是 30fps 源只出 22fps 的直接原因。
    /// </remarks>
    private int DrainAvailableOutputs(MediaPacket sourcePacket)
    {
        int produced = 0;
        const int drainTransientRetryLimit = 8; // DRAIN 期间「无产出但非 NEED_MORE_INPUT」的连续重试上限
        int noFramePulls = 0;
        bool isEos = ReferenceEquals(sourcePacket, DrainPacket); // EOS/DRAIN 模式：豁免稳态背压，必须排空 DPB 残留
        for (int i = 0; i < MaxOutputsPerDrain; i++)
        {
            var frame = ProcessOutputOnce(sourcePacket, out bool needMoreInput);
            if (needMoreInput)
                break; // MFT 真正排空（DRAIN 收口信号）：调用方据此正常结束 EOS 排空
            if (frame != null)
            {
                // (b)(2) 稳态背压：仅稳态限制内部缓冲上限，超界即停止继续从 MFT 取帧；
                // 未取走的帧留在 MFT 内部（绝不丢），下一次 DecodeAsync 借 NOTACCEPTING→Drain 自然取回。
                // EOS/DRAIN 模式豁免（isEos），否则尾段 DPB 残留帧无法排空 = 末段尾帧偶发 Drop 复发。
                if (!isEos && _pendingOutputs.Count >= PendingOutputHardCeiling)
                    break;
                _pendingOutputs.Enqueue(frame);
                produced++;
                noFramePulls = 0;
                continue;
            }
            // frame==null 且 needMoreInput==false：DRAIN 期间的瞬态（重协商失败 / 提取失败 /
            // 硬件 MFT 在吐下一帧前的短暂空窗）。此时不应中断 —— 重试 ProcessOutput 往往能取出
            // DPB 中滞留的尾帧；否则 DPB 剩余 ~10-16 帧（恰为 DPB 深度）永久丢失 = 末段尾帧偶发 Drop。
            // 连续无产出达上限才收口，避免异常 MFT 死循环。
            if (++noFramePulls >= drainTransientRetryLimit)
                break;
        }

        // 积压告警：区分「瞬态突发」与「持续积压」。
        //  - 瞬态：EOS 排空 DPB 残留 / 单帧大 GOP 导致 _pendingOutputs 一次性冲高，但消费侧随后追平（dropped=0，队列回落至低位）→ 良性，不告警。
        //  - 持续：消费侧真卡死，队列长期高于高水位不回落 → 内存增长真缺陷。
        // 仅当「连续多次 DrainAvailableOutputs 超标」才视为持续积压，单次突发自愈不会触发（L492 未达上限即被 else 分支重置）。
        if (_pendingOutputs.Count > PendingBacklogHighWater)
        {
            if (++_pendingBacklogOverCount >= PendingBacklogSustainedLimit && !_warnedPendingBacklog)
            {
                _warnedPendingBacklog = true;
                _logger.LogWarning("MFT 输出积压持续偏高（{Count} 帧，连续 {N} 次超标），消费侧可能未及时取帧", _pendingOutputs.Count, _pendingBacklogOverCount);
            }
        }
        else
        {
            // 已回落至高水位以下 → 视为瞬态自愈，重置连续计数（不告警）
            _pendingBacklogOverCount = 0;
        }
        return produced;
    }

    /// <summary>取出一帧积压输出；无积压返回 null。</summary>
    private VideoFrame? DequeuePendingOutput()
        => _pendingOutputs.Count > 0 ? _pendingOutputs.Dequeue() : null;

    /// <summary>释放全部积压输出（Seek/Flush/关闭时，这些帧属于作废内容）。</summary>
    private void DiscardPendingOutputs()
    {
        while (_pendingOutputs.Count > 0)
            _pendingOutputs.Dequeue().Dispose();
        _warnedPendingBacklog = false;
        _pendingBacklogOverCount = 0;
    }

    /// <summary>
    /// 尝试从 MFT 取出**一帧**解码输出（处理 STREAM_CHANGE 重协商；
    /// 系统解码 MFT 不提供输出 sample，由本方法按 cbSize 预分配）。
    /// </summary>
    /// <param name="sourcePacket">时间戳回退来源（ExtractFrame 优先用输出 sample 自带时间戳）。</param>
    /// <param name="needMoreInput">出参：MFT 已无就绪输出（<c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>）。</param>
    private unsafe VideoFrame? ProcessOutputOnce(MediaPacket sourcePacket, out bool needMoreInput)
    {
        needMoreInput = false;
        const int maxRetries = 8; // 仅用于 STREAM_CHANGE 重协商后的重试
        for (int i = 0; i < maxRetries; i++)
        {
            IntPtr outputSample = _mftProvidesSamples ? IntPtr.Zero : CreateOutputSample();
            bool sampleConsumed = false;
            try
            {
                MftOutputBuffer ob = default;
                ob.pSample = outputSample;

                int hr = _processOutput!(_transform, 0, 1, (IntPtr)(&ob), out _);

                // MFT 可能在 ob 中放事件集合，须释放
                if (ob.pEvents != IntPtr.Zero)
                    Marshal.Release(ob.pEvents);

                if (hr == MFConstants.MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    needMoreInput = true; // MFT 已排空：调用方据此正常收口本轮
                    return null;
                }
                if (hr == MFConstants.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    // 重新协商输出类型/尺寸/输出 buffer 大小后重试
                    if (!RenegotiateOutput())
                        return null;
                    continue;
                }
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                IntPtr resultSample = ob.pSample;
                if (resultSample == IntPtr.Zero)
                    return null;

                sampleConsumed = resultSample == outputSample; // ExtractFrame 负责释放
                return ExtractFrame(resultSample, sourcePacket);
            }
            finally
            {
                // 调用方分配模式下，未被 ExtractFrame 接管的 sample 在此释放（NEED_MORE_INPUT / STREAM_CHANGE / 异常路径）
                if (outputSample != IntPtr.Zero && !sampleConsumed)
                    Marshal.Release(outputSample);
            }
        }
        return null;
    }
}