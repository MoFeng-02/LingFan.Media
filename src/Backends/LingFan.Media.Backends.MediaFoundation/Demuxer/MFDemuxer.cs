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
    private readonly MfDxgiDeviceManagerProvider? _dxgiManagerProvider;
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

    // 两阶段关闭协议构件：关闸 → 排空在途原生调用 → 独占释放或意泄漏。
    private readonly NativeCallGate _readerGate = new();
    private bool _leakedOnClose;   // drain 失败标记：已有意泄漏，禁止任何后续释放尝试

    // 本类必须**自己**持有一份 MF 平台引用，不能只靠构造注入的
    // MFBackend「活着」。持有对象引用只防 GC，不防它被 Dispose——MFStartup/MFShutdown 是进程级的。
    // 缺这份引用时，泄漏路径只护住了 COM 指针、没护住平台：drain 超时后读取线程仍卡在原生
    // ReadSample 内，随后 MFBackend.Dispose() 把引用计数打到 0 ⇒ 真正的 MFShutdown 拆掉整个 MF 平台
    // ⇒ 在途调用的内部状态被抽走 ⇒ 访问违规 ⇒ 原生堆损坏。测试进程里尤其致命：每个用例都新建/释放
    // 一个 MFBackend（计数 0↔1，反复真启停平台），上一用例泄漏的在途线程会被下一次 MFShutdown 踩死，
    // 表现为"冷启动偶发崩溃"。持有自身引用后，泄漏路径永不递减 ⇒ 平台永不拆除 ⇒ 在途调用始终有效，
    // 这正是「宁可泄漏也绝不释放」语义在平台层的自然延伸。
    private bool _mfStartupAcquired;

    // 防止 Close/Dispose/DisposeAsync 重入。0=未开始，1=已开始。
    // 必须是 Interlocked 原子量而非普通 bool：Dispose(sync) 与 DisposeAsync 若在不同线程并发，
    // 普通 bool 的「读-判-写」非原子，两者可同时通过守卫 ⇒ 对同一 IMFSourceReader 执行两次 Marshal.Release
    // ⇒ 引用计数下溢 / 访问违例——正是要根除的原生堆损坏故障族。
    private int _closeStarted;
    private IReadOnlyList<MediaTrack> _tracks = Array.Empty<MediaTrack>();
    private MediaMetadata _metadata = new();

    // 多流交织状态：IMFSourceReader.ReadSample 不接受 ALL_STREAMS（运行时返回 MF_E_INVALID_STREAM），
    // 须逐流调用 ReadSample 后按时间戳挑选最早者，模拟 FFmpeg 的交织输出。
    private int[] _selectedStreamIndices = Array.Empty<int>();
    private readonly Dictionary<int, MediaPacket> _pendingPackets = new();
    private readonly HashSet<int> _exhaustedStreams = new();

    // ── A 方案：SourceReader 自带硬解 + DXGI 出样（零拷贝直通）状态 ──
    // 命中时本 demuxer 变成「解封装 + 解码一体」：ReadSample 直接吐已解码帧
    // （GPU 纹理 → MediaPacket.DecodedFrameResource；退化时 NV12 CPU 字节 → Data+Width/Height/Stride），
    // 下游 MFVideoDecoder 退化为直通适配器，不再跑自己的 MFT。
    // -1 = 未启用（视频流仍输出压缩裸流，走原 MFVideoDecoder MFT 路径，行为与改造前完全一致）。
    private int _decodedVideoStreamIndex = -1;
    private int _decodedVideoWidth;
    private int _decodedVideoHeight;
    private int _decodedVideoStride;     // NV12 CPU 回落时的行跨度（<=0 表示按紧凑 width 处理）
    private bool _loggedVideoPathOnce;   // 首帧一次性诊断：真零拷贝 vs 半 DXVA 回落
    // 路径①-A（IMF2DBuffer2）必须用**独立**闸门：半 DXVA 回落的 warning 会先把 _loggedVideoPathOnce
    //    置位，若共用则「Lock2D 治本成功」的日志永远打不出来 —— 表现为「代码在跑但看不见证据」，
    //    极易让人以为改动未生效。诊断闸门与它守护的分支必须一一对应。
    private bool _loggedVideo2DPathOnce; // 首帧一次性诊断：IMF2DBuffer2 真值 pitch 紧凑化路径
    private long _videoZeroCopyFrames;
    private long _videoCpuFrames;
    private bool _hardwareReaderRequested; // SourceReader 创建时是否成功挂上了 D3D 管理器
    private readonly MediaFoundationOptions _options;

    /// <summary>
    /// 初始化 <see cref="MFDemuxer"/> 的新实例。
    /// </summary>
    /// <param name="backend">MF 后端入口（Singleton）。</param>
    /// <param name="dxgiManagerProvider">
    /// DXGI 设备管理器提供者（Singleton）。用于在 SourceReader 上挂 <c>MF_SOURCE_READER_D3D_MANAGER</c>，
    /// 令其内部解码 MFT 在共享 D3D11 设备上分配输出表面 ⇒ ReadSample 直接吐 GPU 纹理。
    /// 传 <see langword="null"/> 或其返回 <see cref="IntPtr.Zero"/> 时静默回落「压缩裸流 + MFVideoDecoder 自解码」老路径。
    /// </param>
    /// <param name="logger">日志器。</param>
    /// <param name="options">
    /// MF 后端选项。传 <see langword="null"/> 时取默认值（全部启用）。当前仅用到
    /// <see cref="MediaFoundationOptions.EnableReaderDecodeFusion"/>——关闭后跳过 NV12 协商，
    /// 该流继续输出压缩裸流，交由 MFVideoDecoder 自管 MFT 解码（零拷贝定界用）。
    /// </param>
    public MFDemuxer(MFBackend backend, MfDxgiDeviceManagerProvider? dxgiManagerProvider, ILogger<MFDemuxer> logger,
        MediaFoundationOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _dxgiManagerProvider = dxgiManagerProvider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new MediaFoundationOptions();
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

        // 关闭是**不可逆**的（gate 一旦 BeginClose 便永久关闸，见 NativeCallGate 关闭不变量）。
        // 若允许在已关闭实例上重开，OpenCore 的 TryEnter 会失败 ⇒ 出现「_opened=true 但 _sourceReader==Zero」的半开状态。
        // Session 级对象按约定为 Transient（MediaPlayer.OpenAsync 内新建），故此处直接快速失败而非静默降级。
        if (Volatile.Read(ref _closeStarted) != 0)
            throw new InvalidOperationException("该 MFDemuxer 实例已关闭，不可重复打开；请新建实例。");

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
        // IMFSourceReader 触发原生堆损坏（COR_E_EXECUTIONENGINE，非确定性崩溃）。
        // 持本地引用后再发布到字段。并发的 Close 可能在下面 await 期间跑完整套关闭协议，
        // 其 ReleaseNativeResources 会把 _readerScheduler 置 null；届时 catch 里的 CloseSync 又因
        // _closeStarted 已置位而空转 ⇒ 本方法刚创建的调度器无人关闭 ⇒ 后台线程 + 队列泄漏。
        var scheduler = new SingleThreadTaskScheduler("MFDemuxer-Reader");
        var factory = new TaskFactory(scheduler);
        _readerScheduler = scheduler;
        _readerFactory = factory;
        try
        {
            await factory.StartNew(() => OpenCore(_url!, ct), ct).ConfigureAwait(false);
            _opened = true;
        }
        catch
        {
            // OpenCore 可能已在创建 _sourceReader 后抛异常（如 ParseTracks 失败）。
            // 原逻辑仅 Dispose 调度器会泄漏 _sourceReader。此处改走两阶段关闭协议：此时无在途原生调用，
            // drain 立即可成功，_sourceReader 被安全释放。
            CloseSync();

            // 兜底：CloseSync 若因他线程已发起关闭而空转，本地 scheduler 不会被关闭。
            // 用**零超时**——只做 CompleteAdding（足以让后台线程排空并自行退出），不在 async 链路上引入
            // 任何同步阻塞；Shutdown 内部 Interlocked 幂等，与 CloseSync 中的调用重复无副作用。
            //
            // COM 单元不变量守卫：仅在**未走泄漏路径**时才兜底关闭。放行专用线程退出即放行其
            // CoUninitialize，会拆掉泄漏中的 COM 指针所属单元、卸载其 in-proc server ⇒ 泄漏保护失效。
            if (!_leakedOnClose)
                scheduler.Shutdown(TimeSpan.Zero);
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
        // 整个 OpenCore 处于 gate 内，使 _sourceReader/_readSample 的建立受关闭协议保护。
        // 关闸期进入失败必须**显式抛出**（而非静默 return）：静默返回会让 OpenAsync 把 _opened 置 true，
        // 却没有 _sourceReader ⇒ 半开状态。抛出后由 OpenAsync 的 catch 走 CloseSync 并向上传播。
        if (!_readerGate.TryEnter())
            throw new InvalidOperationException("MFDemuxer 正在关闭，无法打开媒体源。");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();

            // 在**任何** MF 原生调用之前取得平台引用，且与 ReleaseNativeResources 中的
            // MFPlatform.Shutdown 严格配对（泄漏路径 LeakNativeResources 刻意不递减）。
            // 幂等守卫：即便调用方违约重复 OpenAsync，也只 +1，避免计数被多加后再也归不了 0。
            if (!_mfStartupAcquired)
            {
                MFPlatform.Startup();
                _mfStartupAcquired = true;
            }

            // ── A 方案：把 IMFDXGIDeviceManager 挂到 SourceReader 的创建属性上 ──
            // SourceReader 拿到管理器后会自行完成「选硬件 MFT → 发 MFT_MESSAGE_SET_D3D_MANAGER →
            // 分配 DXGI 输出表面池」的全套编排（这正是直连 MFT 时会静默回落软件的那一段）。
            // 失败语义：任何一步不成都返回 IntPtr.Zero，等价改造前的「无属性」行为 ⇒ 压缩裸流 + MFVideoDecoder 自解码。
            IntPtr readerAttributes = TryCreateHardwareReaderAttributes(out bool hardwareRequested);
            _hardwareReaderRequested = hardwareRequested;

            int hr;
            IntPtr readerPtr;
            try
            {
                hr = MFInterop.MFCreateSourceReaderFromURL(url, readerAttributes, out readerPtr);
            }
            finally
            {
                // COM 配对：属性 store 的内容在创建时已被 SourceReader 复制（IUnknown 项自行 AddRef），
                // 本类持有的这一份引用用完即释放；管理器本体的生命周期归 MfDxgiDeviceManagerProvider。
                if (readerAttributes != IntPtr.Zero) Marshal.Release(readerAttributes);
            }
            if (hr < 0 || readerPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException($"MFCreateSourceReaderFromURL 失败: HRESULT=0x{hr:X8}");
            }
            _sourceReader = readerPtr;
            // 热路径缓存 ReadSample vtable 委托（绝对槽 9 → index 6；mfreadwrite.idl 顺序：
            // GetStreamSelection=3, SetStreamSelection=4, GetNativeMediaType=5, GetCurrentMediaType=6,
            // SetCurrentMediaType=7, SetCurrentPosition=8, ReadSample=9, Flush=10, GetServiceForStream=11, GetPresentationAttribute=12）
            // 核验：原 index 5 命中 SetCurrentPosition、误改 index 7 命中 Flush（签名不符→栈破坏崩溃），
            // 正确值恒为 index 6（绝对槽 9）。以 Wine/ReactOS 镜像的 Windows SDK idl 为权威。
            _readSample = MfVTable.Get<IMFSourceReader_ReadSample>(_sourceReader, 6);
            _logger.LogInformation("[OPEN-DIAG] 创建 SourceReader+缓存vtable 耗时 {Ms}ms", sw.ElapsedMilliseconds);
            sw.Restart();

            // 实查容器时长：MF 不自动填时长，须从 presentation descriptor 取 MF_PD_DURATION。
            // 这是完整播放「几秒假完成」现象的成因修复点——此前硬编码 TimeSpan.Zero 使 player.Duration 恒 0。
            var duration = QueryContainerDuration(_sourceReader);

            // 解析轨道（携带容器时长，供各轨 VideoInfo/AudioInfo.Duration 与容器保持一致）
            _tracks = ParseTracks(_sourceReader, duration);
            _logger.LogInformation("[OPEN-DIAG] ParseTracks(含音频PCM协商/AAC激活) 耗时 {Ms}ms", sw.ElapsedMilliseconds);
            sw.Restart();

            // 选择所有流（让 SourceReader 输出所有轨道的采样）；SetStreamSelection = 槽 4 → index 1
            foreach (var track in _tracks)
            {
                hr = MfVTable.Get<IMFSourceReader_SetStreamSelection>(_sourceReader, 1)(_sourceReader, (uint)track.Index, true);
                if (hr < 0)
                {
                    _logger.LogWarning("SetStreamSelection 失败: 流 {Index}, HRESULT=0x{HR:X8}", track.Index, hr);
                }
            }

            // 记录已选流索引，供 ReadPacketCore 逐流交织读取（track.Index == MF 流索引）
            _selectedStreamIndices = _tracks.Select(t => t.Index).ToArray();

            // 兜底：若 presentation descriptor 无 MF_PD_DURATION（少数 MP4/ fragmented 文件会缺失），
            // 推算容器时长。优先「末尾定位探测」（O(log n) 索引跳转 + 读少量末帧，远快于整段排空，
            // 消除 OpenAsync 内同步阻塞导致的启动卡顿）；仅极个别无索引/不可定位源才退化整段排空。
            // 必须在 _selectedStreamIndices 就绪后调用。
            if (duration <= TimeSpan.Zero)
            {
                duration = ProbeDurationByEndSeek(_sourceReader);
                if (duration <= TimeSpan.Zero)
                    duration = ProbeDurationByDraining(_sourceReader);
            }
            _logger.LogInformation("[OPEN-DIAG] 时长探测(末尾定位/整段排空) 耗时 {Ms}ms", sw.ElapsedMilliseconds);
            sw.Restart();

            // 解析元数据（MF 不直接提供标题/艺术家等；时长由 QueryContainerDuration 实查，非硬编码 0）
            _metadata = new MediaMetadata
            {
                Duration = duration,
                ContainerFormat = ContainerFormat.Unknown
            };

            _pendingPackets.Clear();
            _exhaustedStreams.Clear();
        }
        finally { _readerGate.Exit(); }
    }

    /// <summary>
    /// 构造带「D3D 设备管理器 + 允许硬件 MFT」的 SourceReader 创建属性（A 方案零拷贝前置）。
    /// </summary>
    /// <param name="hardwareRequested">是否成功表达了硬解意图（决定后续是否协商 NV12 直通）。</param>
    /// <returns>IMFAttributes*（调用方用后 <see cref="Marshal.Release"/>）；不可用返回 <see cref="IntPtr.Zero"/>。</returns>
    /// <remarks>
    /// <para>失败绝不抛异常：任一步不成即返回 <see cref="IntPtr.Zero"/>，
    /// <c>MFCreateSourceReaderFromURL</c> 以空属性创建 ⇒ 完全等价改造前行为（压缩裸流 + MFT 自解码）。
    /// 这是「硬解优先、软解兜底」设计原则在打开阶段的落点。</para>
    /// <para><b>刻意不设</b> <c>MF_SOURCE_READER_ENABLE_(ADVANCED_)VIDEO_PROCESSING</c>：
    /// 插入 Video Processor MFT 会把样本落回系统内存，直接毁掉零拷贝。</para>
    /// <para><b>刻意不设</b> <c>MF_SOURCE_READER_PASSTHROUGH_MODE</c>：该模式强制系统内存样本语义，同样破坏零拷贝。</para>
    /// <para>同步（native 分类）：全为 COM 调用，无 I/O。</para>
    /// </remarks>
    private IntPtr TryCreateHardwareReaderAttributes(out bool hardwareRequested)
    {
        hardwareRequested = false;

        var provider = _dxgiManagerProvider;
        if (provider is null)
        {
            _logger.LogDebug("[MF-D3D] 未注入 DXGI 设备管理器提供者 → SourceReader 走默认（软解）路径");
            return IntPtr.Zero;
        }

        // provider 内部已做一次性闸门 + 全部失败告警；此处只判可用性
        IntPtr manager = provider.TryGetManager();
        if (manager == IntPtr.Zero)
            return IntPtr.Zero;

        if (MFInterop.MFCreateAttributes(out IntPtr attrs, 4) < 0 || attrs == IntPtr.Zero)
        {
            _logger.LogWarning("[MF-D3D] MFCreateAttributes 失败 → SourceReader 不挂 D3D 管理器，回落软解");
            return IntPtr.Zero;
        }

        try
        {
            // ① MF_SOURCE_READER_D3D_MANAGER：IUnknown 属性，必须用 SetUnknown（slotIndex 24，mfobjects.h vtable 实物核验）
            var setUnknown = MfVTable.Get<IMFAttributes_SetUnknown>(attrs, 24);
            Guid d3dKey = MFConstants.MF_SOURCE_READER_D3D_MANAGER;
            int hr = setUnknown(attrs, ref d3dKey, manager);
            if (hr < 0)
            {
                _logger.LogWarning("[MF-D3D] SetUnknown(MF_SOURCE_READER_D3D_MANAGER) 失败 HRESULT=0x{HR:X8} → 回落软解", hr);
                Marshal.Release(attrs);
                return IntPtr.Zero;
            }

            // ② MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS=1：不开则只用软件 MFT，挂了管理器也拿不到 DXGI 表面
            var setUInt32 = MfVTable.Get<IMFAttributes_SetUINT32>(attrs, 18);
            Guid hwKey = MFConstants.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            hr = setUInt32(attrs, ref hwKey, 1);
            if (hr < 0)
            {
                // 非致命：管理器已挂上，部分实现仍可能给出 DXVA 表面。记录后继续。
                _logger.LogWarning("[MF-D3D] SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS) 失败 HRESULT=0x{HR:X8}，继续尝试", hr);
            }

            // ③ MF_SOURCE_READER_DISABLE_DXVA=0：默认即 0，显式写入表达意图（防某些环境的策略默认值反转）
            Guid disableDxvaKey = MFConstants.MF_SOURCE_READER_DISABLE_DXVA;
            setUInt32(attrs, ref disableDxvaKey, 0);

            hardwareRequested = true;
            _logger.LogInformation("[MF-D3D] SourceReader 创建属性已就绪：D3D_MANAGER + ENABLE_HARDWARE_TRANSFORMS（目标：ReadSample 直出 DXGI 纹理）");
            return attrs;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MF-D3D] 构造 SourceReader 硬解属性异常 → 回落软解");
            Marshal.Release(attrs);
            hardwareRequested = false;
            return IntPtr.Zero;
        }
    }

    /// <summary>MFT_OUTPUT_STREAM_INFO.dwFlags：MFT 自行分配输出 sample（零拷贝必要条件）。</summary>
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

    /// <summary>
    /// 取证 SourceReader 为指定流实际建立的 <b>MFT 链</b>（零拷贝失效成因定位）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须做</b>：已把 <c>MF_SOURCE_READER_D3D_MANAGER</c> +
    /// <c>ENABLE_HARDWARE_TRANSFORMS</c> 挂上且全部返回 <c>S_OK</c>，但 ReadSample 出来的样本
    /// QI <c>IMFDXGIBuffer</c> 仍返 <c>E_NOINTERFACE</c>。设计原则「S_OK≠被接受：能力自报+行为副作用双判据」
    /// 要求此时<b>直接查证拓扑</b>而非继续猜。<c>IMFSourceReaderEx::GetTransformForStream</c>
    /// 是唯一能看穿 SourceReader 黑盒的官方接口。</para>
    /// <para><b>三种判决</b>：
    /// ① 链上出现 <c>MFT_CATEGORY_VIDEO_PROCESSOR</c> ⇒ SourceReader 偷插了 VP 做转换，
    ///    它会把 DXGI 表面拉回系统内存 —— 零拷贝头号杀手，须调整输出类型协商避免触发；
    /// ② 解码 MFT 的 <c>MF_SA_D3D11_AWARE</c>=0 ⇒ SourceReader 选中的是纯软件 MFT，D3D 管理器根本无处可用；
    /// ③ 解码 MFT aware=1 但 <c>PROVIDES_SAMPLES</c>=0 ⇒ MFT 没进 DXVA 分配模式
    ///    （即收到了 SET_D3D_MANAGER 却拒绝/回落），问题在驱动或 profile 协商。</para>
    /// <para>纯诊断、零副作用：只读属性、不发消息、不改类型。任何一步失败都只记 Debug 后静默返回，
    /// 绝不影响播放（诊断代码永远不该成为故障源）。所有 COM 引用 COM 配对释放。</para>
    /// <para>同步（native 分类）：全为 COM 调用，无 I/O。</para>
    /// </remarks>
    private void DiagnoseStreamTransformChain(IntPtr readerPtr, int streamIndex)
    {
        IntPtr readerEx = IntPtr.Zero;
        try
        {
            int hrQi = Marshal.QueryInterface(readerPtr, in MFConstants.IID_IMFSourceReaderEx, out readerEx);
            if (hrQi < 0 || readerEx == IntPtr.Zero)
            {
                _logger.LogDebug("[MFT-CHAIN] SourceReader 不支持 IMFSourceReaderEx（hr=0x{HR:X8}），跳过链路取证", hrQi);
                return;
            }

            var getTransform = MfVTable.Get<IMFSourceReaderEx_GetTransformForStream>(readerEx, 13); // 绝对槽 16
            int found = 0;
            bool sawVideoProcessor = false;
            bool sawVideoDecoder = false;
            bool decoderIsHardwareMft = false;

            // dwTransformIndex 从 0 递增枚举，越界返回 MF_E_INVALIDINDEX(0xC00D36B3)。上限 8 防御异常实现死循环。
            for (uint i = 0; i < 8; i++)
            {
                int hr = getTransform(readerEx, (uint)streamIndex, i, out Guid category, out IntPtr transform);
                if (hr < 0 || transform == IntPtr.Zero)
                    break;

                found++;
                try
                {
                    string categoryName =
                        category == MFConstants.MFT_CATEGORY_VIDEO_DECODER ? "视频解码器"
                        : category == MFConstants.MFT_CATEGORY_VIDEO_PROCESSOR ? "视频处理器(VP)"
                        : category == MFConstants.MFT_CATEGORY_VIDEO_EFFECT ? "视频特效"
                        : $"其他({category:B})";
                    if (category == MFConstants.MFT_CATEGORY_VIDEO_PROCESSOR)
                        sawVideoProcessor = true;

                    // ① MF_SA_D3D11_AWARE：该 MFT 是否具备 D3D11 视频解码能力（GetAttributes = 绝对槽 8 → slotIndex 5）
                    //    ② 同时取 MFT 身份（友好名 / HARDWARE_URL / CLSID）——区分「厂商硬件 MFT」与「微软软件 MFT」。
                    string awareText = "属性不可读";
                    string identityText = "身份不可读";
                    if (MfVTable.Get<IMFTransform_GetAttributes>(transform, 5)(transform, out IntPtr mftAttrs) >= 0
                        && mftAttrs != IntPtr.Zero)
                    {
                        try
                        {
                            Guid awareKey = MFConstants.MF_SA_D3D11_AWARE;
                            awareText = MfVTable.Get<IMFAttributes_GetUINT32>(mftAttrs, 4)(mftAttrs, ref awareKey, out uint aware) >= 0
                                ? (aware != 0 ? "D3D11_AWARE=1" : "D3D11_AWARE=0(纯软件)")
                                : "无 D3D11_AWARE 属性(纯软件)";

                            // 官方判据：MFT_ENUM_HARDWARE_URL_Attribute【存在即硬件 MFT】。
                            //    微软内置解码器（如 CMSH264DecoderMFT）同样 D3D11_AWARE=1、也会用 DXVA 硬解，
                            //    但它属于软件 MFT，输出统一落系统内存 —— 这正是「半 DXVA」最常见的成因。
                            string? hwUrl = TryGetAllocatedString(mftAttrs, MFConstants.MFT_ENUM_HARDWARE_URL_Attribute);
                            string? friendly = TryGetAllocatedString(mftAttrs, MFConstants.MFT_FRIENDLY_NAME_Attribute);
                            bool isHardwareMft = !string.IsNullOrEmpty(hwUrl);
                            if (category == MFConstants.MFT_CATEGORY_VIDEO_DECODER)
                            {
                                sawVideoDecoder = true;
                                decoderIsHardwareMft = isHardwareMft;
                            }

                            Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
                            string clsidText =
                                MfVTable.Get<IMFAttributes_GetGUID>(mftAttrs, 7)(mftAttrs, ref clsidKey, out Guid mftClsid) >= 0
                                    ? mftClsid.ToString("B")
                                    : "(未暴露)";

                            identityText = $"{(isHardwareMft ? "【厂商硬件MFT】" : "【微软软件MFT】")}名称=\"{friendly ?? "-"}\" CLSID={clsidText}"
                                + (isHardwareMft ? $" HW_URL={hwUrl}" : string.Empty);
                        }
                        finally { Marshal.Release(mftAttrs); }
                    }

                    // ② PROVIDES_SAMPLES：MFT 是否自分配输出 sample —— DXVA 纹理输出的必要条件
                    //    （GetOutputStreamInfo = 绝对槽 7 → slotIndex 4）
                    string allocText = "输出流信息不可读";
                    if (MfVTable.Get<IMFTransform_GetOutputStreamInfo>(transform, 4)(transform, 0, out MftOutputStreamInfo info) >= 0)
                    {
                        bool providesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                        allocText = providesSamples
                            ? $"MFT自分配输出sample=True(cbSize={info.cbSize})"
                            : $"MFT自分配输出sample=False ⇒ 由SourceReader分配【系统内存】(cbSize={info.cbSize})";
                    }

                    _logger.LogInformation("[MFT-CHAIN] 流 {Index} · MFT[{I}] 分类={Cat} | {Identity} | {Aware} | {Alloc}",
                        streamIndex, i, categoryName, identityText, awareText, allocText);
                }
                finally
                {
                    Marshal.Release(transform);
                }
            }

            if (found == 0)
            {
                _logger.LogWarning("[MFT-CHAIN] 流 {Index} 未枚举到任何 MFT —— SourceReader 可能直通未解码（协商未真正生效）", streamIndex);
            }
            else if (sawVideoProcessor)
            {
                _logger.LogWarning(
                    "[MFT-CHAIN] 流 {Index} 链上存在【视频处理器 VP】—— 零拷贝头号杀手：" +
                    "VP 会把解码器产出的 DXGI 表面拉回系统内存做格式/尺寸转换。" +
                    "成因=输出类型协商触发了转换，须让请求类型与解码器原生输出完全一致。", streamIndex);
            }
            else if (sawVideoDecoder && !decoderIsHardwareMft)
            {
                _logger.LogWarning(
                    "[MFT-CHAIN] 流 {Index} 链上无 VP，但解码器是【微软软件 MFT】（缺 MFT_ENUM_HARDWARE_URL）—— " +
                    "这就是「半 DXVA」的直接成因：软件 MFT 即便 MF_SA_D3D11_AWARE=1、也确实用 DXVA 在 GPU 上完成解码" +
                    "（故 Lock2D 的 pitch 带 GPU 对齐痕迹），但其输出 sample 一律回落系统内存，永远 QI 不到 IMFDXGIBuffer。" +
                    "真零拷贝要求 SourceReader 选中【厂商硬件 MFT】；若当前运行环境该 codec 未注册硬件 MFT，MF 路径无解，应让位 FFmpeg D3D11VA。",
                    streamIndex);
            }
            else
            {
                _logger.LogInformation(
                    "[MFT-CHAIN] 流 {Index} 链上共 {N} 个 MFT，未插入 VP，解码器={Kind} —— 若样本仍非 DXGI，嫌疑转向 SourceReader 封装层读回，" +
                    "可用 --no-fusion 关闭「解封装+解码一体」改走自管 MFT 对照",
                    streamIndex, found, decoderIsHardwareMft ? "厂商硬件MFT" : "非解码器/未知");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 诊断代码绝不能成为故障源
            _logger.LogDebug(ex, "[MFT-CHAIN] 链路取证异常（不影响播放）");
        }
        finally
        {
            if (readerEx != IntPtr.Zero)
                Marshal.Release(readerEx);
        }
    }

    /// <summary>
    /// 从 IMFAttributes 读一个原生自分配的宽字符串（GetAllocatedString，slotIndex 10）。仅用于 MFT 身份取证，非热路径。
    /// </summary>
    /// <remarks>
    /// 属性不存在时返回 <see langword="null"/>（MF_E_ATTRIBUTENOTFOUND）——对 MFT_ENUM_HARDWARE_URL_Attribute 而言，
    /// 「不存在」本身就是有效信息：该 MFT 为软件 MFT。原生 buffer 由 CoTaskMemAlloc 分配，必须 FreeCoTaskMem 配对释放。
    /// </remarks>
    private static string? TryGetAllocatedString(IntPtr attrs, Guid key)
    {
        IntPtr pStr = IntPtr.Zero;
        try
        {
            int hr = MfVTable.Get<IMFAttributes_GetAllocatedString>(attrs, 10)(attrs, ref key, out pStr, out _);
            if (hr < 0 || pStr == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(pStr);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (pStr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pStr);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 伪异步：<c>await Task.Run</c> 卸载 IMFSourceReader.ReadSample（同步 COM 调用）到线程池。
    /// 未来改进：可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。
    /// </remarks>
    public async ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default)
    {
        // 关闸后任何新读请求立即以 EOS（null）返回，让 BufferManager.ReaderLoopAsync 走正常 Complete() 收尾，
        // 避免把正常关闭变成 ObjectDisposedException / InvalidOperationException 异常路径。
        // 本短路**必须先于**下面的 _disposed / _opened 抛出检查，否则形同虚设。
        // 关闭协议的写入顺序是 BeginClose → … → ReleaseNativeResources(_sourceReader=Zero) → _opened=false，
        // 因此关闭完成后再进来的调用会**确定性**命中「解封装器尚未打开」抛出 InvalidOperationException，
        // 被 BufferManager.ReaderLoopAsync 的兜底 catch 记成 LogError("缓冲管理器读取异常")——
        // 把一次正常关闭伪装成读取故障。Dispose() 路径同理会先命中 _disposed 抛 ObjectDisposedException。
        if (_readerGate.IsClosing || Volatile.Read(ref _closeStarted) != 0) return null;

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _sourceReader == IntPtr.Zero)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 必须先取本地快照再用。ReleaseNativeResources 会把 _readerFactory 置 null，
        // 而本方法通过 IsClosing 检查到 StartNew 之间存在窗口（此刻尚未进 gate，drain 可在此期间成功完成）
        // ⇒ 直接 `_readerFactory!` 解引用会 NullReferenceException。快照 + null 短路 ⇒ 按 EOS 收尾。
        var factory = _readerFactory;
        if (factory is null) return null;

        // 伪异步：IMFSourceReader.ReadSample 为同步 COM 调用；在专用单线程上执行（见 OpenAsync）。
        // 未来改进：可通过 IMFSourceReaderCallback 异步回调实现真异步 ReadSample。
        // CompleteAdding 后入队会抛 InvalidOperationException/TaskSchedulerException（经 TPL 包装），
        // 此处捕获并同样以 EOS 返回，消除关闭期噪音异常。
        // 异常过滤器**必须**限定在关闸期。否则 OpenCore/ReadPacketCore 内真实的
        // InvalidOperationException（如 vtable 调用失败）会被无声吞成 EOS，故障被伪装成"正常播完"。
        try
        {
            return await factory.StartNew(() => ReadPacketCore(ct), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (_readerGate.IsClosing) { return null; }
        catch (TaskSchedulerException) when (_readerGate.IsClosing) { return null; }
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
            // ── A 方案：该流已协商为 NV12 解码输出 ⇒ 走「已解码直通」打包（优先 GPU 纹理零拷贝）──
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
    /// <para><b>路径①（目标）</b>：<c>GetBufferByIndex(0)</c> → QI <c>IMFDXGIBuffer</c> → <c>ID3D11Texture2D</c>
    /// ⇒ 帧全程留在显存，<see cref="MediaPacket.DecodedFrameResource"/> 承载纹理所有权，
    /// 一路交到 <c>D3D11Renderer.PresentGpuTexture</c> 做 <c>CopySubresourceRegion</c> 上屏。真·零拷贝。</para>
    /// <para><b>绝不能</b>用 <c>ConvertToContiguousBuffer</c> 取 DXVA buffer：其契约就是「合并并读回连续系统内存」，
    /// 永远返回 CPU buffer，用它做零拷贝在原理上注定失败。</para>
    /// <para><b>路径②（回落）</b>：样本不是 DXGI buffer（=「半 DXVA」：驱动内部把帧读回了系统内存）时，
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

        // ── 路径①：DXGI 纹理零拷贝（GetBufferByIndex = 绝对槽 40 → slotIndex 37）──
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

                // ── 路径①-A：IMF2DBuffer2 真值行跨度紧凑化（半 DXVA 治本）────────────
                //   IMFDXGIBuffer 失败后，MS H264 MFT 内部把帧读回 Direct3DSurface9-backed 2D 内存，
                //   实际 pitch 是 16 字节对齐（典型 1080→1088）。ConvertToContiguousBuffer 把整段当 1D 摊平，
                //   但 IMFMediaBuffer.GetCurrentLength 返回的是 MFT 内部 allocate 的整段长度（含尾部对齐 padding），
                //   反推 stride/codedH 必错 → 紧凑拷贝时 UV 平面偏移错位 → 画面下半段色度错行/横纹。
                //   治本：QI IMF2DBuffer2，Lock2D 取真值 pitch + scanline0，逐行拷成紧凑布局。
                //   若 Lock2D 失败（极少见：旧版 MFT 不实现 2D 路径），再走路径② ConvertToContiguousBuffer 兜底。
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
                                            "[MF-D3D] IMF2DBuffer2.Lock2D 返回 pitch={P} < display={W}，布局异常 ⇒ 丢帧、走路径②兜底",
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
                                            " → 逐行拷成紧凑 {Total}B，路径② ConvertToContiguousBuffer 跳过（治本，不再依赖 curLen 反推）。" +
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
                                "[MF-D3D] IMF2DBuffer2 QI 成功但 Lock2D 失败 hr=0x{HR:X8} scanline0={Sl} pitch={P} → 路径②兜底",
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

        // ── 路径②：NV12 CPU 直通（ConvertToContiguousBuffer = 绝对槽 41 → slotIndex 38）──
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

    /// <inheritdoc/>
    /// <remarks>
    /// 伪异步：<c>await Task.Run</c> 卸载 MF seek 操作到线程池。
    /// </remarks>
    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        // 与 ReadPacketAsync 同构——关闭期短路必须先于 _disposed / _opened 抛出检查，
        // 否则「关闭后 seek」会抛 InvalidOperationException 而非按约定返回 false。
        if (_readerGate.IsClosing || Volatile.Read(ref _closeStarted) != 0) return false;

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened || _sourceReader == IntPtr.Zero)
            throw new InvalidOperationException("解封装器尚未打开");

        ct.ThrowIfCancellationRequested();

        // 同 ReadPacketAsync，先快照后使用；关闭已完成时 seek 直接失败返回而非 NRE。
        var factory = _readerFactory;
        if (factory is null) return false;

        // 伪异步：MF SourceReader seek 为同步 COM 调用；在专用单线程上执行（见 OpenAsync）。
        try
        {
            return await factory.StartNew(() =>
            {
                // seek lambda 触碰 _sourceReader 与 _pendingPackets，须处于 gate 内。
                if (!_readerGate.TryEnter()) return false;
                try
                {
                    // IMFSourceReader::SetCurrentPosition（绝对槽 8 → slotIndex 5，槽位表已核验）。
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
                }
                finally { _readerGate.Exit(); }
            }, ct).ConfigureAwait(false);
        }
        // 与 ReadPacketAsync 同构——仅在关闸期把入队失败视为"seek 无效"，
        // 非关闭期的同类异常必须继续上抛，不得伪装成 seek 失败。
        catch (InvalidOperationException) when (_readerGate.IsClosing) { return false; }
        catch (TaskSchedulerException) when (_readerGate.IsClosing) { return false; }
    }

    /// <inheritdoc/>
    public void Close() => CloseSync();

    /// <inheritdoc/>
    /// <remarks>接口契约：升级为真异步 drain（关闭时存在真实的等待在途调用），不再同步转发。</remarks>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        // 快路径：关闭已发起/完成时不再分配状态机。真正的重入互斥在 CloseAsync 内由 Interlocked 完成。
        if (Volatile.Read(ref _closeStarted) != 0) return ValueTask.CompletedTask;
        return CloseAsync();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        CloseSync();
    }

    // ── 两阶段关闭协议 ──
    // ① BeginClose 关闸 → ② WaitDrain 排空在途原生调用 → ③【在专用 COM 单元线程上】释放 COM 指针
    // → ④ 之后才 Shutdown 调度器（放行 CoUninitialize）→ ⑤ 托管收尾。任一环节失败即有意泄漏。
    //
    // 步骤顺序原则（**勿把 ② 与 ④ 换回来**）：
    //    旧顺序为「① → Shutdown 调度器 → WaitDrain → Release」，即**先让专用线程退出**（其 Loop 的 finally
    //    执行 CoUninitialize）**再在关闭线程上 Marshal.Release**。而 _sourceReader 是 OpenCore 在专用线程上
    //    经 MFCreateSourceReaderFromURL 创建的：CoUninitialize 会关闭该线程的 COM 库、卸载它加载的 in-proc
    //    server（mfreadwrite/mfplat 等），并在它是最后一个 MTA 成员时拆除整个 MTA。此后那次 Release 跳进
    //    已失效的 vtable ⇒ 原生访问违例 ⇒ `Fatal error. Internal CLR error.`（CLR 原生堆损坏）。
    //    该失败是**确定性**的（每次关闭顺序固定），表现为 MF 测试程序集首跑即崩，与此前的 flaky 竞态无关。
    //    基线（无 CoUninitialize）下旧顺序侥幸安全，加入 CoUninitialize 后即刻暴露。
    //    ⇒ 不变量（COM 单元）：**在专用单元线程上创建的 COM 对象，其 Release 必须在同一线程、且先于该线程 CoUninitialize。**
    //
    // 重入互斥：CloseSync / CloseAsync 共用 _closeStarted 这一 Interlocked 令牌，
    //    「先到者执行完整协议、后到者立即返回」。后到者不会等待协议完成——这是刻意取舍：
    //    并发调用 Dispose 与 DisposeAsync 本身即为调用方误用，此处保证的是**绝不二次 Release**
    //    （二次 Release 会直接引发访问违例），而非为误用提供关闭栅栏。

    /// <summary>同步两阶段关闭（供 <see cref="Close"/> / <see cref="Dispose"/> 使用）。</summary>
    private void CloseSync()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
        try
        {
            _readerGate.BeginClose();

            // ② 排空在途原生调用。**此时专用线程仍存活、COM 单元完整**（这正是它必须先于 Shutdown 的原因）。
            if (!_readerGate.WaitDrain(MediaPipelineTimeouts.NativeDrain))
            {
                LeakNativeResources("在途原生调用未在期限内排空", schedulerExited: false);
                return;
            }

            var scheduler = _readerScheduler;
            if (scheduler is null)
            {
                // 从未建立专用线程（Open 之前即关闭）。此时不应存在任何 COM 指针；若存在则说明状态异常，
                // 在未知单元的线程上释放即违反 COM 单元不变量 ⇒ 取安全侧泄漏。
                if (_sourceReader != IntPtr.Zero)
                {
                    LeakNativeResources("存在 COM 指针但专用单元线程已不可用", schedulerExited: true);
                    return;
                }
                ReleaseComObjectsOnOwnerThread(); // 全 no-op，仅为收敛 _mfStartupAcquired
                ReleaseManagedState();
                return;
            }

            // ③ COM 单元不变量：把 Release 投递回创建它的专用单元线程，必须在该线程 CoUninitialize 之前完成。
            if (!scheduler.TryRunOnSchedulerThread(ReleaseComObjectsOnOwnerThread, MediaPipelineTimeouts.NativeDrain))
            {
                LeakNativeResources("无法在专用 COM 单元线程上完成释放", schedulerExited: false);
                return;
            }

            // ④ COM 指针已释放，现在才放行专用线程退出（其 finally 将执行 CoUninitialize）。
            bool schedulerExited = scheduler.Shutdown(MediaPipelineTimeouts.SchedulerJoin);
            if (!schedulerExited)
                _logger.LogWarning("MFDemuxer 专用读取线程未在期限内退出；COM 指针已安全释放，仅线程与队列延迟回收。");

            ReleaseManagedState(); // ⑤
        }
        finally { _opened = false; }
    }

    /// <summary>异步两阶段关闭（供 <see cref="DisposeAsync"/> 使用，全程 await，不阻塞调用线程）。</summary>
    /// <remarks>
    /// 调度器等待走 <c>ShutdownAsync</c>（线程退出 TCS + <c>WaitAsync</c>），
    /// 而非 <c>Shutdown</c> 的 <c>Thread.Join</c>——后者会在异步释放链上引入最长 5s 的硬同步阻塞，
    /// 与「真异步方法一路 await 到底、禁止 .Wait()/.Result 式阻塞」的准则直接冲突。
    /// 步骤顺序与 <see cref="CloseSync"/> 逐条同构（COM 单元不变量同样适用）。
    /// </remarks>
    private async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0) return;
        try
        {
            _readerGate.BeginClose();

            // ②
            if (!await _readerGate.WaitDrainAsync(MediaPipelineTimeouts.NativeDrain).ConfigureAwait(false))
            {
                LeakNativeResources("在途原生调用未在期限内排空", schedulerExited: false);
                return;
            }

            var scheduler = _readerScheduler;
            if (scheduler is null)
            {
                if (_sourceReader != IntPtr.Zero)
                {
                    LeakNativeResources("存在 COM 指针但专用单元线程已不可用", schedulerExited: true);
                    return;
                }
                ReleaseComObjectsOnOwnerThread();
                ReleaseManagedState();
                return;
            }

            // ③ COM 单元不变量
            if (!await scheduler.TryRunOnSchedulerThreadAsync(
                    ReleaseComObjectsOnOwnerThread, MediaPipelineTimeouts.NativeDrain).ConfigureAwait(false))
            {
                LeakNativeResources("无法在专用 COM 单元线程上完成释放", schedulerExited: false);
                return;
            }

            // ④
            bool schedulerExited = await scheduler.ShutdownAsync(MediaPipelineTimeouts.SchedulerJoin).ConfigureAwait(false);
            if (!schedulerExited)
                _logger.LogWarning("MFDemuxer 专用读取线程未在期限内退出；COM 指针已安全释放，仅线程与队列延迟回收。");

            ReleaseManagedState(); // ⑤
        }
        finally { _opened = false; }
    }

    /// <summary>
    /// 协议步骤③：释放原生 COM 资源。<b>必须且只能在创建它们的专用单元线程上执行</b>（COM 单元不变量）。
    /// </summary>
    /// <remarks>
    /// 前置：gate 已排空（独占）——不存在任何其它线程处于闸内，故本方法可独占访问 <c>_sourceReader</c>。
    /// 调用点仅两处，均经 <c>TryRunOnSchedulerThread(Async)</c> 投递或在确认无 COM 指针时内联，不得直接调用。
    /// </remarks>
    private void ReleaseComObjectsOnOwnerThread()
    {
        if (_sourceReader != IntPtr.Zero)
        {
            try
            {
                // 错误链：经 tracer 记录并（严格模式）纳入 UAF/重复释放检测。
                InteropTrace.ReleaseComPtr(_sourceReader, "MFDemuxer:_sourceReader");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "IMFSourceReader 释放异常");
            }
            _sourceReader = IntPtr.Zero;
            _readSample = null; // 同步置空 vtable 委托，漏网调用点得到可诊断 NRE 而非静默 UAF
        }

        // 配对 MFPlatform 引用（仅成功分支执行；LeakNativeResources 绝不递减）。
        // 与 OpenCore 中的 Startup 同线程同单元，且此刻 _sourceReader 已释放，递减不会踩到任何在途调用。
        if (_mfStartupAcquired)
        {
            _mfStartupAcquired = false;
            try { MFPlatform.Shutdown(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "MFShutdown 配对调用异常（忽略，不影响释放流程）");
            }
        }
    }

    /// <summary>协议步骤⑤：纯托管收尾，无单元亲和要求，可在任意线程执行。</summary>
    private void ReleaseManagedState()
    {
        // 释放尚未投递的 lookahead 数据包（MediaPacket 独立拥有托管副本，Dispose 兜底，防泄漏）
        foreach (var pkt in _pendingPackets.Values)
            pkt.Dispose();
        _pendingPackets.Clear();
        _exhaustedStreams.Clear();

        // Shutdown 返回 false（判定时线程仍存活）的路径不会 Dispose 队列；丢弃引用前用零超时
        // 再试一次，让「线程随后才退出」的迟到情形也能回收 BlockingCollection。零超时不阻塞、幂等、不碰 COM。
        _readerScheduler?.Shutdown(TimeSpan.Zero);
        _readerScheduler = null;
        _readerFactory = null;
    }

    /// <summary>协议失败分支：一切不动，有意泄漏并告警。绝不 Release / 置 NULL / 清容器 / 关调度器。</summary>
    private void LeakNativeResources(string reason, bool schedulerExited)
    {
        if (_leakedOnClose) return;
        _leakedOnClose = true;
        _logger.LogError("MFDemuxer 安全关闭失败（{Reason}；schedulerExited={SchedulerExited}）。" +
            "已【有意保留】IMFSourceReader，避免因释放导致原生堆损坏（COR_E_EXECUTIONENGINE）。" +
            "泄漏有界且可诊断；进程自杀不可接受。", reason, schedulerExited);
        // 不 Release、不置 _sourceReader=Zero、不清 _pendingPackets、不置 _readSample=null。
        // **也不递减 MFPlatform 引用**（_mfStartupAcquired 保持 true）。在途的原生 ReadSample
        // 依赖 MF 平台仍然存活；此处若配对 Shutdown，可能让计数归 0 触发真 MFShutdown，把平台从在途调用
        // 脚下抽走 ⇒ 恰好制造本协议要避免的原生堆损坏。平台引用与 COM 指针同进退：要么一起释放，要么一起泄漏。
        //
        // COM 单元不变量推论：**也绝不 Shutdown 调度器**。让专用线程退出即让它 CoUninitialize，
        // 会拆掉泄漏指针所属的 COM 单元、卸载其 in-proc server——泄漏本是为保住指针可用性，
        // 拆单元等于把保护对象连根拔起。线程为后台线程，进程退出时由 OS 回收，泄漏有界。
    }

    // ── 辅助方法 ──

    /// <summary>
    /// 从 IMediaStream 提取 URL（文件路径或网络 URL）。
    /// </summary>
    private static string? ExtractUrl(IMediaStream stream)
    {
        // 修复：原实现错误地把 stream 当作 FileStream（实际为 FileMediaStream，永不命中）导致所有文件均返回 null 而 OpenAsync 抛异常。
        // 改为读取 IMediaStream 中性 Location（文件流返回路径、网络流返回 URL；无地址返回 null）。
        return stream.Location;
    }

    /// <summary>
    /// 解析 MF SourceReader 的轨道信息。
    /// </summary>
    /// <remarks>
    /// 实例方法（非 static）：需要 <c>_logger</c> 上报属性缺失/媒体类型协商失败——这些是静默失败的高发区，
    /// 无日志会让下游拿到 0Hz/0ch 或压缩裸流而无从诊断（勿改回 static）。
    /// </remarks>
    private IReadOnlyList<MediaTrack> ParseTracks(IntPtr readerPtr, TimeSpan containerDuration)
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

                var vcodec = MapVideoCodec(subtype);
                if (vcodec == VideoCodec.Unknown)
                    _logger.LogWarning("[OPEN-DIAG] 未识别视频子类型 {Subtype} → 标记 Unknown（后端仅支持 H264/H265；AV1/VP9/MPEG 等需扩 codec 路由）", subtype);

                // ── A 方案：把该视频流协商为 NV12【解码后】输出 ──
                // 仅在创建期成功挂上 D3D 管理器时才尝试；成功后 SourceReader 会自行加载硬件解码 MFT
                // 并在共享 D3D11 设备上分配输出表面 ⇒ ReadSample 直出可 QI 成 IMFDXGIBuffer 的样本。
                // 必须在构造 MediaTrack **之前**完成：VideoTrackInfo 为 init-only，尺寸须一次性写定
                // （硬件 MFT 可能把显示尺寸对齐到编码尺寸，回读实测值比沿用原生类型更可靠）。
                // 失败/未启用 ⇒ _decodedVideoStreamIndex 保持 -1，该流继续输出压缩裸流，走原 MFVideoDecoder MFT 路径。
                // EnableReaderDecodeFusion=false 是零拷贝定界开关：主动放弃一体化，把解码交回自管 MFT，
                //    用两条路径的帧落点差异判定「读回」发生在 SourceReader 封装层还是 MFT/驱动层。
                if (!_options.EnableReaderDecodeFusion && _decodedVideoStreamIndex < 0)
                {
                    _logger.LogWarning(
                        "[MF-D3D] 诊断开关 EnableReaderDecodeFusion=false —— 跳过 NV12 一体化协商，"
                        + "视频流 {Index} 继续输出压缩裸流，改由 MFVideoDecoder 自管 MFT 解码（用于零拷贝定界）", index);
                }
                else if (_hardwareReaderRequested && _decodedVideoStreamIndex < 0)
                {
                    if (TryConfigureVideoStreamToNv12(readerPtr, index, ref width, ref height, out int decodedStride))
                    {
                        _decodedVideoStreamIndex = index;
                        _decodedVideoWidth = width;
                        _decodedVideoHeight = height;
                        _decodedVideoStride = decodedStride;

                        // 拓扑既已成型，立刻取证 SourceReader 真实建了什么 MFT 链（零拷贝失效成因定位）。
                        DiagnoseStreamTransformChain(readerPtr, index);
                    }
                }

                track = new MediaTrack
                {
                    Index = index,
                    Type = TrackType.Video,
                    VideoCodec = vcodec,
                    VideoInfo = new VideoTrackInfo
                    {
                        Width = width,
                        Height = height,
                        Duration = containerDuration
                    }
                };

                // 提取 H264/H265 解码必需的 out-of-band SPS+PPS（Annex-B 序列头）。
                // MP4(AVCC) 容器内 SPS/PPS 在 avcC 盒、不在每个 sample 内联；不透传给解码器则
                // IMFTransform::ProcessOutput 永久返回 MF_E_TRANSFORM_NEED_MORE_INPUT。
                // 优先直取 MF_MT_MPEG_SEQUENCE_HEADER；缺失则从 MF_MT_MPEG4_SAMPLE_DESCRIPTION（整个 stsd 盒）解析 avcC。
                // 注：早期「MF 媒体源不会填 MF_MT_MPEG_SEQUENCE_HEADER」的结论建立在错误 GUID 上（恒 ATTRIBUTENOTFOUND），
                //     GUID 已依 SDK 头文件修正，两条路径产出均为 Annex-B，可安全并存。
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

                // 必须检查 HRESULT：GetUINT32 失败时 out 参数为 0，静默吞掉会让下游拿到
                // SampleRate=0 / Channels=0 去初始化 WASAPI（属性键 GUID 写错时正是如此，
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

                // 关键：SourceReader 默认输出**压缩原生格式**（AAC/MP3 裸流）。
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
                        Duration = containerDuration
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
    /// 查询容器总时长（presentation descriptor 的 <see cref="MFConstants.MF_PD_DURATION"/>，UINT64/100ns 单位）。
    /// 通过 <c>IMFSourceReader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION)</c> 取得。
    /// </summary>
    /// <remarks>
    /// <para><b>为何必须显式查询</b>：MF 不会自动为源填充「时长」属性，<see cref="_metadata"/> 此前被硬编码为
    /// <c>TimeSpan.Zero</c>，使 <c>player.Duration</c> 恒为 0——这是完整播放测试「几秒假完成」的成因
    /// （测试以 <c>pos &gt;= duration-1</c> 判完成，duration=0 时首轮即满足）。</para>
    /// <para><b>容错</b>：查询失败 / 属性缺失 / 非 VT_UI8 / 值为 0 时回落 <c>TimeSpan.Zero</c>，不抛异常，
    /// 行为与旧代码一致（仅损失时长信息，不影响解码播放）。MF_PD_DURATION 为 VT_UI8 标量，输出 PROPVARIANT 无需 PropVariantClear。</para>
    /// <para><b>槽位</b>：<c>GetPresentationAttribute</c> = 绝对槽 12 → slotIndex 9（见 <see cref="MFComInterfaces"/> 头注释）。</para>
    /// </remarks>
    private TimeSpan QueryContainerDuration(IntPtr readerPtr)
    {
        try
        {
            var getPresentationAttribute = MfVTable.Get<IMFSourceReader_GetPresentationAttribute>(readerPtr, 9);
            Guid durationKey = MFConstants.MF_PD_DURATION;
            // 用 ref + 预初始化（对齐已验证可用的 SetCurrentPosition 同款封送，避免 out 结构体在该路径不稳）。
            var durVar = new MfPropVariant();
            int hr = getPresentationAttribute(readerPtr, MFConstants.MF_SOURCE_READER_MEDIASOURCE,
                ref durationKey, ref durVar);
            if (hr < 0)
            {
                _logger.LogWarning("GetPresentationAttribute(MF_PD_DURATION) 失败: HRESULT=0x{HR:X8}，时长回落 0", hr);
                return TimeSpan.Zero;
            }
            // MF_PD_DURATION 以 VT_UI8 存储 100ns 单位；MfPropVariant.hVal 与之同一 8 字节联合，直接读。
            if (durVar.vt != MfPropVariant.VT_UI8)
            {
                _logger.LogWarning("MF_PD_DURATION 返回非 VT_UI8(VT=0x{VT:X4})，时长回落 0", durVar.vt);
                return TimeSpan.Zero;
            }
            if (durVar.hVal <= 0)
                return TimeSpan.Zero;
            var duration = TimeSpan.FromTicks(durVar.hVal);
            _logger.LogInformation("MF 容器时长: {Duration}", duration);
            return duration;
        }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "查询 MF 容器时长异常，时长回落 0");
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 排空兜底时长探测：逐流 ReadSample 到 EOS，取所有样本时间戳最大值（100ns）作为容器时长。
        /// 仅在 <see cref="QueryContainerDuration"/> 取不到（MF_PD_DURATION 缺失）时调用，保证 Duration 恒有正确值。
        /// 探测后通过 <see cref="IMFSourceReader_SetCurrentPosition"/> 复位读取位置到开头，不影响后续正常播放。
        /// </summary>
        /// <remarks>
        /// <para>样本时间戳由 ReadSample 的 <c>pllTimestamp</c> 直接给出（100ns，可靠），故排空法推算的时长精确。</para>
        /// <para>COM 配对（失败路径不释放）：ReadSample 失败时 <c>*ppSample</c> 语义不可判定，依「泄漏优于误释放」一律不 Release；
        /// 仅成功路径（hr≥0 且 samplePtr≠0）释放样本，与 <see cref="ExtractPacket"/> 同构。</para>
        /// </remarks>
        private TimeSpan ProbeDurationByDraining(IntPtr readerPtr)
        {
            try
            {
                long maxTicks = 0;
                foreach (int s in _selectedStreamIndices)
                {
                    while (true)
                    {
                        int hr = _readSample!(readerPtr, (uint)s, 0,
                            out _, out int streamFlags, out long timestamp, out IntPtr samplePtr);
                        if (hr < 0)
                            break; // 失败路径不释放 *ppSample（泄漏优于误释放）
                        bool eos = (streamFlags & MFConstants.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                        if (samplePtr != IntPtr.Zero)
                            InteropTrace.ReleaseComPtr(samplePtr, "ProbeDurationByDraining:samplePtr");
                        if (eos)
                            break;
                        if (timestamp > maxTicks)
                            maxTicks = timestamp;
                    }
                }

                // 复位读取位置到开头，避免影响后续播放（与 SeekAsync(0) 同效）。
                Guid timeFormat = Guid.Empty;
                var pos = new MfPropVariant { vt = MfPropVariant.VT_I8, hVal = 0 };
                MfVTable.Get<IMFSourceReader_SetCurrentPosition>(readerPtr, 5)(readerPtr, ref timeFormat, ref pos);

                if (maxTicks <= 0)
                    return TimeSpan.Zero;
                var dur = TimeSpan.FromTicks(maxTicks);
                _logger.LogInformation("MF 排空探测时长: {Duration}", dur);
                return dur;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "排空探测时长异常，时长回落 0");
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 末尾定位探测：把读取位置夹到流末尾，逐流读少量末帧取最大样本时间戳（100ns）作为容器时长。
        /// 用 IMFSourceReader 索引跳转（MP4/MKV 等有索引容器为 O(log n)）替代旧「整段排空到 EOS」，
        /// 将 OpenAsync 内推算时长的同步阻塞降到近乎瞬时，消灭启动白屏/黑屏后的长延迟。
        /// 探测后复位读取位置到开头，不影响后续正常播放。
        /// </summary>
        /// <remarks>
        /// <para>样本时间戳由 ReadSample 的 <c>pllTimestamp</c> 直接给出（100ns，可靠），与旧排空法一致。</para>
        /// <para>COM 配对（失败路径不释放）：ReadSample 失败时 <c>*ppSample</c> 语义不可判定，依「泄漏优于误释放」一律不 Release；
        /// 仅成功路径（hr≥0 且 samplePtr≠0）释放样本，与 <see cref="ProbeDurationByDraining"/> 同构。</para>
        /// <para>读上限 16 帧覆盖 B 帧重排导致的末段 PTS 非单调，取最大 PTS（与旧排空法语义一致）。</para>
        /// </remarks>
        private TimeSpan ProbeDurationByEndSeek(IntPtr readerPtr)
        {
            try
            {
                Guid timeFormat = Guid.Empty;

                // 预热：先对任意已选流读一帧，建立 demuxer 可读状态。
                // 部分 MF 实现要求先 ReadSample 后才能 SetCurrentPosition（否则返回失败码）。
                // 该 sample 直接释放，不影响后续探测。
                if (_selectedStreamIndices.Length > 0)
                {
                    try
                    {
                        _readSample!(readerPtr, (uint)_selectedStreamIndices[0], 0,
                            out _, out _, out long _, out IntPtr warmSample);
                        if (warmSample != IntPtr.Zero)
                            InteropTrace.ReleaseComPtr(warmSample, "ProbeDurationByEndSeek:warmSample");
                    }
                    catch { /* 预热失败不影响主路径 */ }
                }

                // GUID_NULL 时间格式 = 100ns 单位；极大时间戳触发 SourceReader 夹到末样本。
                // 回归修复：必须检查 hr。seek 失败时（如本源返回负码）旧代码静默忽略，
                // 导致后续从头读少量帧（如 16/30fps 即约 0.5s）伪装成真实时长。失败时立即返回 Zero，
                // 由 OpenCore 回退 ProbeDurationByDraining 拿正确时长（代价是同步阻塞）。
                var endPos = new MfPropVariant { vt = MfPropVariant.VT_I8, hVal = long.MaxValue };
                int seekHr = MfVTable.Get<IMFSourceReader_SetCurrentPosition>(readerPtr, 5)(readerPtr, ref timeFormat, ref endPos);
                if (seekHr < 0)
                {
                    _logger.LogDebug("末尾定位探测: SetCurrentPosition 失败 HRESULT=0x{HR:X8}，回退整段排空", seekHr);
                    return TimeSpan.Zero;
                }

                long maxTicks = 0;
                foreach (int s in _selectedStreamIndices)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int hr = _readSample!(readerPtr, (uint)s, 0,
                            out _, out int streamFlags, out long timestamp, out IntPtr samplePtr);
                        if (hr < 0)
                            break; // 失败路径不释放 *ppSample（泄漏优于误释放）
                        bool eos = (streamFlags & MFConstants.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                        if (samplePtr != IntPtr.Zero)
                            InteropTrace.ReleaseComPtr(samplePtr, "ProbeDurationByEndSeek:samplePtr");
                        if (timestamp > maxTicks)
                            maxTicks = timestamp;
                        if (eos)
                            break;
                    }
                }

                // 复位读取位置到开头，避免影响后续播放（与 SeekAsync(0) 同效）。
                var resetPos = new MfPropVariant { vt = MfPropVariant.VT_I8, hVal = 0 };
                MfVTable.Get<IMFSourceReader_SetCurrentPosition>(readerPtr, 5)(readerPtr, ref timeFormat, ref resetPos);

                if (maxTicks <= 0)
                {
                    _logger.LogDebug("末尾定位探测: 未取到末帧时间戳，回退整段排空");
                    return TimeSpan.Zero;
                }
                var dur = TimeSpan.FromTicks(maxTicks);
                _logger.LogInformation("MF 末尾定位探测时长: {Duration}", dur);
                return dur;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "末尾定位探测时长异常，回落排空兜底");
                return TimeSpan.Zero;
            }
        }

    /// <summary>
    /// 把指定视频流协商为**解码后 NV12** 输出（A 方案：SourceReader 自带硬解 + DXGI 出样）。
    /// </summary>
    /// <param name="readerPtr">IMFSourceReader*。</param>
    /// <param name="streamIndex">MF 流索引。</param>
    /// <param name="width">进：原生宽；出：MF 实测采纳的输出宽。</param>
    /// <param name="height">进：原生高；出：MF 实测采纳的输出高。</param>
    /// <param name="stride">回读的行跨度（字节）；缺失时为 0，调用方按紧凑 <paramref name="width"/> 处理。</param>
    /// <returns>协商成功（该流自此输出已解码 NV12）返回 <see langword="true"/>；失败返回 <see langword="false"/> 且不改变原有行为。</returns>
    /// <remarks>
    /// <para>与 <see cref="ConfigureAudioStreamToPcm"/> 完全同构（MF 音频早已是这套「demuxer 内解码 + decoder 直通」模式），
    /// 差别只在目标子类型是 NV12 而非 PCM。走已验证的成熟路子，风险最低。</para>
    /// <para>只设 MAJOR_TYPE + SUBTYPE 的<b>部分类型</b>，其余字段留空由 SourceReader 按源填充——
    /// 显式写死尺寸/帧率反而会让硬件 MFT 拒绝协商（MF_E_INVALIDMEDIATYPE）。</para>
    /// <para>协商失败绝不抛异常：返回 false 后该流继续输出压缩裸流，下游 <c>MFVideoDecoder</c> 按老路自解码，
    /// 与改造前行为逐字节一致（硬解优先、软解兜底）。</para>
    /// <para>同步（native 分类）：全为 COM 调用，无 I/O。</para>
    /// </remarks>
    private bool TryConfigureVideoStreamToNv12(IntPtr readerPtr, int streamIndex, ref int width, ref int height, out int stride)
    {
        stride = 0;

        // 未选中的流不会被建管线，SetCurrentMediaType 无从协商（与音频同）。SetStreamSelection = 绝对槽 4 → slotIndex 1。
        int hr = MfVTable.Get<IMFSourceReader_SetStreamSelection>(readerPtr, 1)(readerPtr, (uint)streamIndex, true);
        if (hr < 0)
        {
            _logger.LogWarning("[MF-D3D] 视频流 {Index} SetStreamSelection 失败 HRESULT=0x{HR:X8}，跳过 NV12 协商", streamIndex, hr);
            return false;
        }

        if (MFInterop.MFCreateMediaType(out IntPtr nv12Type) < 0 || nv12Type == IntPtr.Zero)
        {
            _logger.LogWarning("[MF-D3D] 视频流 {Index} MFCreateMediaType 失败，跳过 NV12 协商", streamIndex);
            return false;
        }

        try
        {
            var setGuid = MfVTable.Get<IMFAttributes_SetGUID>(nv12Type, 21); // SetGUID = slotIndex 21
            Guid majorKey = MFConstants.MF_MT_MAJOR_TYPE;
            Guid majorVal = MFConstants.MFMediaType_Video;
            Guid subKey = MFConstants.MF_MT_SUBTYPE;
            Guid subVal = MFConstants.MFVideoFormat_NV12;
            if (setGuid(nv12Type, ref majorKey, ref majorVal) < 0 || setGuid(nv12Type, ref subKey, ref subVal) < 0)
            {
                _logger.LogWarning("[MF-D3D] 视频流 {Index} 构造 NV12 媒体类型失败，跳过协商", streamIndex);
                return false;
            }

            // SetCurrentMediaType = 绝对槽 7 → slotIndex 4；第二参数为保留 DWORD*，必须 NULL。
            hr = MfVTable.Get<IMFSourceReader_SetCurrentMediaType>(readerPtr, 4)(readerPtr, (uint)streamIndex, IntPtr.Zero, nv12Type);
            if (hr < 0)
            {
                // 常见于 SourceReader 找不到能吃该编码的解码 MFT（如桌面版 HEVC 未装扩展）。
                // 这不是错误路径——回落压缩裸流后仍可由 MFVideoDecoder / 回退中间件的 FFmpeg 接手。
                _logger.LogWarning("[MF-D3D] 视频流 {Index} SetCurrentMediaType(NV12) 失败 HRESULT=0x{HR:X8} → " +
                    "回落「压缩裸流 + MFVideoDecoder 自解码」路径", streamIndex, hr);
                return false;
            }
        }
        finally
        {
            Marshal.Release(nv12Type);
        }

        // 回读 MF 实际采纳的输出类型：硬件 MFT 常把显示尺寸对齐到编码尺寸（如 1080→1088），
        // 且会填 MF_MT_DEFAULT_STRIDE。这两项直接决定 CPU 回落路径的逐行拷贝是否错位。
        // GetCurrentMediaType = 绝对槽 6 → slotIndex 3。
        hr = MfVTable.Get<IMFSourceReader_GetCurrentMediaType>(readerPtr, 3)(readerPtr, (uint)streamIndex, out IntPtr actualType);
        if (hr < 0 || actualType == IntPtr.Zero)
        {
            // 协商本身已成功，只是回读失败：沿用原生尺寸继续（stride=0 ⇒ 按紧凑处理）
            _logger.LogWarning("[MF-D3D] 视频流 {Index} GetCurrentMediaType 失败 HRESULT=0x{HR:X8}，沿用原生尺寸 {W}x{H}",
                streamIndex, hr, width, height);
            return true;
        }

        try
        {
            var getUINT64 = MfVTable.Get<IMFMediaType_GetUINT64>(actualType, 5);
            var getUINT32 = MfVTable.Get<IMFMediaType_GetUINT32>(actualType, 4);

            Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
            if (getUINT64(actualType, ref frameSizeKey, out ulong frameSize) >= 0 && frameSize != 0)
            {
                int w = (int)(frameSize >> 32);
                int h = (int)(frameSize & 0xFFFFFFFF);
                if (w > 0 && h > 0) { width = w; height = h; }
            }

            Guid strideKey = MFConstants.MF_MT_DEFAULT_STRIDE;
            if (getUINT32(actualType, ref strideKey, out uint rawStride) >= 0)
            {
                // UINT32 里存的是 INT32：负值表示 bottom-up 布局。NV12 下罕见，取绝对值并留证。
                int s = (int)rawStride;
                if (s < 0)
                {
                    _logger.LogWarning("[MF-D3D] 视频流 {Index} DefaultStride={S}（负=bottom-up），按绝对值处理", streamIndex, s);
                    s = -s;
                }
                stride = s;
            }

            _logger.LogInformation(
                "[MF-D3D] 视频流 {Index} 已协商为 NV12 解码输出：{W}x{H} stride={Stride}（0=紧凑）" +
                " → 本 demuxer 进入「解封装+解码一体」模式，MFVideoDecoder 转直通",
                streamIndex, width, height, stride);
            return true;
        }
        finally
        {
            Marshal.Release(actualType);
        }
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

    /// <summary>读取 IMFAttributes Blob 属性（GetAllocatedBlob=slot13，AOT 安全：原生自分配 buffer 并返回指针+长度，
    /// 与已工作的 GetBlobSize 同形，彻底绕开 GetBlob「原生向调用方 buffer 大块写入」在 AOT 发布二进制静默 AV 退出的路径）。
    /// 属性不存在返回空数组。</summary>
    private static byte[] TryGetBlob(IntPtr attributesPtr, Guid key)
    {
        // Guid 用 GCHandle 固定后传 IntPtr 地址（与 GetBlobSize/GetAllocatedBlob 的 IntPtr guidKey 一致）。
        var kh = GCHandle.Alloc(key, GCHandleType.Pinned);
        IntPtr keyPtr = kh.AddrOfPinnedObject();
        IntPtr blobPtr = IntPtr.Zero;
        uint size = 0;
        try
        {
            var getAllocatedBlob = MfVTable.Get<IMFAttributes_GetAllocatedBlob>(attributesPtr, 13);
            try
            {
                if (getAllocatedBlob(attributesPtr, keyPtr, out blobPtr, out size) < 0 || size == 0 || blobPtr == IntPtr.Zero)
                {
                    return Array.Empty<byte>();
                }
                var result = new byte[size];
                Marshal.Copy(blobPtr, result, 0, (int)size);
                return result;
            }
            catch (Exception)
            {
                // 原生属性读取异常（极少见）不应中断 Open；缺失序列头的解码失败会在后续 ProcessOutput 暴露
                return Array.Empty<byte>();
            }
            finally
            {
                // GetAllocatedBlob 用 CoTaskMem 分配，须 FreeCoTaskMem（非 FreeHGlobal）。
                if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
            }
        }
        finally
        {
            kh.Free();
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
        _ when subtype == MFConstants.MFVideoFormat_HEVC => VideoCodec.H265,
        _ when subtype == MFConstants.MFVideoFormat_HEVC_ES => VideoCodec.H265,
        _ => VideoCodec.Unknown
    };
}
