using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Android.Views;
using LingFan.Media.GPUShare.Android; // AndroidHardwareBufferFrameResource（跨 GPU API 中立帧 DTO，消费方向：Backends→GPUShare.Android）
using LingFan.Media.GPUShare.Android.Bridge; // AndroidAhbRgbaBridge（解码侧 GLES/EGL→AHB 桥接，GPUShare.Android android 资产）
// 本后端命名空间段为 ...MediaCodec，会遮蔽类型 Android.Media.MediaCodec → 用不撞名的别名。
using AndroidMediaCodec = Android.Media.MediaCodec;
// Android.Graphics.PixelFormat 与 Abstractions 全局冲突 → 别名锁定契约层像素格式。
using PixelFormat = LingFan.Media.Abstractions.PixelFormat;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 基于托管 <see cref="AndroidMediaCodec"/> 的视频解码器，双路径：
/// <b>1) GLES 桥接零拷贝（默认优先）</b>：解码器输出到 <see cref="AndroidAhbRgbaBridge"/> 的 SurfaceTexture，
/// 驱动在 GPU 内把 YUV→RGB 渲进 RGBA AHardwareBuffer，Vulkan 渲染器以普通 RGBA 纹理采样上屏
/// （绕开 Adreno 对「YUV AHB + Vulkan YCbCr 采样」的原生空指针崩溃）；
/// <b>2) ByteBuffer + 灵活 YUV420 回退</b>：桥接不可用（API&lt;29 或 EGL/GLES 初始化失败）时，
/// 经 <c>getOutputImage</c> 取标准化三平面 I420 走 CPU。两条路径均走 net-android 托管绑定，
/// 仅桥接的 EGL/GLES/AHardwareBuffer 图形原语经 [LibraryImport]（Android 后端
/// 媒体 API 走托管绑定，仅图形原语例外，与解码器既有 carve-out 一致）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：<see cref="DecodeAsync"/> / <see cref="FlushAsync"/> 为热路径，内部同步托管调用，
/// 返回 <see cref="ValueTask.FromResult{TResult}"/>（与 FFmpegVideoDecoder 同构）。<see cref="Initialize"/> 为同步初始化。</para>
/// <para><b>单一路径：ByteBuffer + 灵活 YUV420</b>：解码器以 <c>COLOR_FormatYUV420Flexible</c> 配置，输出经
/// <see cref="AndroidMediaCodec.GetOutputImage"/> 取 <see cref="Image"/>（YUV_420_888），由托管
/// <see cref="Image.Plane"/> 的 <see cref="Java.Nio.ByteBuffer"/> 经 CPU 提取标准紧凑 I420。平台契约（MediaCodec「原始视频缓冲区」节，
/// AOSP 原文经 MS Learn/Xamarin 转载）明文：灵活 YUV 缓冲既可用于 Surface，也可用于 ByteBuffer 经 getOutputImage 访问，
/// 且自 LOLLIPOP_MR1 起所有视频编解码器均支持——故本路径对硬件与软件解码器同等有效，平面语义标准化
/// （plane0=Y、plane1=U、plane2=V），无需按厂商私有 NV12/NV21 布局猜测。</para>
/// <para><b>为何不走 ImageReader(Surface)+CPU 读</b>：Surface 原生输出是不透明 COLOR_FormatSurface，平台只承诺
/// 可用于呈现/GL 采样，从不承诺能被 CPU 按 YUV_420_888 正确读出（色度是否落盘取决于 gralloc 用途位与厂商实现）。
/// 实测即命中该缺口：V 平面恒≈0 → 画面泛绿。GPU 零拷贝的正确形态是 SurfaceTexture→GL 在 GPU 内把
/// YUV→RGB 渲进 RGBA AHardwareBuffer，再交 Vulkan 以普通 RGBA 采样（<see cref="AndroidAhbRgbaBridge"/> 路径），
/// 已实现为本解码器默认优先的零拷贝路径，不可用时回退本 ByteBuffer CPU 路径。</para>
/// <para><b>可见区</b>：帧由 <see cref="ExtractI420FromImage"/> 按 <see cref="Image.CropRect"/>（= 输出格式
/// crop-* 矩形，.NET 侧已等价解析到 <c>_visibleWidth/_visibleHeight</c>）界定可见像素，跳过 16 对齐填充区；
/// 按 plane 的 pixelStride/rowStride 提取，Y pixelStride 恒为 1，U/V 的 pixelStride 为 1(planar I420) 或
/// 2(semiplanar NV12，U 偶 V 奇)。</para>
/// <para><b>色彩随帧透传</b>：<see cref="ReadOutputFormat"/> 读 <c>KEY_COLOR_STANDARD/RANGE/TRANSFER</c> 填
/// <see cref="VideoColorInfo"/>，随帧交给渲染端选正确的 YUV→RGB 矩阵（治骁龙偏绿）。</para>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
/// <para><b>分部划分</b>：Feed 分部管输入喂入与排空后补喂；Drain 分部管 EOS 两阶段排空；
/// CodecSetup/Output/FrameExtract 分部管解码器创建配置、输出读取与帧提取。
/// 本文件为初始化、解码入口、重排缓冲状态与生命周期。</para>
/// </remarks>
internal sealed partial class AndroidVideoDecoder : IVideoDecoder
{
    private readonly AndroidBackend _backend;
    private readonly ILogger<AndroidVideoDecoder> _logger;

