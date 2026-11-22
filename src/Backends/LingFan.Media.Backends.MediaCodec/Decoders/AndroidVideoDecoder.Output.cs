using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Java.Nio;
using LingFan.Media.GPUShare.Android; // AndroidHardwareBufferFrameResource（跨 GPU API 中立帧 DTO，消费方向：Backends→GPUShare.Android）
using LingFan.Media.GPUShare.Android.Bridge; // AndroidAhbRgbaBridge（解码侧 GLES/EGL→AHB 桥接，GPUShare.Android android 资产）
using AndroidMediaCodec = Android.Media.MediaCodec;
using PixelFormat = LingFan.Media.Abstractions.PixelFormat;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

// AndroidVideoDecoder 的 Output 职责段（部分类，由 AndroidVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeAsync/FlushAsync/FeedInput/Dispose）在主文件；
// 本段含输出读取/帧重排/ByteBuffer 与 AHB 双路径排空，全部同步原生调用。
internal sealed partial class AndroidVideoDecoder
{
    /// <summary>读出一个已解出帧（先返回 FIFO 余帧，再尝试从解码器申领）。</summary>
    private VideoFrame? ReadOutput()
    {
        // 队列非空时也要排空一次：解码器输出缓冲只有经 ReleaseOutputBuffer 归还后
        // 才会回到解码器的缓冲池；若因为我们手上有积压就一直不 dequeue，解码器可用输出缓冲
        // 会持续减少（DPB 压力），最终静默丢弃参考帧 —— 后续帧以缺失/错误的参考做运动补偿，
        // 画面即宏块 garbage，且不报任何错误。排空的帧并入队列后再取队首，顺序不变。
        if (_pendingFrames.Count > 0)
        {
            DrainOutput(0);
            return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
        }
        // Surface 桥接路径必须阻塞等待输出（SurfaceDrainWaitUs）；ByteBuffer/CPU 路径保持 0 非阻塞。
        // 统一 10ms——参照 Grafika 官方范式（TIMEOUT_USEC=10000）：非阻塞轮询在「输入槽满+输出未就绪」
        // 时从不给解码器墙钟时间完成手头工作 → 喂入饥饿 → 坏帧/时序空洞。
        return DrainOutput(SurfaceDrainWaitUs);
    }

    /// <summary>把新帧按 pts 升序插入重排缓冲，并释放已可确定的前缀。</summary>
    private void PushFrameOrdered(VideoFrame frame)
    {
        int i = _reorder.Count;
        while (i > 0 && _reorder[i - 1].Timestamp > frame.Timestamp)
            i--;
        if (i < _reorder.Count)
            _reorderCorrections++; // 真正发生了乱序插入（插到了已有帧之前）
        _reorder.Insert(i, frame);
        FlushReorder(force: false);
    }

