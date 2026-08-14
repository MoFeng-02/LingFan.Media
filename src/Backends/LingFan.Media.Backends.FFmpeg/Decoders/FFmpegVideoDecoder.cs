using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.Models;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// 基于 FFmpeg libavcodec 的 <see cref="IVideoDecoder"/> 实现。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：接口契约，返回 <c>Task.CompletedTask</c>（无 I/O）。</item>
/// <item><see cref="Initialize"/>：同步，avcodec_find_decoder + alloc + open（参数化配置）。</item>
/// <item><see cref="DecodeAsync"/>：热路径异步，返回 <c>ValueTask&lt;VideoFrame?&gt;</c>，
/// avcodec_send_packet + avcodec_receive_frame 是 CPU 密集型，无 I/O，
/// 使用 <c>ValueTask.FromResult</c> 同步完成（减少分配）。</item>
/// <item><see cref="FlushAsync"/>：热路径异步，同上。</item>
/// <item><see cref="Reset"/>：同步，avcodec_flush_buffers。</item>
/// <item><see cref="Dispose"/> / <see cref="DisposeAsync"/>：同步原生释放。</item>
/// </list>
/// <para><b>线程安全</b>：单线程使用（管线线程），非线程安全。</para>
/// <para><b>AOT 兼容</b>：sealed 类，SafeHandle，无反射。</para>
/// </remarks>
internal sealed class FFmpegVideoDecoder : IVideoDecoder, IFramePoolAware<VideoFrame>
{
    /// <summary>
    /// D3D11VA 硬件帧池的额外余量（帧数），供管线长期持有切片使用。
    /// </summary>
    /// <remarks>
    /// 取值依据：VideoPipeline 有界队列 5 + 渲染中 1 + 呈现完成待回收 1 = 7，向上取整留 1 帧安全裕度。
    /// 每帧 NV12 1920x1080 ≈ 3MB，8 帧约 24MB 显存，代价可接受；过小会导致解码停摆（画面冻结但音频前进）。
    /// </remarks>
    private const int D3D11VAExtraHwFrames = 8;

    private readonly ILogger<FFmpegVideoDecoder> _logger;
    private readonly IGpuDeviceContext? _gpuContext;
    private readonly IEnumerable<IGpuFrameProducer>? _frameProducers;
    private IGpuFrameProducer? _gpuProducer;
    private bool _gpuImportMode;
    // D3D11VA NV12 硬解帧 → RGBA32 的 GPU 转换器（位于中性互操作模块 LingFan.Media.GPUShare.D3D11）。
    // 仅 GPU 零拷贝路径（Windows）使用；其持有的共享设备包装不 Dispose（见转换器注释）。
    private LingFan.Media.GPUShare.D3D11.D3D11Nv12ToRgbaConverter? _nv12ToRgbaConverter;
    private readonly FFmpegOptions? _options;
    private SafeAVCodecContextHandle? _codecContextHandle;
    private SafeAVBufferRefHandle? _hwDeviceCtx;
    private IFramePool<VideoFrame>? _framePool;
    private IntPtr _extradataBuffer;          // ctx->extradata 原生缓冲（含 64B padding），本类拥有，Dispose 释放
    private unsafe AVBSFContext* _bsfContext; // mp4toannexb 比特流过滤器（HEVC/H264 in MP4），本类拥有
    // 重播/seek 重建 BSF 所需参数（原始 HVCC/avcC extradata；不可改用 ctx->extradata 的 Annex-B 版）
    private VideoCodec _bsfCodec;
    private AVCodecID _bsfCodecId;
    private ReadOnlyMemory<byte> _bsfCfg;

    // 流时间基：解码帧 pts/dts 以「流 time_base」为单位。由 demuxer 透传，用于建立 ctx->pkt_timebase
    // 并做时间戳换算（入向 pkt->pts、出向 frame.Timestamp）。解码后 avFrame->time_base / ctx->time_base
    // 常为 0，直接换算会使帧时间戳全 0（→ 视频不节流突发提交、主时钟 SyncTo(0) 钉死、pos 不前进）。
    private Rational _timeBase;
    private double _tbSeconds;
    private bool _disposed;
    private bool _initialized;

    // 完整重建所需参数：Reset() 在重播/seek 时关闭旧 AVCodecContext 并复用原 settings 重建，
    // 解决 D3D11VA/VP9 硬件解码器 EOF 排干后 avcodec_flush_buffers 无法重新对齐新流首关键帧的问题。
    private VideoCodec _lastCodec;
    private VideoSettings? _lastSettings;

    // 复位后输出帧诊断计数：Reset() 置 4，DecodeCore 输出前 4 帧时打印 pts，
    // 用于确证「重播首帧已回到 ≈0」（而非跳到末关键帧）。仅诊断。
    private int _decodeDiagCount;

    // 解码器内部缓冲：修复 avcodec_send_packet 返回 EAGAIN（解码器输入满）时「放包」导致关键参考帧
    // 缺失、HEVC 重播整段 RPS 崩的问题。改为 FFmpeg 标准 send/receive 循环：待发送包入队、EAGAIN 时
    // 持有并重试，绝不丢弃；HEVC B 帧重排可能一包多帧时，多产出的帧也入队待返回，绝不丢弃。
    // 注：C# 泛型不接受指针类型参数（CS0306），故以 IntPtr 承载 AVPacket*。
    private readonly Queue<IntPtr> _pendingConv = new();
    private readonly Queue<VideoFrame> _pendingFrames = new();

    // 帧路径统计（与 MFVideoDecoder 的 [DXVA-FRAMEPATH] 对称）：
    // 「硬解激活=True」只证明解码器跑在 GPU 上，不证明**出餐**也是 GPU 纹理。
    // 若硬件帧被下载回系统内存（hwframe transfer / sws 转换），硬件就成了摆设。
    // 这两个计数器在 Dispose 时打印，作为「全程零拷贝」的日志佐证。
    private long _gpuZeroCopyFrames;  // D3D11VA / MediaCodec Surface 零拷贝帧（GPU 纹理直出）
    private long _cpuFallbackFrames;  // 软解 / CPU 内存帧
    private bool _frameSummaryLogged;

    /// <summary>FFmpeg EAGAIN 错误码（跨平台）。必须用 ffmpeg.AVERROR(ffmpeg.EAGAIN) 计算，
    /// 禁止硬编码 -11（Windows 正确，但 macOS/iOS 的 EAGAIN=35，会误判"需要更多数据"为解码失败）。</summary>
    private static readonly int EAGAIN = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <summary>
    /// 初始化 <see cref="FFmpegVideoDecoder"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="gpuContext">可选 GPU 设备上下文（D3D11VA 硬解需要，null=软件解码）。</param>
    /// <param name="options">可选 FFmpeg 配置（含 MediaCodec Surface 注入点）。</param>
    public FFmpegVideoDecoder(ILogger<FFmpegVideoDecoder> logger, IGpuDeviceContext? gpuContext = null, FFmpegOptions? options = null, IEnumerable<IGpuFrameProducer>? frameProducers = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuContext = gpuContext;
        _options = options;
        _frameProducers = frameProducers;
        // 解析匹配当前激活渲染器的零拷贝生产者（依赖倒置：解码器只依赖 IGpuFrameProducer 抽象）。
        // 仅当容器注册了与当前 IGpuDeviceContext.ApiType 同型的生产者时才非空；否则不进入 GPU 零拷贝路径。
        // 与 Vulkan 同源守卫，不硬编码任一渲染器——FFmpeg D3D11VA 在 Windows 上为 Vulkan/OpenGL 渲染器均产出 D3D11 共享句柄。
        // GPUApiType 无 Unknown 哨兵（D3D11=0 不可重定义），故以可空 GPUApiType? 表达"无活跃渲染器上下文"：
        // _gpuContext 为 null 时 activeApi 为 null，p.ApiType == null 恒为 false，_gpuProducer 保持 null（不进入 GPU 零拷贝路径）。
        GPUApiType? activeApi = _gpuContext?.ApiType;
        foreach (var p in _frameProducers ?? Enumerable.Empty<IGpuFrameProducer>())
        {
            if (p.ApiType == activeApi) { _gpuProducer = p; break; }
        }
    }

    /// <inheritdoc/>
    public VideoCodec Codec { get; private set; } = VideoCodec.Unknown;

    /// <inheritdoc/>
    public bool IsHardwareAccelerated { get; private set; }