    private AndroidMediaCodec? _codec;
    private MediaFormat? _outputFormat;        // 当前输出格式（FORMAT_CHANGED 时更新）
    private VideoColorInfo _colorInfo;         // 当前输出色彩空间（KEY_COLOR_*，透传渲染端）
    private string _codecName = "unknown";     // 实际选中的解码器组件名（诊断/硬件判定）
    private bool _hardwareDecoder;             // 选中的是否为厂商硬件解码器

    // 输出可见区（由输出格式 crop-* 键给出；缺失时退回 width/height 全帧）。
    // 平台契约：width = crop-right + 1 - crop-left，height = crop-bottom + 1 - crop-top。
    private int _cropLeft, _cropTop, _visibleWidth, _visibleHeight;
    private int _rotationDegrees; // 显示旋转（度，0/90/180/270），由输出格式 KEY_ROTATION 解析

    // ByteBuffer + 灵活 YUV420 单一路径：经托管 Image.Plane CPU 提取产标准紧凑 I420；
    // 无 GPU 零拷贝依赖，无手写 P/Invoke。

    // GLES 桥接零拷贝路径（增强档，经 AndroidVideoDecodePolicy.EnableHardwareZeroCopy 启用）：解码器把帧
    // 渲进 AndroidAhbRgbaBridge 的 SurfaceTexture，桥接在 GPU 内把 YUV→RGB 渲进 RGBA AHardwareBuffer；
    // 本解码器仅持有 Surface 与桥接引用，不引入任何图形 API 绑定（EGL/GLES 封装在 AndroidAhbRgbaBridge 内）。
    // 桥接不可用或开关未开时 _useAhbFrames=false，走 ByteBuffer CPU 路径（软解软帧，配合渲染端 GPU 上载），
    // 两条路径互不影响。
    private AndroidAhbRgbaBridge? _bridge;
    private Surface? _outputSurface;
    private bool _useAhbFrames;
    private bool _pendingSurfaceTextureFrame; // SurfaceTexture 已入待闩帧（等 ConvertLatest 消费）
    private long _lastPresentationTimeUs = -1; // 最近一次经 Surface 渲出的帧 PTS（us）

    // UV 行读取越界警告一次性标志（稳态帧才会残留上一帧数据，首帧诊断覆盖不到，故需逐帧检测）
    private bool _uvClampWarned;

    // 诊断节流：收包/产帧计数
    private int _packetsFed;
    private int _framesProduced;
    // pts 单调性哨兵：MediaCodec 契约要求按呈现序输出；若实际按解码序（B 帧重排）吐帧，
    // 队首会变成远未来的 pts，管线据此等待而堵死后续所有帧（队头阻塞），集体超时后被丢。
    private long _lastOutputPtsUs = -1;
    private long _ptsRegressions;
    private const int LogInterval = 64;

