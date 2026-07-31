using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

/// <summary>
/// 基于 Media Foundation <c>IMFSourceReader</c> 的 <see cref="IMediaDemuxer"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>（与 FFmpegDemuxer 对称）：</para>
/// <list type="bullet">
/// <item><c>OpenAsync</c>：混合——<c>await stream.ConnectAsync</c>（真异步 I/O）+
/// <c>await Task.Run(OpenCore)</c>（<b>伪异步</b>：MFCreateSourceReaderFromURL + 轨道解析为同步 COM 调用，Task.Run 仅卸载到线程池）。
/// 未来改进：MF 可通过 IMFByteStream + 异步 BeginRead 实现真异步，但复杂度高暂不实施。</item>
/// <item><c>ReadPacketAsync</c>：<b>伪异步</b>——<c>await Task.Run</c> 卸载 IMFSourceReader.ReadSample（同步 COM 调用）到线程池。
/// 未来改进：MF 可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。</item>
/// <item><c>SeekAsync</c>：<b>伪异步</b>——<c>await Task.Run</c> 卸载 MF seek 到线程池。</item>
/// <item><c>InitializeAsync</c>：接口契约，返回 <c>Task.CompletedTask</c>。</item>
/// <item><c>Close</c> / <c>Dispose</c> / <c>DisposeAsync</c>：同步 COM 释放。</item>
/// </list>
/// <para><b>仅 Windows 可用</b>：非 Windows 平台 OpenAsync 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>线程安全</b>：单线程使用（BufferManager 读取线程），非线程安全。</para>
/// <para><b>AOT 兼容</b>：sealed 类，COM 互操作，无反射。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[UnconditionalSuppressMessage("Trimming", "IL2050",
    Justification = "无 [ComImport]，使用原始 vtable P/Invoke，不会被裁剪器移除。仅 Windows 运行时使用。")]
internal sealed class MFDemuxer : IMediaDemuxer
{
    private readonly MFBackend _backend;
    private readonly ILogger<MFDemuxer> _logger;

    private IntPtr _sourceReader; // IMFSourceReader*（原始 vtable P/Invoke，非 [ComImport]）
    private IMFSourceReader_ReadSample? _readSample; // 热路径缓存的 vtable 委托

    // 专用单线程调度器：所有 SourceReader COM 调用（OpenCore/ReadPacketCore/SeekAsync）均在此线程执行，
    // 保证 COM 对象单线程亲和，规避跨线程访问导致的原生堆损坏。
    private SingleThreadTaskScheduler? _readerScheduler;
    private TaskFactory? _readerFactory;

    private string? _url;
    private IMediaStream? _stream;

    private bool _opened;
    private bool _disposed;
    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    // 多流交织状态：IMFSourceReader.ReadSample 不接受 ALL_STREAMS（运行时返回 MF_E_INVALID_STREAM），
    // 须逐流调用 ReadSample 后按时间戳挑选最早者，模拟 FFmpeg 的交织输出。
    private int[] _selectedStreamIndices = Array.Empty<int>();
    private readonly Dictionary<int, MediaPacket> _pendingPackets = new();
    private readonly HashSet<int> _exhaustedStreams = new();

    /// <summary>
    /// 初始化 <see cref="MFDemuxer"/> 的新实例。
    /// </summary>
    /// <param name="backend">MF 后端入口（Singleton）。</param>
    /// <param name="logger">日志器。</param>
    public MFDemuxer(MFBackend backend, ILogger<MFDemuxer> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IReadOnlyList<MediaTrack> Tracks => _tracks;

    /// <inheritdoc/>
    public MediaMetadata Metadata => _metadata;

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 混合：<c>await stream.ConnectAsync</c>（真异步 I/O）+
    /// <c>await Task.Run(OpenCore)</c>（伪异步：MFCreateSourceReaderFromURL + 轨道解析为同步 COM 调用）。
    /// </remarks>
    public async Task OpenAsync(IMediaStream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "MediaFoundation 后端仅支持 Windows。请使用 FFmpeg 或 VLC 作为跨平台后端。");
        }

        ct.ThrowIfCancellationRequested();
        _stream = stream;

        // 异步预建连
        await stream.ConnectAsync(ct).ConfigureAwait(false);

        // 从 IMediaStream 获取 URL（文件路径或网络 URL）
        // MFSourceReader 需要 URL 或 IMFByteStream
        _url = ExtractUrl(stream);