    /// <inheritdoc/>
    /// <remarks>接口契约：无 I/O，返回 <see cref="Task.CompletedTask"/>。</remarks>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>同步参数化配置：avcodec_find_decoder + avcodec_alloc_context3 + avcodec_open2。</remarks>
    public unsafe void Initialize(VideoCodec codec, VideoSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            throw new InvalidOperationException("视频解码器已初始化");

        Codec = codec;
        _lastCodec = codec;
        _lastSettings = settings;
        AVCodecID codecId = MapVideoCodecToFFmpeg(codec);

        // 硬解开关是「两级与」：会话级 VideoSettings.EnableHardwareAcceleration
        // 与后端级 FFmpegOptions.HardwareAcceleration 任一为 false 即禁用。
        // 此前 FFmpegOptions.HardwareAcceleration 声明了却从未被任何代码读取——宿主写
        // AddFFmpeg(o => o.HardwareAcceleration = false) 毫无效果（静默失效）。
        // options 为 null（直接 new 解码器、未走 DI）时按 true 处理，保持既有行为。
        bool hwEnabled = settings.EnableHardwareAcceleration && (_options?.HardwareAcceleration ?? true);

        // Android MediaCodec 硬解必须使用专用解码器（h264_mediacodec 等）——
        // avcodec_find_decoder 默认返回软件解码器，对其挂 hw_device_ctx 无效
        AVCodec* avCodec = null;
        bool useMediaCodec = false;
        if (hwEnabled && OperatingSystem.IsAndroid())
        {
            string? mcName = GetMediaCodecDecoderName(codec);
            if (mcName is not null)
            {
                avCodec = ffmpeg.avcodec_find_decoder_by_name(mcName);
                useMediaCodec = avCodec != null;
                if (!useMediaCodec)
                    _logger.LogWarning("FFmpeg 未编译 MediaCodec 解码器 {Name}，回退到软件解码", mcName);
            }
        }

        // 查找解码器（软件路径 / MediaCodec 未命中回退）
        if (avCodec == null)
            avCodec = ffmpeg.avcodec_find_decoder(codecId);
        if (avCodec == null)
            throw new NotSupportedException($"FFmpeg 未找到视频解码器: {codec} (codec_id={codecId})");

        // 分配上下文
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(avCodec);
        if (ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 失败");

        // 建立流时间基：解码帧 pts/dts 以流 time_base 为单位，须由调用方写入 ctx->pkt_timebase。
        // 解码后 avFrame->time_base / ctx->time_base 常为 0，直接用其换算会使帧时间戳全 0。
        // 故用 demuxer 透传的流 time_base 建立 pkt_timebase，并以同一秒值做时间戳换算。
        _timeBase = settings.TimeBase;
        _tbSeconds = _timeBase.ToDouble();
        if (_timeBase.Denominator > 0)
        {
            AVRational tb = ctx->pkt_timebase;
            tb.num = _timeBase.Numerator;
            tb.den = _timeBase.Denominator;
            ctx->pkt_timebase = tb;
        }

        _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);

        // 应用编解码器私有配置（extradata）：MP4 中 HEVC/H264 为 length-prefixed，解码器需 hvcC/avcC 作为
        // extradata 才能解析参数集；并需 hevc_mp4toannexb/h264_mp4toannexb 比特流过滤器将包转为 Annex-B。
        ApplyCodecConfiguration(ctx, codec, codecId, settings.CodecConfiguration);

        // 配置硬件加速
        if (useMediaCodec)
        {
            // MediaCodec 硬解。宿主注入 Surface → 表面直渲染（零拷贝）；
            // 未注入 → 缓冲模式（ByteBuffer 输出 NV12 软件帧，仍为硬解）
            try
            {
                InitializeMediaCodec(ctx);
            }
            catch (Exception ex)
            {
                // hwdevice 初始化失败不致命：MediaCodec 解码器无 hw_device_ctx 时自动走缓冲模式
                _logger.LogWarning(ex, "MediaCodec 硬件设备上下文初始化失败，使用缓冲模式（仍为硬解）");
            }
            IsHardwareAccelerated = true;
        }
        else if (hwEnabled && _gpuContext is not null && _gpuContext.ApiType == GPUApiType.D3D11)
        {
            // D3D11VA 硬件解码——使用渲染器共享的 D3D11 设备
            // 零拷贝链路：硬解输出 ID3D11Texture2D → D3D11HardwareFrameResource → D3D11Renderer
            try
            {
                // 配套：硬解帧现在由 D3D11HardwareFrameResource 持引用保活切片（详见该类注释），
                //    管线在途帧数 = VideoPipeline 有界队列(5) + 渲染中(1) + 呈现完成待回收(1) ≈ 7。
                //    若不给 hw frames pool 留余量，解码器会因取不到空闲切片而 av_hwframe_get_buffer 失败
                //    → 解码停摆（表现为 present 计数卡死、画面冻结而音频照常前进）。
                //    extra_hw_frames 语义即「在解码器自身 DPB 需求之外，额外分配供调用方长期持有的帧数」。
                ctx->extra_hw_frames = D3D11VAExtraHwFrames;

                InitializeD3D11VA(ctx);
                IsHardwareAccelerated = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "D3D11VA 硬件解码初始化失败，回退到软件解码");
                IsHardwareAccelerated = false;
            }
        }
        else if (hwEnabled && _gpuContext is not null && _gpuContext.ApiType != GPUApiType.D3D11 && _gpuProducer is not null)
        {
            // GPU 零拷贝硬解：FFmpeg 仍走 D3D11VA 产出 D3D11 纹理（Windows），
            // 由匹配的渲染器生产者（Vulkan/OpenGL）经互操作导入为 GPU 纹理上屏（零拷贝，不回读 CPU）。
            // 依赖倒置：解码器只经 IGpuFrameProducer 抽象把原生句柄交给渲染器侧生产者。
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // 同 D3D11 分支：纹理数组须留余量供管线长期持有切片（见 D3D11VA 注释）。
                    ctx->extra_hw_frames = D3D11VAExtraHwFrames;
                    InitializeD3D11VA(ctx);
                    // 解码侧 NV12→RGBA 转换器（GPU 零拷贝必备：Vulkan/GL 无法可移植采样 NV12）。
                    // 构造失败会抛到下方 catch，回落软件解码（_gpuImportMode 保持 false）。
                    _nv12ToRgbaConverter = new LingFan.Media.GPUShare.D3D11.D3D11Nv12ToRgbaConverter(
                        _gpuContext!.DeviceHandle, _gpuContext.ContextHandle);
                    _gpuImportMode = true;
                    IsHardwareAccelerated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GPU 零拷贝（D3D11VA→GPU）初始化失败，回退软件解码");
                    IsHardwareAccelerated = false;
                }
            }
            else
            {
                // Linux/macOS：GPU 零拷贝需 VAAPI→GPU（VaApiInterop 为 Phase 2 桩，未实现）→ 软解。
                // 不进入 _gpuImportMode，避免运行时每帧都尝试不可用的导入。
                IsHardwareAccelerated = false;
                _logger.LogWarning(
                    "已请求 GPU 零拷贝，但当前平台不支持硬解→GPU 互操作（VAAPI 为 Phase 2），" +
                    "本次{Codec}将全程软件解码（CPU 拷贝）。", codec);
            }
        }
        else if (hwEnabled)
        {
            // 硬解已请求但拿不到可用 GPU 设备上下文 → 只能软解。
            // 此处**必须出声**：静默回落会让宿主以为「硬解优先」已生效，实则全程 CPU 解码 + CPU 拷贝，
            // 硬件彻底成摆设，且日志上毫无痕迹（与「带默认实现的接口成员漏转发=静默失效」同类反模式）。
            // 典型成因：只注册了 AddFFmpeg() + AddHeadlessRenderer()，而 IGpuDeviceContext 由
            // AddD3D11Renderer() 或 AddMediaFoundation()（自备窗口无关设备）注册——两者都没注册即为 null。
            IsHardwareAccelerated = false;
            if (_gpuContext is null)
                _logger.LogWarning(
                    "已请求硬件解码，但容器中没有 IGpuDeviceContext → D3D11VA 无法启用，本次{Codec}将全程软件解码（CPU 拷贝）。" +
                    "修法：注册 AddD3D11Renderer()（有头，与渲染器共享设备=零拷贝）或 AddMediaFoundation()（无头自备 D3D11 设备）。",
                    codec);
            else
                _logger.LogWarning(
                    "已请求硬件解码，但 IGpuDeviceContext.ApiType={Api} 非 D3D11 → D3D11VA 无法启用，本次{Codec}将全程软件解码（CPU 拷贝）。",
                    _gpuContext.ApiType, codec);
        }

        // 打开解码器
        int ret = ffmpeg.avcodec_open2(ctx, avCodec, null);
        if (ret < 0 && useMediaCodec)
        {
            // MediaCodec 打开失败（如宿主未调 MediaCodecInterop.SetJavaVM）→ 回退软件解码
            _logger.LogWarning("MediaCodec 解码器打开失败 ({Error})，回退到软件解码。" +
                "提示：宿主须先调用 MediaCodecInterop.SetJavaVM 注入 JavaVM", GetErrorString(ret));
            _hwDeviceCtx?.Dispose();
            _hwDeviceCtx = null;
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            IsHardwareAccelerated = false;

            avCodec = ffmpeg.avcodec_find_decoder(codecId);
            if (avCodec == null)
                throw new NotSupportedException($"FFmpeg 未找到视频解码器: {codec} (codec_id={codecId})");
            ctx = ffmpeg.avcodec_alloc_context3(avCodec);
            if (ctx == null)
                throw new InvalidOperationException("avcodec_alloc_context3 失败（软件回退）");
            _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);
            ret = ffmpeg.avcodec_open2(ctx, avCodec, null);
        }
        if (ret < 0 && IsHardwareAccelerated)
        {
            // D3D11VA 解码器打开失败（如驱动不支持该 profile 的硬件解码会话、
            // 共享设备在该分辨率下无法建立 DXVA 会话）——但软件解码器仍可用，干净回退，
            // 绝不抛异常阻断播放（与上方 MediaCodec 分支对称，落实「软解正确的逻辑」）。
            _logger.LogWarning("D3D11VA 解码器打开失败 ({Error})，回退到软件解码。", GetErrorString(ret));
            _hwDeviceCtx?.Dispose();
            _hwDeviceCtx = null;
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            IsHardwareAccelerated = false;

            avCodec = ffmpeg.avcodec_find_decoder(codecId);
            if (avCodec == null)
                throw new NotSupportedException($"FFmpeg 未找到视频解码器: {codec} (codec_id={codecId})");
            ctx = ffmpeg.avcodec_alloc_context3(avCodec);
            if (ctx == null)
                throw new InvalidOperationException("avcodec_alloc_context3 失败（软件回退）");
            _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);
            ret = ffmpeg.avcodec_open2(ctx, avCodec, null);
        }
        if (ret < 0)
        {
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            throw new InvalidOperationException($"avcodec_open2 失败: {GetErrorString(ret)} (code={ret})");
        }

        _initialized = true;
        _logger.LogInformation("视频解码器初始化: {Codec}, 硬件加速={HwAccel}", codec, IsHardwareAccelerated);
    }

    /// <summary>
    /// 应用编解码器私有配置到解码上下文：设置 <c>extradata</c>，并对 MP4 中的 HEVC/H264 安装
    /// <c>mp4toannexb</c> 比特流过滤器（将 length-prefixed 包转为 Annex-B 起始码格式）。
    /// </summary>
    /// <remarks>
    /// <para>MP4 容器中的 HEVC/H264 样本为长度前缀格式，ffmpeg 解码器需 Annex-B（起始码）才能解析 NAL 单元；
    /// 缺少 extradata 与转换会报「No start code is found. Error splitting the input into NAL units.」。</para>
    /// <para>par_in->extradata 用 ffmpeg 分配器（<c>av_malloc</c>）分配，因 <c>av_bsf_free</c> 经
    /// <c>avcodec_parameters_free→av_freep</c> 释放，须与分配器匹配，避免 <c>Marshal.AllocHGlobal</c> 堆损坏。</para>
    /// </remarks>
    private unsafe void ApplyCodecConfiguration(AVCodecContext* ctx, VideoCodec codec, AVCodecID codecId, ReadOnlyMemory<byte> cfg)
    {
        // 记录参数供 Reset() 重播/seek 重建 BSF（必须从原始 HVCC/avcC extradata 重建，不能沿用 Annex-B 版）
        _bsfCodec = codec;
        _bsfCodecId = codecId;
        _bsfCfg = cfg;

        if (cfg.IsEmpty)
            return;

        bool needBsf = codec == VideoCodec.H264 || codec == VideoCodec.H265;
        if (!needBsf)
        {
            SetExtradata(ctx, cfg);
            return;
        }

        string filterName = codec == VideoCodec.H265 ? "hevc_mp4toannexb" : "h264_mp4toannexb";
        AVBitStreamFilter* filter = ffmpeg.av_bsf_get_by_name(filterName);
        if (filter == null)
        {
            _logger.LogWarning("未找到比特流过滤器 {Filter}，回退仅设置 extradata（HEVC/H264 可能仍无法解码）", filterName);
            SetExtradata(ctx, cfg);
            return;
        }

        AVBSFContext* bsfCtx = null;
        int ret = ffmpeg.av_bsf_alloc(filter, &bsfCtx);
        if (ret < 0 || bsfCtx == null)
        {
            _logger.LogWarning("av_bsf_alloc 失败 ({Error})，回退仅设置 extradata", GetErrorString(ret));
            SetExtradata(ctx, cfg);
            return;
        }

        bsfCtx->par_in->codec_id = codecId;
        bsfCtx->par_in->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;

        // 用 ffmpeg 分配器拷贝 extradata（av_bsf_free 释放时须同分配器）。
        int size = cfg.Length;
        int padded = size + 64;
        byte* edata = (byte*)ffmpeg.av_malloc((UIntPtr)padded);
        if (edata == null)
        {
            ffmpeg.av_bsf_free(&bsfCtx);
            SetExtradata(ctx, cfg);
            return;
        }
        cfg.Span.CopyTo(new Span<byte>(edata, size));
        new Span<byte>(edata + size, 64).Clear();
        bsfCtx->par_in->extradata = edata;
        bsfCtx->par_in->extradata_size = size;

        ret = ffmpeg.av_bsf_init(bsfCtx);
        if (ret < 0)
        {
            _logger.LogWarning("av_bsf_init 失败 ({Error})，回退仅设置 extradata", GetErrorString(ret));
            ffmpeg.av_bsf_free(&bsfCtx);
            SetExtradata(ctx, cfg);
            return;
        }

        // 解码器 extradata 必须与「经 BSF 转换后的包格式」配套，绝不能沿用原始 hvcC/avcC：
        //   ffmpeg 的 hevc/h264 解码器按 extradata 首字节判定码流格式 —— hvcC/avcC（首字节 0x01）
        //   会令 is_nalff / is_avc = 1，解码器随后调用 ff_h2645_packet_split 按「长度前缀」拆 NAL；
        //   而包经 mp4toannexb 之后已是 Annex-B 起始码格式 ⇒ 两者矛盾，解码依旧失败
        //   （表现仍是 "No start code is found. / Error splitting the input into NAL units."）。
        //   av_bsf_init 之后 par_out->extradata 即为 BSF 产出的 Annex-B 参数集（VPS/SPS/PPS），
        //   用它设置解码器才自洽。par_out 为空属理论异常，回退原始 cfg 并告警。
        AVCodecParameters* parOut = bsfCtx->par_out;
        if (parOut != null && parOut->extradata != null && parOut->extradata_size > 0)
        {
            SetExtradata(ctx, new ReadOnlySpan<byte>(parOut->extradata, parOut->extradata_size).ToArray());
        }
        else
        {
            _logger.LogWarning("{Filter} 的 par_out->extradata 为空，回退使用原始 hvcC/avcC extradata（解码可能失败）", filterName);
            SetExtradata(ctx, cfg);
        }

        _bsfContext = bsfCtx;
        _logger.LogDebug("已安装 {Filter} 比特流过滤器（MP4→Annex-B，解码器 extradata={Size}B）",
            filterName, ctx->extradata_size);
    }

    /// <summary>
    /// 将编解码器私有配置写入 <c>ctx->extradata</c>（含 64 字节零填充，符合 ffmpeg 要求）。
    /// 缓冲由本类以 <see cref="Marshal"/> 持有，<see cref="Dispose"/> 时释放。
    /// </summary>
    private unsafe void SetExtradata(AVCodecContext* ctx, ReadOnlyMemory<byte> cfg)
    {
        int size = cfg.Length;
        if (size <= 0)
            return;
        int padded = size + 64;
        IntPtr buf = Marshal.AllocHGlobal(padded);
        Span<byte> span = new((void*)buf, padded);
        cfg.Span.CopyTo(span);
        span[size..].Clear();
        if (_extradataBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_extradataBuffer);   // 重播重建 BSF 时先释放旧缓冲，避免泄漏
        _extradataBuffer = buf;
        ctx->extradata = (byte*)buf;
        ctx->extradata_size = size;
    }

    /// <summary>
    /// 将入向 <see cref="MediaPacket"/> 转换为待发送 AVPacket：填充数据/pts/dts/keyframe 标志，
    /// 若已安装 mp4toannexb BSF 则先经其转换为 Annex-B（hevc_mp4toannexb 为严格 1:1 过滤器）。
    /// 返回的 AVPacket 由调用方入队 <c>_pendingConv</c>，发送成功后由调用方 av_packet_free。
    /// 返回 null 表示当前无法转换（如 BSF 尚未产出，1:1 过滤器下罕见）。
    /// </summary>
    private unsafe AVPacket* ConvertToConv(MediaPacket packet)
    {
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null)
            return null;
        int allocRet = ffmpeg.av_new_packet(pkt, packet.Data.Length);
        if (allocRet < 0)
        {
            ffmpeg.av_packet_free(&pkt);
            return null;
        }
        packet.Data.Span.CopyTo(new Span<byte>(pkt->data, packet.Data.Length));
        // 时间戳换算：以「流 time_base」为单位，绝不能用解码后 ctx->time_base（常 0 → 时间戳全 0）。
        double timeBase = _tbSeconds;
        pkt->pts = timeBase > 0
            ? (long)(packet.Timestamp.TotalSeconds / timeBase)
            : ffmpeg.AV_NOPTS_VALUE;
        pkt->dts = pkt->pts;
        if (packet.KeyFrame)
            pkt->flags |= ffmpeg.AV_PKT_FLAG_KEY;

        if (_bsfContext == null)
            return pkt; // 无 BSF：pkt 即待发送包

        // 经 BSF 转换（BSF 内部引用 pkt 数据；pkt 随后释放安全）
        int bret = ffmpeg.av_bsf_send_packet(_bsfContext, pkt);
        if (bret < 0 && bret != EAGAIN)
        {
            if (bret != ffmpeg.AVERROR_EOF)
                _logger.LogWarning("av_bsf_send_packet 返回 {Ret}: {Error}", bret, GetErrorString(bret));
            ffmpeg.av_packet_free(&pkt);
            return null;
        }
        AVPacket* conv = ffmpeg.av_packet_alloc();
        if (conv == null)
        {
            ffmpeg.av_packet_free(&pkt);
            return null;
        }
        bret = ffmpeg.av_bsf_receive_packet(_bsfContext, conv);
        ffmpeg.av_packet_free(&pkt); // 转换完成，原始包释放（conv 为独立分配）
        if (bret == EAGAIN || bret == ffmpeg.AVERROR_EOF)
        {
            // 1:1 过滤器下罕见：BSF 暂未产出。pkt 已被 BSF 消费，conv 未用。
            ffmpeg.av_packet_free(&conv);
            return null;
        }
        if (bret < 0)
        {
            _logger.LogWarning("av_bsf_receive_packet 返回 {Ret}: {Error}", bret, GetErrorString(bret));
            ffmpeg.av_packet_free(&conv);
            return null;
        }
        return conv;
    }

    /// <summary>由已接收的 AVFrame 构造 <see cref="VideoFrame"/>（区分 D3D11VA/MediaCodec/软件路径）。</summary>
    /// <remarks>
    /// 唯一帧路径分流点：三条支路各自计数（GPU 零拷贝 / CPU 拷贝），构造成功后才计数，
    /// 避免异常路径污染统计。计数结果由 <see cref="Dispose"/> 打印 <c>[FFMPEG-FRAMEPATH]</c>。
    /// </remarks>
    private unsafe VideoFrame? MakeFrame(AVFrame* avFrame)
    {
        var hwFmt = (AVPixelFormat)avFrame->format;
        if (hwFmt is AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD or AVPixelFormat.AV_PIX_FMT_D3D11)
            return CreateHardwareFrameFromAVFrame(avFrame);
        if (hwFmt == AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
            return CreateMediaCodecSurfaceFrame(avFrame);
        return CreateVideoFrameFromAVFrame(avFrame);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 热路径异步：avcodec_send_packet + avcodec_receive_frame 是 CPU 密集型同步操作，
    /// 使用 <see cref="ValueTask.FromResult{TResult}"/> 同步完成，减少分配。
    /// </remarks>
    public unsafe ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("视频解码器尚未初始化");

        VideoFrame? frame = DecodeCore(packet);
        return ValueTask.FromResult(frame);
    }

    /// <summary>DecodeAsync 的核心逻辑。</summary>
    /// <remarks>
    /// FFmpeg 标准 send/receive 循环：新包先经 BSF 转 Annex-B 入队 <c>_pendingConv</c>；
    /// 随后尽可能把队首包喂给解码器（<c>avcodec_send_packet</c> 返回 EAGAIN 表示输入缓冲满，
    /// 此时停发并留在队中，下次调用再重试——绝不放包）；最后排空解码器已产出的所有帧
    /// （HEVC B 帧重排可能一包多帧，多出的帧入 <c>_pendingFrames</c> 留待下次返回，绝不丢弃）。
    /// 这样无论首播还是重播，都不会因 EAGAIN 丢弃关键参考帧，解决 HEVC 重播 RPS 崩。
    /// </remarks>
    private unsafe VideoFrame? DecodeCore(MediaPacket packet)
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        // 1) 转换新包并入队
        AVPacket* conv = ConvertToConv(packet);
        if (conv != null)
            _pendingConv.Enqueue((IntPtr)conv);

        AVFrame* avFrame = ffmpeg.av_frame_alloc();
        if (avFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败");
        try
        {
            // 2) 发送所有解码器能接受的包（EAGAIN 即停，留待重试）
            while (_pendingConv.Count > 0)
            {
                AVPacket* p = (AVPacket*)_pendingConv.Peek();
                int ret = ffmpeg.avcodec_send_packet(ctx, p);
                if (ret == 0)
                {
                    _pendingConv.Dequeue();
                    ffmpeg.av_packet_free(&p);
                }
                else if (ret == EAGAIN)
                {
                    break; // 解码器输入满，稍后重试（包仍保留在队中）
                }
                else if (ret == ffmpeg.AVERROR_EOF)
                {
                    _pendingConv.Dequeue();
                    ffmpeg.av_packet_free(&p);
                }
                else
                {
                    _logger.LogWarning("avcodec_send_packet 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                    _pendingConv.Dequeue();
                    ffmpeg.av_packet_free(&p);
                }
            }

            // 3) 排空解码器已产出的所有帧，一律入队（保证 FIFO 帧序，绝不丢弃）
            while (true)
            {
                int ret = ffmpeg.avcodec_receive_frame(ctx, avFrame);
                if (ret == EAGAIN || ret == ffmpeg.AVERROR_EOF)
                    break;
                if (ret < 0)
                {
                    _logger.LogWarning("avcodec_receive_frame 返回 {Ret}: {Error}", ret, GetErrorString(ret));
                    break;
                }
                VideoFrame? f = MakeFrame(avFrame);
                if (f != null)
                {
                    // 复位后输出帧诊断：前 4 帧打印 pts，确证重播首帧已回到 ≈0。
                    if (_decodeDiagCount > 0)
                    {
                        --_decodeDiagCount;
                        try
                        {
                            _logger.LogInformation(
                                "[DECODE-DIAG] 复位后输出帧#{Seq}: pts={Pts} key={Key} (pts≈0 表示已正确回到起点)",
                                4 - _decodeDiagCount, f.Timestamp, f.KeyFrame);
                        }
                        catch { }
                    }
                    _pendingFrames.Enqueue(f);
                }
                ffmpeg.av_frame_unref(avFrame);
            }
        }
        finally
        {
            AVFrame* af = avFrame;
            ffmpeg.av_frame_free(&af);
        }

        // 4) 从队首取一帧返回（多余帧留待后续调用返回，帧序严格 FIFO）
        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <inheritdoc/>
    /// <remarks>热路径异步：刷新缓冲取出剩余帧，同步完成。</remarks>
    public unsafe ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("视频解码器尚未初始化");

        VideoFrame? frame = FlushCore();
        return ValueTask.FromResult(frame);
    }

    /// <summary>FlushAsync 的核心逻辑：排空内部队列后发送 null packet 刷新解码器。</summary>
    /// <remarks>
    /// 调用顺序严格为「积压帧 → 待发包 → null packet → receive」：
    /// DecodeCore 的 EAGAIN 重试队列在 EOS 时可能仍有残包，必须先喂完再进入 draining，
    /// 否则尾部若干帧会被静默丢弃（表现为末段掉帧 / Ended 提前）。
    /// </remarks>
    private unsafe VideoFrame? FlushCore()
    {
        AVCodecContext* ctx = (AVCodecContext*)_codecContextHandle!.DangerousGetHandle();

        // 0) 先交出 DecodeCore 一包多帧时积压的帧（严格 FIFO）
        if (_pendingFrames.Count > 0)
            return _pendingFrames.Dequeue();

        // 1) 把仍挂在待发队列里的包尽力喂完；EAGAIN 表示解码器输入满 → 先去 receive 腾空间
        while (_pendingConv.Count > 0)
        {
            AVPacket* pending = (AVPacket*)_pendingConv.Peek();
            int sret = ffmpeg.avcodec_send_packet(ctx, pending);
            if (sret == EAGAIN)
                break;
            _pendingConv.Dequeue();
            ffmpeg.av_packet_free(&pending);
            if (sret < 0 && sret != ffmpeg.AVERROR_EOF)
                _logger.LogWarning("avcodec_send_packet(flush) 返回 {Ret}: {Error}", sret, GetErrorString(sret));
        }

        // 2) 待发队列排空后才进入 draining 模式（重复发 null packet 是安全的）
        if (_pendingConv.Count == 0)
            ffmpeg.avcodec_send_packet(ctx, null);

        int ret;
        AVFrame* avFrame = ffmpeg.av_frame_alloc();
        if (avFrame == null)
            throw new InvalidOperationException("av_frame_alloc 失败");
        try
        {
            ret = ffmpeg.avcodec_receive_frame(ctx, avFrame);
            if (ret < 0)
                return null;

            // Flush 时同样需检查 D3D11VA 硬解输出格式（与 DecodeCore 一致）
            // 同 DecodeCore：FFmpeg 8 产出 AV_PIX_FMT_D3D11，两种命名都接纳。
            var hwFmtFlush = (AVPixelFormat)avFrame->format;
            if (hwFmtFlush is AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD or AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                return CreateHardwareFrameFromAVFrame(avFrame);
            }

            // Flush 时同样需检查 MediaCodec 表面输出格式（与 DecodeCore 一致）
            if ((AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
            {
                return CreateMediaCodecSurfaceFrame(avFrame);
            }

            return CreateVideoFrameFromAVFrame(avFrame);
        }
        finally
        {
            AVFrame* p = avFrame;
            ffmpeg.av_frame_free(&p);
        }
    }

    /// <inheritdoc/>
    public void SetFramePool(IFramePool<VideoFrame>? pool)
    {
        _framePool = pool;
    }

    /// <summary>
    /// 清空 send/receive 内部队列：释放未发送的包、归还未交出的帧。
    /// </summary>
    /// <remarks>
    /// 重播/seek 必调：上一轮残留的待发包属于旧时间线，若混入新流会造成参考帧错乱；
    /// 残留帧未归还帧池则会导致 D3D11VA 硬件帧池枯竭（画面冻结但音频前进）。
    /// </remarks>
    private unsafe void ClearPendingQueues()
    {
        while (_pendingConv.Count > 0)
        {
            AVPacket* p = (AVPacket*)_pendingConv.Dequeue();
            ffmpeg.av_packet_free(&p);
        }
        while (_pendingFrames.Count > 0)
        {
            _pendingFrames.Dequeue().Dispose();
        }
    }

    /// <inheritdoc/>
    public unsafe void Reset()
    {
        if (_codecContextHandle == null) return;

        // 先丢弃旧时间线的残留包/帧，避免混入新流造成参考帧错乱或 D3D11VA 硬件帧池枯竭。
        ClearPendingQueues();

        // 重播/seek 全量重建：D3D11VA/VP9 硬件解码器在流末 EOF 排干后，
        // 仅 avcodec_flush_buffers 无法让其重新对齐到新流首关键帧——它会把重播首帧当旧流续帧
        // 一路丢帧直到下一个真关键帧 → 视频冻结较长时间。
        // 成因：硬件解码器内部参考帧/序列状态在 EOF 后未干净复位。
        // 唯一稳妥修复=关闭旧 AVCodecContext 并完整重建（同 Initialize 路径，复用原 settings）。
        // 引用计数配对（不泄漏共享 D3D11 设备）：先 Dispose 旧 _hwDeviceCtx（av_buffer_unref 释放时
        //   ffmpeg 内部 Release 掉 InitializeD3D11VA 时对共享设备加的 2 个引用），再 avcodec_free_context，
        //   最后 Initialize 重新 AddRef + 重建 hw_device_ctx。旧解码器仍被在途帧(D3D11HardwareFrameResource
        //   持有的 av_frame_clone 引用)保活的纹理，待那些帧归还池后由 ffmpeg 引用计数自动释放，安全。
        try
        {
            _hwDeviceCtx?.Dispose();
            _hwDeviceCtx = null;
            if (_bsfContext != null)
            {
                AVBSFContext* local = _bsfContext;
                _bsfContext = null;
                ffmpeg.av_bsf_free(&local);
            }
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            _initialized = false;

            // 复用原 settings 完整重建（重新分配 ctx + 挂共享 D3D11 设备 + 重建 BSF/extradata + open）
            Initialize(_lastCodec, _lastSettings!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视频解码器重播全量重建失败");
            throw;
        }

        _decodeDiagCount = 4; // 诊断：下一个 DecodeCore 输出前 4 帧打印 pts
        _logger.LogDebug("视频解码器已全量重建(重播/seek 复位)");
    }

    /// <inheritdoc/>
    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 收尾帧路径统计（零拷贝验证计数）：与 MFVideoDecoder 的 [DXVA-FRAMEPATH] 对称。
        // 判据：硬解激活(IsHardwareAccelerated) 且 GPU 零拷贝帧 > 0 才算「硬件没白用」；
        // 若硬解激活但 GPU=0，说明帧被下载回系统内存 —— 属于「半硬解」缺陷，必须暴露而非静默。
        if (!_frameSummaryLogged)
        {
            _frameSummaryLogged = true;
            if (_gpuZeroCopyFrames > 0 || _cpuFallbackFrames > 0)
                _logger.LogInformation(
                    "[FFMPEG-FRAMEPATH] 解码帧路径统计：GPU零拷贝={Gpu} 帧 / CPU拷贝={Cpu} 帧 | 硬解激活={Hw} | 零拷贝生效={Verdict}",
                    _gpuZeroCopyFrames, _cpuFallbackFrames, IsHardwareAccelerated,
                    IsHardwareAccelerated && _gpuZeroCopyFrames > 0 ? "是" : "否(全程 CPU 帧)");
        }

        ClearPendingQueues();
        _hwDeviceCtx?.Dispose();
        _hwDeviceCtx = null;
        // 释放 NV12→RGBA 转换器（仅释放其内部 QI 的视频设备/上下文与处理器；共享设备包装不 Dispose）。
        _nv12ToRgbaConverter?.Dispose();
        _nv12ToRgbaConverter = null;
        // 先释放比特流过滤器（其内部 par_in->extradata 由 ffmpeg 分配器管理）
        if (_bsfContext != null)
        {
            AVBSFContext* local = _bsfContext;
            ffmpeg.av_bsf_free(&local);
            _bsfContext = null;
        }
        // 再释放本类持有的 extradata 缓冲（ctx->extradata 已被解码器读取，先于 codec context 释放安全）
        if (_extradataBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_extradataBuffer);
            _extradataBuffer = IntPtr.Zero;
        }
        _codecContextHandle?.Dispose();
        _codecContextHandle = null;
        _initialized = false;
    }

    /// <inheritdoc/>
    /// <remarks>接口契约：原生释放为快速同步操作。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 辅助方法 ──

    /// <summary>从 AVFrame 创建 VideoFrame。</summary>
    /// <remarks>
    /// <para>若 _framePool 可用，从池中 Rent 帧壳并调用 Reset 填充数据，复用 VideoFrame 实例减少 GC。</para>
    /// <para>packed BGRA/RGBA 帧走 av_frame_clone 引用计数路径——
    /// 克隆帧共享原生 buffer（内部对所有 buf 做 av_buffer_ref），SoftwareFrameResource
    /// 直接映射 data[0] 并携带实际 stride，Dispose 时经 SafeAVFrameHandle 释放引用。
    /// YUV/planar 格式渲染层无法直接消费（Skia 仅支持 BGRA/RGBA），保持既有拷贝路径。</para>
    /// </remarks>
    private unsafe VideoFrame CreateVideoFrameFromAVFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;
        AVPixelFormat pixFmt = (AVPixelFormat)avFrame->format;
        PixelFormat format = MapPixelFormatFromFFmpeg(pixFmt);

        // packed 4 字节格式优先零拷贝；失败（非引用计数帧/OOM）回退拷贝
        SoftwareFrameResource? resource = null;
        if (format == PixelFormat.P010)
        {
            // 10-bit 单一收敛点：解码侧把 P010(NV12 布局、10bit 存 16-bit 高位)
            // CPU 解包为 BGRA32，渲染器只消费 BGRA32——与 NV12 软件帧/D3D11 不可绑 SRV
            // 的处理完全一致。无黑屏、无静默降 8-bit；真·GPU 10-bit 零拷贝着色器留作未来增强。
            resource = CreateP010ToBgraResource(avFrame, width, height);
        }
        else if (format == PixelFormat.YUV420P10)
        {
            // 10-bit 平面收敛点：ffmpeg 软解 10-bit HEVC 主流出帧为 yuv420p10le(三平面、
            // 10bit 存 16-bit 高位)。与 P010 完全相同的 BT.601 全范围解包到 BGRA32，
            // 仅平面布局不同。若不处理此分支，默认会误当 8-bit YUV420P 解包 => 画面发绿/错色，
            // 且 present 计数仍 PASS（假阳性）。
            resource = CreateYuv420P10ToBgraResource(avFrame, width, height);
        }
        else if (pixFmt is AVPixelFormat.AV_PIX_FMT_BGRA or AVPixelFormat.AV_PIX_FMT_RGBA)
        {
            resource = TryCreateZeroCopyResource(avFrame, width, height, format);
        }
        resource ??= CreateCopyResource(avFrame, width, height, pixFmt, format);

        TimeSpan timestamp = avFrame->pts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        TimeSpan duration = avFrame->duration > 0
            ? TimeSpan.FromTicks((long)(avFrame->duration * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        bool keyFrame = (avFrame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;

        // 从池中 Rent 帧壳并 Reset 填充数据，复用 VideoFrame 实例。
        // 用 resource.Format（解包路径下为 BGRA32）而非 format，避免枚举/实际布局不一致。
        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, resource.Format, resource, timestamp, duration, keyFrame);
        System.Threading.Interlocked.Increment(ref _cpuFallbackFrames);
        return frame;
    }

    /// <summary>
    /// av_frame_clone 引用计数共享原生 buffer，避免整帧拷贝。
    /// </summary>
    /// <returns>零拷贝资源；克隆失败（非引用计数帧/内存不足/异常布局）返回 null 由调用方回退拷贝。</returns>
    private static unsafe SoftwareFrameResource? TryCreateZeroCopyResource(
        AVFrame* avFrame, int width, int height, PixelFormat format)
    {
        if (width <= 0 || height <= 0)
            return null; // 异常尺寸交由拷贝路径统一报错

        // av_frame_clone = av_frame_alloc + av_frame_ref：共享所有 buf（引用计数 +1），不拷贝像素
        AVFrame* clone = ffmpeg.av_frame_clone(avFrame);
        if (clone == null)
            return null;

        var owner = new SafeAVFrameHandle((IntPtr)clone);

        int stride = clone->linesize[0];
        if (stride <= 0 || clone->data[0] == null)
        {
            // 负 stride（自底向上布局）或空数据：不支持零拷贝，释放克隆回退
            owner.Dispose();
            return null;
        }

        // packed 4 字节格式：精确长度 = stride*(height-1) + 行有效载荷，绝不越界读取
        int rowPayload = width * 4;
        int length = stride * (height - 1) + rowPayload;
        var memory = new NativeBufferMemoryManager((IntPtr)clone->data[0], length).Memory;
        return new SoftwareFrameResource(width, height, format, memory, stride, owner);
    }

    /// <summary>
    /// 拷贝路径（YUV/planar 及零拷贝回退）：av_image_copy_to_buffer 到 ArrayPool buffer。
    /// </summary>
    private static unsafe SoftwareFrameResource CreateCopyResource(
        AVFrame* avFrame, int width, int height, AVPixelFormat pixFmt, PixelFormat format)
    {
        // 使用 FFmpeg av_image_get_buffer_size 计算所需缓冲区大小
        int bufSize = ffmpeg.av_image_get_buffer_size(pixFmt, width, height, 1);
        if (bufSize <= 0)
            throw new InvalidOperationException(
                $"av_image_get_buffer_size 返回 {bufSize}（format={pixFmt}, {width}x{height}）");

        // 使用 ArrayPool 租借内存，减少 GC 压力（60fps 每秒 60 个帧）
        var resource = new SoftwareFrameResource(width, height, format, bufSize);

        // AVFrame.data/linesize 是 Array8，av_image_copy_to_buffer 需要 Array4，需转换
        var srcData = new byte_ptrArray4();
        srcData[0] = avFrame->data[0];
        srcData[1] = avFrame->data[1];
        srcData[2] = avFrame->data[2];
        srcData[3] = avFrame->data[3];

        var srcLinesize = new int_array4();
        srcLinesize[0] = avFrame->linesize[0];
        srcLinesize[1] = avFrame->linesize[1];
        srcLinesize[2] = avFrame->linesize[2];
        srcLinesize[3] = avFrame->linesize[3];

        // 使用 av_image_copy_to_buffer 正确处理所有像素格式（YUV420P/YUV422P/YUV444P/NV12 等），
        // 避免手动计算色度平面高度导致的非 YUV420 格式数据损坏。
        // Pin Memory<byte> 获取原始指针供 FFmpeg 互操作（using var 确保方法返回前释放 GCHandle）
        using var pin = resource.Data.Pin();
        ffmpeg.av_image_copy_to_buffer(
            (byte*)pin.Pointer, bufSize,
            srcData, srcLinesize,
            pixFmt, width, height, 1);

        return resource;
    }

    /// <summary>
    /// P010(NV12 布局、10bit 存于 16-bit 高位) CPU 解包为 BGRA32 的单一收敛点。
    /// </summary>
    /// <remarks>
    /// <para>10-bit 值经右移 6 位归一化到 0..1023，再套用与 NV12 GPU 着色器 / Skia CPU 兜底
    /// 完全一致的 BT.601 全范围系数（Rv=1.402, Gu=0.344136, Gv=0.714136, Bu=1.772），
    /// 保证 10-bit 与 8-bit 帧色彩不漂移、无静默降精度。这是「10-bit 上屏」的唯一正确入口；
    /// 真·GPU 10-bit 零拷贝着色器（采 unorm16）留作未来增强。</para>
    /// <para>P010 唯一来源是 FFmpeg 软解（MF 解码器硬编码只产 NV12），故解包只落在解码侧，
    /// 渲染器永远只消费 BGRA32，三渲染器（D3D11/OpenGL/Vulkan）+ Skia 均免改。</para>
    /// </remarks>
    private static unsafe SoftwareFrameResource CreateP010ToBgraResource(
        AVFrame* avFrame, int width, int height)
    {
        bool bigEndian = (AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_P010BE;

        int bgraBufSize = width * height * 4;
        var resource = new SoftwareFrameResource(width, height, PixelFormat.BGRA32, bgraBufSize);

        byte* yPlane = avFrame->data[0];
        byte* uvPlane = avFrame->data[1];
        int yStride = avFrame->linesize[0];
        int uvStride = avFrame->linesize[1];
        if (yPlane == null || uvPlane == null || yStride <= 0 || uvStride <= 0)
            throw new InvalidOperationException("P010 帧平面/步幅无效");

        using var pin = resource.Data.Pin();
        byte* dst = (byte*)pin.Pointer;

        // BT.601 全范围系数（与 NV12 着色器、Skia LUT 一致）
        const double rV = 1.402;
        const double gU = 0.344136;
        const double gV = 0.714136;
        const double bU = 1.772;

        for (int y = 0; y < height; y++)
        {
            byte* yRow = yPlane + (nint)y * yStride;
            byte* uvRow = uvPlane + (nint)(y >> 1) * uvStride;
            byte* dstRow = dst + (nint)y * (width * 4);
            for (int x = 0; x < width; x++)
            {
                int yVal = Read10Bit(yRow, x, bigEndian);
                int uVal = Read10Bit(uvRow, (x >> 1) * 2, bigEndian);
                int vVal = Read10Bit(uvRow, (x >> 1) * 2 + 1, bigEndian);

                double yf = yVal / 1023.0;
                double uf = uVal / 1023.0 - 0.5;
                double vf = vVal / 1023.0 - 0.5;

                double r = yf + rV * vf;
                double g = yf - gU * uf - gV * vf;
                double b = yf + bU * uf;

                int di = x * 4;
                dstRow[di] = (byte)ClampTo8Bit(b);
                dstRow[di + 1] = (byte)ClampTo8Bit(g);
                dstRow[di + 2] = (byte)ClampTo8Bit(r);
                dstRow[di + 3] = 255;
            }
        }

        return resource;
    }

    /// <summary>
    /// YUV420P10(三平面、10bit 存于 16-bit 高位) CPU 解包为 BGRA32 的单一收敛点。
    /// </summary>
    /// <remarks>
    /// <para>与 <see cref="CreateP010ToBgraResource"/> 共用同一套 BT.601 全范围系数与
    /// <see cref="Read10Bit"/>（10bit 右移 6 归一），仅平面布局不同：Y/U/V 各自独立平面，
    /// 色度按 2x2 子采样。ffmpeg 软解 10-bit HEVC 主流出帧即为此布局，故这是「10-bit 上屏」
    /// 真正会被触发的主路径。</para>
    /// <para>渲染器永远只消费 BGRA32（资源 Format 已为 BGRA32），三渲染器 + Skia 均免改。</para>
    /// </remarks>
    private static unsafe SoftwareFrameResource CreateYuv420P10ToBgraResource(
        AVFrame* avFrame, int width, int height)
    {
        bool bigEndian = (AVPixelFormat)avFrame->format == AVPixelFormat.AV_PIX_FMT_YUV420P10BE;

        int bgraBufSize = width * height * 4;
        var resource = new SoftwareFrameResource(width, height, PixelFormat.BGRA32, bgraBufSize);

        byte* yPlane = avFrame->data[0];
        byte* uPlane = avFrame->data[1];
        byte* vPlane = avFrame->data[2];
        int yStride = avFrame->linesize[0];
        int uStride = avFrame->linesize[1];
        int vStride = avFrame->linesize[2];
        if (yPlane == null || uPlane == null || vPlane == null
            || yStride <= 0 || uStride <= 0 || vStride <= 0)
            throw new InvalidOperationException("YUV420P10 帧平面/步幅无效");

        using var pin = resource.Data.Pin();
        byte* dst = (byte*)pin.Pointer;

        // BT.601 全范围系数（与 P010 / NV12 着色器 / Skia LUT 完全一致，色彩不漂移）
        const double rV = 1.402;
        const double gU = 0.344136;
        const double gV = 0.714136;
        const double bU = 1.772;

        for (int y = 0; y < height; y++)
        {
            byte* yRow = yPlane + (nint)y * yStride;
            int cy = y >> 1;
            byte* uRow = uPlane + (nint)cy * uStride;
            byte* vRow = vPlane + (nint)cy * vStride;
            byte* dstRow = dst + (nint)y * (width * 4);
            for (int x = 0; x < width; x++)
            {
                int yVal = Read10Bit(yRow, x, bigEndian);
                int uVal = Read10Bit(uRow, x >> 1, bigEndian);
                int vVal = Read10Bit(vRow, x >> 1, bigEndian);

                double yf = yVal / 1023.0;
                double uf = uVal / 1023.0 - 0.5;
                double vf = vVal / 1023.0 - 0.5;

                double r = yf + rV * vf;
                double g = yf - gU * uf - gV * vf;
                double b = yf + bU * uf;

                int di = x * 4;
                dstRow[di] = (byte)ClampTo8Bit(b);
                dstRow[di + 1] = (byte)ClampTo8Bit(g);
                dstRow[di + 2] = (byte)ClampTo8Bit(r);
                dstRow[di + 3] = 255;
            }
        }

        return resource;
    }

    /// <summary>从 16-bit 半平面读出第 <paramref name="sampleIndex"/> 个样本的 10-bit 值(0..1023)。</summary>
    /// <remarks>
    /// 本机 ffmpeg 将 10-bit 值<b>右对齐</b>存入 16-bit 字（值位于低 10 位，高 6 位填零），
    /// 故直接取低 10 位即可，不要做 <c>raw &gt;&gt; 6</c>（那会把中性色度 512 错读成 8，导致满屏绿）。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int Read10Bit(byte* plane, int sampleIndex, bool bigEndian)
    {
        byte* p = plane + (nint)sampleIndex * 2;
        ushort raw = bigEndian
            ? (ushort)((p[0] << 8) | p[1])
            : (ushort)((p[1] << 8) | p[0]);
        return raw & 0x3FF; // 10-bit 右对齐：值位于低 10 位 => 0..1023
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampTo8Bit(double v)
    {
        int i = (int)(v * 255.0 + 0.5);
        return i < 0 ? 0 : i > 255 ? 255 : i;
    }

    // ── D3D11VA 硬件解码 ──

    /// <summary>
    /// 初始化 D3D11VA 硬件解码设备上下文（使用渲染器共享的 D3D11 设备，实现零拷贝）。
    /// </summary>
    /// <remarks>
    /// <para>同步操作（sync 分类）：FFmpeg hwdevice_ctx API 和 COM 操作均为同步原生调用，无 I/O await。</para>
    /// <para>共享设备零拷贝链路：渲染器 ID3D11Device → FFmpeg D3D11VA → 硬解纹理 → D3D11Renderer CopySubresourceRegion。</para>
    /// <para><b>引用计数所有权</b>：FFmpeg 在销毁 AVHWDeviceContext 时<b>必定</b> Release
    /// <c>device</c> 与 <c>device_context</c>（官方文档明示，与是否用户提供无关）。因本方法借用的是
    /// 渲染器工厂持有的共享设备，故写入 hwctx 前对两者各 <c>Marshal.AddRef</c> 一次；否则工厂
    /// Dispose 时将在已销毁对象上 Release，产生确定性 AccessViolation。详见方法内注释。</para>
    /// </remarks>
    /// <param name="ctx">FFmpeg 编解码上下文（设置其 hw_device_ctx 字段）。</param>
    private unsafe void InitializeD3D11VA(AVCodecContext* ctx)
    {
        var gpuCtx = _gpuContext!;
        if (gpuCtx.DeviceHandle == IntPtr.Zero)
            throw new InvalidOperationException("GPU 设备句柄无效");
        if (gpuCtx.ContextHandle == IntPtr.Zero)
            throw new InvalidOperationException("GPU 设备上下文句柄无效");

        // 1. 分配 D3D11VA 硬件设备上下文
        AVBufferRef* hwRef = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (hwRef == null)
            throw new InvalidOperationException("av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA) 返回 null");

        try
        {
            // 2. 设置共享 D3D11 设备（通过原始指针操作 AVD3D11VADeviceContext）
            // AVBufferRef.data → AVHWDeviceContext → hwctx → AVD3D11VADeviceContext
            // AVD3D11VADeviceContext 布局：device(void*), device_context(void*), lock_ctx(uint), lock(fn*), unlock(fn*)
            AVHWDeviceContext* hwCtx = (AVHWDeviceContext*)hwRef->data;
            IntPtr* hwctxPtrs = (IntPtr*)hwCtx->hwctx;

            // FFmpeg 所有权契约（hwcontext_d3d11va.h 官方文档，device / device_context 两字段逐字相同）：
            //   "Deallocating the AVHWDeviceContext will always release this interface,
            //    and it does not matter whether it was user-allocated."
            //   即 av_buffer_unref 引用归零时 d3d11va_device_free() 会对 device 与 device_context
            //   各调用一次 Release —— 无论指针是不是用户塞进来的。
            //
            //   这里塞的是渲染器工厂（D3D11RendererFactory，Singleton）持有的共享设备，工厂自己
            //   在 Dispose 时还要 Release 一次。若不补 AddRef，ffmpeg 那次 Release 会吃掉工厂的
            //   那一份引用 ⇒ 设备/上下文提前销毁 ⇒ 工厂 Dispose 时在已销毁对象上 Release ⇒
            //   确定性 AccessViolation（SharpGen.Runtime.ComObject.Release → CppObject.get_Item）。
            //   故必须在写入 hwctx 前各 AddRef 一次，为 ffmpeg「借出」一份它有权释放的引用。
            //
            //   配对性：AddRef 紧贴写入（其间无抛出点）。此后任何失败路径都走 catch 内的
            //   av_buffer_unref(hwRef) → free 回调 Release 两者，与本处 AddRef 精确配对，不泄漏、不多释放。
            //   video_device / video_context 由 ffmpeg 在 init 内自行 QI（自带 AddRef），无需我方干预。
            Marshal.AddRef(gpuCtx.DeviceHandle);
            Marshal.AddRef(gpuCtx.ContextHandle);

            hwctxPtrs[0] = gpuCtx.DeviceHandle;    // device (ID3D11Device*)
            hwctxPtrs[1] = gpuCtx.ContextHandle;    // device_context (ID3D11DeviceContext*)

            // 3. 初始化设备上下文（FFmpeg 检测 device 已设 → 使用共享设备，创建默认 lock/unlock）
            int ret = ffmpeg.av_hwdevice_ctx_init(hwRef);
            if (ret < 0)
                throw new InvalidOperationException($"av_hwdevice_ctx_init 失败: {GetErrorString(ret)} (code={ret})");

            // 4. 设置到编解码上下文（av_buffer_ref 增加引用计数）
            AVBufferRef* ctxRef = ffmpeg.av_buffer_ref(hwRef);
            if (ctxRef == null)
                throw new InvalidOperationException("av_buffer_ref 返回 null（内存不足）");
            ctx->hw_device_ctx = ctxRef;

            // 5. 创建 SafeHandle 管理 hwRef 生命周期（在 try 内——OOM 时 catch 可释放）
            _hwDeviceCtx = new SafeAVBufferRefHandle((IntPtr)hwRef);
        }
        catch
        {
            // 失败时释放 hwRef（av_buffer_unref 通过 SafeHandle 在 Dispose 中处理）
            AVBufferRef* p = hwRef;
            ffmpeg.av_buffer_unref(&p);
            throw;
        }

        _logger.LogInformation("D3D11VA 硬件解码已初始化（共享 D3D11 设备）");
    }

    /// <summary>
    /// 从 D3D11VA 硬解输出的 AVFrame 创建 VideoFrame（零拷贝 GPU 纹理路径）。
    /// </summary>
    /// <remarks>
    /// <para>D3D11VA 帧布局：data[0] = ID3D11Texture2D*（纹理数组），data[1] = 纹理数组索引。</para>
    /// <para>输出 PixelFormat.NV12（D3D11VA 标准输出格式）。</para>
    /// <para><b>切片保活</b>：必须 <c>av_frame_clone</c> 持有 <c>buf[0]</c> 引用，
    /// 否则调用方 <c>DecodeCore</c> 的 <c>finally { av_frame_free }</c> 会让切片立即回池，
    /// 解码器随后把新图像写进同一切片 ⇒ 渲染时拷到错帧（画面抽帧后跳场景）。
    /// 与软解 BGRA 零拷贝路径、MediaCodec 表面路径保持同一所有权模型。</para>
    /// <para>同步操作（hot 路径）：av_frame_clone（仅引用计数，不拷贝像素）+ COM AddRef + 对象构造，无 I/O。</para>
    /// </remarks>
    private unsafe VideoFrame CreateHardwareFrameFromAVFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;

        TimeSpan timestamp = avFrame->pts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        TimeSpan duration = avFrame->duration > 0
            ? TimeSpan.FromTicks((long)(avFrame->duration * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        bool keyFrame = (avFrame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;

        // GPU 零拷贝：D3D11VA 纹理 → DXGI 共享句柄 → 渲染器生产者（Vulkan/OpenGL）导入为 GPU 纹理上屏。
        if (_gpuImportMode && _gpuProducer is not null)
            return CreateGpuImportFrame(avFrame, width, height, timestamp, duration, keyFrame);

        // D3D11 零拷贝（默认 D3D11 渲染器路径）
        // D3D11VA 帧：data[0] = ID3D11Texture2D*，data[1] = 纹理数组索引
        IntPtr texturePtr = (IntPtr)avFrame->data[0];
        int subresourceIndex = (int)(IntPtr)avFrame->data[1];

        if (texturePtr == IntPtr.Zero)
            throw new InvalidOperationException("D3D11VA 帧纹理指针为空");

        // 切片保活：av_frame_clone = av_frame_alloc + av_frame_ref，对 buf[0]（池内切片）引用计数 +1。
        //    不拷贝任何显存，纯引用计数操作，零拷贝语义不变。
        AVFrame* clone = ffmpeg.av_frame_clone(avFrame);
        if (clone == null)
            throw new InvalidOperationException("av_frame_clone 失败（D3D11VA 硬解帧，内存不足）");

        var frameOwner = new SafeAVFrameHandle((IntPtr)clone);

        // D3D11VA 标准输出 NV12 格式（构造失败时由构造函数负责释放 frameOwner，不泄漏）
        var resource = new D3D11HardwareFrameResource(
            texturePtr, width, height, PixelFormat.NV12, subresourceIndex, frameOwner);

        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, PixelFormat.NV12, resource, timestamp, duration: duration, keyFrame);
        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
        return frame;
    }

    /// <summary>
    /// GPU 零拷贝帧创建：把 D3D11VA 硬解纹理经 DXGI 共享句柄导入为渲染器 GPU 纹理（<see cref="IGpuTextureResource"/>）。
    /// </summary>
    /// <remarks>
    /// <para>解码侧先经 GPUShare.D3D11 的 D3D11Nv12ToRgbaConverter 把 NV12 硬解帧 GPU 转 RGBA32
    /// （VideoProcessorBlt，无 CPU 回读），再以 DXGI 共享 NT 句柄交给渲染器生产者导入；三渲染器统一收 RGBA 走零拷贝。
    /// 单纹理、ArrayLayers=1（转换器产出即 RGBA 单平面）。</para>
    /// <para>导入失败（扩展不可用 / 句柄无效 / 切片不兼容）→ 回落 CPU 传输（<see cref="TransferHardwareFrameToCpu"/>），
    /// 计入 [FFMPEG-FRAMEPATH] CPU 拷贝，绝不报"零拷贝已生效"假绿（S_OK≠被接受）。</para>
    /// </remarks>
    private unsafe VideoFrame CreateGpuImportFrame(
        AVFrame* avFrame, int width, int height, TimeSpan timestamp, TimeSpan duration, bool keyFrame)
    {
        IntPtr texturePtr = (IntPtr)avFrame->data[0];
        int subresourceIndex = (int)(IntPtr)avFrame->data[1];

        // GPU 零拷贝（解码侧 NV12→RGBA）：D3D11VA 硬解帧经 VideoProcessorBlt 转 RGBA32，
        // 三渲染器（Vulkan/GL/D3D11）统一收 RGBA 走零拷贝，避开 NV12 双平面不可移植采样。
        if (texturePtr != IntPtr.Zero && _nv12ToRgbaConverter is not null)
        {
            try
            {
                if (_nv12ToRgbaConverter.TryConvert(texturePtr, subresourceIndex, width, height,
                        out var rgbaHandle, out var rgbaTexture)
                    && rgbaTexture is not null)
                {
                    var source = new GpuFrameImportSource
                    {
                        Kind = GpuFrameImportKind.D3D11SharedHandle,
                        Handle = rgbaHandle,
                        Width = width,
                        Height = height,
                        Format = PixelFormat.RGBA32,
                        SubresourceIndex = 0,
                        ArrayLayers = 1,
                    };
                    // 把共享句柄交给生产者：TryImport 调用即把 rgbaHandle 所有权转移给生产者；
                    // 无论导入成功或失败，生产者均负责 CloseHandle，本解码器不关，避免双关。
                    if (_gpuProducer!.TryImport(source, out var tex) && tex is not null)
                    {
                        // 共享引用已转移给渲染器；释放解码侧 RGBA 纹理包装（底层资源由共享句柄保活）。
                        rgbaTexture.Dispose();
                        var frame = _framePool?.Rent() ?? new VideoFrame();
                        frame.Reset(width, height, PixelFormat.RGBA32, tex, timestamp, duration, keyFrame);
                        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
                        return frame;
                    }

                    _logger.LogWarning(
                        "GPU 零拷贝导入未接受（S_OK≠被接受：行为副作用未成立），本帧回落 CPU 传输。");
                    rgbaTexture.Dispose();
                }
                else
                {
                    _logger.LogWarning(
                        "NV12→RGBA GPU 转换未成功，本帧回落 CPU 传输。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "GPU 零拷贝转换/导入异常（S_OK≠被接受），本帧回落 CPU 传输。");
            }
        }

        // 回落：硬解帧 → CPU（av_hwframe_transfer_data），计入 CPU 拷贝。
        System.Threading.Interlocked.Increment(ref _cpuFallbackFrames);
        return TransferHardwareFrameToCpu(avFrame);
    }

    /// <summary>
    /// GPU 零拷贝导入失败时的稳健回落：把 D3D11VA 硬解帧经 <c>av_hwframe_transfer_data</c> 传输到 CPU 像素帧。
    /// </summary>
    /// <remarks>同步原生调用（无 I/O），属于 CPU 拷贝路径；NV12 走 CreateCopyResource 拷贝，释放临时帧安全。</remarks>
    private unsafe VideoFrame TransferHardwareFrameToCpu(AVFrame* avFrame)
    {
        AVFrame* sw = ffmpeg.av_frame_alloc();
        if (sw == null)
            throw new InvalidOperationException("av_frame_alloc 失败（GPU 零拷贝回落 CPU）");
        try
        {
            int ret = ffmpeg.av_hwframe_transfer_data(sw, avFrame, 0);
            if (ret < 0)
                throw new InvalidOperationException($"av_hwframe_transfer_data 失败: {GetErrorString(ret)}");
            return CreateVideoFrameFromAVFrame(sw);
        }
        finally
        {
            ffmpeg.av_frame_free(&sw);
        }
    }

    // ── Android MediaCodec 硬件解码 ──

    /// <summary>
    /// 初始化 MediaCodec 硬件设备上下文（宿主注入 Surface → 表面直渲染；未注入 → 缓冲模式）。
    /// </summary>
    /// <remarks>
    /// <para>同步操作（sync 分类）：FFmpeg hwdevice_ctx API 均为同步原生调用，无 I/O await。</para>
    /// <para>FFmpeg.AutoGen 8.1.0 无 <c>av_mediacodec_alloc_context</c> 包装（反射探针核验）→
    /// 走通用 <c>av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_MEDIACODEC)</c> + 原始指针设置
    /// <c>AVMediaCodecDeviceContext</c>（与 D3D11VA 同款手法）。</para>
    /// <para>AVMediaCodecDeviceContext 布局（FFmpeg 8 hwcontext_mediacodec.h）：
    /// <c>surface(void*)</c>, <c>native_window(void*)</c>, <c>create_window(int)</c>。</para>
    /// <para><b>前置条件</b>：宿主须已调用 <see cref="MediaCodecInterop.SetJavaVM"/> 注入 JavaVM。</para>
    /// </remarks>
    /// <param name="ctx">FFmpeg 编解码上下文（设置其 hw_device_ctx 字段）。</param>
    private unsafe void InitializeMediaCodec(AVCodecContext* ctx)
    {
        IntPtr surface = _options?.MediaCodecSurface ?? IntPtr.Zero;
        IntPtr nativeWindow = _options?.MediaCodecNativeWindow ?? IntPtr.Zero;

        // 1. 分配 MediaCodec 硬件设备上下文
        AVBufferRef* hwRef = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC);
        if (hwRef == null)
            throw new InvalidOperationException("av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_MEDIACODEC) 返回 null");

        try
        {
            // 2. 设置宿主注入的 Surface / ANativeWindow（Surface 优先；均为空 = 缓冲模式）
            AVHWDeviceContext* hwCtx = (AVHWDeviceContext*)hwRef->data;
            IntPtr* mcCtx = (IntPtr*)hwCtx->hwctx; // AVMediaCodecDeviceContext
            if (surface != IntPtr.Zero)
                mcCtx[0] = surface;        // surface (android/view/Surface jobject 全局引用)
            else if (nativeWindow != IntPtr.Zero)
                mcCtx[1] = nativeWindow;   // native_window (ANativeWindow*)

            // 3. 初始化设备上下文
            int ret = ffmpeg.av_hwdevice_ctx_init(hwRef);
            if (ret < 0)
                throw new InvalidOperationException($"av_hwdevice_ctx_init(MEDIACODEC) 失败: {GetErrorString(ret)} (code={ret})");

            // 4. 设置到编解码上下文（av_buffer_ref 增加引用计数）
            AVBufferRef* ctxRef = ffmpeg.av_buffer_ref(hwRef);
            if (ctxRef == null)
                throw new InvalidOperationException("av_buffer_ref 返回 null（内存不足）");
            ctx->hw_device_ctx = ctxRef;

            // 5. SafeHandle 管理 hwRef 生命周期
            _hwDeviceCtx = new SafeAVBufferRefHandle((IntPtr)hwRef);
        }
        catch
        {
            AVBufferRef* p = hwRef;
            ffmpeg.av_buffer_unref(&p);
            throw;
        }

        _logger.LogInformation("MediaCodec 硬件解码已初始化（{Mode}）",
            surface != IntPtr.Zero || nativeWindow != IntPtr.Zero ? "表面直渲染" : "缓冲模式");
    }

    /// <summary>
    /// 从 MediaCodec 表面模式输出的 AVFrame 创建 VideoFrame（零拷贝送显路径）。
    /// </summary>
    /// <remarks>
    /// <para>MediaCodec 表面帧布局：<c>data[3]</c> = <c>AVMediaCodecBuffer*</c>，像素驻留 GPU 不可 CPU 访问。</para>
    /// <para>经 <c>av_frame_clone</c>（引用计数）保活缓冲——外层 DecodeCore 的 av_frame_free 不影响克隆帧。</para>
    /// <para>渲染层匹配 <see cref="MediaCodecFrameResource"/> 后调用其 Render() 送显。</para>
    /// <para>同步操作（hot 路径）：引用计数克隆 + 对象构造，无 I/O。</para>
    /// </remarks>
    private unsafe VideoFrame CreateMediaCodecSurfaceFrame(AVFrame* avFrame)
    {
        int width = avFrame->width;
        int height = avFrame->height;

        // 克隆帧（共享引用计数缓冲）保活 AVMediaCodecBuffer
        AVFrame* clone = ffmpeg.av_frame_clone(avFrame);
        if (clone == null)
            throw new InvalidOperationException("av_frame_clone 失败（MediaCodec 表面帧，内存不足）");

        var frameOwner = new SafeAVFrameHandle((IntPtr)clone);
        IntPtr mcBuffer = (IntPtr)clone->data[3]; // AVMediaCodecBuffer*
        if (mcBuffer == IntPtr.Zero)
        {
            frameOwner.Dispose();
            throw new InvalidOperationException("MediaCodec 表面帧 data[3] (AVMediaCodecBuffer) 为空");
        }

        var resource = new MediaCodecFrameResource(mcBuffer, width, height, frameOwner);

        TimeSpan timestamp = avFrame->pts != ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks((long)(avFrame->pts * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        TimeSpan duration = avFrame->duration > 0
            ? TimeSpan.FromTicks((long)(avFrame->duration * _tbSeconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;
        bool keyFrame = (avFrame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0;

        var frame = _framePool?.Rent() ?? new VideoFrame();
        frame.Reset(width, height, PixelFormat.NV12, resource, timestamp, duration, keyFrame);
        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
        return frame;
    }

    /// <summary>获取 FFmpeg MediaCodec 专用解码器名（无对应硬解则返回 null）。</summary>
    private static string? GetMediaCodecDecoderName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264_mediacodec",
        VideoCodec.H265 => "hevc_mediacodec",
        VideoCodec.AV1 => "av1_mediacodec",
        VideoCodec.VP9 => "vp9_mediacodec",
        VideoCodec.MPEG2 => "mpeg2_mediacodec",
        VideoCodec.MPEG4 => "mpeg4_mediacodec",
        _ => null
    };

    private static AVCodecID MapVideoCodecToFFmpeg(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => AVCodecID.AV_CODEC_ID_H264,
        VideoCodec.H265 => AVCodecID.AV_CODEC_ID_HEVC,
        VideoCodec.AV1 => AVCodecID.AV_CODEC_ID_AV1,
        VideoCodec.VP9 => AVCodecID.AV_CODEC_ID_VP9,
        VideoCodec.MPEG2 => AVCodecID.AV_CODEC_ID_MPEG2VIDEO,
        VideoCodec.MPEG4 => AVCodecID.AV_CODEC_ID_MPEG4,
        _ => throw new NotSupportedException($"不支持的视频编解码器: {codec}")
    };

    private static PixelFormat MapPixelFormatFromFFmpeg(AVPixelFormat fmt) => fmt switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P => PixelFormat.YUV420P,
        AVPixelFormat.AV_PIX_FMT_YUV422P => PixelFormat.YUV422P,
        AVPixelFormat.AV_PIX_FMT_YUV444P => PixelFormat.YUV444P,
        AVPixelFormat.AV_PIX_FMT_NV12 => PixelFormat.NV12,
        AVPixelFormat.AV_PIX_FMT_NV21 => PixelFormat.NV21,
        AVPixelFormat.AV_PIX_FMT_P010LE => PixelFormat.P010,
        AVPixelFormat.AV_PIX_FMT_P010BE => PixelFormat.P010,
        AVPixelFormat.AV_PIX_FMT_YUV420P10LE => PixelFormat.YUV420P10,
        AVPixelFormat.AV_PIX_FMT_YUV420P10BE => PixelFormat.YUV420P10,
        AVPixelFormat.AV_PIX_FMT_BGRA => PixelFormat.BGRA32,
        AVPixelFormat.AV_PIX_FMT_RGBA => PixelFormat.RGBA32,
        AVPixelFormat.AV_PIX_FMT_RGB24 => PixelFormat.RGB24,
        _ => PixelFormat.YUV420P
    };

    private static string GetErrorString(int errorCode)
    {
        unsafe
        {
            byte* buf = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
            ffmpeg.av_strerror(errorCode, buf, ffmpeg.AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error code {errorCode}";
        }
    }
}