    /// <summary>把重排缓冲中「已确定不会再有更小 pts 插到它前面」的帧按序交出。
    /// 判定：与上一次已释放 pts 的间隔 ≤ <see cref="ReorderHoldAhead"/> 即为自然顺序的下一帧；
    /// 差距过大说明是重排的 P 帧，中间 B 帧还没到，继续攒（受深度上限与 EOS 强制约束）。</summary>
    private void FlushReorder(bool force)
    {
        while (_reorder.Count > 0)
        {
            var head = _reorder[0];
            bool confident = force
                || _reorder.Count >= MaxReorderHold
                || _lastReleasedPts == TimeSpan.MinValue
                || head.Timestamp - _lastReleasedPts <= ReorderHoldAhead;
            if (!confident)
                break;

            _reorder.RemoveAt(0);
            // 【PTS 空洞探测器】在「重排后、交付前」的序列上检测 pts 跳变。
            // 正常 30fps 帧距 ≈33ms；跳变 > 60ms（≈2 帧距）即说明中间有帧没从解码器出来。
            // 【为什么这是花屏的决定性判据】单 IDR 流中 H.264 P/B 帧错误会传播到下一个 IDR ——
            // 解码器只要吞掉一帧，其后的整个 GOP 全是宏块马赛克（表现即「第一个 GOP 碎、中途自愈」）。
            // 传输链路逐字节无损时，损坏只可能源自解码器吞帧。
            if (_lastReleasedPts != TimeSpan.MinValue)
            {
                TimeSpan gap = head.Timestamp - _lastReleasedPts;
                if (gap > ReleaseGapThreshold)
                {
                    _releaseGaps++;
                    if (_releaseGaps <= 8 || (_releaseGaps % 20) == 0)
                        _logger.LogWarning(
                            "[ANDROID-VID] 帧空洞 #{N}: 交付序 pts 从 {Prev:g} 跳到 {Cur:g}，缺 {Miss:F1} 帧 " +
                            "（解码器吞帧 ⇒ 其后参考链全碎 → 花屏）",
                            _releaseGaps, _lastReleasedPts, head.Timestamp,
                            gap.TotalMilliseconds / 33.333 - 1);
                }
            }
            _pendingFrames.Enqueue(head);
            if (_lastReleasedPts != TimeSpan.MinValue && head.Timestamp < _lastReleasedPts)
                _reorderCorrections++;
            _lastReleasedPts = head.Timestamp;
        }
    }

    /// <summary>排空解码器输出，将可用帧入 FIFO，返回队首（超时内无帧返回 null）。
    /// 双路径分发：<see cref="_useAhbFrames"/> 为真走 GLES 桥接零拷贝（<see cref="DrainOutputAhb"/>），
    /// 否则走 ByteBuffer CPU 提取。</summary>
    private VideoFrame? DrainOutput(long timeoutUs)
    {
        if (_useAhbFrames)
            return DrainOutputAhb(timeoutUs);

        // 优先吐出上次已解码、尚未取走的帧：否则每取一帧都要先走一次 DequeueOutputBuffer
        // 并白等整个超时窗口（EOS 排空阶段 timeoutUs=40ms），末段上百帧将耗时数秒，
        // 且极易在等待途中被上层误判为「已排空」而提前收尾。
        if (_pendingFrames.Count > 0)
            return _pendingFrames.Dequeue();

        _drainCalls++;
        int dequeuedThisCall = 0;
        while (true)
        {
            var info = new AndroidMediaCodec.BufferInfo();
            int idx = _codec!.DequeueOutputBuffer(info, timeoutUs);
            if (idx == InfoTryAgainLater) { _drainTryAgain++; break; }
            if (idx == InfoOutputFormatChanged)
            {
                _outputFormat?.Dispose();
                _outputFormat = _codec.OutputFormat;
                ReadOutputFormat(_outputFormat);
                continue;
            }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) { _drainTryAgain++; break; }

            if (((int)info.Flags & FlagEndOfStream) != 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                _eosOutputSeen = true; // DRAIN 完成判据：此后 FIFO 排尽即无更多帧
                break;
            }
            if (info.Size <= 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }

            // getOutputImage 取标准化平面（已设 Formatyuv420flexible，平台契约保证可用），
            // 统一经 ExtractI420FromImage 提紧凑 I420——不按 stride 猜测厂商私有布局。
            var image = _codec.GetOutputImage(idx);
            if (image is null)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }
            VideoFrame? frame;
            try
            {
                frame = ExtractI420FromImage(image, info.PresentationTimeUs,
                    ((int)info.Flags & FlagKeyFrame) != 0);
            }
            finally
            {
                // ByteBuffer 路径（render=false）必须显式关闭 Image，释放 native reader。
                // 实测：仅 ReleaseOutputBuffer 而不 Close 会导致系统警告
                // "A resource failed to call close"，且 CCodec 把未释放的 Image 计入 client-held
                // buffer，大量出现 pipelineRoom<=numClientBuffers，解码器异常复用/丢帧 → 画面
                // 大面积块状破碎、色块错位。此处用 try/catch 保护，确保即使 Close 异常也不会
                // 阻止缓冲归还。AHB 零拷贝路径（render=true）不在此分支。
                try { image.Close(); }
                catch (Exception closeEx)
                {
                    _logger.LogDebug(closeEx, "[ANDROID-VID] Image.Close 异常（忽略，继续归还缓冲）");
                }
                _codec.ReleaseOutputBuffer(idx, false);
            }
            if (frame is null) continue;

