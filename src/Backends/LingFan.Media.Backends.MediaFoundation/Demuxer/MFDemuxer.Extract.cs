using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

// MFDemuxer 的 Extract 职责段（部分类，由 MFDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/Seek/Dispose）在主文件；IMFSourceReader 同步 COM 调用，COM 线程亲和。
internal sealed partial class MFDemuxer
{
    /// <summary>
    /// ReadPacketAsync 的同步核心逻辑。多流交织：逐流读取并维护每流 1 个 lookahead 包，按时间戳返回最早的。
    /// </summary>
    /// <remarks>
    /// <para>IMFSourceReader.ReadSample 不接受 MF_SOURCE_READER_ALL_STREAMS（运行时返回 MF_E_INVALID_STREAM），
    /// 故改为逐流调用 ReadSample，再按 sample 时间戳挑选最早者，模拟 FFmpeg 的交织输出，
    /// 供 BufferManager 按 TrackIndex 路由到视频/音频队列。</para>
    /// </remarks>
    private MediaPacket? ReadPacketCore(CancellationToken ct)
    {
        // 整个 ReadPacketCore（含对 _pendingPackets/_exhaustedStreams 的读写）处于 gate 内，
        // 与 Close 的释放形成互斥，杜绝托管侧数据竞争。关闸时 TryEnter 返回 false ⇒ 立即以 EOS 返回。
        if (!_readerGate.TryEnter()) return null;
        try
        {
            ct.ThrowIfCancellationRequested();

            const int maxEmptyRounds = 256; // 限制流 tick 空转轮数，避免无数据时空转卡死
            int emptyRounds = 0;

            while (true)
            {
                // 关闸后立刻结束当前包读取，把 drain 时间压到"一次 ReadSample 返回"的量级。
                if (_readerGate.IsClosing) return null;

                // 释放期止读：Stop/Dispose 取消后尽快退出循环。
                ct.ThrowIfCancellationRequested();

                // 1. 返回已缓存中时间戳最早的包
                MediaPacket? earliest = null;
                int earliestStream = -1;
                foreach (var kvp in _pendingPackets)
                {
                    if (earliest == null || kvp.Value.Timestamp < earliest.Timestamp)
                    {
                        earliest = kvp.Value;
                        earliestStream = kvp.Key;
                    }
                }
                if (earliest != null)
                {
                    _pendingPackets.Remove(earliestStream);
                    return earliest;
                }

                // 2. 无缓存：为尚未结束的流各读一个样本填充 lookahead
                bool progressed = false;
                foreach (int s in _selectedStreamIndices)
                {
                    if (_exhaustedStreams.Contains(s))
                        continue;

                    var pkt = ExtractPacket(s, out bool eos);
                    if (eos)
                    {
                        _exhaustedStreams.Add(s);
                        continue;
                    }
                    if (pkt != null)
                    {
                        _pendingPackets[s] = pkt;
                        progressed = true;
                    }
                }

                if (_pendingPackets.Count > 0)
                    continue; // 下一轮返回最早

                // 全部流结束且无缓存 → EOS
                if (_exhaustedStreams.Count >= _selectedStreamIndices.Length)
                    return null;

                // 仍有活跃流但本次未取到（流 tick）：限次重试，避免空转
                if (!progressed)
                {
                    if (++emptyRounds > maxEmptyRounds)
                        return null; // 防御性退出，交由上层在下个包请求时重试
                    Thread.Sleep(1);
                }
                else
                {
                    emptyRounds = 0;
                }
            }
        }
        finally { _readerGate.Exit(); }
    }

