using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.GPUShare.D3D11;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// <see cref="IVideoDecoder"/> 的 MediaFoundation 实现（基于 <c>IMFTransform</c> 真实 MFT 解码）。
/// </summary>
/// <remarks>
/// <para><b>实现说明</b>：经 <c>CoCreateInstance</c> 实例化
/// H264/H265 解码 MFT（CLSID_CMSH264DecoderMFT / CLSID_CMSH265DecoderMFT），通过 <c>IMFTransform</c> vtable
/// 调用 <c>SetInputType</c>/<c>SetOutputType</c>/<c>ProcessInput</c>/<c>ProcessOutput</c> 完成真实解码。</para>
/// <para><b>异步策略</b>（与 FFmpegVideoDecoder 对称，内部纯内存的方法返回同步完成值，非伪异步）：</para>
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
/// <para><b>vtable 槽位</b>：公式 <c>slotIndex = SDK 绝对槽 − 3</c>；关键槽位以 SDK 头文件为准——
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
    // ReleaseComObjects 中配对 MFShutdown 并复位。防回归：只 Startup 不 Shutdown 会使进程级平台
    // 引用计数只增不减，MF 平台常驻进程永不释放（内存/句柄泄漏）。
    private bool _mfStartupAcquired;

    // 两阶段关闭协议构件：关闸 → 排空在途原生调用 → 独占释放或有意泄漏。
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

    private long _notAcceptingDrops;      // 排空后仍被拒收而不得不丢弃的包数（应恒为 0）
    private bool _drainSent;              // MFT_MESSAGE_COMMAND_DRAIN 只发一次
    private TimeSpan _lastFramePts;       // 上一成功帧 PTS（DRAIN/重排帧时间戳缺失时递增外推，禁用 0）
    private TimeSpan _lastFrameDur;       // 上一成功帧 Duration
    private const int MaxNotAcceptingRetries = 8;  // 「排空→重投」最大轮数，防活锁

    // 显示孔径偏移（MFVideoArea.OffsetX/OffsetY）
    // 读取 Area.cx/cy 之外还必须读 OffsetX/OffsetY：若 OffsetX != 0，从 (0,0) 起裁会使画面整体平移，
    // 左/上边缘吃进编码填充（宏块边缘扩展 = 竖向拉丝），且奇数 OffsetX 在 4:2:0 下令色度错半像素（色噪）。
    // 偏移参与裁剪（见 TryExtractCodedFrame 的孔径裁剪逻辑）。
    private int _apertureOffsetX;
    private int _apertureOffsetY;
    /// <summary>MFVideoArea blob 的原始 16 字节 hex，仅用于首帧诊断（防止结构布局理解错误时无从对证）。</summary>
    private string? _apertureBlobHex;

    // DXVA 硬件解码零拷贝
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

    // SourceReader 自带硬解「直通包」路径
    // MFDemuxer 建 SourceReader 时挂 MF_SOURCE_READER_D3D_MANAGER + ENABLE_HARDWARE_TRANSFORMS，
    // 并把视频流输出协商为 NV12 ⇒ ReadSample 直接吐【已解码】样本。此时 packet 携带：
    //   (1) DecodedFrameResource（DXGI 纹理）  → 真·零拷贝，本类只做所有权移交，绝不再过 MFT；
    //   (2) Width/Height/Stride + NV12 字节    → 「半 DXVA」回落，本类只做去 stride 紧凑化，仍省掉二次解码。
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
            // 0. 【直通分支】SourceReader 已在 demuxer 内完成硬解 ⇒ 本类不得再过一遍 MFT。
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

    // 两阶段关闭协议
    // 重入互斥：CloseNativeSync / CloseNativeAsync 共用 _closed 这一 Interlocked 令牌，
    //    先到者执行完整协议，后到者立即返回（不等待）。保证的是**绝不二次 Release**。

    private static void ThrowIfFailed(int hr) => Marshal.ThrowExceptionForHR(hr);

    /// <summary>解码器发现诊断总开关：设环境变量 <c>LINGFAN_MF_DECODER_DIAG=1</c> 时打印全部候选 MFT CLSID /
    /// ActivateObject hr / MF_SA_D3D11_AWARE 等细节（诊断 Store HEVC MFT 枚举/激活问题时用），常态关闭避免生产日志噪声。</summary>
    private static bool DiagEnabled => Environment.GetEnvironmentVariable("LINGFAN_MF_DECODER_DIAG") == "1";

}