        if (string.IsNullOrEmpty(_url))
        {
            throw new NotSupportedException(
                "MediaFoundation 后端当前仅支持 URL/文件路径源。对于流式输入，请使用 FFmpeg 后端。");
        }

        // 伪异步：MFCreateSourceReaderFromURL + 轨道解析为同步 COM 调用。
        // 全部 SourceReader COM 调用钉在专用单线程（SingleThreadTaskScheduler），避免跨线程池线程访问
        // IMFSourceReader 触发原生堆损坏（COR_E_EXECUTIONENGINE / 0x80131506，非确定性崩溃）。
        _readerScheduler = new SingleThreadTaskScheduler("MFDemuxer-Reader");
        _readerFactory = new TaskFactory(_readerScheduler);
        try
        {
            await _readerFactory.StartNew(() => OpenCore(_url!, ct), ct).ConfigureAwait(false);
            _opened = true;
        }
        catch
        {
            // OpenCore 失败：专用线程尚未承载在途任务，直接释放避免线程泄漏。
            _readerScheduler.Dispose();
            _readerScheduler = null;
            _readerFactory = null;
            throw;
        }

        _logger.LogInformation("MediaFoundation 打开成功: {TrackCount} 条轨道, 时长 {Duration}",
            _tracks.Count, _metadata.Duration);
    }

    /// <summary>
    /// OpenAsync 的同步核心逻辑。在 Task.Run 线程上执行（伪异步）。
    /// </summary>
    private void OpenCore(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        int hr = MFInterop.MFCreateSourceReaderFromURL(url, IntPtr.Zero, out IntPtr readerPtr);
        if (hr < 0 || readerPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException($"MFCreateSourceReaderFromURL 失败: HRESULT=0x{hr:X8}");
        }
        _sourceReader = readerPtr;
        // 热路径缓存 ReadSample vtable 委托（绝对槽 9 → index 6；mfreadwrite.idl 顺序：
        // GetStreamSelection=3, SetStreamSelection=4, GetNativeMediaType=5, GetCurrentMediaType=6,
        // SetCurrentMediaType=7, SetCurrentPosition=8, ReadSample=9, Flush=10, GetServiceForStream=11, GetPresentationAttribute=12）
        // ⚠️ 审计核验（2026-07-28）：原 index 5 命中 SetCurrentPosition、误改 index 7 命中 Flush（签名不符→栈破坏崩溃），
        // 正确值恒为 index 6（绝对槽 9）。以 Wine/ReactOS 镜像的 Windows SDK idl 为权威。
        _readSample = MfVTable.Get<IMFSourceReader_ReadSample>(_sourceReader, 6);

        // 解析轨道
        _tracks = ParseTracks(_sourceReader);

        // 选择所有流（让 SourceReader 输出所有轨道的采样）；SetStreamSelection = 槽 4 → index 1
        foreach (var track in _tracks)
        {
            hr = MfVTable.Get<IMFSourceReader_SetStreamSelection>(_sourceReader, 1)(_sourceReader, (uint)track.Index, true);
            if (hr < 0)
            {
                _logger.LogWarning("SetStreamSelection 失败: 流 {Index}, HRESULT=0x{HR:X8}", track.Index, hr);
            }
        }

        // 解析元数据（MF 不直接提供标题/艺术家等，从轨道推算时长）
        _metadata = new MediaMetadata
        {
            Duration = TimeSpan.Zero,
            ContainerFormat = ContainerFormat.Unknown
        };

        // 记录已选流索引，供 ReadPacketCore 逐流交织读取（track.Index == MF 流索引）
        _selectedStreamIndices = _tracks.Select(t => t.Index).ToArray();
        _pendingPackets.Clear();
        _exhaustedStreams.Clear();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 伪异步：<c>await Task.Run</c> 卸载 IMFSourceReader.ReadSample（同步 COM 调用）到线程池。
    /// 未来改进：可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。
    /// </remarks>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _sourceReader == IntPtr.Zero)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：IMFSourceReader.ReadSample 为同步 COM 调用；在专用单线程上执行（见 OpenAsync）。
        // 未来改进：可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。
        return await _readerFactory!.StartNew(() => ReadPacketCore(ct), ct).ConfigureAwait(false);
    }

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
        ct.ThrowIfCancellationRequested();

        const int maxEmptyRounds = 256; // 限制流 tick 空转轮数，避免无数据时空转卡死
        int emptyRounds = 0;

        while (true)
        {
            // 释放期止读：Stop/Dispose 取消后尽快退出循环，缩短 MediaPlayer 等待读线程的窗口，
            // 避免 5s join 超时后继续释放造成 use-after-free（配合 Close() 的调度器先行关闭双保险）。
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

            // 仍有活跃流但本轮未取到（流 tick）：限次重试，避免空转
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
        int hr = _readSample!(_sourceReader, (uint)streamIndex, 0,
            out int actualStreamIndex, out int streamFlags, out long timestamp, out IntPtr samplePtr);
        if (hr < 0)
        {
            // 防御：部分失败路径下原生侧可能已写入 *ppSample，须释放避免 COM 引用泄漏。
            if (samplePtr != IntPtr.Zero) Marshal.Release(samplePtr);
            _logger.LogWarning("IMFSourceReader.ReadSample(流{Stream}) 失败: HRESULT=0x{HR:X8}", streamIndex, hr);
            eos = true; // 出错视为该流结束，避免无限重试
            return null;
        }
        if ((streamFlags & MFConstants.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
        {
            eos = true;
            if (samplePtr != IntPtr.Zero) Marshal.Release(samplePtr);
            return null;
        }
        if (samplePtr == IntPtr.Zero)
            return null; // 流 tick，无数据，下次再试

        // 提取采样数据：ConvertToContiguousBuffer = 绝对槽 41 → index 38
        // （IMFAttributes 恰 30 方法，IMFSample 第 9 方法；运行时已验证 slot38 返回有效 buffer）
        hr = MfVTable.Get<IMFSample_ConvertToContiguousBuffer>(samplePtr, 38)(samplePtr, out IntPtr bufferPtr);
        if (hr < 0 || bufferPtr == IntPtr.Zero)
        {
            Marshal.Release(samplePtr);
            _logger.LogWarning("ConvertToContiguousBuffer 失败: HRESULT=0x{HR:X8}", hr);
            return null;
        }

        // Lock = 槽 3 → index 0；Unlock = 槽 4 → index 1（运行时已验证）
        var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(bufferPtr, 0);
        var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(bufferPtr, 1);
        hr = lockDel(bufferPtr, out IntPtr dataPtr, out uint maxLen, out uint curLen);
        if (hr < 0 || curLen == 0)
        {
            unlockDel(bufferPtr);
            Marshal.Release(bufferPtr);
            Marshal.Release(samplePtr);
            _logger.LogWarning("IMFMediaBuffer.Lock 失败: HRESULT=0x{HR:X8}", hr);
            return null;
        }

        byte[] data = new byte[curLen];
        Marshal.Copy(dataPtr, data, 0, (int)curLen);

        unlockDel(bufferPtr);
        Marshal.Release(bufferPtr);

        // 关键帧标记：MFSampleExtension_CleanPoint（IMFSample 继承 IMFAttributes，GetUINT32 = slotIndex 4）。
        // 属性缺失时按非关键帧处理（音频等无该属性的流不受影响——调用方仅对视频用 KeyFrame）。
        Guid cleanPointKey = MFConstants.MFSampleExtension_CleanPoint;
        bool keyFrame = MfVTable.Get<IMFMediaType_GetUINT32>(samplePtr, 4)(samplePtr, ref cleanPointKey, out uint cleanPoint) >= 0
                        && cleanPoint != 0;

        Marshal.Release(samplePtr);

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

    /// <inheritdoc/>
    /// <remarks>
    /// 伪异步：<c>await Task.Run</c> 卸载 MF seek 操作到线程池。
    /// </remarks>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _sourceReader == IntPtr.Zero)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 伪异步：MF SourceReader seek 为同步 COM 调用；在专用单线程上执行（见 OpenAsync）。
        return await _readerFactory!.StartNew(() =>
        {
            // IMFSourceReader::SetCurrentPosition（绝对槽 8 → slotIndex 5，槽位表已审计核验）。
            // guidTimeFormat = GUID_NULL → varPosition 为 100ns 单位（VT_I8）；
            // SourceReader 会定位到 ≤ 目标位置的最近关键帧起读。
            Guid timeFormat = Guid.Empty;
            var pos = new MfPropVariant { vt = MfPropVariant.VT_I8, hVal = position.Ticks };
            var setPosition = MfVTable.Get<IMFSourceReader_SetCurrentPosition>(_sourceReader, 5);
            int hr = setPosition(_sourceReader, ref timeFormat, ref pos);
            if (hr < 0)
            {
                _logger.LogWarning("MF Seek 失败: {Position}, HRESULT=0x{HR:X8}", position, hr);
                return false;
            }

            // seek 后 lookahead 缓存全部失效：释放未投递的数据包并重置 EOS 标记
            foreach (var pkt in _pendingPackets.Values)
                pkt.Dispose();
            _pendingPackets.Clear();
            _exhaustedStreams.Clear();

            _logger.LogDebug("MF Seek 到 {Position}", position);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (!_opened) return;
        _opened = false;

        // ⚠️ 释放顺序铁律（2026-07-31 审计修复）：必须【先】关调度器、【后】释放 SourceReader。
        // SingleThreadTaskScheduler.Dispose 语义 = CompleteAdding + 排空队列中全部待执行任务 + Join(2s)。
        // 若先 Marshal.Release(_sourceReader)，排空阶段仍会执行排队的 ReadPacketCore/SeekAsync 任务，
        // 它们拿着悬空指针调 ReadSample → 原生 use-after-free → 0x80131506（COR_E_EXECUTIONENGINE）
        // 非确定性堆损坏崩溃（曾在 dotnet test Run2/Run3 实测复现，崩溃栈落在 ReadPacketAsync ← ReaderLoopAsync）。
        if (_readerScheduler != null)
        {
            _readerScheduler.Dispose();
            _readerScheduler = null;
            _readerFactory = null;
        }

        // 调度器已排空并退出——此后不可能再有任何线程触碰 _sourceReader，才允许释放。
        // 释放尚未投递的 lookahead 数据包（MediaPacket 独立拥有托管副本，Dispose 兜底，防泄漏）
        foreach (var pkt in _pendingPackets.Values)
            pkt.Dispose();
        _pendingPackets.Clear();
        _exhaustedStreams.Clear();

        if (_sourceReader != IntPtr.Zero)
        {
            try
            {
                Marshal.Release(_sourceReader);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "IMFSourceReader 释放异常");
            }
            _sourceReader = IntPtr.Zero;
            _readSample = null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：COM 释放为快速同步操作，委托 Dispose + CompletedTask。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Close();
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 从 IMediaStream 提取 URL（文件路径或网络 URL）。
    /// </summary>
    private static string? ExtractUrl(IMediaStream stream)
    {
        // 修复 C4：原实现误判 stream is FileStream（实际为 FileMediaStream，永不命中）导致所有文件均返回 null 而 OpenAsync 抛异常。
        // 改为读取 IMediaStream 中性 Location（文件流返回路径、网络流返回 URL；无地址返回 null）。
        return stream.Location;
    }

    /// <summary>
    /// 解析 MF SourceReader 的轨道信息。
    /// </summary>
    /// <remarks>
    /// 实例方法（非 static）：需要 <c>_logger</c> 上报属性缺失/媒体类型协商失败——这些是静默失败的高发区，
    /// 无日志会让下游拿到 0Hz/0ch 或压缩裸流而无从排查（2026-07-31 实锤，勿改回 static）。
    /// </remarks>
    private IReadOnlyList<MediaTrack> ParseTracks(IntPtr readerPtr)
    {
        var tracks = new List<MediaTrack>();
        int index = 0;

        // GetNativeMediaType = 槽 5 → index 2
        var getNativeMediaType = MfVTable.Get<IMFSourceReader_GetNativeMediaType>(readerPtr, 2);

        // IMFMediaType vtable 委托（继承 IMFAttributes）：GetMajorType=槽33→30、GetUINT32=槽7→4、GetUINT64=槽8→5、GetGuid=槽10→7。
        // 在拿到首个 mediaType 指针后解析一次（所有 IMFMediaType 实例 vtable 相同），循环内复用。
        IMFMediaType_GetMajorType? getMajorType = null;
        IMFMediaType_GetUINT32? getUINT32 = null;
        IMFMediaType_GetUINT64? getUINT64 = null;
        IMFMediaType_GetGuid? getGuid = null;

        // 遍历所有流
        while (true)
        {
            int hr = getNativeMediaType(readerPtr, (uint)index, 0, out IntPtr mediaTypePtr);
            if (hr == MFConstants.MF_E_NO_MORE_TYPES || hr < 0)
                break;

            if (mediaTypePtr == IntPtr.Zero)
            {
                index++;
                continue;
            }

            if (getMajorType == null)
            {
                getMajorType = MfVTable.Get<IMFMediaType_GetMajorType>(mediaTypePtr, 30);
                getUINT32 = MfVTable.Get<IMFMediaType_GetUINT32>(mediaTypePtr, 4);
                getUINT64 = MfVTable.Get<IMFMediaType_GetUINT64>(mediaTypePtr, 5);
                getGuid = MfVTable.Get<IMFMediaType_GetGuid>(mediaTypePtr, 7);
            }

            getMajorType(mediaTypePtr, out Guid majorType);

            MediaTrack? track = null;

            if (majorType == MFConstants.MFMediaType_Video)
            {
                Guid subtypeKey = MFConstants.MF_MT_SUBTYPE;
                Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
                getGuid!(mediaTypePtr, ref subtypeKey, out Guid subtype);
                getUINT64!(mediaTypePtr, ref frameSizeKey, out ulong frameSize);
                int width = (int)(frameSize >> 32);
                int height = (int)(frameSize & 0xFFFFFFFF);

                track = new MediaTrack
                {
                    Index = index,
                    Type = TrackType.Video,
                    VideoCodec = MapVideoCodec(subtype),
                    VideoInfo = new VideoTrackInfo
                    {
                        Width = width,
                        Height = height,
                        Duration = TimeSpan.Zero
                    }
                };

                // 提取 H264/H265 解码必需的 out-of-band SPS+PPS（Annex-B 序列头）。
                // MP4(AVCC) 容器内 SPS/PPS 在 avcC 盒、不在每个 sample 内联；不透传给解码器则
                // IMFTransform::ProcessOutput 永久返回 MF_E_TRANSFORM_NEED_MORE_INPUT。
                // 优先直取 MF_MT_MPEG_SEQUENCE_HEADER；缺失则从 MF_MT_MPEG4_SAMPLE_DESCRIPTION（整个 stsd 盒）解析 avcC。
                // 注：早期「MF 媒体源不会填 MF_MT_MPEG_SEQUENCE_HEADER」的结论建立在错误 GUID 上（恒 ATTRIBUTENOTFOUND），
                //     GUID 已于 2026-07-31 依 SDK 头文件修正，两条路径产出均为 Annex-B，可安全并存。
                if (track.VideoCodec is VideoCodec.H264 or VideoCodec.H265)
                {
                    var seqHeader = TryGetBlob(mediaTypePtr, MFConstants.MF_MT_MPEG_SEQUENCE_HEADER);
                    if (seqHeader.Length == 0)
                    {
                        var stsd = TryGetBlob(mediaTypePtr, MFConstants.MF_MT_MPEG4_SAMPLE_DESCRIPTION);
                        if (stsd.Length > 0)
                            seqHeader = ParseAvcCToAnnexB(stsd);
                    }
                    track.VideoInfo!.CodecConfiguration = seqHeader;
                }
            }
            else if (majorType == MFConstants.MFMediaType_Audio)
            {
                Guid subtypeKey = MFConstants.MF_MT_SUBTYPE;
                Guid sampleRateKey = MFConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND;
                Guid channelsKey = MFConstants.MF_MT_AUDIO_NUM_CHANNELS;
                Guid bitsPerSampleKey = MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE;
                getGuid!(mediaTypePtr, ref subtypeKey, out Guid audioSubtype);

                // ⚠️ 必须检查 HRESULT：GetUINT32 失败时 out 参数为 0，静默吞掉会让下游拿到
                // SampleRate=0 / Channels=0 去初始化 WASAPI（2026-07-31 实锤：属性键 GUID 写错时正是如此，
                // 且因无 hr 检查而长期无声失败）。缺失时回落到 CD 音质默认并告警。
                if (getUINT32!(mediaTypePtr, ref sampleRateKey, out uint sampleRate) < 0 || sampleRate == 0)
                {
                    _logger.LogWarning("音频流 {Index} 缺少 MF_MT_AUDIO_SAMPLES_PER_SECOND，回落 44100Hz", index);
                    sampleRate = 44100;
                }
                if (getUINT32!(mediaTypePtr, ref channelsKey, out uint channels) < 0 || channels == 0)
                {
                    _logger.LogWarning("音频流 {Index} 缺少 MF_MT_AUDIO_NUM_CHANNELS，回落 2 声道", index);
                    channels = 2;
                }
                if (getUINT32!(mediaTypePtr, ref bitsPerSampleKey, out uint bitsPerSample) < 0 || bitsPerSample == 0)
                {
                    _logger.LogWarning("音频流 {Index} 缺少 MF_MT_AUDIO_BITS_PER_SAMPLE，回落 16bit", index);
                    bitsPerSample = 16;
                }

                // ⚠️ 关键（2026-07-31 修复）：SourceReader 默认输出**压缩原生格式**（AAC/MP3 裸流）。
                // MFAudioDecoder 是直通实现（不自带 MFT），若不在此显式协商为 PCM，
                // 下游会把 AAC 字节当成 S16 PCM 直喂 WASAPI → 噪声/静音。
                // 此前该缺陷被 IID_IAudioRenderClient 的 GUID 错误（音频链路根本没跑起来）长期掩盖。
                var pcm = ConfigureAudioStreamToPcm(readerPtr, index, sampleRate, channels, bitsPerSample);

                track = new MediaTrack
                {
                    Index = index,
                    Type = TrackType.Audio,
                    AudioCodec = MapAudioCodec(audioSubtype), // 保留源编码标识（AAC/MP3），供 UI/诊断显示
                    AudioInfo = new AudioTrackInfo
                    {
                        // 注意：此处为 SourceReader **输出**（解码后 PCM）的实测参数，
                        // 而非容器内压缩流的参数——MediaPlayer 据此初始化 WASAPI 设备，必须是输出侧。
                        SampleRate = pcm.SampleRate,
                        Channels = pcm.Channels,
                        BitsPerSample = pcm.BitsPerSample,
                        Duration = TimeSpan.Zero
                    }
                };
            }

            if (track != null)
            {
                tracks.Add(track);
            }

            Marshal.Release(mediaTypePtr);
            index++;
        }

        return tracks;
    }

    /// <summary>
    /// 把指定音频流协商为**解码后 PCM** 输出，并回读 MF 实测采纳的格式。
    /// </summary>
    /// <param name="readerPtr">IMFSourceReader*。</param>
    /// <param name="streamIndex">MF 流索引。</param>
    /// <param name="nativeSampleRate">原生（压缩）媒体类型上的采样率，协商失败时作为回落值。</param>
    /// <param name="nativeChannels">原生声道数，协商失败时作为回落值。</param>
    /// <param name="nativeBits">原生位深，协商失败时作为回落值。</param>
    /// <returns>SourceReader 输出侧实测的 PCM 参数。</returns>
    /// <remarks>
    /// <para>MSDN 推荐做法：只设 MAJOR_TYPE=Audio + SUBTYPE=PCM 的<b>部分类型</b>，其余字段留空，
    /// SourceReader 会自动加载对应解码器（AAC/MP3 Decoder MFT）+ 必要的重采样器，并按源填充剩余字段。</para>
    /// <para>本实现额外显式要求 16bit：下游 <c>AudioFrame</c>/WASAPI 按 S16 切分字节，
    /// 若个别源协商出 32bit 会导致帧数计算错误。若 MFT 拒绝该约束（hr&lt;0），
    /// 剔除 BITS_PER_SAMPLE 后以纯部分类型重试，最大化兼容性。</para>
    /// </remarks>
    private (int SampleRate, int Channels, int BitsPerSample) ConfigureAudioStreamToPcm(
        IntPtr readerPtr, int streamIndex, uint nativeSampleRate, uint nativeChannels, uint nativeBits)
    {
        var fallback = ((int)nativeSampleRate, (int)nativeChannels, (int)nativeBits);

        // 未选中的流不会被 SourceReader 建管线，SetCurrentMediaType 亦无从协商——先行选中（幂等，
        // OpenCore 稍后仍会统一再选一次）。SetStreamSelection = 绝对槽 4 → slotIndex 1。
        int hr = MfVTable.Get<IMFSourceReader_SetStreamSelection>(readerPtr, 1)(readerPtr, (uint)streamIndex, true);
        if (hr < 0)
        {
            _logger.LogWarning("音频流 {Index} SetStreamSelection 失败: HRESULT=0x{HR:X8}，跳过 PCM 协商", streamIndex, hr);
            return fallback;
        }

        if (MFInterop.MFCreateMediaType(out IntPtr pcmType) < 0 || pcmType == IntPtr.Zero)
        {
            _logger.LogWarning("音频流 {Index} MFCreateMediaType 失败，跳过 PCM 协商（将输出压缩裸流）", streamIndex);
            return fallback;
        }

        try
        {
            // SetGUID = slotIndex 21（已运行时验证，见 MFComInterfaces 槽位表）
            var setGuid = MfVTable.Get<IMFAttributes_SetGUID>(pcmType, 21);
            Guid majorKey = MFConstants.MF_MT_MAJOR_TYPE;
            Guid majorVal = MFConstants.MFMediaType_Audio;
            Guid subKey = MFConstants.MF_MT_SUBTYPE;
            Guid subVal = MFConstants.MFAudioFormat_PCM;
            if (setGuid(pcmType, ref majorKey, ref majorVal) < 0 || setGuid(pcmType, ref subKey, ref subVal) < 0)
            {
                _logger.LogWarning("音频流 {Index} 构造 PCM 媒体类型失败，跳过 PCM 协商", streamIndex);
                return fallback;
            }

            Guid bitsKey = MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE;
            MfVTable.Get<IMFAttributes_SetUINT32>(pcmType, 18)(pcmType, ref bitsKey, 16);

            // SetCurrentMediaType = 绝对槽 7 → slotIndex 4（mfreadwrite.h:386 核验）；第二参数为保留 DWORD*，必须 NULL。
            var setCurrent = MfVTable.Get<IMFSourceReader_SetCurrentMediaType>(readerPtr, 4);
            hr = setCurrent(readerPtr, (uint)streamIndex, IntPtr.Zero, pcmType);
            if (hr < 0)
            {
                // 退一步：剔除 16bit 约束（DeleteItem = slotIndex 16），以纯部分类型再试
                MfVTable.Get<IMFAttributes_DeleteItem>(pcmType, 16)(pcmType, ref bitsKey);
                hr = setCurrent(readerPtr, (uint)streamIndex, IntPtr.Zero, pcmType);
            }
            if (hr < 0)
            {
                _logger.LogError("音频流 {Index} SetCurrentMediaType(PCM) 失败: HRESULT=0x{HR:X8}。" +
                    "SourceReader 将继续输出压缩裸流，音频输出会异常。", streamIndex, hr);
                return fallback;
            }
        }
        finally
        {
            Marshal.Release(pcmType);
        }

        // 回读 MF 实际采纳的输出类型（采样率/声道通常沿用源，位深为 16）。
        // GetCurrentMediaType = 绝对槽 6 → slotIndex 3。
        hr = MfVTable.Get<IMFSourceReader_GetCurrentMediaType>(readerPtr, 3)(readerPtr, (uint)streamIndex, out IntPtr actualType);
        if (hr < 0 || actualType == IntPtr.Zero)
        {
            _logger.LogWarning("音频流 {Index} GetCurrentMediaType 失败: HRESULT=0x{HR:X8}，沿用原生参数", streamIndex, hr);
            return fallback;
        }

        try
        {
            var getUINT32 = MfVTable.Get<IMFMediaType_GetUINT32>(actualType, 4);
            Guid rateKey = MFConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND;
            Guid chKey = MFConstants.MF_MT_AUDIO_NUM_CHANNELS;
            Guid bitsKey = MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE;

            if (getUINT32(actualType, ref rateKey, out uint rate) < 0 || rate == 0)
                rate = nativeSampleRate;
            if (getUINT32(actualType, ref chKey, out uint ch) < 0 || ch == 0)
                ch = nativeChannels;
            if (getUINT32(actualType, ref bitsKey, out uint bits) < 0 || bits == 0)
                bits = 16; // PCM 协商成功但未回填位深：MF 默认 16bit

            _logger.LogInformation("音频流 {Index} 已协商为 PCM 输出: {Rate}Hz {Ch}ch {Bits}bit", streamIndex, rate, ch, bits);
            return ((int)rate, (int)ch, (int)bits);
        }
        finally
        {
            Marshal.Release(actualType);
        }
    }

    /// <summary>读取 IMFAttributes Blob 属性（GetBlobSize=slot11 / GetBlob=slot12）。属性不存在返回空数组。</summary>
    private static byte[] TryGetBlob(IntPtr attributesPtr, Guid key)
    {
        var getBlobSize = MfVTable.Get<IMFAttributes_GetBlobSize>(attributesPtr, 11);
        if (getBlobSize(attributesPtr, ref key, out uint size) < 0 || size == 0)
            return Array.Empty<byte>();

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var getBlob = MfVTable.Get<IMFAttributes_GetBlob>(attributesPtr, 12);
            if (getBlob(attributesPtr, ref key, buffer, size) < 0)
                return Array.Empty<byte>();

            var result = new byte[size];
            Marshal.Copy(buffer, result, 0, (int)size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 从 stsd 盒数据（MF_MT_MPEG4_SAMPLE_DESCRIPTION 透传）中定位 avcC / hvcC 盒，
    /// 把参数集（SPS/PPS/VPS）转换为 Annex-B 序列头（00 00 00 01 起始码拼接）。
    /// 解析失败返回空数组（解码器侧兜底：无序列头时不设置 MF_MT_MPEG_SEQUENCE_HEADER）。
    /// </summary>
    private static byte[] ParseAvcCToAnnexB(byte[] stsd)
    {
        // avcC 盒（ISO/IEC 14496-15 5.3.3.1）：configurationVersion(1) profile(1) compat(1) level(1)
        //   lengthSizeMinusOne(1, 低2位) numOfSPS(1, 低5位) { spsLen(2BE) sps } numOfPPS(1) { ppsLen(2BE) pps }
        int avcc = IndexOfFourCC(stsd, (byte)'a', (byte)'v', (byte)'c', (byte)'C');
        if (avcc >= 0)
        {
            var output = new List<byte>(64);
            int p = avcc + 4; // 跳过 fourcc，指向 configurationVersion
            if (p + 6 > stsd.Length) return Array.Empty<byte>();
            int numSps = stsd[p + 5] & 0x1F;
            p += 6;
            for (int i = 0; i < numSps; i++)
                if (!AppendLengthPrefixedNal(stsd, ref p, output)) return Array.Empty<byte>();
            if (p >= stsd.Length) return Array.Empty<byte>();
            int numPps = stsd[p];
            p += 1;
            for (int i = 0; i < numPps; i++)
                if (!AppendLengthPrefixedNal(stsd, ref p, output)) return Array.Empty<byte>();
            return output.ToArray();
        }

        // hvcC 盒（ISO/IEC 14496-15 8.3.3.1）：22 字节固定头 + numOfArrays(1) +
        //   每数组 { arrayHeader(1) numNalus(2BE) { naluLen(2BE) nalu } }（含 VPS/SPS/PPS 数组）
        int hvcc = IndexOfFourCC(stsd, (byte)'h', (byte)'v', (byte)'c', (byte)'C');
        if (hvcc >= 0)
        {
            var output = new List<byte>(128);
            int p = hvcc + 4 + 22;
            if (p >= stsd.Length) return Array.Empty<byte>();
            int numArrays = stsd[p];
            p += 1;
            for (int a = 0; a < numArrays; a++)
            {
                if (p + 3 > stsd.Length) return Array.Empty<byte>();
                int numNalus = (stsd[p + 1] << 8) | stsd[p + 2];
                p += 3;
                for (int n = 0; n < numNalus; n++)
                    if (!AppendLengthPrefixedNal(stsd, ref p, output)) return Array.Empty<byte>();
            }
            return output.ToArray();
        }

        return Array.Empty<byte>();
    }

    /// <summary>读取「2 字节大端长度 + NAL 数据」并以 00 00 00 01 起始码追加到 output。越界返回 false。</summary>
    private static bool AppendLengthPrefixedNal(byte[] data, ref int p, List<byte> output)
    {
        if (p + 2 > data.Length) return false;
        int len = (data[p] << 8) | data[p + 1];
        p += 2;
        if (len <= 0 || p + len > data.Length) return false;
        output.Add(0); output.Add(0); output.Add(0); output.Add(1);
        for (int i = 0; i < len; i++) output.Add(data[p + i]);
        p += len;
        return true;
    }

    /// <summary>在字节数组中查找 4 字节 fourcc，返回起始索引；未找到返回 -1。</summary>
    private static int IndexOfFourCC(byte[] data, byte c0, byte c1, byte c2, byte c3)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
            if (data[i] == c0 && data[i + 1] == c1 && data[i + 2] == c2 && data[i + 3] == c3)
                return i;
        return -1;
    }

    private static AudioCodec MapAudioCodec(Guid subtype) => subtype switch
    {
        _ when subtype == MFConstants.MFAudioFormat_AAC => AudioCodec.AAC,
        _ when subtype == MFConstants.MFAudioFormat_MP3 => AudioCodec.MP3,
        _ when subtype == MFConstants.MFAudioFormat_PCM => AudioCodec.PCM,
        _ => AudioCodec.Unknown
    };

    private static VideoCodec MapVideoCodec(Guid subtype) => subtype switch
    {
        _ when subtype == MFConstants.MFVideoFormat_H264 => VideoCodec.H264,
        _ when subtype == MFConstants.MFVideoFormat_H265 => VideoCodec.H265,
        _ => VideoCodec.Unknown
    };
}
