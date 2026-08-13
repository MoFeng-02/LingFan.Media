using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;

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
/// <para><b>vtable 槽位</b>：公式 <c>slotIndex = SDK 绝对槽 − 3</c>；关键槽位已在运行时逐一验证（MFTDiag 全 S_OK）——
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
internal sealed partial class MFVideoDecoder : IVideoDecoder
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
    // MFStartup/MFShutdown 配对标志：Initialize 成功调用 MFStartup 后置 true，
    // ReleaseComObjects 中配对 MFShutdown 并复位。原实现只 Startup 不 Shutdown → 进程级平台
    // 引用计数只增不减，MF 平台常驻进程永不释放（内存/句柄泄漏）。
    private bool _mfStartupAcquired;

    // 两阶段关闭协议构件：关闸 → 排空在途原生调用 → 独占释放或意泄漏。
    private readonly NativeCallGate _transformGate = new();
    private bool _leakedOnClose;   // drain 失败标记：已有意泄漏，禁止任何后续释放尝试

    // 防止 Dispose/DisposeAsync 重入。0=未关闭，1=已发起关闭。
    // 必须是 Interlocked 原子量而非普通 bool：并发的 Dispose 与 DisposeAsync
    // 在普通 bool 上「读-判-写」非原子，可同时通过守卫 ⇒ 对同一 IMFTransform 二次 Marshal.Release
    // ⇒ 引用计数下溢 / 访问违例（原生堆损坏故障族）。
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

    // ── MFT 输出积压队列（防丢包缓冲）──────────────────────────────
    // H.264 解码 MFT 是 **N 进 M 出**：受 B 帧重排/DPB 影响，单次 ProcessInput 后可能
    //    产出 0 帧、也可能产出多帧；且在**输出未被取空前会以 MF_E_NOTACCEPTING 拒收新输入**。
    //    IVideoDecoder 契约是「一次调用最多返回一帧」，故多产出的帧暂存于此队列，由后续调用
    //    依次取走 —— 保证「入包数 == 出帧数」，绝不丢帧。
    //    旧实现只取 1 个输出且把 NOTACCEPTING 当非致命吞掉，等价于按比例静默丢弃压缩包：
    //    30fps 源实测只出 22fps，且参考帧缺失 ⇒ 花屏（宏块拖影）+ PTS 缺口（卡顿/回弹）。
    private readonly Queue<VideoFrame> _pendingOutputs = new();
    private long _notAcceptingDrops;      // 排空后仍被拒收而不得不丢弃的包数（应恒为 0）
    private bool _drainSent;              // MFT_MESSAGE_COMMAND_DRAIN 只发一次
    private TimeSpan _lastFramePts;       // 上一成功帧 PTS（DRAIN/重排帧时间戳缺失时递增外推，禁用 0）
    private TimeSpan _lastFrameDur;       // 上一成功帧 Duration
    private bool _warnedPendingBacklog;   // 积压异常告警只打一次
    private int _pendingBacklogOverCount;  // 连续超过高水位的 DrainAvailableOutputs 次数（区分瞬态突发 vs 持续积压）
    private const int PendingBacklogHighWater = 24;     // 积压高水位（> 即异常；正常仅 DPB 深度 ≤16）
    private const int PendingBacklogSustainedLimit = 4;  // 连续超标次数上限，超过才视为「持续积压」真缺陷
    // (b)② 架构补短：稳态 _pendingOutputs 硬上限（EOS/DRAIN 模式豁免）。
    // 仅限稳态 decode-ahead，防止个别大 GOP/重排瞬间把内部缓冲无界堆高（内存尖峰）；
    // 超限即停止继续从 MFT 取帧，未取走的帧留在 MFT 内部（绝不丢），下一次 DecodeAsync 借
    // NOTACCEPTING→Drain 路径自然取回。EOS 必须排空 DPB 残留以救尾帧，故 DRAIN 路径不受此限。
    private const int PendingOutputHardCeiling = 32;    // 高于正常 DPB 深度(≤16)，低于 EOS 豁免突发的 39 帧
    private const int MaxNotAcceptingRetries = 8;  // 「排空→重投」最大轮数，防活锁
    private const int MaxOutputsPerDrain = 64;     // 单轮最多取帧数，防异常 MFT 无限吐帧

    // ── 显示孔径偏移（MFVideoArea.OffsetX/OffsetY）─────────────────────────────
    // 旧代码只读 Area.cx/cy 而丢弃 Offset，并在注释里臆断「aperture 偏移为 0」。
    //    若 OffsetX != 0，从 (0,0) 起裁会使画面整体平移，左/上边缘吃进编码填充（宏块边缘扩展
    //    = 竖向拉丝），且奇数 OffsetX 在 4:2:0 下令色度错半像素（色噪）。此处改为实测并参与裁剪。
    private int _apertureOffsetX;
    private int _apertureOffsetY;
    /// <summary>MFVideoArea blob 的原始 16 字节 hex，仅用于首帧诊断（防止结构布局理解错误时无从对证）。</summary>
    private string? _apertureBlobHex;

    // ── DXVA 硬件解码零拷贝──────────────────────────────
    // 依赖契约层 IGpuDeviceContext（共享 D3D11 设备），不引用任何渲染器模块，严守依赖倒置。
    // 有头：设备由 D3D11 渲染器注册（同设备 → 零拷贝）；无头：由 MF 自备（MfGpuDeviceContext），均经同一契约。
    private readonly IGpuDeviceContext? _gpuContext;
    // GPU 零拷贝生产者（中立桥，ApiType==激活渲染器）：DXVA 产出的 D3D11 纹理经其导入为渲染器 GPU 纹理上屏。
    // 仅当宿主同时注册 D3D11 设备上下文（DXVA 必需）与匹配 ApiType 的生产者时启用；否则为 null → 走 D3D11 零拷贝。
    private readonly IGpuFrameProducer? _gpuProducer;
    private bool _dxvaActive;        // DXVA 是否成功激活（决定 ExtractFrame 走 GPU 纹理还是软解拷贝）
    private IntPtr _dxvaManager;     // IMFDXGIDeviceManager*（DXVA 必需；Dispose 时 Release）
    private uint _dxvaResetToken;    // ResetDevice 配对 reset token
    private IntPtr _decoderActivate; // 经 MFTEnumEx 得到的 IMFActivate*（ActivateObject 激活后保留其上下文；Dispose 时 Release）
    private long _gpuZeroCopyFrames;  // DXVA 零拷贝 GPU 纹理帧计数（验证计数）
    private long _cpuFallbackFrames;  // 软解 CPU 拷贝帧计数
    private bool _frameSummaryLogged; // 收尾帧路径统计仅打印一次
    private bool _loggedDxvaDiagOnce; // DXVA 纹理提取失败诊断仅打印一次（专用标志，勿复用软解布局标志）

    // ── A 方案：SourceReader 自带硬解「直通包」路径──────────────────
    // MFDemuxer 建 SourceReader 时挂 MF_SOURCE_READER_D3D_MANAGER + ENABLE_HARDWARE_TRANSFORMS，
    // 并把视频流输出协商为 NV12 ⇒ ReadSample 直接吐【已解码】样本。此时 packet 携带：
    //   ① DecodedFrameResource（DXGI 纹理）  → 真·零拷贝，本类只做所有权移交，绝不再过 MFT；
    //   ② Width/Height/Stride + NV12 字节    → 「半 DXVA」回落，本类只做去 stride 紧凑化，仍省掉二次解码。
    // 未命中直通（属性挂载失败/流未协商成功）时 packet 仍是压缩裸流，走原 MFT 路径 —— 行为完全不变。
    private long _passthroughGpuFrames;   // 直通 GPU 纹理帧（零拷贝验证计数）
    private long _passthroughCpuFrames;   // 直通 NV12 CPU 帧（半 DXVA 回落）
    private bool _loggedPassthroughOnce;  // 直通路径首帧诊断仅打印一次
    private bool _loggedPassthroughLayoutWarnOnce; // 直通 NV12 布局异常告警仅打印一次

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

    public MFVideoDecoder(
        ILogger<MFVideoDecoder> logger,
        IGpuDeviceContext? gpuContext = null,
        IEnumerable<IGpuFrameProducer>? gpuFrameProducers = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuContext = gpuContext;
        // 解析匹配当前激活渲染器的零拷贝生产者（依赖倒置：解码器只依赖 IGpuFrameProducer 抽象）。
        // 与 Vulkan 同源守卫，不硬编码任一渲染器——MF DXVA 在 Windows 上为 Vulkan/OpenGL 渲染器均产出 D3D11 共享句柄。
        _gpuProducer = gpuFrameProducers?.FirstOrDefault(p => p.ApiType == _gpuContext?.ApiType);
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
        // 关闭不可逆（gate 一旦 BeginClose 便永久关闸，见 NativeCallGate 关闭不变量）。
        // 已关闭实例若重新 Initialize，后续 DecodeAsync 的 TryEnter 恒失败 ⇒ 静默恒返回 null 帧（哑解码器）。
        // 故直接快速失败；Session 级对象按约定为 Transient，请新建实例。
        if (Volatile.Read(ref _closed) != 0)
            throw new InvalidOperationException("该 MF 视频解码器实例已关闭，不可重新初始化；请新建实例。");

        Codec = codec;
        // 修正：原实现 = settings.EnableHardwareAcceleration 原样回显（假回显 bug）。
        // 现先置 false，待下方 DXVA 接入成功后再置 true；失败/无设备则保持 false（软件解码）。
        IsHardwareAccelerated = false;

        // 输入 subtype（H265 系统 MFT 注册的输入 subtype 为 "HEVC"）
        if (codec == VideoCodec.H264) _inputSubtype = MFConstants.MFVideoFormat_H264;
        else if (codec == VideoCodec.H265) _inputSubtype = MFConstants.MFVideoFormat_HEVC;
        else
            throw new NotSupportedException($"MF 视频解码器不支持 {codec}（仅 H264/H265 经系统 MFT）。");

        // 整个原生建立段必须处于 gate 内，与 MFDemuxer.OpenCore 对称。
        // 上面的 _closed 检查只是 TOCTOU 快速失败，**不构成互斥**。缺闸时的竞态：
        // 本线程通过检查后，并发的 Dispose/DisposeAsync 会因 _inFlight==0 立即判定「已排空」→ 执行
        // ReleaseComObjects（含 MFPlatform.Shutdown 使引用计数 −1），而本线程随后仍在
        // CoCreateInstance / SetInputType / SetOutputType / ProcessMessage 上跑原生 COM。后果三重：
        //   ① 本解码器若是最后一个 MF 消费者，平台被真正 MFShutdown 拆除 ⇒ 原生访问违规 ⇒ 原生堆损坏；
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
            // 置于 gate 内：Startup 与 ReleaseComObjects 里的 Shutdown 必须被同一把闸串行化。
            MFPlatform.Startup();
            _mfStartupAcquired = true;

            // 解码器发现诊断（仅 LINGFAN_MF_DECODER_DIAG=1 时打印全部候选 CLSID，避免生产日志噪声）
            if (DiagEnabled) DumpAllVideoDecoders();

            // 实例化解码 MFT：优先 CoCreateInstance（已知 stock CLSID，如 H264 同步 stock MFT）；
            // 失败则 MFTEnumEx + IMFActivate::ActivateObject（覆盖 Store/异步 MFT——其 IMFActivate 不设
            // CLSID 属性、亦不可 CoCreateInstance，必须 ActivateObject）。注意：Store 安装的编解码器扩展
            // （如 Microsoft HEVC 视频扩展）在未打包桌面进程中 ActivateObject 常返回 E_INVALIDARG/E_ACCESSDENIED，
            // 此时 FindDecoderTransform 会抛出准确异常并建议改用 ffmpeg 后端。
            _transform = FindDecoderTransform(_inputSubtype);
            if (_transform == IntPtr.Zero)
                throw new PlatformNotSupportedException(
                    $"未找到 {codec} 解码 MFT（系统可能未注册对应解码器）。");
            _logger.LogInformation("[DECODER-ENUM] 已获得 {Codec} 解码 MFT transform={Transform:X}（输入 subtype={Subtype:B}）", codec, _transform, _inputSubtype);

            // 缓存 vtable 委托（slotIndex = 绝对槽 − 3；经 Windows SDK mftransform.h 声明顺序推得，
            // 并已于运行时逐槽验证）。必须在任何 _processMessage 等调用之前缓存。
            // IMFTransform 顺序：GetStreamLimits=0 … GetAttributes=5 … SetInputType=12/SetOutputType=13/
            // ProcessMessage=20/ProcessInput=21/ProcessOutput=22
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
                var kh = GCHandle.Alloc(seqKey, GCHandleType.Pinned);
                try
                {
                    Marshal.Copy(cfg.Span.ToArray(), 0, h, cfg.Length);
                    ThrowIfFailed(setBlob(_inputTypePtr, kh.AddrOfPinnedObject(), h, (uint)cfg.Length));
                }
                finally
                {
                    Marshal.FreeHGlobal(h);
                    kh.Free();
                }
            }

            // ── DXVA 硬件解码零拷贝接入────────────────────────
            // 依赖契约层 IGpuDeviceContext（共享 D3D11 设备），不引用渲染器，严守依赖倒置。
            // 时序原则（对照 SDK 实现）：SET_D3D_MANAGER 必须在 SetInputType **之前**发送
            // （mftransform.h 权威值 0x2，MSDN 原文「必须在 SetInputType / SetOutputType 之前调用」）。MFT 在 SetInputType
            // 内部即查询 DXGI 管理器选 DXVA 配置；若此刻未收到，MFT 锁定软件路径 → 输出恒为系统内存
            // （半 DXVA：硬解激活=True 但 GPU零拷贝=0）；该消息曾被放到「类型协商之后」，现订正回「SetInputType 之前」。
            // 失败一律回退软件解码，绝不抛异常阻断播放。
            _dxvaActive = false;
            if (settings.EnableHardwareAcceleration && _gpuContext is not null && _gpuContext.ApiType == GPUApiType.D3D11)
            {
                try
                {
                    // ① 能力探测：MF_SA_D3D11_AWARE。
                    // 必须先探测再发消息：MFT 对**不支持的消息按 IMFTransform 约定返回 S_OK 静默忽略**，
                    //    仅凭 ProcessMessage 的 HRESULT 无法区分「真接受」与「忽略」——这正是「硬解激活=True
                    //    却 GPU零拷贝=0」假绿能长期存在的原因。此处把 MFT 自报能力作为第一道判据。
                    if (!QueryD3D11Aware())
                        throw new NotSupportedException("该解码 MFT 未声明 MF_SA_D3D11_AWARE（不支持 Direct3D 11 视频解码）");

                    // ② 多线程保护：解码 MFT 与渲染器分处不同线程共享同一 ID3D11Device，
                    //    未开保护时 D3D11 运行时不做内部同步 ⇒ 竞态/设备移除（MSDN 硬性要求）。
                    if (!MfDxvaInterop.TryEnableMultithreadProtection(_gpuContext.DeviceHandle))
                        _logger.LogWarning("[DXVA-DIAG] D3D11 设备不支持 ID3D10Multithread，无法开启多线程保护（DXVA 下存在竞态风险）");

                    int dxhr = MfDxvaInterop.MFCreateDXGIDeviceManager(out _dxvaResetToken, out _dxvaManager);
                    Marshal.ThrowExceptionForHR(dxhr);

                    var resetDevice = MfVTable.Get<MfDxvaInterop.IMFDXGIDeviceManager_ResetDevice>(_dxvaManager, 4); // 绝对 7 = ResetDevice（vtable: CloseDeviceHandle=3,GetVideoService=4,LockDevice=5,OpenDeviceHandle=6,ResetDevice=7,TestDevice=8,UnlockDevice=9；MfVTable.Get 读 vtable+(3+slot)，故 slot=4）
                    dxhr = resetDevice(_dxvaManager, _gpuContext.DeviceHandle, _dxvaResetToken);
                    Marshal.ThrowExceptionForHR(dxhr);

                    // ④ 设备能力真值探测（决定性判据）：共享 D3D11 设备能否为当前编码解码到 NV12 分配 DXGI 表面。
                    // 这是「半 DXVA（GPU 硬解但输出读回系统内存）」的唯一权威判据。若设备不支持
                    //    该编码 DXVA 解码到 NV12，MFT 会**静默**把结果拷贝回系统内存 → 输出 buffer 是普通
                    //    IMFMediaBuffer（QI IMFDXGIBuffer=E_NOINTERFACE）→ 零拷贝永不生效。若不查，会陷入
                    //    「硬解激活=True 却 GPU零拷贝=0」的假绿（与消息号假绿同源）。
                    //    探测失败的处置：不阻断初始化，仅打告警，让后续真实输出行为说话。
                    var dxvaProfile = MfDxvaInterop.DxvaProfileForCodec(codec);
                    if (!MfDxvaInterop.TryProbeDxvaSupport(_gpuContext.DeviceHandle, dxvaProfile, out bool dxvaCapable))
                        _logger.LogWarning("[DXVA-DIAG] 共享 D3D11 设备不支持 ID3D11VideoDevice（无视频解码能力）→ 零拷贝不可能，将走软解");
                    else if (!dxvaCapable)
                        _logger.LogWarning("[DXVA-DIAG] CheckVideoDecoderFormat(profile→NV12)=不支持 → 设备无法为 {Codec} 分配 DXGI 解码表面，MFT 将静默回落读回系统内存（半 DXVA），零拷贝不成立", codec);
                    else
                        _logger.LogInformation("[DXVA-DIAG] CheckVideoDecoderFormat(profile→NV12)=支持 → 设备具备 {Codec} DXGI 零拷贝解码能力", codec);

                    // ⑥ 决定性验证：DXGI 管理器是否真正绑定上了设备（解码器经 GetVideoService 取设备）。
                    //    ResetDevice 即便 HRESULT 成功，若 P/Invoke 偏差致绑定未生效，解码器取回空设备 ⇒ 静默读回。
                    string? mgrDiag = MfDxvaInterop.ProbeManagerBoundDevice(_dxvaManager, dxvaProfile);
                    if (mgrDiag != null)
                        _logger.LogInformation("{Diag}", mgrDiag);

                    // ⑦ 枚举设备真实解码 profile + 逐个验证 NV12，排查 profile 不匹配致 CreateVideoDecoder 失败。
                    string? profDiag = MfDxvaInterop.ProbeDecoderProfiles(_gpuContext.DeviceHandle);
                    if (profDiag != null)
                        _logger.LogInformation("{Diag}", profDiag);

                    // ⑤ 适配器身份探针（第二道成因探针）：确认共享设备是否落在 WARP/错误适配器上。
                    // CheckVideoDecoderFormat 仅验格式、不验真实硬件解码引擎；若设备是 WARP，
                    // 格式查询仍可能通过，但解码器在真正解码时无法建立硬件视频解码会话 ⇒ 静默读回系统内存。
                    string? adapterDiag = MfDxvaInterop.ProbeDeviceAdapter(_gpuContext.DeviceHandle);
                    if (adapterDiag != null)
                        _logger.LogInformation("{Diag}", adapterDiag);

                    // ③ MF DXGI 设备管理器已于上方创建并绑定（MFCreateDXGIDeviceManager + ResetDevice）。
                    //    发送 MFT_MESSAGE_SET_D3D_MANAGER(=0x2) 的时机在下方「SetInputType 之前」（订正，
                    //    非「类型协商之后」）。
                    // 消息号必须是 0x2：SDK mftransform.h 中根本不存在 SET_D3D11_MANAGER；D3D9/D3D11
                    //    共用本消息，只靠 ulParam 接口类型区分。旧代码误写 0x80000013 被 MFT 当未知消息
                    //    返回 S_OK 忽略 = 假激活的成因。
                }
                catch (Exception ex)
                {
                    _dxvaActive = false;
                    IsHardwareAccelerated = false;
                    if (_dxvaManager != IntPtr.Zero) { Marshal.Release(_dxvaManager); _dxvaManager = IntPtr.Zero; }
                    _logger.LogWarning(ex, "MF DXVA 硬解初始化失败，回退软件解码");
                }
            }
            else if (settings.EnableHardwareAcceleration)
            {
                _logger.LogWarning("未提供 D3D11 设备上下文（IGpuDeviceContext），MF 无法启用 DXVA，回退软件解码");
            }

            // ── DXVA：SET_D3D_MANAGER 必须在 SetInputType 之前发送（SDK 实物权威）──
            // mftransform.h：MFT_MESSAGE_SET_D3D_MANAGER = 0x2，MSDN 明文「必须在 SetInputType /
            // SetOutputType 之前调用」。MFT 在 SetInputType 内部即查询 DXGI 设备管理器选择 DXVA 配置；
            // 若此刻尚未收到 manager，MFT 锁定软件路径 → 输出 buffer 恒为系统内存（半 DXVA：
            // 硬解激活=True 但 GPU零拷贝=0）；此前被移到类型协商之后，正是当前「半 DXVA」的成因；
            // 现按 SDK 订正回「SetInputType 之前」。消息号 0x2 已修正，本组合（0x2 + SetInputType 前）此前从未被测。
            if (_dxvaManager != IntPtr.Zero)
            {
                try
                {
                    int setHr = _processMessage!(_transform, MFConstants.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)_dxvaManager);
                    Marshal.ThrowExceptionForHR(setHr);
                    _dxvaActive = true;
                    IsHardwareAccelerated = true;
                    _logger.LogInformation("MF DXVA 硬解已激活（SET_D3D_MANAGER 已于 SetInputType 前发送，共享 D3D11 设备）");
                }
                catch (Exception ex)
                {
                    _dxvaActive = false;
                    IsHardwareAccelerated = false;
                    Marshal.Release(_dxvaManager);
                    _dxvaManager = IntPtr.Zero;
                    _logger.LogWarning(ex, "MF DXVA 硬解激活失败（SET_D3D_MANAGER 被拒），回退软件解码");
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

            // DXVA 生效的**第二道也是决定性判据**：类型协商完成后 MFT 是否改报 PROVIDES_SAMPLES。
            // 系统 H264/H265 解码 MFT 在软件路径下由调用方分配输出 sample；一旦 DXVA 真正接管，
            // 它必须自行分配 DXGI 纹理 sample（纹理只能由 MFT 从 D3D11 解码器输出池取），
            // 于是 GetOutputStreamInfo 会置 MFT_OUTPUT_STREAM_PROVIDES_SAMPLES。
            // 若此位为 0 而 _dxvaActive=true，说明 MFT 收下了 manager 却没走 DXVA —— 此时我方会继续用
            // MFCreateMemoryBuffer 造系统内存 sample 交给 MFT 填充，输出永远不可能是 DXGI 表面
            // （每帧 QI(IMFDXGIBuffer) 必得 E_NOINTERFACE）。必须在此显式暴露，绝不让它退化成静默软解。
            if (_dxvaActive)
            {
                _logger.LogInformation(
                    "[DXVA-DIAG] 类型协商后输出流信息：MFT自分配输出sample={Provides}, cbSize={Size} → 零拷贝{Verdict}",
                    _mftProvidesSamples, _outputBufferSize,
                    _mftProvidesSamples ? "前置条件成立" : "★前置条件不成立（MFT 未接管输出分配 = DXVA 未真正启用）★");
                if (!_mftProvidesSamples)
                    _logger.LogWarning(
                        "[DXVA-DIAG] MFT 接受了 SET_D3D_MANAGER 但仍要求调用方分配输出 sample —— DXVA 未真正启用" +
                        "（可能：设备缺 VIDEO_SUPPORT / 该分辨率无硬件解码配置 / 驱动不支持该 profile）。本次将全程软解。");
            }

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
            // 必须**先出闸、再走关闭协议**：CloseNativeSync 内的 WaitDrain 等待在途计数归零，
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
            //（NativeCallGate 的关闭不变量「危险侧」失配，见该类 Exit 备注）。
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

        // decode 全程处于 gate 内，与 Dispose 的释放形成互斥（窗口 A）。关闸时立即返回空帧。
        if (!_transformGate.TryEnter())
            return new ValueTask<VideoFrame?>((VideoFrame?)null);
        try
        {
            // 0. 【A 方案 · 直通分支】SourceReader 已在 demuxer 内完成硬解 ⇒ 本类不得再过一遍 MFT。
            //    必须置于下方 `packet.Data.Length == 0` 守卫【之前】：GPU 纹理直通包的 Data 恰是
            //       ReadOnlyMemory<byte>.Empty（帧全程在显存，没有系统内存副本），落到那条守卫上会被
            //       整帧静默丢弃 ⇒ 画面全黑而日志无任何异常（正是设计原则明令禁止的静默失效）。
            //    未命中直通的压缩包（Width<=0 且无 DecodedFrameResource）原样落到下方 MFT 路径。
            var passthrough = TryBuildPassthroughFrame(packet);
            if (passthrough is not null)
                return new ValueTask<VideoFrame?>(passthrough);

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
                // MFT 契约：MF_E_NOTACCEPTING 意为「**本包未被接收**」——MFT 内部尚有
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

    /// <summary>
    /// 【A 方案】尝试把 <see cref="MFDemuxer"/> 的「已解码直通包」直接包成 <see cref="VideoFrame"/>，跳过本类 MFT。
    /// </summary>
    /// <param name="packet">待判定的包。</param>
    /// <returns>直通帧；非直通包（压缩裸流）返回 <see langword="null"/>，由调用方走原 MFT 路径。</returns>
    /// <remarks>
    /// <para><b>路径①（目标·零拷贝）</b>：<see cref="MediaPacket.HasDecodedFrameResource"/> 为真 ⇒ demuxer 已从
    /// <c>IMFDXGIBuffer</c> 取到 <c>ID3D11Texture2D</c>。此处仅调用 <see cref="MediaPacket.TakeDecodedFrameResource"/>
    /// <b>移交所有权</b>给 <see cref="VideoFrame"/>，全程无一字节内存拷贝。
    /// 必须 Take 而非读 <see cref="MediaPacket.DecodedFrameResource"/>：否则 packet 释放时会销毁仍在渲染管线中的纹理。</para>
    /// <para><b>路径②（半 DXVA 回落）</b>：无 GPU 资源但带 <c>Width/Height</c> + NV12 字节 ⇒ 去 stride 紧凑化后
    /// 包成 <see cref="SoftwareFrameResource"/>。仍比改造前好：省掉 MFT 的二次解码。</para>
    /// <para><b>硬解自报</b>：仅在拿到 <see cref="IGpuTextureResource"/> 时才把 <see cref="IsHardwareAccelerated"/>
    /// 置真——CPU 直通帧无法证明其是否由硬件解出（驱动可能内部读回），遵守「S_OK≠被接受，须行为副作用双判据」。</para>
    /// </remarks>
    private VideoFrame? TryBuildPassthroughFrame(MediaPacket packet)
    {
        // ── 路径①：GPU 纹理零拷贝直通 ───────────────────────────────────────────
        if (packet.HasDecodedFrameResource)
        {
            var resource = packet.TakeDecodedFrameResource();
            if (resource is not null)
            {
                int w = packet.Width > 0 ? packet.Width : _width;
                int h = packet.Height > 0 ? packet.Height : _height;
                if (w <= 0 || h <= 0)
                {
                    // 尺寸不明无法构帧：资源已被 Take 走，必须由本方法负责释放，绝不泄漏纹理。
                    resource.Dispose();
                    if (!_loggedPassthroughLayoutWarnOnce)
                    {
                        _loggedPassthroughLayoutWarnOnce = true;
                        _logger.LogWarning("[MF-PASSTHRU] 直通 GPU 帧尺寸未知（{W}x{H}），已释放纹理并丢弃该帧", w, h);
                    }
                    return null;
                }

                Interlocked.Increment(ref _passthroughGpuFrames);
                if (resource is IGpuTextureResource)
                    IsHardwareAccelerated = true;   // 拿到显存纹理才算硬解生效

                if (!_loggedPassthroughOnce)
                {
                    _loggedPassthroughOnce = true;
                    _logger.LogInformation(
                        "[MF-PASSTHRU] 解码器直通模式：SourceReader 已硬解出 GPU 纹理 {W}x{H} NV12，MFT 完全旁路 —— 全程零拷贝",
                        w, h);
                }
                return new VideoFrame(w, h, PixelFormat.NV12, resource,
                    packet.Timestamp, packet.Duration, packet.KeyFrame);
            }
        }

        // ── 路径②：NV12 CPU 直通（半 DXVA 回落）─────────────────────────────────
        // 判据：带解码尺寸即为直通包（压缩裸流包的 Width/Height 恒为 0，见 MFDemuxer.ExtractPacket）。
        if (packet.Width > 0 && packet.Height > 0 && packet.Data.Length > 0)
            return BuildCpuPassthroughFrame(packet);

        return null;
    }

    /// <summary>把直通包中的 NV12 字节去 stride 紧凑化，包成 <see cref="VideoFrame"/>。</summary>
    /// <remarks>
    /// <para><b>NV12 源布局</b>：Y 平面 <c>stride × codedH</c> 行 + UV 交错平面 <c>stride × (codedH/2)</c> 行。
    /// 目标为紧凑布局（stride == width），与本类软解路径产出的帧格式完全一致，下游渲染器无需分支。</para>
    /// <para><b>codedH 反推</b>：MF 输出 buffer 的行数按【编码高】（宏块 16 对齐）计，可能 &gt; FRAME_SIZE 的高；
    /// 若按 display 高算 UV 平面偏移会整体错位（画面下半段出现色度错行/绿边）。故用
    /// <c>totalRows = len/stride</c>、<c>codedH = totalRows*2/3</c> 反推真实 Y 平面行数。</para>
    /// <para><b>越界即弃</b>：任一平面所需字节超出 buffer 实长，一律打印一次告警并丢帧，
    /// 绝不「尽力拷贝」——错位画面比丢帧更难定位，且违反「不得静默失效」。</para>
    /// </remarks>
    private VideoFrame? BuildCpuPassthroughFrame(MediaPacket packet)
    {
        int width = packet.Width;
        int height = packet.Height;
        int stride = packet.Stride > 0 ? packet.Stride : width;
        var src = packet.Data.Span;

        int totalRows = src.Length / stride;
        int codedHeight = totalRows * 2 / 3;
        if (codedHeight < height) codedHeight = height;   // 反推失真时退回 display 高，由下方越界检查兜底

        int uvRows = height / 2;
        long needY = (long)(height - 1) * stride + width;
        long uvOffset = (long)stride * codedHeight;
        long needUv = uvRows > 0 ? uvOffset + (long)(uvRows - 1) * stride + width : uvOffset;

        if (src.Length < needY || src.Length < needUv)
        {
            if (!_loggedPassthroughLayoutWarnOnce)
            {
                _loggedPassthroughLayoutWarnOnce = true;
                _logger.LogWarning(
                    "[MF-PASSTHRU] NV12 直通布局校验失败：buffer={Len}B 但 display={W}x{H} stride={S} 推导 codedH={CH} " +
                    "需要 Y≥{NeedY}B / UV尾≥{NeedUv}B ⇒ 布局假定破产，丢弃该帧（绝不按错误 stride 拷贝出错位画面）",
                    src.Length, width, height, stride, codedHeight, needY, needUv);
            }
            return null;
        }

        int dstLen = width * height * 3 / 2;
        var resource = new SoftwareFrameResource(width, height, PixelFormat.NV12, dstLen);
        var dst = resource.Data.Span;

        if (stride == width && codedHeight == height)
        {
            // 紧凑源：整块拷贝（最快路径）
            src.Slice(0, dstLen).CopyTo(dst);
        }
        else
        {
            for (int y = 0; y < height; y++)
                src.Slice(y * stride, width).CopyTo(dst.Slice(y * width, width));

            int srcUv = (int)uvOffset;
            int dstUv = width * height;
            for (int y = 0; y < uvRows; y++)
                src.Slice(srcUv + y * stride, width).CopyTo(dst.Slice(dstUv + y * width, width));
        }

        Interlocked.Increment(ref _passthroughCpuFrames);
        if (!_loggedPassthroughOnce)
        {
            _loggedPassthroughOnce = true;
            _logger.LogInformation(
                "[MF-PASSTHRU] 解码器直通模式（CPU）：SourceReader 已解码但样本落在系统内存（半 DXVA）—— " +
                "{W}x{H} NV12 stride={S} codedH={CH}，MFT 旁路但仍有一次内存拷贝",
                width, height, stride, codedHeight);
        }
        return new VideoFrame(width, height, PixelFormat.NV12, resource,
            packet.Timestamp, packet.Duration, packet.KeyFrame);
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
                // (b)② 稳态背压：仅稳态限制内部缓冲上限，超界即停止继续从 MFT 取帧；
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
            {
                ts = TimeSpan.FromTicks(sampleTime);
                _lastFramePts = ts;
            }
            else if (_lastFramePts != TimeSpan.Zero)
            {
                // DRAIN/重排帧时间戳缺失：用上一成功帧 PTS + Duration 递增外推，禁用 0
                // （否则末段帧 PTS=0 → delta 巨负 → 必 Drop，正是尾帧丢失诱因之一）。
                ts = _lastFramePts + _lastFrameDur;
                _lastFramePts = ts;
            }
            if (getDur(sample, out long sampleDur) >= 0 && sampleDur > 0)
            {
                dur = TimeSpan.FromTicks(sampleDur);
                _lastFrameDur = dur;
            }

            // DXVA 零拷贝路径：必须用 GetBufferByIndex 取 sample 原始 buffer，再 QI IMFDXGIBuffer。
            // 绝不能改用 ConvertToContiguousBuffer：其契约就是「把 sample 合并并读回到连续系统内存」，
            //    永远返回 CPU buffer、绝不可能承载 DXGI 纹理——用 ConvertToContiguousBuffer 做零拷贝在原理上注定失败。
            // vtable 槽位（与运行时已验证的 ConvertToContiguousBuffer=38 / AddBuffer=39 一致，IMFSample 第 8 方法）：
            //   GetBufferByIndex = 绝对 40 → slotIndex 37
            IntPtr rawBuffer = IntPtr.Zero;
            if (_dxvaActive)
            {
                var getBuffer = MfVTable.Get<IMFSample_GetBufferByIndex>(sample, 37);
                int hrRaw = getBuffer(sample, 0, out rawBuffer);
            if (hrRaw >= 0 && rawBuffer != IntPtr.Zero)
            {
                var gpu = TryExtractGpuTexture(rawBuffer);
                    if (gpu is not null)
                    {
                        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
                        // rawBuffer 是 IMFMediaBuffer 自身的引用（GetBufferByIndex 已 AddRef），纹理引用由 gpu 持有。
                        InteropTrace.ReleaseComPtr(rawBuffer, "ExtractFrame:dxva-rawBuffer");
                        return new VideoFrame(_width, _height, PixelFormat.NV12, gpu, ts, dur, sourcePacket.KeyFrame);
                    }
                    // 原始 buffer 非 DXGI（或 GetResource 失败）：释放 rawBuffer，回落 ConvertToContiguousBuffer 软解。
                    InteropTrace.ReleaseComPtr(rawBuffer, "ExtractFrame:dxva-rawBuffer-fallback");
                    rawBuffer = IntPtr.Zero;
                }
                else
                {
                    _logger.LogWarning("GetBufferByIndex(0) 失败，HRESULT=0x{HR:X8}，回落 ConvertToContiguousBuffer", hrRaw);
                    rawBuffer = IntPtr.Zero;
                }
            }

            // 软解拷贝路径：输出数据可能分散在多个 buffer，ConvertToContiguousBuffer 合并并读回系统内存。
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

                // COM 配对原则（与 MFDemuxer.ExtractPacket 同构）：
                //    IMFMediaBuffer.Unlock 只能与【成功的】Lock 配对，且恰好一次。
                //    下方「Lock → 拷贝 → Unlock」整体置于【嵌套 try】，所有提前 return
                //    （currentLength==0 / 尺寸未知 / Lock 失败）都发生在进入该嵌套 try 之前，
                //    故绝不会触发 Unlock。旧代码把 Unlock 放在外层 finally，导致未 Lock 即 Unlock
                //    （2D/DXGI 临时拷贝实现的 Unlock 会野指针写）→ 滞后至下次 CLR 堆操作才以原生堆损坏暴露。
                // COM 配对原则（与 MFDemuxer.ExtractPacket 同构）：
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
                        // 修正：起点必须用 MFVideoArea.OffsetX/OffsetY，不得臆断为 0。
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

                System.Threading.Interlocked.Increment(ref _cpuFallbackFrames);
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
    /// 从 DXVA 输出 buffer 提取 GPU 纹理（零拷贝）。
    /// </summary>
    /// <remarks>
    /// <para>优先经当前激活渲染器生产者把 D3D11 纹理导入为 <see cref="IGpuTextureResource"/>（MF DXVA → GPU 零拷贝上屏）；
    /// 导入不可用 / 失败（扩展缺失、纹理非共享、切片不兼容）→ 回落 <see cref="MfD3D11TextureResource"/>（D3D11 零拷贝），
    /// 计入 [DXVA-FRAMEPATH] GPU 纹理帧。绝不报"零拷贝已生效"假绿（S_OK≠被接受）。</para>
    /// <para>依赖倒置：仅经 <see cref="IGpuFrameProducer"/> 抽象，不引用渲染器模块；
    /// COM 引用计数严守 GetResource 配对（成功导入时释放 tex 的 GetResource 引用，共享句柄由渲染器侧消费）。</para>
    /// </remarks>
    private IGpuTextureResource? TryExtractGpuTexture(IntPtr buffer)
    {
        Guid iidDxgi = MFConstants.IID_IMFDXGIBuffer;
        int hr = Marshal.QueryInterface(buffer, in iidDxgi, out IntPtr dxgi);
        if (hr < 0 || dxgi == IntPtr.Zero)
        {
            // 每帧都打 warning 会刷屏，故只深度诊断一次。
            if (!_loggedDxvaDiagOnce)
            {
                _loggedDxvaDiagOnce = true;
                DiagnoseNonDxgiBuffer(buffer, hr);
            }
            return null;
        }
        try
        {
            var getResource = MfVTable.Get<MfDxvaInterop.IMFDXGIBuffer_GetResource>(dxgi, 0);   // 绝对 3
            var getSub = MfVTable.Get<MfDxvaInterop.IMFDXGIBuffer_GetSubresourceIndex>(dxgi, 1); // 绝对 4

            Guid iidTex = MFConstants.IID_ID3D11Texture2D;
            hr = getResource(dxgi, ref iidTex, out IntPtr tex);
            if (hr < 0 || tex == IntPtr.Zero)
            {
                _logger.LogWarning("[DXVA-DIAG] IMFDXGIBuffer.GetResource(ID3D11Texture2D) 失败：HRESULT=0x{HR:X8}", hr);
                return null;
            }
            hr = getSub(dxgi, out uint sub);
            if (hr < 0)
            {
                Marshal.Release(tex); // COM 配对：GetResource 成功已 AddRef 纹理，getSub 失败须释放，否则纹理引用泄漏
                _logger.LogWarning("[DXVA-DIAG] IMFDXGIBuffer.GetSubresourceIndex 失败：HRESULT=0x{HR:X8}", hr);
                return null;
            }

            // GPU 零拷贝：D3D11 纹理 → DXGI 共享 NT 句柄 → 渲染器生产者导入为 GPU 纹理。
            if (_gpuProducer is not null)
            {
                try
                {
                    var d3dTex = new ID3D11Texture2D(tex);
                    using var dxgiRes = d3dTex.QueryInterface<IDXGIResource1>();
                    nint sharedHandle = dxgiRes.CreateSharedHandle(
                        null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
                    int arrayLayers = (int)d3dTex.Description.ArraySize;
                    var source = new GpuFrameImportSource
                    {
                        Kind = GpuFrameImportKind.D3D11SharedHandle,
                        Handle = sharedHandle,
                        Width = _width,
                        Height = _height,
                        Format = PixelFormat.NV12,
                        SubresourceIndex = (int)sub,
                        ArrayLayers = arrayLayers,
                    };
                    if (_gpuProducer.TryImport(source, out var gpuTex) && gpuTex is not null)
                    {
                        // 导入成功：GPU 纹理已绑定外部内存，原 tex 的 GetResource 引用可回收；
                        // 共享句柄由渲染器侧消费，调用方不得关闭（生产者契约 S_OK≠被接受）。
                        Marshal.Release(tex);
                        return gpuTex;
                    }
                    // 导入未接受（S_OK≠被接受）：关闭共享句柄，回落 D3D11 资源。
                    CloseHandle(sharedHandle);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DXVA-FRAMEPATH] GPU 零拷贝导入异常，回落 D3D11 资源。");
                }
            }

            return new MfD3D11TextureResource(tex, _width, _height, PixelFormat.NV12, (int)sub);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DXVA-DIAG] DXVA 纹理提取异常，回落软解拷贝");
            return null;
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>关闭 DXGI 共享 NT 句柄（GPU 零拷贝导入失败回落时由调用方负责关闭）。</summary>
    /// <remarks>原始 P/Invoke（[LibraryImport]，AOT 安全；本类为 Windows-only）。</remarks>
    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    /// <summary>
    /// DXVA 已激活（PROVIDES_SAMPLES=True）但输出 buffer 不支持 <c>IMFDXGIBuffer</c> 时的一次性深度诊断。
    /// 说明 MFT 把 GPU 纹理读回到了系统内存（"半 DXVA"）：解码走硬解，但零拷贝提取失败。
    /// 本方法摊开 buffer 真实身份（是否仍是 IMFMediaBuffer / IMF2DBuffer / ID3D11Texture2D）与
    /// 输出媒体类型属性（subtype / NominalRange / DefaultStride），用于定位"读回"的成因。
    /// 全部诊断改用【已核验正确的 IID】+【Lock 实测】两类可信手段：
    ///    - IID_IMFMediaBuffer 此前字节级错误（0x...3508→0x3507）曾令所有 QI 假阴性，现已订正；
    ///    - Lock 复用真实 CPU 路径的 vtable 槽位 0/1/2，绝不依赖 QI，故即使 QI 仍异常也能给出真值。
    /// </summary>
    private void DiagnoseNonDxgiBuffer(IntPtr buffer, int dxgiHr)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[DXVA-DIAG] 输出 buffer 非 IMFDXGIBuffer(QI=0x").Append(dxgiHr.ToString("X8")).Append(") → 深度诊断：");

            // ① buffer 是否仍是有效 IMFMediaBuffer（用【已订正】的 IID，真值）
            int hrMb = Marshal.QueryInterface(buffer, in MFConstants.IID_IMFMediaBuffer, out IntPtr mb);
            bool isMb = hrMb >= 0;
            sb.Append(" IMFMediaBuffer=").Append(isMb ? "是" : "否");
            if (isMb)
            {
                // ② 真值校验：直接 Lock（复用 CPU 路径 vtable 槽位 0/1/2）取长度，确认是系统内存 NV12。
                //    若 Lock 成功且长度≈_outputBufferSize(cbSize)，则确认「解码器产出系统内存读回 buffer」。
                try
                {
                    var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(mb, 0);
                    var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(mb, 1);
                    var getLen = MfVTable.Get<IMFMediaBuffer_GetCurrentLength>(mb, 2);
                    if (getLen(mb, out uint len) >= 0) sb.Append(" len=").Append(len);
                    if (lockDel(mb, out IntPtr _, out uint _, out uint _) >= 0)
                    {
                        sb.Append(" Lock=OK");
                        unlockDel(mb);
                    }
                    else sb.Append(" Lock=FAIL");
                }
                catch (Exception lex) { sb.Append(" Lock=异常(").Append(lex.GetType().Name).Append(")"); }
                Marshal.Release(mb);
            }

            // ③ 是否 DXVA2( D3D9 )表面：微软 H264 解码器在部分配置下内部走 DXVA2 而非 DXGI，
            //    此时 buffer 实现 IMF2DBuffer，零拷贝须走 D3D9 路径而非 IMFDXGIBuffer。
            int hr2d = Marshal.QueryInterface(buffer, in MFConstants.IID_IMF2DBuffer, out IntPtr b2d);
            sb.Append(" | IMF2DBuffer=").Append(hr2d >= 0 ? "是" : "否");
            if (hr2d >= 0) Marshal.Release(b2d);

            // ④ 是否直接包了 ID3D11Texture2D（极少数 MFT 不经 IMFDXGIBuffer 直接包纹理）
            int hrTex = Marshal.QueryInterface(buffer, in MFConstants.IID_ID3D11Texture2D, out IntPtr tex);
            sb.Append(" | ID3D11Texture2D=").Append(hrTex >= 0 ? "是" : "否");
            if (hrTex >= 0) Marshal.Release(tex);

            // ⑤ 输出媒体类型属性：subtype / NominalRange / DefaultStride
            if (_outputTypePtr != IntPtr.Zero)
            {
                var getGuid = MfVTable.Get<IMFMediaType_GetGuid>(_outputTypePtr, 7);
                Guid subKey = MFConstants.MF_MT_SUBTYPE;
                if (getGuid(_outputTypePtr, ref subKey, out Guid sub) >= 0)
                    sb.Append(" | outSubtype=").Append(sub.ToString("B").Substring(0, 8));
                var getU32 = MfVTable.Get<IMFAttributes_GetUINT32>(_outputTypePtr, 4);
                Guid nrKey = MFConstants.MF_MT_VIDEO_NOMINAL_RANGE;
                int hrNr = getU32(_outputTypePtr, ref nrKey, out uint nr);
                sb.Append(" | NominalRange=").Append(hrNr >= 0 ? nr.ToString() : "缺失");
                Guid strideKey = MFConstants.MF_MT_DEFAULT_STRIDE;
                int hrSt = getU32(_outputTypePtr, ref strideKey, out uint st);
                sb.Append(" | DefaultStride=").Append(hrSt >= 0 ? ((int)st).ToString() : "缺失");
            }

            _logger.LogWarning(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DXVA-DIAG] 深度诊断自身异常（忽略）");
        }
    }

    /// <summary>
    /// STREAM_CHANGE 后重新协商输出媒体类型，更新尺寸与输出 buffer 大小。
    /// MS 推荐流程：从 <c>GetOutputAvailableType</c> 枚举<b>新的</b>可用类型（优先 NV12）→ <c>SetOutputType</c>；
    /// 不能把 <c>GetOutputCurrentType</c> 的旧类型原样设回（运行时验证会返回 MF_E_INVALIDMEDIATYPE）。
    /// </summary>
    private bool RenegotiateOutput()
    {
        // RenegotiateOutput 触碰 _transform/_outputTypePtr，须处于 gate 内。
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

    /// <summary>
    /// 探测该解码 MFT 是否声明 <c>MF_SA_D3D11_AWARE</c>（支持 Direct3D 11 视频解码）。
    /// </summary>
    /// <remarks>
    /// 为何必须探测：<c>IMFTransform::ProcessMessage</c> 的契约允许 MFT 对**不认识的消息直接返回 S_OK
    /// 静默忽略**，因此「SET_D3D_MANAGER 返回 S_OK」**不能**证明 DXVA 已启用。MFT 自报的
    /// MF_SA_D3D11_AWARE 属性才是能力层面的权威判据。属性缺失（MF_E_ATTRIBUTENOTFOUND）即视为不支持。
    /// </remarks>
    private bool QueryD3D11Aware()
    {
        var getAttrs = MfVTable.Get<IMFTransform_GetAttributes>(_transform, 5); // 绝对 8
        int hr = getAttrs(_transform, out IntPtr attrs);
        if (hr < 0 || attrs == IntPtr.Zero)
        {
            _logger.LogWarning("[DXVA-DIAG] IMFTransform.GetAttributes 失败：HRESULT=0x{HR:X8}，无法确认 D3D11 能力", hr);
            return false;
        }
        try
        {
            Guid key = MFConstants.MF_SA_D3D11_AWARE;
            hr = MfVTable.Get<IMFAttributes_GetUINT32>(attrs, 4)(attrs, ref key, out uint aware);
            if (hr < 0)
            {
                _logger.LogWarning("[DXVA-DIAG] MFT 未设置 MF_SA_D3D11_AWARE（HRESULT=0x{HR:X8}）→ 不支持 D3D11 硬解", hr);
                return false;
            }
            _logger.LogInformation("[DXVA-DIAG] MF_SA_D3D11_AWARE={Aware}", aware);
            return aware != 0;
        }
        finally
        {
            Marshal.Release(attrs);
        }
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
        // AOT 安全：用 GetAllocatedBlob（slot13，原生自分配 buffer），绕开 GetBlob 向调用方 buffer 大块写入在 AOT 崩溃的路径。
        Guid apertureKey = MFConstants.MF_MT_MINIMUM_DISPLAY_APERTURE;
        var kh = GCHandle.Alloc(apertureKey, GCHandleType.Pinned);
        IntPtr aperturePtr = kh.AddrOfPinnedObject();
        IntPtr blobPtr = IntPtr.Zero;
        try
        {
            var getAllocatedBlob = MfVTable.Get<IMFAttributes_GetAllocatedBlob>(_outputTypePtr, 13);
            if (getAllocatedBlob(_outputTypePtr, aperturePtr, out blobPtr, out uint blobSize) >= 0 && blobSize >= 16 && blobPtr != IntPtr.Zero)
            {
                // 原始字节留证：结构布局若理解有误，只有 hex 能对证（vtable/结构必须照抄头文件，不凭空猜测）
                var raw = new byte[16];
                Marshal.Copy(blobPtr, raw, 0, 16);
                _apertureBlobHex = Convert.ToHexString(raw);

                // MFOffset = { WORD fract; short value; }（fract 在低地址）；实际偏移 = value + fract/65536
                int offXValue = Marshal.ReadInt16(blobPtr, 2);
                ushort offXFract = (ushort)Marshal.ReadInt16(blobPtr, 0);
                int offYValue = Marshal.ReadInt16(blobPtr, 6);
                ushort offYFract = (ushort)Marshal.ReadInt16(blobPtr, 4);

                int cx = Marshal.ReadInt32(blobPtr, 8);
                int cy = Marshal.ReadInt32(blobPtr, 12);
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
            // GetAllocatedBlob 用 CoTaskMem 分配，须 FreeCoTaskMem（非 FreeHGlobal）。
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
            kh.Free();
        }
    }

    /// <inheritdoc/>
    public unsafe ValueTask<VideoFrame?> FlushAsync()
    {
        // 关闭期快速返回 null（EOS 语义），绝不触碰 _transform
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
        // 关闭期直接 no-op，绝不触碰 _transform
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

    // ── 两阶段关闭协议──
    // 重入互斥：CloseNativeSync / CloseNativeAsync 共用 _closed 这一 Interlocked 令牌，
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
        _logger.LogError("MFVideoDecoder 关闭超时：仍有在途原生调用。已【有意保留】IMFTransform，避免因释放导致原生堆损坏（COR_E_EXECUTIONENGINE）。");
    }

    private void ReleaseComObjects()
    {
        // 收尾帧路径统计（DXVA 零拷贝验证计数）：先于字段复位打印一次，确认零拷贝是否真生效。
        if (!_frameSummaryLogged)
        {
            _frameSummaryLogged = true;
            long totalZeroCopy = _gpuZeroCopyFrames + _passthroughGpuFrames;
            long totalCpu = _cpuFallbackFrames + _passthroughCpuFrames;
            if (totalZeroCopy > 0 || totalCpu > 0)
                _logger.LogInformation(
                    "[DXVA-FRAMEPATH] 解码帧路径统计：GPU零拷贝={Gpu} 帧（MFT自解={MftGpu} / SourceReader直通={PtGpu}） / " +
                    "CPU拷贝={Cpu} 帧（MFT软解={MftCpu} / 直通半DXVA={PtCpu}） | 硬解激活={Hw} | 零拷贝生效={Verdict}",
                    totalZeroCopy, _gpuZeroCopyFrames, _passthroughGpuFrames,
                    totalCpu, _cpuFallbackFrames, _passthroughCpuFrames,
                    IsHardwareAccelerated,
                    totalZeroCopy > 0 ? "是" : "否(全程回落CPU拷贝)");
        }

        if (_transform != IntPtr.Zero)
        {
            Marshal.Release(_transform);
            _transform = IntPtr.Zero;

            // 与 MFDemuxer.ReleaseNativeResources 置 _readSample=null 对称。
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
        // DXVA 管理器释放（与 _transform 同生命周期）：MFCreateDXGIDeviceManager 创建的 COM 对象。
        // 配对的 D3D11 设备由 IGpuDeviceContext 持有（渲染器或无头 MfGpuDeviceContext），此处仅释放管理器自身。
        if (_dxvaManager != IntPtr.Zero)
        {
            Marshal.Release(_dxvaManager);
            _dxvaManager = IntPtr.Zero;
        }
        // Store MFT 激活上下文（ActivateObject 时保留其 IMFActivate*；CloseNativeSync 不触碰，此处统一释放）
        if (_decoderActivate != IntPtr.Zero)
        {
            Marshal.Release(_decoderActivate);
            _decoderActivate = IntPtr.Zero;
        }
        _dxvaResetToken = 0;
        _dxvaActive = false;
        _width = 0;
        _height = 0;
        _initialized = false;

        // MFShutdown 配对（引用计数落地）：
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

    /// <summary>临时诊断：枚举全部已注册视频解码器（不过滤 subtype），打印每个 CLSID，确认 HEVC Store 解码器是否可枚举。</summary>
    private void DumpAllVideoDecoders()
    {
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER;
        int hr = MFInterop.MFTEnumExRaw(
            ref category,
            MFConstants.MFT_ENUM_FLAG_ALL | MFConstants.MFT_ENUM_FLAG_UNTRUSTED_STOREMFT,
            IntPtr.Zero, IntPtr.Zero,
            out IntPtr arr, out uint count);
        _logger.LogWarning("[DECODER-DIAG] DumpAllVideoDecoders MFTEnumEx(ALL|STORE, 不过滤) hr=0x{Hr:X8} count={Count}", hr & 0xFFFFFFFF, count);
        if (hr >= 0 && arr != IntPtr.Zero && count > 0)
        {
            Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
            for (uint i = 0; i < count; i++)
            {
                IntPtr act = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
                if (act == IntPtr.Zero) continue;
                try
                {
                    var getGuid = MfVTable.Get<IMFAttributes_GetGUID>(act, 7);
                    int hr2 = getGuid(act, ref clsidKey, out Guid g);
                    _logger.LogWarning("[DECODER-DIAG]   [{I}] hr2=0x{Hr2:X8} CLSID={G:B}", i, hr2 & 0xFFFFFFFF, g);
                }
                finally { Marshal.Release(act); }
            }
            MFInterop.CoTaskMemFree(arr);
        }
    }

    /// <summary>经 <see cref="MFInterop.MFTEnumEx"/> 找到可实例化为 <c>IMFTransform</c> 的解码 MFT CLSID。</summary>
    /// <remarks>
    /// HEVC 视频扩展（Store 安装）注册为<strong>异步 Store MFT</strong>，旧 <c>MFTEnum</c> 枚举不到，
    /// 必须用 <c>MFTEnumEx</c> 并包含 <c>ASYNCMFT | HARDWARE | UNTRUSTED_STOREMFT</c>。
    /// 运行时验证：H264 → CLSID_MSH264DecoderMFT (62ce7e72-4c71-4d20-b15d-452831a87d9d)。
    /// </remarks>
    /// <summary>实例化解码 MFT，返回已激活的 <c>IMFTransform*</c>（零表示未找到）。
    /// 策略：① 已知 stock CLSID + <c>CoCreateInstance</c>（H264 等同步 stock MFT）；
    /// ② 失败则 <c>MFTEnumEx</c> + <c>IMFActivate::ActivateObject</c>（覆盖 Store/异步 HEVC MFT——
    /// 其 <c>IMFActivate</c> 不设 <c>MFT_TRANSFORM_CLSID_Attribute</c>、亦不可 CoCreateInstance，必须 ActivateObject）。</summary>
    private IntPtr FindDecoderTransform(Guid inputSubtype)
    {
        // 1) 已知 stock CLSID + CoCreateInstance（H264 stock MFT 等同步解码器）
        IntPtr t = CoCreateStockDecoder(inputSubtype);
        if (t != IntPtr.Zero) return t;

        // 2) MFTEnumEx + ActivateObject（Store/异步 HEVC MFT 等不设 CLSID 属性、不可 CoCreateInstance）。
        //    返回 lastActivateHr/lastCount：枚举到候选但激活失败（典型 Store HEVC MFT 拒绝未打包桌面进程）。
        int lastActivateHr = 0;
        uint lastCount = 0;
        t = ActivateViaMFTEnumEx(inputSubtype,
            MFConstants.MFT_ENUM_FLAG_ALL | MFConstants.MFT_ENUM_FLAG_UNTRUSTED_STOREMFT,
            out lastActivateHr, out lastCount);
        if (t != IntPtr.Zero) return t;
        t = ActivateViaMFTEnumEx(inputSubtype, MFConstants.MFT_ENUM_FLAG_ALL, out lastActivateHr, out lastCount);
        if (t != IntPtr.Zero) return t;

        // 枚举到候选但无法在当前进程激活（Store 编解码器扩展需打包应用身份）
        if (lastCount > 0)
        {
            throw new PlatformNotSupportedException(
                $"找到 {_codecName(inputSubtype)} 解码 MFT（共 {lastCount} 个候选）但无法在当前进程激活" +
                $"（ActivateObject hr=0x{lastActivateHr & 0xFFFFFFFF:X8}）。Store 安装的编解码器扩展" +
                "（如 Microsoft HEVC 视频扩展）通常只供打包应用（系统播放器/照片）使用，未打包桌面进程直接激活会被拒绝。" +
                "建议：HEVC 解码改走 ffmpeg 后端；或待本库异步 MFT 驱动 + 打包支持落地后再试。");
        }
        return IntPtr.Zero;
    }

    /// <summary>经 <c>MFTEnumEx</c> 找到匹配 subtype 的 <c>IMFActivate</c>，调用 <c>ActivateObject</c> 得到 <c>IMFTransform*</c>。
    /// 这是 MFTEnumEx 结果的标准激活方式——Store/异步 MFT 不设 CLSID 属性、不可 CoCreateInstance。</summary>
    private IntPtr ActivateViaMFTEnumEx(Guid inputSubtype, uint flags, out int lastActivateHr, out uint lastCount)
    {
        lastActivateHr = 0;
        lastCount = 0;
        MFInterop.MftRegisterTypeInfo input = new()
        {
            guidMajorType = MFConstants.MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER;
        int hr = MFInterop.MFTEnumEx(
            ref category, flags, ref input,
            IntPtr.Zero, out IntPtr arr, out lastCount);
        if (DiagEnabled)
            Console.Error.WriteLine($"[DECODER-DIAG] ActivateViaMFTEnumEx flags=0x{flags:X} hr=0x{hr & 0xFFFFFFFF:X8} count={lastCount}");
        if (hr < 0 || arr == IntPtr.Zero || lastCount == 0)
            return IntPtr.Zero;

        IntPtr transform = IntPtr.Zero;
        try
        {
            for (uint i = 0; i < lastCount && transform == IntPtr.Zero; i++)
            {
                IntPtr activate = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
                if (activate == IntPtr.Zero) continue;
                try
                {
                    var activateObject = MfVTable.Get<IMFActivate_ActivateObject>(activate, 28); // 绝对槽 31（IUnknown3+IMFAttributes28+ActivateObject）
                    Guid iid = MFConstants.IID_IMFTransform;
                    int hr2 = activateObject(activate, ref iid, out IntPtr pTransform);
                    if (DiagEnabled)
                        Console.Error.WriteLine($"[DECODER-DIAG]   [{i}] ActivateObject hr=0x{hr2 & 0xFFFFFFFF:X8} pTransform={(hr2 >= 0 ? pTransform.ToString("X") : "-")}");
                    if (hr2 < 0 && lastActivateHr == 0)
                        lastActivateHr = hr2;   // 记录首个激活失败 hr（供异常消息提示）
                    if (hr2 >= 0 && pTransform != IntPtr.Zero)
                    {
                        transform = pTransform;
                        _decoderActivate = activate;   // 保留 activate 上下文（Store 激活依赖它），Dispose 时释放
                        activate = IntPtr.Zero;        // 所有权已转移，本循环不再 Release
                        if (DiagEnabled) LogTransformDiag(transform, i);
                    }
                }
                finally
                {
                    if (activate != IntPtr.Zero) Marshal.Release(activate);
                }
            }
        }
        finally
        {
            MFInterop.CoTaskMemFree(arr);
        }
        return transform;
    }

    /// <summary>已知 stock CLSID + <c>CoCreateInstance</c>（H264 等同步 stock MFT）。返回 <c>IMFTransform*</c> 或零。</summary>
    private IntPtr CoCreateStockDecoder(Guid inputSubtype)
    {
        Guid clsid = EnumDecoderClsidEx(inputSubtype,
            MFConstants.MFT_ENUM_FLAG_ALL | MFConstants.MFT_ENUM_FLAG_UNTRUSTED_STOREMFT);
        if (clsid == Guid.Empty)
            clsid = EnumDecoderClsidEx(inputSubtype, MFConstants.MFT_ENUM_FLAG_ALL);
        // 兜底旧 MFTEnum（旧 API 对 H264 stock MFT 仍有效）
        if (clsid == Guid.Empty)
            clsid = EnumDecoderClsidLegacy(inputSubtype);
        if (clsid == Guid.Empty)
            return IntPtr.Zero;
        Guid iid = MFConstants.IID_IMFTransform;
        int hr = MFInterop.CoCreateInstance(ref clsid, IntPtr.Zero, MFInterop.CLSCTX_ALL, ref iid, out IntPtr t);
        if (hr < 0)
        {
            Console.Error.WriteLine($"[DECODER-DIAG] CoCreateInstance({clsid:B}) hr=0x{hr & 0xFFFFFFFF:X8}");
            return IntPtr.Zero;
        }
        _logger.LogInformation("[DECODER-ENUM] 选中 {Codec} 解码 MFT CLSID={Clsid:B}（CoCreateInstance）", _codecName(inputSubtype), clsid);
        return t;
    }

    /// <summary>把输入 subtype 映射为可读编解码器名（仅日志用）。</summary>
    private static string _codecName(Guid subtype)
    {
        if (subtype == MFConstants.MFVideoFormat_H264) return "H264";
        if (subtype == MFConstants.MFVideoFormat_HEVC || subtype == MFConstants.MFVideoFormat_HEVC_ES) return "HEVC";
        return subtype.ToString("B");
    }

    /// <summary>解码器发现诊断总开关：设环境变量 <c>LINGFAN_MF_DECODER_DIAG=1</c> 时打印全部候选 MFT CLSID /
    /// ActivateObject hr / MF_SA_D3D11_AWARE 等细节（排查 Store HEVC MFT 枚举/激活问题时用），常态关闭避免生产日志噪声。</summary>
    private static bool DiagEnabled => Environment.GetEnvironmentVariable("LINGFAN_MF_DECODER_DIAG") == "1";

    /// <summary>临时诊断：打印 transform 关键属性（异步标志、MF_SA_D3D11_AWARE、CLSID），确认 HEVC 解码器能力。</summary>
    private void LogTransformDiag(IntPtr transform, uint index)
    {
        var getAttrs = MfVTable.Get<IMFTransform_GetAttributes>(transform, 5);
        int hr = getAttrs(transform, out IntPtr attrs);
        if (hr < 0 || attrs == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] GetAttributes hr=0x{hr & 0xFFFFFFFF:X8}");
            return;
        }
        try
        {
            var getUint = MfVTable.Get<IMFAttributes_GetUINT32>(attrs, 4);
            Guid flagsKey = MFConstants.MF_TRANSFORM_FLAGS_Attribute;
            int hr2 = getUint(attrs, ref flagsKey, out uint flagsVal);
            bool isAsync = (hr2 >= 0) && ((flagsVal & 0x1) != 0);
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] FLAGS hr=0x{hr2 & 0xFFFFFFFF:X8} val=0x{flagsVal:X8} async={isAsync}");
            Guid awareKey = MFConstants.MF_SA_D3D11_AWARE;
            int hr3 = getUint(attrs, ref awareKey, out uint aware);
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] MF_SA_D3D11_AWARE hr=0x{hr3 & 0xFFFFFFFF:X8} val={aware}");
            var getGuid = MfVTable.Get<IMFAttributes_GetGUID>(attrs, 7);
            Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
            int hr4 = getGuid(attrs, ref clsidKey, out Guid g);
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] CLSID(from transform) hr=0x{hr4 & 0xFFFFFFFF:X8} {g:B}");
        }
        finally
        {
            Marshal.Release(attrs);
        }
    }

    /// <summary>使用 <c>MFTEnumEx</c> 枚举给定输入 subtype 的解码 MFT，返回首个有效 CLSID（无则 <see cref="Guid.Empty"/>）。</summary>
    private static Guid EnumDecoderClsidEx(Guid inputSubtype, uint flags)
    {
        MFInterop.MftRegisterTypeInfo input = new()
        {
            guidMajorType = MFConstants.MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER; // 静态只读字段不可作 ref 实参（CS0199）
        int hr = MFInterop.MFTEnumEx(
            ref category, flags, ref input,
            IntPtr.Zero, out IntPtr pActivateArray, out uint count);
        Console.Error.WriteLine($"[DECODER-DIAG] EnumDecoderClsidEx flags=0x{flags:X} hr=0x{hr & 0xFFFFFFFF:X8} count={count}");
        if (hr < 0 || pActivateArray == IntPtr.Zero || count == 0)
            return Guid.Empty;

        Guid result = Guid.Empty;
        Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr activate = Marshal.ReadIntPtr(pActivateArray, (int)(i * IntPtr.Size));
                if (activate == IntPtr.Zero)
                    continue;
                try
                {
                    if (result == Guid.Empty)
                    {
                        var getGuid = MfVTable.Get<IMFAttributes_GetGUID>(activate, 7); // IMFAttributes::GetGUID
                        int hr2 = getGuid(activate, ref clsidKey, out Guid candidate);
                        if (hr2 >= 0 && candidate != Guid.Empty)
                            result = candidate;
                    }
                }
                finally
                {
                    Marshal.Release(activate);
                }
            }
        }
        finally
        {
            MFInterop.CoTaskMemFree(pActivateArray);
        }
        return result;
    }

    /// <summary>使用旧 <c>MFTEnum</c> 兜底枚举给定输入 subtype 的解码 MFT（仅同步 MFT）。</summary>
    private static Guid EnumDecoderClsidLegacy(Guid inputSubtype)
    {
        MFInterop.MftRegisterTypeInfo input = new()
        {
            guidMajorType = MFConstants.MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER;
        int found = MFInterop.MFTEnum(
            ref category, 0, ref input,
            IntPtr.Zero, IntPtr.Zero, out IntPtr pClsidArray, out uint count);
        // HRESULT 语义：S_OK=0 即成功——绝不能写 "<= 0"（会让成功被当成失败，制造"无注册 MFT"假象）
        if (found < 0 || pClsidArray == IntPtr.Zero || count == 0)
            return Guid.Empty;
        try
        {
            // CLSID 数组元素为 16 字节 GUID
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