    /// <summary>
    /// 从指定流读取单个样本并提取为 <see cref="MediaPacket"/>。
    /// </summary>
    /// <param name="streamIndex">MF 流索引（= MediaTrack.Index）。</param>
    /// <param name="eos">该流是否已达结束（出错或流结束）。</param>
    /// <returns>提取出的包；流 tick（暂无可读样本）时返回 null 且 eos=false。</returns>
    private MediaPacket? ExtractPacket(int streamIndex, out bool eos)
    {
        eos = false;

        // ReadSample = 绝对槽 9 → index 6（mfreadwrite.idl 顺序；运行时已验证 slot1/2 自洽布局）
        // 错误链：检查 _sourceReader 是否已被释放（UAF 前兆，严格模式直接抛）。
        InteropTrace.OnVTableGet(_sourceReader, "ExtractPacket:_sourceReader(ReadSample self)");
        int hr = _readSample!(_sourceReader, (uint)streamIndex, 0,
            out int actualStreamIndex, out int streamFlags, out long timestamp, out IntPtr samplePtr);
        if (hr < 0)
        {
            // COM 配对（失败路径不释放）：失败路径【不得】释放 *ppSample。
            // COM 规范要求方法失败时输出接口指针为 NULL；此处若非零，说明原生实现违规，
            // 其语义（是否已 AddRef、是否已悬垂）无从判定 —— Release 有 double-free 风险。
            // 依既定原则「泄漏优于误释放」：一律不释放，仅记录诊断。
            if (samplePtr != IntPtr.Zero)
            {
                _logger.LogError(
                    "IMFSourceReader.ReadSample(流{Stream}) 失败(HRESULT=0x{HR:X8}) 但写回了非空 *ppSample=0x{Ptr:X}；" +
                    "按 COM 规范此为原生侧违规，已有意泄漏该引用以避免 double-free。", streamIndex, hr, samplePtr);
            }
            _logger.LogWarning("IMFSourceReader.ReadSample(流{Stream}) 失败: HRESULT=0x{HR:X8}", streamIndex, hr);
            eos = true; // 出错视为该流结束，避免无限重试
            return null;
        }
        if ((streamFlags & MFConstants.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
        {
            eos = true;
            if (samplePtr != IntPtr.Zero) InteropTrace.ReleaseComPtr(samplePtr, "ExtractPacket:samplePtr(EOS)");
            return null;
        }
        if (samplePtr == IntPtr.Zero)
            return null; // 流 tick，无数据，下次再试

        // 错误链：分配成功后清除该地址（LFH 可能复用已释放地址）的陈旧"已释放"标记，避免 UAF 误报。
        InteropTrace.OnAlloc(samplePtr, "ExtractPacket:samplePtr");

        // 自此 samplePtr 持有一份引用，所有退出路径（含异常）恰好释放一次。
        try
        {
            // 该流已协商为 NV12 解码输出 ⇒ 走「已解码直通」打包（优先 GPU 纹理零拷贝）
            // 只有 TryConfigureVideoStreamToNv12 成功的那一条视频流会命中；其余流（音频 PCM、
            // 未协商成功的视频压缩裸流）继续走下方通用字节拷贝路径。
            if (_decodedVideoStreamIndex >= 0 && actualStreamIndex == _decodedVideoStreamIndex)
                return ExtractDecodedVideoPacket(samplePtr, actualStreamIndex, timestamp);

            // 提取采样数据：ConvertToContiguousBuffer = 绝对槽 41 → index 38
            // （IMFAttributes 恰 30 方法，IMFSample 第 9 方法；运行时已验证 slot38 返回有效 buffer）
            hr = MfVTable.Get<IMFSample_ConvertToContiguousBuffer>(samplePtr, 38)(samplePtr, out IntPtr bufferPtr);
            if (hr < 0 || bufferPtr == IntPtr.Zero)
            {
                _logger.LogWarning("ConvertToContiguousBuffer 失败: HRESULT=0x{HR:X8}", hr);
                return null;
            }

            // 错误链：分配成功后清除陈旧标记（同 OnAlloc 注释）。
            InteropTrace.OnAlloc(bufferPtr, "ExtractPacket:bufferPtr");

            byte[] data;
            try
            {
                // Lock = 槽 3 → index 0；Unlock = 槽 4 → index 1（运行时已验证）。
                // 错误链：经 InteropTrace 记录并（严格模式下）校验 Lock/Unlock 配对。
                var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(bufferPtr, 0);
                var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(bufferPtr, 1);
                hr = InteropTrace.LockBuffer(bufferPtr, lockDel, out IntPtr dataPtr, out _, out uint curLen,
                    "ExtractPacket:IMFMediaBuffer.Lock");

                // COM 配对原则（与 WASAPI GetBuffer/ReleaseBuffer 同构）：
                //    IMFMediaBuffer.Unlock 只能与【成功的】Lock 配对，且恰好一次。
                //    Lock 失败时【绝不能】Unlock —— ConvertToContiguousBuffer 返回的缓冲区常是
                //    2D/DXGI 表面的「临时连续拷贝」实现，其 Unlock 会执行回拷 + 释放临时区；
                //    在从未 Lock 的状态下调用 ⇒ 野指针写 / 重复释放 ⇒ 污染原生堆。
                //    此类损坏不在原地崩溃，而是滞后到下一次 CLR 内部堆操作
                //    （如 MfVTable.Get 的 GetDelegateForFunctionPointer）才以原生堆损坏暴露。
                //    防回归：勿把 Lock 失败与空缓冲混为一谈——Lock 失败时 Unlock 正是上述堆损坏的来源，勿回退。
                if (hr < 0)
                {
                    _logger.LogWarning("IMFMediaBuffer.Lock 失败: HRESULT=0x{HR:X8}（未 Unlock，符合配对规范）", hr);
                    return null;
                }

                // 至此 Lock 已成功 ⇒ 必须 Unlock 恰好一次（含下方任意 return / 异常路径）。
                try
                {
                    if (curLen == 0 || dataPtr == IntPtr.Zero)
                        return null; // 空缓冲：视为流 tick，交由上层重试

                    data = new byte[curLen];
                    Marshal.Copy(dataPtr, data, 0, (int)curLen);
                }
                finally
                {
                    InteropTrace.UnlockBuffer(bufferPtr, unlockDel, "ExtractPacket:IMFMediaBuffer.Unlock");
                }
            }
            finally
            {
                InteropTrace.ReleaseComPtr(bufferPtr, "ExtractPacket:bufferPtr");
            }

            // 关键帧标记：MFSampleExtension_CleanPoint（IMFSample 继承 IMFAttributes，GetUINT32 = slotIndex 4）。
            // 属性缺失时按非关键帧处理（音频等无该属性的流不受影响——调用方仅对视频用 KeyFrame）。
            Guid cleanPointKey = MFConstants.MFSampleExtension_CleanPoint;
            bool keyFrame = MfVTable.Get<IMFMediaType_GetUINT32>(samplePtr, 4)(samplePtr, ref cleanPointKey, out uint cleanPoint) >= 0
                            && cleanPoint != 0;

            // 提取时间戳（100ns 单位 → TimeSpan）
            TimeSpan ts = timestamp > 0
                ? TimeSpan.FromTicks(timestamp)
                : TimeSpan.Zero;

            return new MediaPacket(
                actualStreamIndex,
                data,
                ts,
                TimeSpan.Zero,
                keyFrame);
        }
        finally
        {
            InteropTrace.ReleaseComPtr(samplePtr, "ExtractPacket:samplePtr");
        }
    }
}
