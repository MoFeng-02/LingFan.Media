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
internal sealed partial class MFDemuxer : IMediaDemuxer
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

    // SourceReader 自带硬解 + DXGI 出样（零拷贝直通）状态
    // 命中时本 demuxer 变成「解封装 + 解码一体」：ReadSample 直接吐已解码帧
    // （GPU 纹理 → MediaPacket.DecodedFrameResource；退化时 NV12 CPU 字节 → Data+Width/Height/Stride），
    // 下游 MFVideoDecoder 退化为直通适配器，不再跑自己的 MFT。
    // -1 = 未启用（视频流仍输出压缩裸流，走 MFVideoDecoder MFT 路径）。
    private int _decodedVideoStreamIndex = -1;
    private int _decodedVideoWidth;
    private int _decodedVideoHeight;
    private int _decodedVideoStride;     // NV12 CPU 回落时的行跨度（<=0 表示按紧凑 width 处理）
    private bool _loggedVideoPathOnce;   // 首帧一次性诊断：真零拷贝 vs 半 DXVA 回落
    // 路径(1)-A（IMF2DBuffer2）必须用**独立**闸门：半 DXVA 回落的 warning 会先把 _loggedVideoPathOnce
    //    置位，若共用则 [MF-D3D] IMF2DBuffer2 路径的成功日志永远打不出来 —— 表现为「代码在跑但看不见证据」，
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
    /// 传 <see langword="null"/> 或其返回 <see cref="IntPtr.Zero"/> 时静默回落「压缩裸流 + MFVideoDecoder 自解码」路径。
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
    /// 伪异步：<c>await Task.Run(OpenCore)</c>（MFCreateSourceReaderFromURL + 轨道解析为同步 COM 调用）。
    /// 流本身不被读取（MF 按地址经自身网络栈打开），故无预建连；SSRF 校验已在流工厂创建时完成。
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

        // 从 IMediaStream 获取 URL（文件路径或网络 URL）
        // MFSourceReader 需要 URL 或 IMFByteStream。
        // 不调用 stream.ConnectAsync：MF 按地址自建连接，流本身不会被读取（预建连只会白建一条连接）。
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
            // 仅 Dispose 调度器会泄漏 _sourceReader，故走两阶段关闭协议：此时无在途原生调用，
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

            // 把 IMFDXGIDeviceManager 挂到 SourceReader 的创建属性上
            // SourceReader 拿到管理器后会自行完成「选硬件 MFT → 发 MFT_MESSAGE_SET_D3D_MANAGER →
            // 分配 DXGI 输出表面池」的全套编排（这正是直连 MFT 时会静默回落软件的那一段）。
            // 失败语义：任何一步不成都返回 IntPtr.Zero，等价「无属性」创建 ⇒ 压缩裸流 + MFVideoDecoder 自解码。
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
            // 硬编码 TimeSpan.Zero 会使 player.Duration 恒 0，基于时长的完成判定会假完成。
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

    /// <summary>MFT_OUTPUT_STREAM_INFO.dwFlags：MFT 自行分配输出 sample（零拷贝必要条件）。</summary>
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

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

    // 两阶段关闭协议
    // (1) BeginClose 关闸 → (2) WaitDrain 排空在途原生调用 → (3)【在专用 COM 单元线程上】释放 COM 指针
    // → (4) 之后才 Shutdown 调度器（放行 CoUninitialize）→ (5) 托管收尾。任一环节失败即有意泄漏。
    //
    // 步骤顺序原则（**勿把 (2) 与 (4) 换回来**）：
    //    若先让专用线程退出（其 Loop 的 finally 执行 CoUninitialize）再在关闭线程上 Marshal.Release：
    //    _sourceReader 是 OpenCore 在专用线程上经 MFCreateSourceReaderFromURL 创建的，
    //    CoUninitialize 会关闭该线程的 COM 库、卸载它加载的 in-proc
    //    server（mfreadwrite/mfplat 等），并在它是最后一个 MTA 成员时拆除整个 MTA。此后那次 Release 跳进
    //    已失效的 vtable ⇒ 原生访问违例 ⇒ 确定性的原生堆损坏（关闭顺序固定，非 flaky 竞态）。
    //    ⇒ 不变量（COM 单元）：**在专用单元线程上创建的 COM 对象，其 Release 必须在同一线程、且先于该线程 CoUninitialize。**
    //
    // 重入互斥：CloseSync / CloseAsync 共用 _closeStarted 这一 Interlocked 令牌，
    //    「先到者执行完整协议、后到者立即返回」。后到者不会等待协议完成——这是刻意取舍：
    //    并发调用 Dispose 与 DisposeAsync 本身即为调用方误用，此处保证的是**绝不二次 Release**
    //    （二次 Release 会直接引发访问违例），而非为误用提供关闭栅栏。

    // 辅助方法

    /// <summary>
    /// 从 IMediaStream 提取 URL（文件路径或网络 URL）。
    /// </summary>
    private static string? ExtractUrl(IMediaStream stream)
    {
        // 读取 IMediaStream 中性 Location（文件流返回路径、网络流返回 URL；无地址返回 null）。
        // 勿把 stream 强转 FileStream——实际类型为 FileMediaStream，永不命中，所有文件都会返回 null。
        return stream.Location;
    }

}