            // pts 单调性哨兵：MediaCodec 契约要求按呈现序输出，若解码器实际按解码序（B 帧重排）
            // 吐帧，队首会变成一个远未来的 pts，管线据此「等待」而把其后所有帧堵死（队头阻塞），
            // 待主时钟追上后这些帧又集体超时被丢 —— 表现为开播期花屏/时序错乱。
            long ptsUs = info.PresentationTimeUs;
            if (_lastOutputPtsUs >= 0 && ptsUs < _lastOutputPtsUs)
            {
                _ptsRegressions++;
                if (_ptsRegressions <= 8)
                    _logger.LogWarning(
                        "[ANDROID-VID] pts 回退 #{N}: 本帧={Cur}us 上一帧={Prev}us 回退={Back:F1}ms（解码序≠呈现序？）",
                        _ptsRegressions, ptsUs, _lastOutputPtsUs, (_lastOutputPtsUs - ptsUs) / 1000.0);
            }
            _lastOutputPtsUs = ptsUs;

            // Trace 级：解码线程为实时线程，周期性 Information 写 logcat（双 provider）可达数十 ms，
            // 会周期性延迟解码出帧（卡顿诱因之一），调高日志级别即可查看。
            if ((_framesProduced % LogInterval) == 0)
                _logger.LogTrace("[ANDROID-VID] 产帧 #{Count} {W}x{H} {Fmt} pts={Pts:g}",
                    _framesProduced, frame.Width, frame.Height, frame.Format, frame.Timestamp);
            _framesProduced++;
            _drainDequeued++;
            PushFrameOrdered(frame);

