using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
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
internal sealed partial class FFmpegVideoDecoder : IVideoDecoder, IFramePoolAware<VideoFrame>
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
    // Linux VAAPI 硬件解码导出（真实零拷贝路径）：IVaApiExport 由 Platforms.Linux 注册（依赖倒置，后端不引用 Platforms）。
    private IVaApiExport? _vaApiExport;
    // VAAPI 零拷贝会话级锁定：首次导出/导入失败即置位（环境不支持导入），后续帧直接 CPU 传输。
    // 环境级能力而非时间线状态，Reset() 重建不复位。
    private bool _vaZeroCopyUnavailable;
    private nint _vaDisplay; // VAAPI VADisplay（VA Surface → dma_buf 导出所需）
    // D3D11VA NV12 硬解帧 → RGBA32 的 GPU 转换器（位于中性互操作模块 LingFan.Media.GPUShare.D3D11）。
    // 仅 GPU 零拷贝路径（Windows）使用；其持有的共享设备包装不 Dispose（见转换器注释）。
    private LingFan.Media.GPUShare.D3D11.D3D11Nv12ToRgbaConverter? _nv12ToRgbaConverter;
    // NV12→RGBA 转换失败详情已打印标志（仅首帧打印完整异常，后续静默防刷屏；重播时重置）。
    private bool _nv12ConvertFailureLogged;
    // D3D11VA 所用 D3D11 设备（渲染器共享设备场景 / ffmpeg 自有设备场景）。
    // ffmpeg 自有设备零拷贝：渲染器为 GL/Vulkan 时，ffmpeg 须拥有【独立 D3D11 设备】做 D3D11VA
    // （GL/VK 的 IGpuDeviceContext.DeviceHandle 是 GL/Vk 设备，非 ID3D11Device，绝不可喂给 D3D11VA），
    // 经 NV12→RGBA 转换 + 共享句柄导出，再由 GL/VK 生产者导入零拷贝。
    private IntPtr _vaDeviceHandle;
    private IntPtr _vaContextHandle;
    private Vortice.Direct3D11.ID3D11Device? _vaOwnedDevice;        // 仅 ffmpeg 自有设备场景（非 D3D11 渲染器）时非空（ffmpeg 自有，须我方 Dispose）
    private Vortice.Direct3D11.ID3D11DeviceContext? _vaOwnedContext;
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

    // 帧路径统计：「硬解激活=True」只证明解码器跑在 GPU 上，不证明出帧也是 GPU 纹理。
    // 若硬件帧被下载回系统内存（hwframe transfer / sws 转换），硬件就成了摆设。
    // 这两个计数器在 Dispose 时打印，作为「全程零拷贝」的日志佐证。
    private long _gpuZeroCopyFrames;  // D3D11VA / MediaCodec Surface 零拷贝帧（GPU 纹理直出）
    private long _cpuFallbackFrames;  // 软解 / CPU 内存帧
    private bool _frameSummaryLogged;

    /// <summary>FFmpeg EAGAIN 错误码（跨平台）。必须用 FF.AVERROR(FF.EAGAIN) 计算，
    /// 禁止硬编码 -11（Windows 正确，但 macOS/iOS 的 EAGAIN=35，会误判"需要更多数据"为解码失败）。</summary>
    private static readonly int EAGAIN = FF.AVERROR(FF.EAGAIN);

    /// <summary>
    /// 初始化 <see cref="FFmpegVideoDecoder"/> 的新实例。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="gpuContext">可选 GPU 设备上下文（D3D11VA 硬解需要，null=软件解码）。</param>
    /// <param name="options">可选 FFmpeg 配置（含 MediaCodec Surface 注入点）。</param>
    /// <param name="frameProducers">可选 GPU 帧生产者集合（按激活渲染器 ApiType 匹配，零拷贝导入用；null=无零拷贝路径）。</param>
    /// <param name="vaApiExport">可选 Linux VAAPI 导出桥（VA Surface → dma_buf；null=非 VAAPI 平台）。</param>
    public FFmpegVideoDecoder(ILogger<FFmpegVideoDecoder> logger, IGpuDeviceContext? gpuContext = null, FFmpegOptions? options = null, IEnumerable<IGpuFrameProducer>? frameProducers = null, IVaApiExport? vaApiExport = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _gpuContext = gpuContext;
        _options = options;
        _frameProducers = frameProducers;
        _vaApiExport = vaApiExport;
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

    /// <inheritdoc/>
    public void SetFramePool(IFramePool<VideoFrame>? pool)
    {
        _framePool = pool;
    }

    // 辅助方法

    // D3D11VA 硬件解码

    // Linux VAAPI 硬件解码（真实零拷贝路径）

    // Android MediaCodec 硬件解码

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
            byte* buf = stackalloc byte[FF.AV_ERROR_MAX_STRING_SIZE];
            FF.av_strerror(errorCode, buf, (UIntPtr)FF.AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"error code {errorCode}";
        }
    }
}
