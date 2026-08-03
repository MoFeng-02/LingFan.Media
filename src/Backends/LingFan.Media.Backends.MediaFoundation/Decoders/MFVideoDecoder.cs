using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// <see cref="IVideoDecoder"/> 的 MediaFoundation 实现（基于 <c>IMFTransform</c> 真实 MFT 解码）。
/// </summary>
/// <remarks>
/// <para><b>C 组 MF-4 真实落地</b>：消除原 <c>sqrt</c> 尺寸猜测空壳，改为经 <c>CoCreateInstance</c> 实例化
/// H264/H265 解码 MFT（CLSID_CMSH264DecoderMFT / CLSID_CMSH265DecoderMFT），通过 <c>IMFTransform</c> vtable
/// 调用 <c>SetInputType</c>/<c>SetOutputType</c>/<c>ProcessInput</c>/<c>ProcessOutput</c> 完成真实解码。</para>
/// <para><b>异步策略</b>（与 FFmpegVideoDecoder 对称，遵守总记忆第七章）：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <see cref="Task.CompletedTask"/>（无 I/O await，非伪异步）。</item>
/// <item><see cref="Initialize"/>：同步（sync 分类）—— MFStartup + CoCreateInstance + 建输入/输出媒体类型（<c>IMFAttributes::SetGUID</c> vtable）+ SetInputType/SetOutputType + BEGIN_STREAMING。</item>
/// <item><see cref="DecodeAsync"/>：热路径，<c>IMFTransform.ProcessInput/ProcessOutput</c> 为同步 COM 调用，返回 <see cref="ValueTask{TResult}"/>（同步完成，减少分配）。</item>
/// <item><see cref="FlushAsync"/>：热路径，发送 DRAIN 取剩余输出帧。</item>
/// <item><see cref="Reset"/>：同步，<c>ProcessMessage(COMMAND_FLUSH)</c>。</item>
/// </list>
/// <para><b>仅 Windows 可用</b>：非 Windows 平台 Initialize 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>AOT 兼容</b>：sealed 类；COM 互操作走原始 vtable P/Invoke（<see cref="MfVTable"/> 委托封送）+ 真实导出的 MF 扁平 API，
/// 不使用 <c>[ComImport]</c>/RCW，NativeAOT 兼容。</para>
/// <para><b>vtable 槽位</b>：公式 <c>slotIndex = SDK 绝对槽 − 3</c>；全部关键槽位已本机运行时验证（2026-07-29，MFTDiag 全 S_OK）——
/// IMFTransform：GetOutputStreamInfo=4, GetOutputAvailableType=11, SetInputType=12, SetOutputType=13,
/// GetOutputCurrentType=15, ProcessMessage=20, ProcessInput=21, ProcessOutput=22（注意 GetAttributes=5 不可漏数）；
/// IMFSample：GetBufferCount=36, ConvertToContiguousBuffer=38, AddBuffer=39；IMFAttributes：GetUINT64=5, SetGUID=21。</para>
/// <para><b>媒体类型属性</b>：建/读属性走 <c>IMFAttributes</c> vtable（<c>SetGUID=21</c>/<c>GetUINT64=5</c>）——
/// mfplat.dll 没有 <c>MFSetAttributeGUID</c>/<c>MFGetAttributeUINT64</c> 导出（mfapi.h inline helper），P/Invoke 必炸。</para>
/// <para><b>输出 sample 分配</b>：系统 H264/H265 同步解码 MFT 不设置 <c>MFT_OUTPUT_STREAM_PROVIDES_SAMPLES</c>，
/// 输出 sample 由调用方按 <c>GetOutputStreamInfo().cbSize</c> 预分配（本类实现），STREAM_CHANGE 后重查大小。</para>
/// <para><b>输出尺寸</b>：输入类型不写 FRAME_SIZE（MFT 从 SPS 推断）；输出 NV12 尺寸经 <c>IMFAttributes::GetUINT64(MF_MT_FRAME_SIZE)</c>
/// 从输出媒体类型读取，STREAM_CHANGE 时经 <c>GetOutputCurrentType</c> 重新协商。</para>
/// <para><b>时间戳</b>：输出帧 PTS/时长从输出 sample 读取（<c>GetSampleTime</c>），而非直接沿用输入 packet——
/// H264/H265 含 B 帧时解码输出顺序与输入顺序不同。读取失败时回退输入 packet 时间戳。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MFVideoDecoder : IVideoDecoder
{
    // MFT_MESSAGE_TYPE（mftransform.h 权威值）
    private const int MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
    private const int MFT_MESSAGE_COMMAND_DRAIN = 0x00000001;
    private const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const int MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    // MFT_OUTPUT_STREAM_INFO.dwFlags：MFT 自行分配输出 sample
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

    /// <summary>MFT_OUTPUT_DATA_BUFFER（x64 布局：4+pad、8、4+pad、8 = 32 字节，Sequential 默认对齐一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputBuffer
    {
        public int dwStreamID;
        public IntPtr pSample;
        public int dwStatus;
        public IntPtr pEvents;
    }

    private readonly ILogger<MFVideoDecoder> _logger;
    private bool _initialized;
    // MFStartup/MFShutdown 配对标志（2026-07-31 审计修复）：Initialize 成功调用 MFStartup 后置 true，
    // ReleaseComObjects 中配对 MFShutdown 并复位。原实现只 Startup 不 Shutdown → 进程级平台
    // 引用计数只增不减，MF 平台常驻进程永不释放（内存/句柄泄漏）。
    private bool _mfStartupAcquired;

    // 两阶段关闭协议构件（V1/V3）：关闸 → 排空在途原生调用 → 独占释放或意泄漏。
    private readonly NativeCallGate _transformGate = new();
    private bool _leakedOnClose;   // drain 失败标记：已有意泄漏，禁止任何后续释放尝试

    // 防止 Dispose/DisposeAsync 重入。0=未关闭，1=已发起关闭。
    // ⚠️ 必须是 Interlocked 原子量而非普通 bool（审计 A-2）：并发的 Dispose 与 DisposeAsync
    // 在普通 bool 上「读-判-写」非原子，可同时通过守卫 ⇒ 对同一 IMFTransform 二次 Marshal.Release
    // ⇒ 引用计数下溢 / 访问违例（0x80131506 故障族）。
    private int _closed;

    private Guid _inputSubtype;
    private IntPtr _transform;
    private IntPtr _inputTypePtr;
    private IntPtr _outputTypePtr;
    private int _width;         // 显示宽（display aperture；无 aperture 时 = 编码宽）
    private int _height;        // 显示高
    private int _codedWidth;    // 编码宽（宏块对齐，如 1920）——NV12 平面 stride 依据
    private int _codedHeight;   // 编码高（宏块对齐，如 1088）——chroma 平面偏移依据
    private bool _loggedLayoutOnce; // 首帧布局诊断只打一次（display/coded 尺寸 + MF 源 buffer 真实长度）

    // ── MFT 输出积压队列（2026-08-03 丢包根因修复）──────────────────────────────
    // 🔴 H.264 解码 MFT 是 **N 进 M 出**：受 B 帧重排/DPB 影响，单次 ProcessInput 后可能
    //    产出 0 帧、也可能产出多帧；且在**输出未被取空前会以 MF_E_NOTACCEPTING 拒收新输入**。
    //    IVideoDecoder 契约是「一次调用最多返回一帧」，故多产出的帧暂存于此队列，由后续调用
    //    依次取走 —— 保证「入包数 == 出帧数」，绝不丢帧。
    //    旧实现只取 1 个输出且把 NOTACCEPTING 当非致命吞掉，等价于按比例静默丢弃压缩包：
    //    30fps 源实测只出 22fps，且参考帧缺失 ⇒ 花屏（宏块拖影）+ PTS 缺口（卡顿/回弹）。
    private readonly Queue<VideoFrame> _pendingOutputs = new();
    private long _notAcceptingDrops;      // 排空后仍被拒收而不得不丢弃的包数（应恒为 0）
    private bool _drainSent;              // MFT_MESSAGE_COMMAND_DRAIN 只发一次
    private bool _warnedPendingBacklog;   // 积压异常告警只打一次
    private const int MaxNotAcceptingRetries = 8;  // 「排空→重投」最大轮数，防活锁
    private const int MaxOutputsPerDrain = 64;     // 单轮最多取帧数，防异常 MFT 无限吐帧

    // ── 显示孔径偏移（MFVideoArea.OffsetX/OffsetY）─────────────────────────────
    // 🔴 2026-08-03：旧代码只读 Area.cx/cy 而丢弃 Offset，并在注释里臆断「aperture 偏移为 0」。
    //    若 OffsetX != 0，从 (0,0) 起裁会使画面整体平移，左/上边缘吃进编码填充（宏块边缘扩展
    //    = 竖向拉丝），且奇数 OffsetX 在 4:2:0 下令色度错半像素（色噪）。此处改为实测并参与裁剪。
    private int _apertureOffsetX;
    private int _apertureOffsetY;
    /// <summary>MFVideoArea blob 的原始 16 字节 hex，仅用于首帧诊断（防止结构布局理解错误时无从对证）。</summary>
    private string? _apertureBlobHex;

    // 输出 sample 分配策略（GetOutputStreamInfo）
    private bool _mftProvidesSamples;
    private uint _outputBufferSize;

    // 缓存的 IMFTransform vtable 委托（AOT 兼容；slotIndex = 绝对槽 − 3）
    private IMFTransform_GetOutputStreamInfo? _getOutputStreamInfo;
    private IMFTransform_SetInputType? _setInputType;
    private IMFTransform_SetOutputType? _setOutputType;
    private IMFTransform_ProcessMessage? _processMessage;
    private IMFTransform_ProcessInput? _processInput;
    private IMFTransform_ProcessOutput? _processOutput;

    public MFVideoDecoder(ILogger<MFVideoDecoder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec { get; private set; }

    /// <inheritdoc/>
    public bool IsHardwareAccelerated { get; private set; }

    /// <inheritdoc/>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MediaFoundation 后端仅支持 Windows。");
        if (_initialized)
            throw new InvalidOperationException("MF 视频解码器已初始化，请先 Dispose 再重新初始化。");
        // V3 补强：关闭不可逆（gate 一旦 BeginClose 便永久关闸，见 NativeCallGate 不变量 I1）。
        // 已关闭实例若重新 Initialize，后续 DecodeAsync 的 TryEnter 恒失败 ⇒ 静默恒返回 null 帧（哑解码器）。
        // 故直接快速失败；Session 级对象按约定为 Transient，请新建实例。
        if (Volatile.Read(ref _closed) != 0)
            throw new InvalidOperationException("该 MF 视频解码器实例已关闭，不可重新初始化；请新建实例。");

        Codec = codec;
        IsHardwareAccelerated = settings.EnableHardwareAcceleration;

        // 输入 subtype（H265 系统 MFT 注册的输入 subtype 为 "HEVC"）
        if (codec == VideoCodec.H264) _inputSubtype = MFConstants.MFVideoFormat_H264;
        else if (codec == VideoCodec.H265) _inputSubtype = MFConstants.MFVideoFormat_HEVC;
        else
            throw new NotSupportedException($"MF 视频解码器不支持 {codec}（仅 H264/H265 经系统 MFT）。");

        // ⚠️ 复审 C-1（2026-08-01 二轮审计）：整个原生建立段必须处于 gate 内，与 MFDemuxer.OpenCore 对称。
        // 上面的 _closed 检查只是 TOCTOU 快速失败，**不构成互斥**。缺闸时的竞态：
        // 本线程通过检查后，并发的 Dispose/DisposeAsync 会因 _inFlight==0 立即判定「已排空」→ 执行
        // ReleaseComObjects（含 MFPlatform.Shutdown 使引用计数 −1），而本线程随后仍在
        // CoCreateInstance / SetInputType / SetOutputType / ProcessMessage 上跑原生 COM。后果三重：
        //   ① 本解码器若是最后一个 MF 消费者，平台被真正 MFShutdown 拆除 ⇒ 原生访问违规 ⇒ 0x80131506；
        //   ② _closed 已置 1，此后任何 Close 都在 Interlocked 处直接返回 ⇒ 本方法建立的 _transform /
        //      _inputTypePtr / _outputTypePtr 永久泄漏，且 MF 引用计数永久失衡（多一次 Startup 无配对）；
        //   ③ gate 恒关 ⇒ DecodeAsync 的 TryEnter 恒 false ⇒ 退化为静默返回 null 帧的「哑解码器」（黑屏无报错）。
        // 入闸后三者全部消失：Close 侧必须等本段 Exit 才可能判定排空。
        if (!_transformGate.TryEnter())
            throw new InvalidOperationException("该 MF 视频解码器实例正在关闭，无法初始化；请新建实例。");

        bool holdsGate = true;
        int hr;
        try
        {
            // MFStartup（进程级引用计数，幂等；成功后须与 ReleaseComObjects 中的 MFShutdown 配对）。
            // 经 MFPlatform 引用计数封装：MF 平台仅在所有消费者（MFBackend 解封装 + 本解码器）全部释放后才真正拆除，
            // 避免某一侧先释放触发 MFShutdown 把正在另一线程 in-flight 的原生 ReadSample/ProcessInput 踩成 AV。
            // 置于 gate 内（复审 C-1）：Startup 与 ReleaseComObjects 里的 Shutdown 必须被同一把闸串行化。
            MFPlatform.Startup();
            _mfStartupAcquired = true;

            // 经 MFTEnum 动态发现注册的解码 MFT（避免硬编码 CLSID 在部分 Windows 上未注册 / HEVC 可选）
            Guid clsid = FindDecoderClsid(_inputSubtype);
            if (clsid == Guid.Empty)
                throw new PlatformNotSupportedException(
                    $"未找到 {codec} 解码 MFT（系统可能未注册对应解码器；HEVC 需安装“HEVC 视频扩展”）。");

            // CoCreateInstance 实例化解码 MFT
            Guid iid = MFConstants.IID_IMFTransform;
            hr = MFInterop.CoCreateInstance(ref clsid, IntPtr.Zero, MFInterop.CLSCTX_ALL, ref iid, out _transform);
            Marshal.ThrowExceptionForHR(hr);

            // 缓存 vtable 委托（slotIndex = 绝对槽 − 3；经 Windows SDK mftransform.h 声明顺序推得，
            // 并已于本机 MFTDiag 运行时逐槽验证（2026-07-29，CLSID_MSH264DecoderMFT 62ce7e72 全 S_OK））。
            // IMFTransform 顺序（注意 GetAttributes=5，早期注释曾漏它导致全体差 1）：
            //   GetStreamLimits=0/GetStreamCount=1/GetStreamIDs=2/GetInputStreamInfo=3/GetOutputStreamInfo=4/GetAttributes=5/
            //   GetInputStreamAttributes=6/GetOutputStreamAttributes=7/DeleteInputStream=8/AddInputStreams=9/GetInputAvailableType=10/
            //   GetOutputAvailableType=11/SetInputType=12/SetOutputType=13/GetInputCurrentType=14/GetOutputCurrentType=15/
            //   GetInputStatus=16/GetOutputStatus=17/SetOutputBounds=18/ProcessEvent=19/ProcessMessage=20/ProcessInput=21/ProcessOutput=22
            _getOutputStreamInfo = MfVTable.Get<IMFTransform_GetOutputStreamInfo>(_transform, 4);   // 绝对 7
            _setInputType = MfVTable.Get<IMFTransform_SetInputType>(_transform, 12);                // 绝对 15
            _setOutputType = MfVTable.Get<IMFTransform_SetOutputType>(_transform, 13);              // 绝对 16
            _processMessage = MfVTable.Get<IMFTransform_ProcessMessage>(_transform, 20);            // 绝对 23
            _processInput = MfVTable.Get<IMFTransform_ProcessInput>(_transform, 21);                // 绝对 24
            _processOutput = MfVTable.Get<IMFTransform_ProcessOutput>(_transform, 22);             // 绝对 25

            // GUID 局部副本（static readonly 字段不可直接作 ref 实参，CS0199）
            Guid mtMajorType = MFConstants.MF_MT_MAJOR_TYPE;
            Guid mtSubtype = MFConstants.MF_MT_SUBTYPE;
            Guid mediaTypeVideo = MFConstants.MFMediaType_Video;
            Guid formatNv12 = MFConstants.MFVideoFormat_NV12;

            // 建输入媒体类型（MajorType=Video, Subtype=H264/HEVC；不写 FRAME_SIZE，MFT 从 SPS 推断）。
            // 属性写入走 IMFAttributes::SetGUID vtable（slotIndex=21，运行时已验证）——
            // mfplat.dll **没有** MFSetAttributeGUID 导出（mfapi.h inline helper），P/Invoke 会 EntryPointNotFound。
            hr = MFInterop.MFCreateMediaType(out _inputTypePtr);
            Marshal.ThrowExceptionForHR(hr);
            var setGuidIn = MfVTable.Get<IMFAttributes_SetGUID>(_inputTypePtr, 21);
            ThrowIfFailed(setGuidIn(_inputTypePtr, ref mtMajorType, ref mediaTypeVideo));
            ThrowIfFailed(setGuidIn(_inputTypePtr, ref mtSubtype, ref _inputSubtype));

            // 应用 out-of-band 编解码器私有配置（H264/H265 的 SPS+PPS / avcC / hvcC）→ MF_MT_MPEG_SEQUENCE_HEADER。
            // MP4 容器内 SPS/PPS 在 avcC 盒，不在每个 sample 内联；缺它解码器永久 NEED_MORE_INPUT。
            // 走 IMFAttributes::SetBlob vtable（slotIndex=23，运行时已验证）；须在 SetInputType 之前写入。
            if (settings.CodecConfiguration.Length > 0)
            {
                Guid seqKey = MFConstants.MF_MT_MPEG_SEQUENCE_HEADER;
                var setBlob = MfVTable.Get<IMFAttributes_SetBlob>(_inputTypePtr, 23);
                var cfg = settings.CodecConfiguration;
                IntPtr h = Marshal.AllocHGlobal(cfg.Length);
                try
                {
                    Marshal.Copy(cfg.Span.ToArray(), 0, h, cfg.Length);
                    ThrowIfFailed(setBlob(_inputTypePtr, ref seqKey, h, (uint)cfg.Length));
                }
                finally
                {
                    Marshal.FreeHGlobal(h);
                }
            }

            hr = _setInputType!(_transform, 0, _inputTypePtr, 0);
            Marshal.ThrowExceptionForHR(hr);

            // 输出类型协商（MSDN 规范路径）：枚举 MFT 自报的可用输出类型，优先选 NV12；
            // 枚举失败（输入类型未定尺寸时部分 MFT 返回 MF_E_TRANSFORM_TYPE_NOT_SET）则回退手工建最小 NV12 类型。
            IntPtr chosenOutType = SelectOutputType(ref formatNv12);
            if (chosenOutType == IntPtr.Zero)
            {
                hr = MFInterop.MFCreateMediaType(out chosenOutType);
                Marshal.ThrowExceptionForHR(hr);
                var setGuidOut = MfVTable.Get<IMFAttributes_SetGUID>(chosenOutType, 21);
                ThrowIfFailed(setGuidOut(chosenOutType, ref mtMajorType, ref mediaTypeVideo));
                ThrowIfFailed(setGuidOut(chosenOutType, ref mtSubtype, ref formatNv12));
            }
            _outputTypePtr = chosenOutType;

            hr = _setOutputType!(_transform, 0, _outputTypePtr, 0);
            Marshal.ThrowExceptionForHR(hr);

            // 查询输出 sample 分配策略与所需大小
            QueryOutputStreamInfo();

            // 通知开始流式处理
            _processMessage!(_transform, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            _processMessage!(_transform, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

            // 读取输出尺寸（若 MFT 已填充）
            TryReadOutputDimensions();

            _initialized = true;
            _logger.LogDebug("MF 视频解码器初始化: {Codec}, 硬解={Hw}, MFT提供输出sample={Provides}, cbSize={Size}",
                codec, IsHardwareAccelerated, _mftProvidesSamples, _outputBufferSize);
        }
        catch
        {
            // ⚠️ 必须**先出闸、再走关闭协议**（复审 C-1）：CloseNativeSync 内的 WaitDrain 等待在途计数归零，
            // 而此刻唯一的在途计数正是本线程自己 —— 不先 Exit 就会自等满 NativeDrain(5s) 超时 ⇒ 误判「排空失败」
            // ⇒ 把本可安全释放的 _transform 错误地「有意泄漏」并打 Error 日志（直接污染关闭洁净度门控）。
            // 先 Exit 使计数归零后，Close 侧的 drain 立即成功，资源被真正释放。
            _transformGate.Exit();
            holdsGate = false;
            CloseNativeSync();
            throw;
        }
        finally
        {
            // holdsGate 防多余 Exit：多余 Exit 会把他人的在途计数错误减到 0 ⇒ 提前判定排空 ⇒ use-after-free
            //（NativeCallGate 不变量 I6 的「危险侧」失配，见该类 Exit 备注）。
            if (holdsGate) _transformGate.Exit();
        }
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask; // 契约方法：无 I/O await，非伪异步
    }

    /// <inheritdoc/>
    public unsafe ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // V2：decode 全程处于 gate 内，与 Dispose 的释放形成互斥（窗口 A）。关闸时立即返回空帧。
        if (!_transformGate.TryEnter())
            return new ValueTask<VideoFrame?>((VideoFrame?)null);
        try
        {
            if (!_initialized || _transform == IntPtr.Zero)
                return new ValueTask<VideoFrame?>((VideoFrame?)null);
            if (packet.Data.Length == 0)
                return new ValueTask<VideoFrame?>((VideoFrame?)null);

            // 1. 创建 sample + 内存 buffer，拷贝压缩数据
            IntPtr sample = IntPtr.Zero;
            try
            {
                sample = CreateSampleWithData(packet);

                // 2. ProcessInput。
                // 🔴 MFT 契约铁律：MF_E_NOTACCEPTING 意为「**本包未被接收**」——MFT 内部尚有
                //    未取走的输出占着位置，必须先 ProcessOutput 排空、再**重投同一 sample**。
                //    它绝不是「非致命可忽略」：旧实现吞掉该码后 finally 直接 Release(sample)，
                //    压缩包就此蒸发 ⇒ 参考帧缺失（花屏）+ PTS 缺口（卡顿/回弹）。
                //    MFT 未接收时不会 AddRef，我方引用仍然有效，故可安全重投。
                int hr = _processInput!(_transform, 0, sample, 0);
                for (int attempt = 0;
                     hr == MFConstants.MF_E_NOTACCEPTING && attempt < MaxNotAcceptingRetries;
                     attempt++)
                {
                    if (DrainAvailableOutputs(packet) == 0)
                        break; // 既不收输入也不吐输出：异常状态，跳出避免活锁
                    hr = _processInput(_transform, 0, sample, 0);
                }

                if (hr == MFConstants.MF_E_NOTACCEPTING)
                {
                    // 排空后仍拒收：只能丢弃本包，但必须显式计数 + 告警，绝不静默
                    _notAcceptingDrops++;
                    _logger.LogWarning(
                        "MFT 排空输出后仍拒收输入包（PTS={Pts}），本包丢弃，累计 {Count} 个 —— 将造成参考帧缺失/花屏",
                        packet.Timestamp, _notAcceptingDrops);
                }
                else if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }
            }
            finally
            {
                if (sample != IntPtr.Zero)
                    Marshal.Release(sample);
            }

            // 3. 排空本轮所有就绪输出（可能 0~多帧），取队首返回；
            //    其余留在 _pendingOutputs 供后续调用依次取走 —— 入包数 == 出帧数。
            DrainAvailableOutputs(packet);
            return new ValueTask<VideoFrame?>(DequeuePendingOutput());
        }
        finally { _transformGate.Exit(); }
    }

    /// <summary>创建携带 packet 压缩数据的 IMFSample（buffer 所有权移交 sample）。</summary>
    private unsafe IntPtr CreateSampleWithData(MediaPacket packet)
    {
        int hr = MFInterop.MFCreateSample(out IntPtr sample);
        Marshal.ThrowExceptionForHR(hr);
        InteropTrace.OnAlloc(sample, "CreateSampleWithData:sample");

        IntPtr buffer = IntPtr.Zero;
        try
        {
            hr = MFInterop.MFCreateMemoryBuffer(packet.Data.Length, out buffer);
            Marshal.ThrowExceptionForHR(hr);
            InteropTrace.OnAlloc(buffer, "CreateSampleWithData:buffer");

            // Lock → 拷贝 → Unlock → 标记有效长度（IMFMediaBuffer：Lock=0, Unlock=1, GetCurrentLength=2, SetCurrentLength=3）
            var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(buffer, 0);
            var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(buffer, 1);
            var setLenDel = MfVTable.Get<IMFMediaBuffer_SetCurrentLength>(buffer, 3);

            hr = lockDel(buffer, out IntPtr pBuf, out _, out _);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                packet.Data.Span.CopyTo(new Span<byte>((void*)pBuf, packet.Data.Length));
            }
            finally
            {
                unlockDel(buffer);
            }
            setLenDel(buffer, (uint)packet.Data.Length);

            // buffer 挂到 sample（AddBuffer 内部 AddRef；本地引用在 finally 释放）
            // IMFSample 槽位（mfobjects.idl 顺序，ConvertToContiguousBuffer=38 已真机锚定）：AddBuffer = slotIndex 39（运行时已验证）
            var addBuf = MfVTable.Get<IMFSample_AddBuffer>(sample, 39);
            hr = addBuf(sample, buffer);
            Marshal.ThrowExceptionForHR(hr);

            // 设置样本时间/时长（100ns 单位）
            var setTime = MfVTable.Get<IMFSample_SetSampleTime>(sample, 33);     // IMFAttributes(30) + 3
            var setDur = MfVTable.Get<IMFSample_SetSampleDuration>(sample, 35);  // IMFAttributes(30) + 5
            setTime(sample, packet.Timestamp.Ticks);   // TimeSpan.Ticks 即 100ns 单位
            setDur(sample, packet.Duration.Ticks);

            return sample;
        }
        catch
        {
            Marshal.Release(sample);
            throw;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.Release(buffer);
        }
    }

    /// <summary>创建空输出 sample（调用方分配模式，按 GetOutputStreamInfo.cbSize）。</summary>
    private IntPtr CreateOutputSample()
    {
        int size = (int)Math.Max(_outputBufferSize, 1);
        int hr = MFInterop.MFCreateSample(out IntPtr sample);
        Marshal.ThrowExceptionForHR(hr);
        InteropTrace.OnAlloc(sample, "CreateOutputSample:sample");

        IntPtr buffer = IntPtr.Zero;
        try
        {
            hr = MFInterop.MFCreateMemoryBuffer(size, out buffer);
            Marshal.ThrowExceptionForHR(hr);
            InteropTrace.OnAlloc(buffer, "CreateOutputSample:buffer");

            var addBuf = MfVTable.Get<IMFSample_AddBuffer>(sample, 39); // AddBuffer = slotIndex 39（运行时已验证）
            hr = addBuf(sample, buffer);
            Marshal.ThrowExceptionForHR(hr);
            return sample;
        }
        catch
        {
            Marshal.Release(sample);
            throw;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.Release(buffer);
        }
    }

    /// <summary>
    /// 排空 MFT **当前所有就绪输出**，全部压入 <see cref="_pendingOutputs"/>，返回本轮产出帧数。
    /// </summary>
    /// <remarks>
    /// 🔴 每次 ProcessInput 之后必须循环 ProcessOutput 直到 <c>MF_E_TRANSFORM_NEED_MORE_INPUT</c>。
    /// 只取一帧会让输出在 MFT 内部积压，进而使后续 ProcessInput 持续返回
    /// <c>MF_E_NOTACCEPTING</c> ⇒ 输入包被拒 ⇒ 丢帧。这是 30fps 源只出 22fps 的直接原因。
    /// </remarks>
    private int DrainAvailableOutputs(MediaPacket sourcePacket)
    {
        int produced = 0;
        for (int i = 0; i < MaxOutputsPerDrain; i++)
        {
            var frame = ProcessOutputOnce(sourcePacket, out bool needMoreInput);
            if (frame == null)
            {
                // needMoreInput = MFT 已排空（正常收口）；否则为重协商失败/提取失败，同样停止本轮
                _ = needMoreInput;
                break;
            }
            _pendingOutputs.Enqueue(frame);
            produced++;
        }

        // 积压异常兜底告警：正常仅为 MFT 重排深度（H.264 DPB ≤ 16）
        if (_pendingOutputs.Count > 24 && !_warnedPendingBacklog)
        {
            _warnedPendingBacklog = true;
            _logger.LogWarning("MFT 输出积压异常偏高（{Count} 帧），消费侧可能未及时取帧", _pendingOutputs.Count);
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

    /// <summary>从输出 sample 提取 NV12 数据并构建 <see cref="VideoFrame"/>（接管并释放 sample 引用）。</summary>
    private unsafe VideoFrame? ExtractFrame(IntPtr sample, MediaPacket sourcePacket)
    {
        try
        {
            // 时间戳优先从输出 sample 读（B 帧重排后输出顺序 ≠ 输入顺序）；失败回退输入 packet
            var ts = sourcePacket.Timestamp;
            var dur = sourcePacket.Duration;
            var getTime = MfVTable.Get<IMFSample_GetSampleTime>(sample, 32);     // IMFAttributes(30) + 2
            var getDur = MfVTable.Get<IMFSample_GetSampleDuration>(sample, 34);  // IMFAttributes(30) + 4
            if (getTime(sample, out long sampleTime) >= 0)
                ts = TimeSpan.FromTicks(sampleTime);
            if (getDur(sample, out long sampleDur) >= 0 && sampleDur > 0)
                dur = TimeSpan.FromTicks(sampleDur);

            // 输出数据可能分散在多个 buffer，ConvertToContiguousBuffer 合并
            // IMFSample 槽位：ConvertToContiguousBuffer = 绝对 41 → slotIndex 38
            // （IMFAttributes 恰 30 方法，IMFSample 第 9 方法；运行时已验证 slot38 返回有效 buffer）
            var toContig = MfVTable.Get<IMFSample_ConvertToContiguousBuffer>(sample, 38);
            int hr = toContig(sample, out IntPtr outBuffer);
            if (hr < 0 || outBuffer == IntPtr.Zero)
                return null;

            try
            {
                var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(outBuffer, 0);
                var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(outBuffer, 1);
                var getLen = MfVTable.Get<IMFMediaBuffer_GetCurrentLength>(outBuffer, 2);

                getLen(outBuffer, out uint currentLength);
                if (currentLength == 0)
                    return null;

                // 确保尺寸已知（首帧 STREAM_CHANGE 后刚协商出）
                if (_width <= 0 || _height <= 0)
                    TryReadOutputDimensions();
                if (_width <= 0 || _height <= 0)
                {
                    _logger.LogWarning("MF 解码输出尺寸未知，跳过该帧。");
                    return null;
                }

                // 🔴 R5 配对铁律（2026-08-02 校准，与 MFDemuxer.ExtractPacket 同构）：
                //    IMFMediaBuffer.Unlock 只能与【成功的】Lock 配对，且恰好一次。
                //    下方「Lock → 拷贝 → Unlock」整体置于【嵌套 try】，所有提前 return
                //    （currentLength==0 / 尺寸未知 / Lock 失败）都发生在进入该嵌套 try 之前，
                //    故绝不会触发 Unlock。旧代码把 Unlock 放在外层 finally，导致未 Lock 即 Unlock
                //    （2D/DXGI 临时拷贝实现的 Unlock 会野指针写）→ 滞后至下次 CLR 堆操作才以 0x80131506 暴露。
                // 🔴 R5 配对铁律（2026-08-02 校准，与 MFDemuxer.ExtractPacket 同构）：
                //    IMFMediaBuffer.Unlock 只能与【成功的】Lock 配对，且恰好一次。
                //    错误链：经 InteropTrace 记录并（严格模式）校验 Lock/Unlock 配对。
                hr = InteropTrace.LockBuffer(outBuffer, lockDel, out IntPtr pData, out _, out _,
                    "ExtractFrame:IMFMediaBuffer.Lock");
                if (hr < 0)
                {
                    _logger.LogWarning("IMFMediaBuffer.Lock 失败: HRESULT=0x{HR:X8}（未 Unlock，符合配对规范）", hr);
                    return null;
                }
                SoftwareFrameResource resource;
                try
                {
                    var src = new ReadOnlySpan<byte>((void*)pData, (int)currentLength);

                    // 首帧布局诊断：把「显示尺寸 / 编码尺寸 / MF 源 buffer 真实长度」一次性摊开。
                    // 关键判据——currentLength 是否等于 codedW*codedH*3/2：
                    //   相等 → 下方裁剪路径的「源 stride == codedWidth」假定成立；
                    //   不等 → 假定破产，逐行拷贝会渐进错位（斜条纹/花屏）。
                    if (!_loggedLayoutOnce)
                    {
                        _loggedLayoutOnce = true;
                        bool cropPath = !(_codedWidth <= 0 || (_codedWidth == _width && _codedHeight == _height));
                        long codedLen = (long)_codedWidth * _codedHeight * 3 / 2;
                        long compactLen = (long)_width * _height * 3 / 2;
                        int uvRows = _codedHeight * 3 / 2;
                        int derivedStride = uvRows > 0 ? (int)(currentLength / (uint)uvRows) : 0;
                        _logger.LogInformation(
                            "[NV12-LAYOUT] display={W}x{H} coded={CW}x{CH} MF源buffer={Len}B | " +
                            "codedW*codedH*1.5={CodedLen}B compact={CompactLen}B | 按codedH推导源stride={S} | 路径={Path} | stride假定={Verdict}",
                            _width, _height, _codedWidth, _codedHeight, currentLength,
                            codedLen, compactLen, derivedStride,
                            cropPath ? "裁剪逐行" : "整块拷贝",
                            currentLength == codedLen ? "成立" : "★破产★");

                        // 显示孔径偏移留证：旧代码臆断为 0，此处摊开实测值 + 原始 blob，供逐字节对证。
                        int gapX = _codedWidth - _width;
                        int gapY = _codedHeight - _height;
                        string offVerdict = _apertureOffsetX == 0 && _apertureOffsetY == 0
                            ? (gapX > 0 && gapX % 2 == 0 && _apertureOffsetX == 0
                                ? "偏移=0（裁剪全在右/下边）"
                                : "偏移=0")
                            : $"★偏移非零 → 旧代码从(0,0)起裁会整体平移 {_apertureOffsetX}列/{_apertureOffsetY}行★";
                        _logger.LogInformation(
                            "[APERTURE] OffsetX={OX} OffsetY={OY} | 编码-显示差 宽={GX} 高={GY} | blob16B={Hex} | {Verdict}",
                            _apertureOffsetX, _apertureOffsetY, gapX, gapY,
                            _apertureBlobHex ?? "(未取到)", offVerdict);

                        // 自给自足的黄金参照：把【裁剪之前】的完整 coded 帧落盘，
                        // 直接肉眼/数值确认真实画面从第几列开始，无需 ffmpeg 等外部解码器对照。
                        MFCodedFrameDump.TryDump(src, _codedWidth, _codedHeight, _width, _height,
                            _apertureOffsetX, _apertureOffsetY, _logger);
                    }

                    if (_codedWidth <= 0 || (_codedWidth == _width && _codedHeight == _height))
                    {
                        // 编码尺寸即显示尺寸（或编码尺寸未知）：整块拷贝
                        resource = new SoftwareFrameResource(_width, _height, PixelFormat.NV12, (int)currentLength);
                        src.CopyTo(resource.Data.Span);
                    }
                    else
                    {
                        // 显示孔径裁剪：NV12 编码布局 = Y[codedW×codedH] + UV[codedW×codedH/2]，
                        // 逐行拷贝到紧凑的 display 布局（stride 假定 = codedWidth，MFT 输出 NV12 无额外行距）。
                        // 🔴 2026-08-03 修正：起点必须用 MFVideoArea.OffsetX/OffsetY，不得臆断为 0。
                        //    旧注释「H264 裁剪只发生在右/下边」是未经验证的假设——非对称裁剪（如 1920→1906
                        //    左右各去 7）会让 (0,0) 起裁的画面整体平移，左边缘吃进编码填充（宏块边缘扩展）。
                        // UV 列偏移须对齐到偶数：NV12 的 UV 是 U,V 交错对，一对覆盖 2 列 Y。
                        int offX = _apertureOffsetX;
                        int offY = _apertureOffsetY;
                        int uvOffX = offX & ~1;      // 向下取偶
                        int uvOffY = offY / 2;

                        int dstLen = _width * _height * 3 / 2;
                        resource = new SoftwareFrameResource(_width, _height, PixelFormat.NV12, dstLen);
                        var dst = resource.Data.Span;
                        for (int y = 0; y < _height; y++)
                            src.Slice((offY + y) * _codedWidth + offX, _width).CopyTo(dst.Slice(y * _width, _width));
                        int srcUv = _codedWidth * _codedHeight;
                        int dstUv = _width * _height;
                        for (int y = 0; y < _height / 2; y++)
                            src.Slice(srcUv + (uvOffY + y) * _codedWidth + uvOffX, _width)
                               .CopyTo(dst.Slice(dstUv + y * _width, _width));
                    }
                }
                finally
                {
                    InteropTrace.UnlockBuffer(outBuffer, unlockDel, "ExtractFrame:IMFMediaBuffer.Unlock");
                }

                return new VideoFrame(_width, _height, PixelFormat.NV12, resource, ts, dur, sourcePacket.KeyFrame);
            }
            finally
            {
                InteropTrace.ReleaseComPtr(outBuffer, "ExtractFrame:outBuffer");
            }
        }
        finally
        {
            InteropTrace.ReleaseComPtr(sample, "ExtractFrame:sample");
        }
    }

    /// <summary>
    /// STREAM_CHANGE 后重新协商输出媒体类型，更新尺寸与输出 buffer 大小。
    /// MS 推荐流程：从 <c>GetOutputAvailableType</c> 枚举<b>新的</b>可用类型（优先 NV12）→ <c>SetOutputType</c>；
    /// 不能把 <c>GetOutputCurrentType</c> 的旧类型原样设回（本机验证会返回 MF_E_INVALIDMEDIATYPE 0xC00D36B4）。
    /// </summary>
    private bool RenegotiateOutput()
    {
        // V2（防御性）：RenegotiateOutput 触碰 _transform/_outputTypePtr，须处于 gate 内。
        // 正常仅从 DecodeAsync/FlushAsync（已在 gate 内）调用，此处再包裹一次无副作用（嵌套 Enter/Exit 配对）。
        if (!_transformGate.TryEnter()) return false;
        try
        {
            Guid nv12 = MFConstants.MFVideoFormat_NV12;
            IntPtr newType = SelectOutputType(ref nv12);
            if (newType == IntPtr.Zero)
            {
                _logger.LogWarning("MF 输出流变更后无可用输出类型");
                return false;
            }

            int hr = _setOutputType!(_transform, 0, newType, 0);
            if (hr < 0)
            {
                Marshal.Release(newType);
                _logger.LogWarning("MF 输出流变更后 SetOutputType 失败: 0x{Hr:X8}", hr);
                return false;
            }

            // 应用成功后替换缓存的输出类型引用
            if (_outputTypePtr != IntPtr.Zero)
                Marshal.Release(_outputTypePtr);
            _outputTypePtr = newType;

            TryReadOutputDimensions();
            QueryOutputStreamInfo();
            _logger.LogDebug("MF 输出流重协商完成: {W}x{H}, cbSize={Size}", _width, _height, _outputBufferSize);
            return true;
        }
        finally { _transformGate.Exit(); }
    }

    /// <summary>
    /// 枚举 MFT 可用输出类型（GetOutputAvailableType，slotIndex=11），返回 subtype 匹配 <paramref name="wantedSubtype"/> 的类型
    /// （调用方接管引用）；未匹配则返回首个可用类型；枚举不到返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    private IntPtr SelectOutputType(ref Guid wantedSubtype)
    {
        var getAvail = MfVTable.Get<IMFTransform_GetOutputAvailableType>(_transform, 11); // 绝对 14
        Guid mtSubtype = MFConstants.MF_MT_SUBTYPE;
        IntPtr first = IntPtr.Zero;
        for (uint i = 0; ; i++)
        {
            int hr = getAvail(_transform, 0, i, out IntPtr type);
            if (hr < 0 || type == IntPtr.Zero)
                break;

            // IMFMediaType.GetGUID = slotIndex 7（IMFAttributes 第 8 方法；MFDemuxer.ParseTracks 同槽已验证）
            var getGuid = MfVTable.Get<IMFMediaType_GetGuid>(type, 7);
            if (getGuid(type, ref mtSubtype, out Guid subtype) >= 0 && subtype == wantedSubtype)
            {
                if (first != IntPtr.Zero) Marshal.Release(first);
                return type; // 命中目标 subtype（NV12）
            }

            if (first == IntPtr.Zero) first = type; // 记住首个作回退
            else Marshal.Release(type);
        }
        return first;
    }

    /// <summary>查询输出流信息：MFT 是否自行提供输出 sample 与所需 buffer 大小。</summary>
    private void QueryOutputStreamInfo()
    {
        int hr = _getOutputStreamInfo!(_transform, 0, out MftOutputStreamInfo info);
        if (hr >= 0)
        {
            _mftProvidesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            _outputBufferSize = info.cbSize;
        }
        else
        {
            // 查询失败时保守回退：按 NV12 最大预估（重协商后会再查）
            _mftProvidesSamples = false;
            if (_outputBufferSize == 0 && _width > 0 && _height > 0)
                _outputBufferSize = (uint)(_width * _height * 3 / 2);
        }
    }

    /// <summary>
    /// 从输出媒体类型读取尺寸：FRAME_SIZE（uint64 = (w&lt;&lt;32)|h）为宏块对齐<b>编码尺寸</b>（如 1920x1088）；
    /// <b>显示尺寸</b>（1920x1080）从 MF_MT_MINIMUM_DISPLAY_APERTURE（MFVideoArea blob）取，缺失时回退编码尺寸。
    /// </summary>
    private void TryReadOutputDimensions()
    {
        if (_outputTypePtr == IntPtr.Zero)
            return;
        Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
        // IMFAttributes::GetUINT64 = slotIndex 5（运行时已验证）；mfplat 无 MFGetAttributeUINT64 导出（inline helper）
        int hr = MfVTable.Get<IMFMediaType_GetUINT64>(_outputTypePtr, 5)(_outputTypePtr, ref frameSizeKey, out ulong fs);
        if (hr >= 0 && fs != 0)
        {
            _codedWidth = (int)(fs >> 32);
            _codedHeight = (int)(fs & 0xFFFFFFFF);
            _width = _codedWidth;
            _height = _codedHeight;
        }

        // 显示孔径：MFVideoArea（16 字节）= MFOffset OffsetX(4: short fract + short value) ×2 + SIZE(cx4, cy4)
        Guid apertureKey = MFConstants.MF_MT_MINIMUM_DISPLAY_APERTURE;
        var getBlobSize = MfVTable.Get<IMFAttributes_GetBlobSize>(_outputTypePtr, 11);
        if (getBlobSize(_outputTypePtr, ref apertureKey, out uint blobSize) >= 0 && blobSize >= 16)
        {
            IntPtr buf = Marshal.AllocHGlobal((int)blobSize);
            try
            {
                if (MfVTable.Get<IMFAttributes_GetBlob>(_outputTypePtr, 12)(_outputTypePtr, ref apertureKey, buf, blobSize) >= 0)
                {
                    // 原始字节留证：结构布局若理解有误，只有 hex 能对证（宪法：vtable/结构必须照抄头文件，不臆测）
                    var raw = new byte[16];
                    Marshal.Copy(buf, raw, 0, 16);
                    _apertureBlobHex = Convert.ToHexString(raw);

                    // MFOffset = { WORD fract; short value; }（fract 在低地址）；实际偏移 = value + fract/65536
                    int offXValue = Marshal.ReadInt16(buf, 2);
                    ushort offXFract = (ushort)Marshal.ReadInt16(buf, 0);
                    int offYValue = Marshal.ReadInt16(buf, 6);
                    ushort offYFract = (ushort)Marshal.ReadInt16(buf, 4);

                    int cx = Marshal.ReadInt32(buf, 8);
                    int cy = Marshal.ReadInt32(buf, 12);
                    if (cx > 0 && cy > 0 && cx <= _codedWidth && cy <= _codedHeight)
                    {
                        _width = cx;
                        _height = cy;
                        // 偏移必须落在编码帧内，否则视为异常并归零（宁可退回旧行为，也不越界读源 buffer）
                        _apertureOffsetX = offXValue >= 0 && offXValue + cx <= _codedWidth ? offXValue : 0;
                        _apertureOffsetY = offYValue >= 0 && offYValue + cy <= _codedHeight ? offYValue : 0;
                        if (offXFract != 0 || offYFract != 0)
                            _logger.LogWarning(
                                "[APERTURE] 显示孔径偏移含非零小数部分 fractX={FX} fractY={FY}（已按整数部分处理，可能产生半像素误差）",
                                offXFract, offYFract);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    /// <inheritdoc/>
    public unsafe ValueTask<VideoFrame?> FlushAsync()
    {
        // V1：关闭期快速返回 null（EOS 语义），绝不触碰 _transform
        if (!_transformGate.TryEnter())
            return new ValueTask<VideoFrame?>((VideoFrame?)null);
        try
        {
            // 积压优先交付：DRAIN 前必须先把已产出的帧发完，否则流尾整批丢失
            if (_pendingOutputs.Count > 0)
                return new ValueTask<VideoFrame?>(DequeuePendingOutput());

            if (!_initialized || _transform == IntPtr.Zero)
                return new ValueTask<VideoFrame?>((VideoFrame?)null);

            // DRAIN 只发一次（重复发送会被 MFT 忽略或报错）；之后持续排空直到 NEED_MORE_INPUT。
            // 调用方按「反复调用直到返回 null」的约定收口 EOS。
            if (!_drainSent)
            {
                _processMessage!(_transform, MFT_MESSAGE_COMMAND_DRAIN, 0);
                _drainSent = true;
            }
            DrainAvailableOutputs(DrainPacket);
            return new ValueTask<VideoFrame?>(DequeuePendingOutput());
        }
        finally { _transformGate.Exit(); }
    }

    // Flush 用的占位 packet（仅提供回退时间戳，ExtractFrame 优先用输出 sample 自带时间戳）
    private static readonly MediaPacket DrainPacket = new(0, ReadOnlyMemory<byte>.Empty, TimeSpan.Zero, TimeSpan.Zero, false);

    /// <inheritdoc/>
    public void Reset()
    {
        // V1：关闭期直接 no-op，绝不触碰 _transform
        if (!_transformGate.TryEnter()) return;
        try
        {
            // Seek：MFT 内部缓冲作废的同时，已产出的积压帧同属旧内容，必须一并释放，
            // 否则 Seek 后会先吐出一串旧画面（视觉上的「回弹」）。
            DiscardPendingOutputs();
            _drainSent = false;

            if (!_initialized || _transform == IntPtr.Zero)
                return;
            _processMessage!(_transform, MFT_MESSAGE_COMMAND_FLUSH, 0);
        }
        finally { _transformGate.Exit(); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        CloseNativeSync();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // 快路径：关闭已发起/完成时不再分配状态机。重入互斥在 CloseNativeAsync 内由 Interlocked 完成。
        if (Volatile.Read(ref _closed) != 0) return ValueTask.CompletedTask;
        return CloseNativeAsync();
    }

    // ── 两阶段关闭协议（V3/V4）──
    // ⚠️ 重入互斥（审计 A-2）：CloseNativeSync / CloseNativeAsync 共用 _closed 这一 Interlocked 令牌，
    //    先到者执行完整协议，后到者立即返回（不等待）。保证的是**绝不二次 Release**。

    private void CloseNativeSync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _transformGate.BeginClose();
        bool drained = _transformGate.WaitDrain(MediaPipelineTimeouts.NativeDrain);
        // 排空成功即无在途解码，此时释放积压帧安全（帧为托管资源，泄漏路径下同样应释放）
        if (drained) DiscardPendingOutputs();
        if (drained)
            ReleaseComObjects();
        else
            LeakTransform();
    }

    private async ValueTask CloseNativeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _transformGate.BeginClose();
        if (await _transformGate.WaitDrainAsync(MediaPipelineTimeouts.NativeDrain).ConfigureAwait(false))
            ReleaseComObjects();
        else
            LeakTransform();
    }

    private void LeakTransform()
    {
        if (_leakedOnClose) return;
        _leakedOnClose = true;
        _logger.LogError("MFVideoDecoder 关闭超时：仍有在途原生调用。已【有意泄漏】IMFTransform 以避免原生堆损坏（0x80131506 COR_E_EXECUTIONENGINE）。");
    }

    private void ReleaseComObjects()
    {
        if (_transform != IntPtr.Zero)
        {
            Marshal.Release(_transform);
            _transform = IntPtr.Zero;

            // 复审 C-2：与 MFDemuxer.ReleaseNativeResources 置 _readSample=null 对称。
            // 缓存的 vtable 委托持有的是已释放对象的函数指针；置 null 后，万一有漏网调用点，
            // 得到的是可诊断的 NullReferenceException（`_processInput!` 处），而非静默的 use-after-free。
            _getOutputStreamInfo = null;
            _setInputType = null;
            _setOutputType = null;
            _processMessage = null;
            _processInput = null;
            _processOutput = null;
        }
        if (_inputTypePtr != IntPtr.Zero)
        {
            Marshal.Release(_inputTypePtr);
            _inputTypePtr = IntPtr.Zero;
        }
        if (_outputTypePtr != IntPtr.Zero)
        {
            Marshal.Release(_outputTypePtr);
            _outputTypePtr = IntPtr.Zero;
        }
        _width = 0;
        _height = 0;
        _initialized = false;

        // MFShutdown 配对（2026-07-31 审计修复 + 引用计数落地）：
        // MFStartup/MFShutdown 经 MFPlatform 做成进程级引用计数——本解码器每次 Initialize 都 +1，释放时 -1；
        // 平台真正拆除仅发生在计数归 0 时（即 MFBackend 解封装器也释放之后），配对本身安全，不再踩 in-flight 原生调用。
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

    private static void ThrowIfFailed(int hr) => Marshal.ThrowExceptionForHR(hr);

    /// <summary>经 <see cref="MFInterop.MFTEnum"/> 找到可实例化为 <c>IMFTransform</c> 的解码 MFT CLSID。</summary>
    /// <remarks>MFTEnum（旧 API）的 Flags 参数按 MSDN 为 Reserved 必须 0（MFT_ENUM_FLAG_* 系 MFTEnumEx 专用）；
    /// 本机运行时验证（2026-07-29）：H264 → CLSID_MSH264DecoderMFT (62ce7e72-4c71-4d20-b15d-452831a87d9d)。</remarks>
    private static Guid FindDecoderClsid(Guid inputSubtype)
        => EnumDecoderClsid(inputSubtype, 0);

    /// <summary>枚举给定输入 subtype 的解码 MFT，返回首个有效 CLSID（无则 <see cref="Guid.Empty"/>）。</summary>
    private static Guid EnumDecoderClsid(Guid inputSubtype, uint flags)
    {
        MFInterop.MftRegisterTypeInfo input = new()
        {
            guidMajorType = MFConstants.MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER; // 静态只读字段不可作 ref 实参（CS0199）
        int found = MFInterop.MFTEnum(
            ref category, flags, ref input,
            IntPtr.Zero, IntPtr.Zero, out IntPtr pClsidArray, out uint count);
        // HRESULT 语义：S_OK=0 即成功——绝不能写 "<= 0"（会把成功误判为失败，制造"无注册 MFT"假象）
        if (found < 0 || pClsidArray == IntPtr.Zero || count == 0)
            return Guid.Empty;
        try
        {
            // CLSID 数组元素为 16 字节 GUID（Sequential 布局：guidMajorType/guidSubtype 各 16 字节）
            for (uint i = 0; i < count; i++)
            {
                IntPtr p = IntPtr.Add(pClsidArray, (int)(i * 16));
                Guid candidate = Marshal.PtrToStructure<Guid>(p);
                if (candidate != Guid.Empty)
                    return candidate;
            }
            return Guid.Empty;
        }
        finally
        {
            MFInterop.CoTaskMemFree(pClsidArray);
        }
    }
}
