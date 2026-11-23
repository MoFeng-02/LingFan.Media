using Android.Media;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// EOS 排空分部：FlushAsync 两阶段协议（EOS 入队重试 + 排空直到输出 EOS）与取帧诊断计数。
/// </summary>
internal sealed partial class AndroidVideoDecoder
{
    private readonly Queue<VideoFrame> _pendingFrames = new();

    // 取帧诊断计数（Dispose 汇总，定位零产帧各分支分布）
    private long _drainCalls, _drainDequeued, _drainTryAgain;
    private bool _eosQueued;    // EOS 已入队（FlushAsync 重试语义，Reset 清零）
    private bool _eosOutputSeen;// 解码器已回报输出 EOS（DRAIN 真正完成的判据）

    // EOS 排空总超时（秒）兜底：正常 DRAIN 远快于此，仅用于解码器异常时防死锁，
    // 避免 FlushAsync 无限等待把呈现线程挂死。
    private const int DrainTimeoutSeconds = 5;
    // 单次 DrainOutput 最多提取的帧数。开播期解码器积压时，无上限的排空会把全部就绪帧
    // 一次性灌进 _pendingFrames，使 in-flight 帧数远超帧池每桶容量（16）；超额帧每帧都要
    // 新分配大数组、归还即弃 ⇒ 开播期垃圾风暴 ⇒ GC 停顿渲染线程，进而放大呈现侧抖动。
    // 加上限把 in-flight 峰值压回池容量内（未取完的帧留待下一轮，不丢帧）。
    /// <summary>单次排空最多提取的帧数上限（由 4 放宽到 12）。
    /// 原值 4 是为压制开播期 LOH GC 风暴（无上限排空会让 in-flight 帧数远超帧池每桶容量）。
    /// 但它同时把帧堵在解码器输出队列里、延缓缓冲归还，在 DPB 压力下会让解码器静默丢弃参考帧
    /// ⇒ 宏块 garbage。取 12：仍远低于帧池每桶 16 的上限（GC 风险可控），但排空吞吐翻三倍。</summary>
    private const int MaxFramesPerDrain = 12;

    /// <summary>Surface 桥接（AHB/GL 路径）常规排空的 dequeue 阻塞超时（μs）。
    /// Surface 输出模式下 MediaCodec 的帧渲染与 dequeue 等待强耦合：非阻塞（timeout=0）轮询会令
    /// 解码器持续吞入输入却不渲染输出（画面冻结在首帧后）。10ms 与 FlushAsync 常规排空超时一致：
    /// 30fps 帧距 33ms，等得起。</summary>
    private const long SurfaceDrainWaitUs = 10_000;

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        // 先排空待喂入队列
        FeedInput();

        // 已有解码完成但尚未交出的帧：立即返回，绝不阻塞在 EOS 入队上
        // （否则 EOS 入队重试期间会把已解码帧白白拖住，末段帧被上层 Complete 截断）。
        if (_pendingFrames.Count > 0)
            return new ValueTask<VideoFrame?>(_pendingFrames.Dequeue());

        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
                        + System.Diagnostics.Stopwatch.Frequency * DrainTimeoutSeconds;

        // 阶段 1：EOS 入队（必须成功）
        // 输入槽满时（喂入被阻）须先排空输出腾出槽位再重试。
        // 旧实现「固定 16 次重试后放弃」+「随后 !_eosQueued 即返回 null」，会让上层 DecodeLoop
        // 立刻判定排空完成并 Complete 帧队列 —— 末段 GOP 全部滞留解码器。
        // 现象：视频末段数秒永久丢失，表现为「画面卡在最后几秒、音频照常播完」。
        // 修正：重试到成功为止（受总 deadline 约束）；**排空取到的帧立即交还上层、绝不丢弃**
        // （旧代码 `_ = DrainOutput(5_000);` 直接丢弃返回值 = 每轮白丢一帧）。
        while (!_eosQueued)
        {
            FeedInput();

            // 【关键】待喂队列未排空前，绝不能入 EOS。
            // EOS 一旦入队，_pendingInput 里剩余的包就永远进不了解码器，其帧永久丢失
            // （表现为画面冻结在中段而音频照常播完）。
            if (_pendingInput.Count > 0)
            {
                if (DrainOutput(5_000) is { } pending)
                {
                    FeedAfterDrain();
                    return new ValueTask<VideoFrame?>(pending);
                }
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    _logger?.LogWarning(
                        "[ANDROID-VID] EOS 入队受阻：待喂队列仍有 {Pending} 包未入解码器，末段帧将丢失",
                        _pendingInput.Count);
                    break;
                }
                continue;
            }

            int inIdx = _codec!.DequeueInputBuffer(2_000);
            if (inIdx >= 0)
            {
                _codec.QueueInputBuffer(inIdx, 0, 0, 0, (MediaCodecBufferFlags)FlagEndOfStream);
                _eosQueued = true;
                break;
            }
            // 槽仍满：排空输出解锁解码器。取到的帧直接返回上层（下一轮 FlushAsync 再继续入 EOS）。
            if (DrainOutput(5_000) is { } unlocked)
            {
                FeedAfterDrain();
                return new ValueTask<VideoFrame?>(unlocked);
            }

            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
            {
                _logger?.LogWarning(
                    "[ANDROID-VID] EOS 入队超时（{Sec}s，输入槽持续占满），末段重排帧可能丢失",
                    DrainTimeoutSeconds);
                break;
            }
        }

        // 阶段 2：排空输出，直到解码器回报输出 EOS
        // 【关键】单次 DrainOutput 的 TRY_AGAIN **绝不代表排空完成**：EOS 入队后，解码器内部
        // 仍持有末段 GOP 的 B 帧重排缓冲，需多轮 dequeue 才逐步吐出，期间必然穿插 TRY_AGAIN。
        // 修正：持续重试直到 `_eosOutputSeen`（DRAIN 真正的完成判据），总超时兜底防死锁。
        while (true)
        {
            var frame = DrainOutput(_eosQueued && !_eosOutputSeen ? 40_000 : 10_000);
            if (frame is not null)
                return new ValueTask<VideoFrame?>(frame);

            // 未取到帧：仅当已见到输出 EOS 时才算真正排空。
            // 注意：不再因 `!_eosQueued` 提前返回——EOS 未入队时仍应继续把解码器已有输出取干净。
            if (_eosOutputSeen)
                return new ValueTask<VideoFrame?>((VideoFrame?)null);

            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
            {
                _logger?.LogWarning(
                    "[ANDROID-VID] EOS 排空超时（{Sec}s，未见输出 EOS），末段重排帧可能丢失",
                    DrainTimeoutSeconds);
                return new ValueTask<VideoFrame?>((VideoFrame?)null);
            }
            // 未见 EOS：解码器仍在重排，继续等下一轮。
        }
    }
}