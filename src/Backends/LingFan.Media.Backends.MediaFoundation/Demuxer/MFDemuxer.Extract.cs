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
            // A 方案：该流已协商为 NV12 解码输出 ⇒ 走「已解码直通」打包（优先 GPU 纹理零拷贝）
            // 只有 TryConfigureVideoStreamToNv12 成功的那一条视频流会命中；其余流（音频 PCM、
            // 未协商成功的视频压缩裸流）继续走下方通用字节拷贝路径，行为与改造前一致。
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
                //    历史回归点：旧代码写作 `if (hr < 0 || curLen == 0) { unlockDel(...); }`，
                //    把 Lock 失败与空缓冲混为一谈，是本崩溃的真正成因，勿回退。
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

    /// <summary>
    /// 把「SourceReader 已解码」的视频样本打包为直通 <see cref="MediaPacket"/>（优先 GPU 纹理零拷贝）。
    /// </summary>
    /// <param name="samplePtr">IMFSample*（<b>引用归调用方</b>，本方法不释放）。</param>
    /// <param name="streamIndex">MF 流索引。</param>
    /// <param name="sampleTimeTicks">ReadSample 回填的样本时间（100ns）。</param>
    /// <returns>直通包；提取失败返回 <see langword="null"/>（按流 tick 处理，交由上层重试）。</returns>
    /// <remarks>
    /// <para><b>路径(1)（目标）</b>：<c>GetBufferByIndex(0)</c> → QI <c>IMFDXGIBuffer</c> → <c>ID3D11Texture2D</c>
    /// ⇒ 帧全程留在显存，<see cref="MediaPacket.DecodedFrameResource"/> 承载纹理所有权，
    /// 一路交到 <c>D3D11Renderer.PresentGpuTexture</c> 做 <c>CopySubresourceRegion</c> 上屏。真·零拷贝。</para>
    /// <para><b>绝不能</b>用 <c>ConvertToContiguousBuffer</c> 取 DXVA buffer：其契约就是「合并并读回连续系统内存」，
    /// 永远返回 CPU buffer，用它做零拷贝在原理上注定失败。</para>
    /// <para><b>路径(2)（回落）</b>：样本不是 DXGI buffer（=「半 DXVA」：驱动内部把帧读回了系统内存）时，
    /// 走 <c>ConvertToContiguousBuffer</c> 取 NV12 字节，配 <c>Width/Height/Stride</c> 交给下游直通成 <c>VideoFrame</c>。
    /// 仍比改造前好——至少省掉了 MFVideoDecoder 的第二次 MFT 解码。</para>
    /// <para><b>诊断</b>：首帧一次性打印命中的是哪条路径 + 失败 HRESULT，避免逐帧刷屏又不至于静默失效。</para>
    /// </remarks>
    private MediaPacket? ExtractDecodedVideoPacket(IntPtr samplePtr, int streamIndex, long sampleTimeTicks)
    {
        // 时间戳：ReadSample 已回填（100ns）。时长优先读 sample 自带（IMFSample.GetSampleDuration = 绝对槽 34 → 同 MFVideoDecoder）。
        TimeSpan ts = sampleTimeTicks > 0 ? TimeSpan.FromTicks(sampleTimeTicks) : TimeSpan.Zero;
        TimeSpan dur = TimeSpan.Zero;
        if (MfVTable.Get<IMFSample_GetSampleDuration>(samplePtr, 34)(samplePtr, out long rawDur) >= 0 && rawDur > 0)
            dur = TimeSpan.FromTicks(rawDur);

        // 关键帧标记（IMFSample 继承 IMFAttributes，GetUINT32 = slotIndex 4）
        Guid cleanPointKey = MFConstants.MFSampleExtension_CleanPoint;
        bool keyFrame = MfVTable.Get<IMFMediaType_GetUINT32>(samplePtr, 4)(samplePtr, ref cleanPointKey, out uint cleanPoint) >= 0
                        && cleanPoint != 0;

        int width = _decodedVideoWidth;
        int height = _decodedVideoHeight;
        if (width <= 0 || height <= 0)
        {
            if (!_loggedVideoPathOnce)
            {
                _loggedVideoPathOnce = true;
                _logger.LogWarning("[MF-D3D] 视频流 {Index} 解码输出尺寸未知（{W}x{H}），无法打包直通帧", streamIndex, width, height);
            }
            return null;
        }

        // 路径(1)：DXGI 纹理零拷贝（GetBufferByIndex = 绝对槽 40 → slotIndex 37）
        int hr = MfVTable.Get<IMFSample_GetBufferByIndex>(samplePtr, 37)(samplePtr, 0, out IntPtr rawBuffer);
        if (hr >= 0 && rawBuffer != IntPtr.Zero)
        {
            InteropTrace.OnAlloc(rawBuffer, "ExtractDecodedVideoPacket:rawBuffer");
            try
            {
                var gpu = MfDxgiTextureExtractor.TryExtract(rawBuffer, width, height, PixelFormat.NV12, out int exHr);
                if (gpu is not null)
                {
                    _videoZeroCopyFrames++;
                    if (!_loggedVideoPathOnce)
                    {
                        _loggedVideoPathOnce = true;
                        _logger.LogInformation(
                            "[MF-D3D] 零拷贝命中：SourceReader 直出 DXGI 纹理 {W}x{H} NV12 —— 解码→上屏全程显存，无系统内存往返",
                            width, height);
                    }
                    // 纹理所有权交给 packet（下游 MFVideoDecoder 用 TakeDecodedFrameResource 移交给 VideoFrame）
                    return new MediaPacket(streamIndex, ReadOnlyMemory<byte>.Empty, ts, dur, keyFrame,
                        width: width, height: height, decodedFrameResource: gpu);
                }

                if (!_loggedVideoPathOnce)
                {
                    _loggedVideoPathOnce = true;
                    _logger.LogWarning(
                        "[MF-D3D] 样本 buffer 非 DXGI（QI/GetResource HRESULT=0x{HR:X8}）——即「半 DXVA」：" +
                        "驱动内部把帧读回了系统内存。回落 NV12 CPU 直通（仍省掉一次 MFT 解码）。", exHr);
                }

                // 路径(1)-A：IMF2DBuffer2 真值行跨度紧凑化（半 DXVA 治本）
                //   IMFDXGIBuffer 失败后，MS H264 MFT 内部把帧读回 Direct3DSurface9-backed 2D 内存，
                //   实际 pitch 是 16 字节对齐（典型 1080→1088）。ConvertToContiguousBuffer 把整段当 1D 摊平，
                //   但 IMFMediaBuffer.GetCurrentLength 返回的是 MFT 内部 allocate 的整段长度（含尾部对齐 padding），
                //   反推 stride/codedH 必错 → 紧凑拷贝时 UV 平面偏移错位 → 画面下半段色度错行/横纹。
                //   治本：QI IMF2DBuffer2，Lock2D 取真值 pitch + scanline0，逐行拷成紧凑布局。
                //   若 Lock2D 失败（极少见：旧版 MFT 不实现 2D 路径），再走路径(2) ConvertToContiguousBuffer 兜底。
                int hr2d = Marshal.QueryInterface(rawBuffer, in MFConstants.IID_IMF2DBuffer2, out IntPtr b2d);
                if (hr2d >= 0 && b2d != IntPtr.Zero)
                {
                    InteropTrace.OnAlloc(b2d, "ExtractDecodedVideoPacket:b2d");
                    try
                    {
                        var lock2d = MfVTable.Get<IMF2DBuffer2_Lock2D>(b2d, 0);  // 槽 3 → slotIndex 0（mfobjects.h:1687）
                        var unlock2d = MfVTable.Get<IMF2DBuffer2_Unlock2D>(b2d, 1);  // 槽 4 → slotIndex 1（mfobjects.h:1694）
                        int hrLock = lock2d(b2d, out IntPtr scanline0, out int rawPitch);
                        if (hrLock >= 0 && scanline0 != IntPtr.Zero && rawPitch != 0)
                        {
                            try
                            {
                                int pitch = rawPitch < 0 ? -rawPitch : rawPitch;   // NV12 顶到底，pitch 应正；防御性取绝对值
                                if (pitch < width)
                                {
                                    // 异常：pitch < display width ⇒ 越界读，必丢帧
                                    if (!_loggedVideo2DPathOnce)
                                    {
                                        _loggedVideo2DPathOnce = true;
                                        _logger.LogWarning(
                                            "[MF-D3D] IMF2DBuffer2.Lock2D 返回 pitch={P} < display={W}，布局异常 ⇒ 丢帧、走路径(2)兜底",
                                            pitch, width);
                                    }
                                }
                                else
                                {
                                    int dstLen = width * height * 3 / 2;
                                    byte[] data2d = new byte[dstLen];

                                    // Y 平面：height 行，每行 width 字节，按真值 pitch 步进取
                                    //   用 IntPtr.Add 替代 IntPtr+long（.NET 10 IntPtr 无 operator+(IntPtr, long)）
                                    //   所有偏移对 1080×1920 NV12（pitch≤1088）都在 int 范围内，绝不上溢。
                                    for (int y = 0; y < height; y++)
                                        Marshal.Copy(IntPtr.Add(scanline0, y * pitch), data2d, y * width, width);

                                    // UV 平面：紧接 Y 平面之后；UV 高 = displayHeight/2；每行 width 字节（U/V 交错）
                                    int uvRows = height / 2;
                                    int srcUvStart = height * pitch;
                                    for (int y = 0; y < uvRows; y++)
                                        Marshal.Copy(IntPtr.Add(scanline0, srcUvStart + y * pitch),
                                            data2d, width * height + y * width, width);

                                    if (!_loggedVideo2DPathOnce)
                                    {
                                        _loggedVideo2DPathOnce = true;
                                        // 对齐值须运行时推断，不可硬编「16 对齐」：GPU 行距对齐随驱动而异（可能 256B 对齐），
                                        //    远大于软件 MFT 惯用的 16B 对齐（1080→1088）。pitch 带 GPU 行距对齐痕迹，
                                        //    本身就是「硬解确已发生、只是最后一步被读回系统内存」的物证。
                                        int alignGuess =
                                            pitch % 256 == 0 ? 256 : pitch % 128 == 0 ? 128 :
                                            pitch % 64 == 0 ? 64 : pitch % 32 == 0 ? 32 :
                                            pitch % 16 == 0 ? 16 : 1;
                                        _logger.LogInformation(
                                            "[MF-D3D] 半 DXVA 治本：IMF2DBuffer2.Lock2D 拿到真值 pitch={P}（display={W}，驱动行距对齐≈{Align}B，填充 {Pad}B/行）" +
                                            " → 逐行拷成紧凑 {Total}B，路径(2) ConvertToContiguousBuffer 跳过（治本，不再依赖 curLen 反推）。" +
                                            "pitch 带 GPU 对齐痕迹 ⇒ 硬解确已发生，仅最后一步被读回系统内存",
                                            pitch, width, alignGuess, pitch - width, dstLen);
                                    }

                                    _videoCpuFrames++;
                                    return new MediaPacket(streamIndex, data2d, ts, dur, keyFrame,
                                        width: width, height: height, stride: width);
                                }
                            }
                            finally
                            {
                                int hrUn = unlock2d(b2d);
                                if (hrUn < 0)
                                    _logger.LogWarning("[MF-D3D] IMF2DBuffer2.Unlock2D 失败 HRESULT=0x{HR:X8}", hrUn);
                            }
                        }
                        else if (!_loggedVideo2DPathOnce)
                        {
                            _loggedVideo2DPathOnce = true;
                            _logger.LogWarning(
                                "[MF-D3D] IMF2DBuffer2 QI 成功但 Lock2D 失败 hr=0x{HR:X8} scanline0={Sl} pitch={P} → 路径(2)兜底",
                                hrLock, scanline0, rawPitch);
                        }
                    }
                    finally
                    {
                        InteropTrace.ReleaseComPtr(b2d, "ExtractDecodedVideoPacket:b2d");
                    }
                }
            }
            finally
            {
                InteropTrace.ReleaseComPtr(rawBuffer, "ExtractDecodedVideoPacket:rawBuffer");
            }
        }
        else if (!_loggedVideoPathOnce)
        {
            _loggedVideoPathOnce = true;
            _logger.LogWarning("[MF-D3D] GetBufferByIndex(0) 失败 HRESULT=0x{HR:X8}，回落 ConvertToContiguousBuffer", hr);
        }

        // 路径(2)：NV12 CPU 直通（ConvertToContiguousBuffer = 绝对槽 41 → slotIndex 38）
        hr = MfVTable.Get<IMFSample_ConvertToContiguousBuffer>(samplePtr, 38)(samplePtr, out IntPtr bufferPtr);
        if (hr < 0 || bufferPtr == IntPtr.Zero)
        {
            _logger.LogWarning("[MF-D3D] 已解码视频样本 ConvertToContiguousBuffer 失败: HRESULT=0x{HR:X8}", hr);
            return null;
        }

        InteropTrace.OnAlloc(bufferPtr, "ExtractDecodedVideoPacket:bufferPtr");
        byte[] data;
        try
        {
            var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(bufferPtr, 0);
            var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(bufferPtr, 1);
            hr = InteropTrace.LockBuffer(bufferPtr, lockDel, out IntPtr dataPtr, out _, out uint curLen,
                "ExtractDecodedVideoPacket:IMFMediaBuffer.Lock");

            // COM 配对原则：Unlock 只能与【成功的】Lock 配对，且恰好一次。Lock 失败绝不 Unlock
            //    （2D/DXGI 临时拷贝实现的 Unlock 会回拷+释放临时区 ⇒ 未 Lock 即调用 = 野指针写）。
            if (hr < 0)
            {
                _logger.LogWarning("[MF-D3D] 已解码视频样本 Lock 失败: HRESULT=0x{HR:X8}（未 Unlock，符合配对规范）", hr);
                return null;
            }

            try
            {
                if (curLen == 0 || dataPtr == IntPtr.Zero)
                    return null; // 空缓冲：按流 tick 处理

                data = new byte[curLen];
                Marshal.Copy(dataPtr, data, 0, (int)curLen);
            }
            finally
            {
                InteropTrace.UnlockBuffer(bufferPtr, unlockDel, "ExtractDecodedVideoPacket:IMFMediaBuffer.Unlock");
            }
        }
        finally
        {
            InteropTrace.ReleaseComPtr(bufferPtr, "ExtractDecodedVideoPacket:bufferPtr");
        }

        // 行跨度：优先协商期回读的 MF_MT_DEFAULT_STRIDE；缺失时按 buffer 实长反推（NV12 总行数 = height*3/2）。
        // 反推值 < width 说明布局假定破产 ⇒ 置 0 交由下游按紧凑处理并留证，绝不静默按错误 stride 逐行拷贝。
        int stride = _decodedVideoStride;
        if (stride <= 0)
        {
            int totalRows = height * 3 / 2;
            int derived = totalRows > 0 ? data.Length / totalRows : 0;
            stride = derived >= width ? derived : 0;
        }

        _videoCpuFrames++;
        return new MediaPacket(streamIndex, data, ts, dur, keyFrame,
            width: width, height: height, stride: stride);
    }
}