    private VideoCodec _codecType = VideoCodec.Unknown;
    private VideoSettings _settings = null!;
    private bool _initialized;
    private bool _disposed;

    // 平面读取复用缓冲（grow-only）：每帧把 Y/U/V plane 的 ByteBuffer 完整拷入此缓冲，
    // 再按 rowStride/pixelStride 索引提取，消除按整帧尺寸反复 new 的 GC 风暴。
    private byte[] _extractRaw = Array.Empty<byte>();

    // 提取阶段性能累计（样本/ ticks），用于定位卡顿瓶颈。
    private int _extractSamples;
    private long _extractTicks;

    // 帧几何变化探测器
    // 每帧的可见区（crop）与平面跨距（rowStride/pixelStride）都是从 Image 现读的，未做缓存。
    // 若 OMX.qcom 在开播期回报的几何与稳态不同（已知部分高通组件早期帧 plane 元数据不稳定），
    // 本实现会原样继承错误 —— 症状正是「开播十来秒花屏、之后完全正常」。
    // 因此逐帧比对几何，任何一项变化都打日志（含帧序号与 PTS），用于一锤定音。
    private int _geoVw, _geoVh, _geoCl, _geoCt, _geoYRow, _geoUvRow, _geoUvPix;
    private bool _geoInit;
    private int _geoChanges;

    // MediaCodec dequeue 返回码 / flags 位（公开 AOSP 值）。
    private const int InfoTryAgainLater = -1;
    private const int InfoOutputFormatChanged = -2;
    private const int InfoOutputBuffersChanged = -3;
    private const int FlagKeyFrame = 1;
    private const int FlagEndOfStream = 4;

    // 本后端媒体 API 仅使用 net-android 托管的 Android.Media.* 绑定（MediaCodec / Image.Plane）；
    // 显式禁止手写 P/Invoke：Android/iOS/macOS 走 net-* workload 内置绑定，AOT 安全、零反射
    // （架构约束）。GPU 零拷贝的图形原语（EGL/GLES/AHardwareBuffer）属 carve-out，
    // 经 [LibraryImport] 封装在 AndroidAhbRgbaBridge 内（与解码器既有 carve-out 一致），后端主体仍零 P/Invoke。