            // 单轮预算：开播期解码器积压时，无上限的排空会把全部就绪帧一次性灌进 _pendingFrames，
            // 使 in-flight 帧数远超帧池每桶容量（maxArraysPerBucket=16）。超额帧每帧都要新分配
            // 大数组、归还即弃 ⇒ 开播期垃圾风暴 ⇒ GC 停顿渲染线程。加上限把峰值压回池容量内；
            // 未取完的帧留待下一轮，不丢帧（DequeueOutputBuffer 会立即再次返回就绪缓冲）。
            if (++dequeuedThisCall >= MaxFramesPerDrain)
                break;
        }

        // 重排缓冲兜底：EOS 已入队即强制排空，绝不让末帧滞留缓冲里被 Complete 截断。
        FlushReorder(force: _eosQueued);

        // 周期性诊断（定位 dequeue 是否恒 TRY_AGAIN）。
        // Trace 级：解码线程为实时线程，周期性 Information 写 logcat（双 provider）可达数十 ms，
        // 会周期性延迟解码出帧（卡顿诱因之一），调高日志级别即可查看。
        if ((_drainCalls % LogInterval) == 0)
            _logger.LogTrace("[ANDROID-VID] 诊断: 排空={Calls} dequeue成功={Deq} tryAgain={Try} 喂入={Fed} 累计产帧={Frames} pts回退={Reg} 重排缓冲={Hold} 校正={Fix} 待喂={Pend} 补喂={PostFed}/{PostCalls} 阻塞={Blk} 空洞={Gap}",
                _drainCalls, _drainDequeued, _drainTryAgain, _inputQueued, _framesProduced, _ptsRegressions,
                _reorder.Count, _reorderCorrections, _pendingInput.Count,
                _postDrainFed, _postDrainCalls, _inputDequeueBlocked, _releaseGaps);

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <summary>排空解码器输出（GLES 桥接零拷贝路径）：dequeueOutputBuffer → ReleaseOutputBuffer(render:true)
    /// 把帧渲进桥接 SurfaceTexture（驱动在 GPU 内完成 YUV→RGB）→ <see cref="AndroidAhbRgbaBridge.ConvertLatest"/>
    /// 闩帧并渲进 RGBA AHardwareBuffer → 包 <see cref="AndroidHardwareBufferFrameResource"/>。
    /// 全程零 CPU 像素拷贝，绕开 Adreno 对「YUV AHB + Vulkan YCbCr 采样」的原生空指针崩溃。
    /// 渲染侧据 AHB 的 ExternalFormat==0 自动路由到 <c>VulkanRgbaToRgbaConverter</c> RGBA 直通。</summary>
    /// <remarks>一帧一产：每次调用最多渲染一帧到 SurfaceTexture 并转换一帧。
    /// <see cref="AndroidAhbRgbaBridge.ConvertLatest"/> 返回 0（GL 异常态）时保留
    /// <see cref="_pendingSurfaceTextureFrame"/> 标志，下一轮<b>先闩后排空</b>优先重试——
    /// 滞留帧已被渲进 SurfaceTexture，若先排空新包会把新帧渲入致 updateTexImage 闩取最新帧、
    /// 滞留帧被静默顶出（永不显示）。先闩先出保证帧不丢、内容与 pts 恒对齐；
    /// GL 持续异常时每轮至多一次闩取尝试，无空转死循环。</remarks>
    private VideoFrame? DrainOutputAhb(long timeoutUs)
    {
        _drainCalls++;

        // 先闩后排空：优先闩出上一轮滞留帧（见方法 remarks）。滞留帧的 pts 仍由
        // _lastPresentationTimeUs 持有（该帧 ReleaseOutputBuffer 时绑定，此后无新帧渲入）。
        if (_pendingSurfaceTextureFrame)
        {
            var pending = TryLatchSurfaceTextureFrame();
            if (pending != null)
                return pending;
        }

        while (true)
        {
            var info = new AndroidMediaCodec.BufferInfo();
            int idx = _codec!.DequeueOutputBuffer(info, timeoutUs);
            if (idx == InfoTryAgainLater) { _drainTryAgain++; break; }
            if (idx == InfoOutputFormatChanged)
            {
                _outputFormat?.Dispose();
                _outputFormat = _codec.OutputFormat;
                ReadOutputFormat(_outputFormat);
                continue;
            }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) { _drainTryAgain++; break; }

            if (((int)info.Flags & FlagEndOfStream) != 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                _eosOutputSeen = true; // DRAIN 完成判据
                break;
            }
            if (info.Size <= 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }

            // 把帧渲进桥接 SurfaceTexture（render:true → 驱动等解码 fence，完成 GPU 内 YUV→RGB），
            // 后续 ConvertLatest 经 SurfaceTexture.updateTexImage 闩取。
            _lastPresentationTimeUs = info.PresentationTimeUs;
            _logger.LogTrace("[ANDROID-AHB-DEC]  ReleaseOutputBuffer(render) idx={Idx} pts={Pts}us", idx, info.PresentationTimeUs);
            _codec.ReleaseOutputBuffer(idx, true);
            _logger.LogTrace("[ANDROID-AHB-DEC]  ReleaseOutputBuffer(render) 返回");
            _pendingSurfaceTextureFrame = true;
            break; // 一帧已入 SurfaceTexture，跳出交给 ConvertLatest
        }

        // 周期性诊断
        if ((_drainCalls % LogInterval) == 0)
            _logger.LogTrace("[ANDROID-AHB-DEC] 诊断: 排空={Calls} tryAgain={Try} 喂入={Fed} 累计产帧={Frames}",
                _drainCalls, _drainTryAgain, _inputQueued, _framesProduced);

        // 仅当 SurfaceTexture 有待闩帧时才触发 GL 转换（避免空跑）。闩取失败（GL 异常态）时
        // 保留标志：下一轮先闩后排空优先重试，本帧不会被新渲入的帧顶替（调用序保证）。
        if (!_pendingSurfaceTextureFrame)
            return null;
        return TryLatchSurfaceTextureFrame();
    }
}