    public AndroidVideoDecoder(AndroidBackend backend, ILogger<AndroidVideoDecoder> logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public VideoCodec Codec => _codecType;

    /// <inheritdoc/>
    /// <remarks>反映实际选中的解码器组件：厂商硬件解码器为真，AOSP 软件解码器（c2.android.* /
    /// OMX.google.*）为假。输出经 CPU 平面提取不改变解码本身是否由硬件完成。</remarks>
    public bool IsHardwareAccelerated => _hardwareDecoder;

    /// <inheritdoc/>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("AndroidVideoDecoder 已初始化，不可重复 Initialize。");

        if (!OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException(
                "Android 视频解码器仅支持 Android 运行时。请使用 FFmpeg 作为跨平台后端。");

        string? mime = AndroidCodecMaps.VideoCodecToMime(codec);
        if (mime is null)
            throw new NotSupportedException($"Android MediaCodec 不支持的视频编解码器: {codec}");

        _codecType = codec;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // 编码尺寸：软解路径不向 configure 强塞 width/height——由解码器从 csd(SPS) 自行推导真实尺寸
        // （解码器内置解析器才是真值来源；手写 SPS 解析曾有 bug 误报，已废弃）。
        // 真值在其 OutputFormat：解码器回报的 width/height/crop 才是真实可见尺寸，帧经 ExtractI420FromImage
        // 按 plane 的 pixelStride/rowStride 与 CropRect 提取（详见该方法）。
        var csd = settings.CodecConfiguration;
        _logger.LogInformation("[ANDROID-VID] csd({Len}B) hex={Hex}",
            csd.Length, Convert.ToHexString(csd.Span));
        int frameW = settings.Width ?? 0, frameH = settings.Height ?? 0;
        // 【仅诊断】手写 SPS 位流解析不可信（实测存在大幅误判）——真值以解码器 OutputFormat 为准。
        // 绝不可参与 configure 决策（错误尺寸喂硬解会导致 0 帧产出）。
        if (csd.Length > 0 && AndroidCodecMaps.TryParseH264WidthHeight(csd.ToArray(), out int pw, out int ph))
            _logger.LogInformation("[ANDROID-VID] SPS 诊断解析 {W}x{H}（容器声明 {DW}x{DH}；仅供对照，不参与决策）",
                pw, ph, frameW, frameH);

        // 双路径：GLES 桥接零拷贝（Surface 输出 → RGBA AHB，增强档按开关启用）与 ByteBuffer CPU 路径（默认）
        // 平台契约（MediaCodec「原始视频缓冲区」节）明文：灵活 YUV 缓冲（COLOR_FormatYUV420Flexible）
        // 既可用于输入/输出 Surface，也可用于 ByteBuffer 模式经 getOutputImage 访问；且自
        // LOLLIPOP_MR1 起「所有视频编解码器均支持灵活 YUV 4:2:0 缓冲」——即本路径对硬件与软件
        // 解码器同等有效，平面语义标准化（plane0=Y、plane1=U、plane2=V），无需按厂商私有布局猜测。
        //
        // 不采用「输出到 ImageReader(YUV_420_888) 再 CPU 读」：Surface 输出的原生格式是不透明的
        // COLOR_FormatSurface，平台只承诺其可用于呈现/GL 采样，从不承诺该缓冲能被 CPU 按
        // YUV_420_888 语义正确读出（是否可读、色度是否落盘取决于 gralloc 用途位与厂商实现）。
        // 实测即命中该缺口：Y/U 有效而 V 平面恒 ≈0，而色度平面为 0（非 128）正是画面整体
        // 泛绿的成因。GPU 零拷贝的正确形态是 SurfaceTexture/AHardwareBuffer→GL/Vulkan 采样，
        // 而非经 ImageReader 回读 CPU；该形态即本解码器的 GLES 桥接零拷贝路径（AndroidAhbRgbaBridge），
        // 经 AndroidVideoDecodePolicy 启用，不可用时回退到本 ByteBuffer CPU 路径。
        //
        // 解码分档（见 AndroidVideoDecodePolicy）：
        // 1) 硬解 + CPU 帧（能播档，默认）：OMX 硬件解码器 + ByteBuffer CPU 帧 + 渲染端 Skia 软渲。
        //    OMX 走旧 OMX 框架（非 Codec2），无 c2 的 numClientBuffers 僵死；此为实测通过路径。
        // 2) 桥接零拷贝（增强档，EnableHardwareZeroCopy）：软解（c2）+ GLES 桥接 → RGBA AHB。
        //    Vulkan AHB 采样在 Adreno 上驱动崩，待 GL 路线重做。
        // 桥接不可用时回落 1) 硬解 + ByteBuffer（绝不 c2 软解 + ByteBuffer——那是 numClientBuffers 僵死档）。
        bool zeroCopy = AndroidVideoDecodePolicy.EnableHardwareZeroCopy;
        _useAhbFrames = zeroCopy && TryCreateAhbOutputSurface(frameW, frameH);
        // 零拷贝档也用硬解（实测：c2 软解 + Surface 桥在输入正常喂入时仍产帧停滞；
        // 「软解+桥接产帧稳定」系未充分验证的假设）。硬解 + Surface 模式与历史结论一致
        // （「高通硬解 ByteBuffer 输出停滞系误诊，Surface 模式才是正解」）。
        // 单变量对照实验：ForceSoftwareDecoder 只换解码器（AOSP 软解），帧提取/重排/同步/上屏完全不变。
        var codecObj = CreateVideoCodec(mime, codec,
            preferSoftwareDecoder: AndroidVideoDecodePolicy.ForceSoftwareDecoder);
        try
        {
            ConfigureFlexibleYuv(ref codecObj, mime, codec, csd, frameW, frameH, _outputSurface, _useAhbFrames);
            codecObj.Start();

            _outputFormat?.Dispose();
            _outputFormat = codecObj.OutputFormat; // getOutputFormat 无参重载 → OutputFormat 属性
            ReadOutputFormat(_outputFormat);
        }
        catch
        {
            codecObj.Release();
            // 初始化失败：桥接 Surface 未被解码器消费，显式释放避免 EGL/GLES 资源泄漏。
            _bridge?.Dispose();
            _bridge = null;
            _outputSurface = null;
            _useAhbFrames = false;
            throw;
        }

        _codec = codecObj;
        _initialized = true;
        _logger.LogInformation("[ANDROID-VID] 初始化完成: {Codec} → {Mime}, 路径={Path}, 解码器={Name}, 硬件={Hw}, csd长度={CsdLen}",
            codec, mime, _useAhbFrames ? "AHB零拷贝(GLES桥接)" : "ByteBuffer(CPU)", _codecName, _hardwareDecoder,
            settings.CodecConfiguration.Length);
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // 实际初始化已在 Initialize 完成（同步托管调用）。
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (packet is null)
        {
            VideoFrame? drained = ReadOutput();
            FeedAfterDrain(); // 排空后补喂（理由见 FeedAfterDrain 注释）
            return new ValueTask<VideoFrame?>(drained);
        }

        // 诊断节流：收包节奏。
        // Trace 级：解码线程为实时线程，周期性 Information 写 logcat（双 provider）可达数十 ms，
        // 会周期性延迟解码出帧 → 队列下探 → 呈现等帧（卡顿诱因之一），调高日志级别即可查看。
        if ((_packetsFed % LogInterval) == 0)
            _logger.LogTrace("[ANDROID-VID] 收包 #{Count} size={Size} pts={Pts:g} key={Key}",
                _packetsFed, packet.Data.Length, packet.Timestamp, packet.KeyFrame);
        _packetsFed++;

        _pendingInput.Enqueue(packet);
        FeedInput();               // 尽力喂（此时槽位多半仍满）
        VideoFrame? frame = ReadOutput();  // 排空输出 → 输入槽位腾出
        FeedAfterDrain();          // 立刻补喂，否则要等下一个包到来（30 包/秒）
        return new ValueTask<VideoFrame?>(frame);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_codec is null) return;
        _codec.Flush();
        _eosQueued = false;
        _eosOutputSeen = false;
        _uvClampWarned = false;
        _pendingSurfaceTextureFrame = false; // 清空待闩帧标志，避免 Flush 后误闩旧帧
        _lastPresentationTimeUs = -1;
        _lastOutputPtsUs = -1;
        _lastReleasedPts = TimeSpan.MinValue;
        _geoInit = false;      // 几何基线重新采集（Flush 后解码器可能重配输出格式）
        _geoChanges = 0;
        _releaseGaps = 0;      // Flush 后解码器重建参考链，空洞计数重新开始
        DrainAndDispose(_reorder);
        _reorder.Clear();
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
    }

    // 自适应重排缓冲
    // MediaCodec 契约要求按**呈现序**输出，但部分 OMX 厂商实现实测按**解码序**（B 帧重排）吐帧：
    // pts 回退集中出现在开播初期（I/P/B 交错输出）。
    // 后果：管线只 Peek 队首，队首 pts 远在未来时整队被堵（队头阻塞），主时钟追上后
    // 其后各帧又集体超时被丢 —— 开播期时序错乱/花屏。
    //
    // 关键约束：解码器领先量为负（从不跑在主时钟前面），
    // 所以**不能**用「固定深度重排缓冲」——那会原样变成延迟，被丢帧阈值吃掉。
    // 本实现只在检测到「超前跳变」时才暂存，稳态（pts 连续）逐帧直通，零额外延迟。
    private readonly List<VideoFrame> _reorder = new();
    private TimeSpan _lastReleasedPts = TimeSpan.MinValue;
    /// <summary>超过已释放 pts 多少算「超前跳变」（判定为重排的 P 帧，需等中间 B 帧补齐）。
    /// 取 50ms：大于 30fps 帧间隔，正常连续帧不会触发。</summary>
    private static readonly TimeSpan ReorderHoldAhead = TimeSpan.FromMilliseconds(50);
    /// <summary>重排缓冲深度上限（防失控；超过即强制按序释放）。</summary>
    private const int MaxReorderHold = 8;
    private int _reorderCorrections;
    /// <summary>交付序列的 pts 空洞判定阈值：60ms ≈ 2 个 30fps 帧距。正常连续帧 33ms 不会触发。</summary>
    private static readonly TimeSpan ReleaseGapThreshold = TimeSpan.FromMilliseconds(60);
    /// <summary>交付序列检测到的 pts 空洞数（解码器吞帧的直接证据）。</summary>
    private int _releaseGaps;

    // 帧间差分探测器（坏帧探针）
    // 正常相邻帧的亮度采样平均绝对差（MAD）平稳；参考帧损坏产生的宏块 garbage 会让 MAD 出现尖峰。
    // 用途：区分「帧丢了」（MAD 平稳，只是 pts 跳变）与「解码器输出了坏帧」（MAD 尖峰且持续若干帧）。
    // 分块覆盖全画面：12 行 × 8 段 × 8 字节 = 768 字节，共 96 个块位。
    // 早期版本只采 8 行 × 64 字节（占 Y 平面 0.025%），宏块 garbage 绝大多数落在采样点之外，
    // 产生「假阴性」（画面肉眼花屏但探针报告零突变）。必须按块覆盖整幅画面。
    private const int DeltaRows = 12;
    private const int DeltaCols = 8;
    private const int DeltaBytesPerBlock = 8;
    private const int DeltaSampleLen = DeltaRows * DeltaCols * DeltaBytesPerBlock;
    /// <summary>单块平均绝对差超过该值即计入「坏块」。</summary>
    private const double DeltaBlockBadThreshold = 25.0;
    private readonly byte[] _prevYSample = new byte[DeltaSampleLen];
    private bool _hasPrevSample;
    private double _madBase;
    private int _spikeFrames;

    // 平面提取统一由 ExtractI420FromImage（Image.Plane，按 pixelStride/rowStride + CropRect 可见区）承担，见上方。

    private void EnsureInitialized()
    {
        if (!_initialized || _codec is null)
            throw new InvalidOperationException("AndroidVideoDecoder 尚未 Initialize。");
    }

    private static void DrainAndDispose(Queue<MediaPacket> q)
    {
        while (q.Count > 0) q.Dequeue().Dispose();
    }

    private static void DrainAndDispose(Queue<VideoFrame> q)
    {
        while (q.Count > 0) q.Dequeue().Dispose();
    }

    private static void DrainAndDispose(List<VideoFrame> list)
    {
        foreach (var f in list) f.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
    }

    private void DisposeCore()
    {
        DrainAndDispose(_reorder);
        _reorder.Clear();
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
        // 先释放编解码器，再释放桥接（Surface 须活得比编解码器久；桥接 Dispose 会一并释放其 Surface/SurfaceTexture）。
        _codec?.Release();
        _codec = null;
        _outputFormat?.Dispose();
        _outputFormat = null;
        // 桥接释放会同时 Dispose 其持有的 Surface/SurfaceTexture，故 _outputSurface 仅置空、不再单独释放。
        _bridge?.Dispose();
        _bridge = null;
        _outputSurface = null;
        _pendingSurfaceTextureFrame = false;
        _useAhbFrames = false;

        // 取帧汇总（确证 DrainOutput 是否被调用、各分支分布；含 AHB 零拷贝与 ByteBuffer 回退）
        if (_drainCalls > 0)
        {
            _logger.LogWarning(
                "[ANDROID-VID] 取帧汇总：drainCalls={Calls} dequeue成功={Dq} tryAgain={Ta} 喂入={Fed} 累计产帧={Frames} 解码器={Name} 硬件={Hw} AHB零拷贝={Ahb}",
                _drainCalls, _drainDequeued, _drainTryAgain, _inputQueued, _framesProduced, _codecName, _hardwareDecoder, _useAhbFrames);
        }
    }
}