using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Java.Nio;
// 本后端命名空间段为 ...MediaCodec，会遮蔽类型 Android.Media.MediaCodec → 用不撞名的别名。
using AndroidMediaCodec = Android.Media.MediaCodec;
// Android.Graphics.PixelFormat 与 Abstractions 全局冲突 → 别名锁定契约层像素格式。
using PixelFormat = LingFan.Media.Abstractions.PixelFormat;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 基于托管 <see cref="AndroidMediaCodec"/> 的视频解码器：统一走 ByteBuffer + 灵活 YUV420 单一路径
/// （经 <c>getOutputImage</c> 取标准化三平面 I420），net-android 内置绑定，非手写 P/Invoke。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：<see cref="DecodeAsync"/> / <see cref="FlushAsync"/> 为热路径，内部同步托管调用，
/// 返回 <see cref="ValueTask.FromResult{TResult}"/>（与 FFmpegVideoDecoder 同构）。<see cref="Initialize"/> 为同步初始化。</para>
/// <para><b>单一路径：ByteBuffer + 灵活 YUV420</b>：解码器以 <c>COLOR_FormatYUV420Flexible</c> 配置，输出经
/// <see cref="AndroidMediaCodec.GetOutputImage"/> 取 <see cref="Image"/>（YUV_420_888），由托管
/// <see cref="Image.Plane"/> 的 <see cref="ByteBuffer"/> 经 CPU 提取标准紧凑 I420。平台契约（MediaCodec「原始视频缓冲区」节，
/// AOSP 原文经 MS Learn/Xamarin 转载）明文：灵活 YUV 缓冲既可用于 Surface，也可用于 ByteBuffer 经 getOutputImage 访问，
/// 且自 LOLLIPOP_MR1 起所有视频编解码器均支持——故本路径对硬件与软件解码器同等有效，平面语义标准化
/// （plane0=Y、plane1=U、plane2=V），无需按厂商私有 NV12/NV21 布局猜测。</para>
/// <para><b>为何不走 ImageReader(Surface)+CPU 读</b>：Surface 原生输出是不透明 COLOR_FormatSurface，平台只承诺
/// 可用于呈现/GL 采样，从不承诺能被 CPU 按 YUV_420_888 正确读出（色度是否落盘取决于 gralloc 用途位与厂商实现）。
/// 真机实测即命中该缺口：V 平面恒≈0 → 画面泛绿。GPU 零拷贝的正确形态是 AHardwareBuffer→GL/Vulkan 采样，
/// 另行推进（见设计文档 §5.2），不在本路径内。</para>
/// <para><b>可见区</b>：帧由 <see cref="ExtractI420FromImage"/> 按 <see cref="Image.CropRect"/>（= 输出格式
/// crop-* 矩形，.NET 侧已等价解析到 <c>_visibleWidth/_visibleHeight</c>）界定可见像素，跳过 16 对齐填充区；
/// 按 plane 的 pixelStride/rowStride 提取，Y pixelStride 恒为 1，U/V 的 pixelStride 为 1(planar I420) 或
/// 2(semiplanar NV12，U 偶 V 奇)。</para>
/// <para><b>色彩随帧透传</b>：<see cref="ReadOutputFormat"/> 读 <c>KEY_COLOR_STANDARD/RANGE/TRANSFER</c> 填
/// <see cref="VideoColorInfo"/>，随帧交给渲染端选正确的 YUV→RGB 矩阵（治骁龙偏绿）。</para>
/// <para><b>仅 Android 可用</b>：非 Android 运行时 <see cref="Initialize"/> 抛 <see cref="PlatformNotSupportedException"/>。</para>
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

    private readonly Queue<MediaPacket> _pendingInput = new();
    private readonly Queue<VideoFrame> _pendingFrames = new();

    // 取帧诊断计数（Dispose 汇总，定位零产帧各分支分布）
    private long _drainCalls, _drainDequeued, _drainTryAgain;
    private bool _eosQueued;    // EOS 已入队（FlushAsync 重试语义，Reset 清零）
    private bool _eosOutputSeen;// 解码器已回报输出 EOS（DRAIN 真正完成的判据）

    // 诊断节流：收包/产帧计数
    private int _packetsFed;
    private int _framesProduced;
    private int _inputQueued;              // 实际入队的输入缓冲数（vs 仅收包）
    private long _inputDequeueBlocked;     // dequeueInputBuffer 返回 -1 的次数（输入槽满）
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

    // MediaCodec dequeue 返回码 / flags 位（公开 AOSP 值）。
    private const int InfoTryAgainLater = -1;
    private const int InfoOutputFormatChanged = -2;
    private const int InfoOutputBuffersChanged = -3;
    private const int FlagKeyFrame = 1;
    private const int FlagEndOfStream = 4;

    // 本后端仅使用 net-android 托管的 Android.Media.* 绑定（ImageReader / MediaCodec / Image.Plane）。
    // 显式禁止手写 P/Invoke：Android/iOS/macOS 走 net-* workload 内置绑定，AOT 安全、零反射
    // （符合 2026-08-22 架构裁定）。GPU 零拷贝（AHB→GPU）已暂缓，见设计文档 §5.2。

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
        // （解码器内置解析器才是真值来源；c2 软解对本样例导出 1080x1920 与容器声明一致，
        // 旧「容器失真报 1080x1920、SPS 实为 320x240」结论源于手写 SPS 解析器的 bug，已证伪）。
        // 真值在其 OutputFormat：解码器回报的 width/height/crop 才是真实可见尺寸，帧经 ExtractI420FromImage
        // 按 plane 的 pixelStride/rowStride 与 CropRect 提取（详见该方法）。
        var csd = settings.CodecConfiguration;
        _logger.LogInformation("[ANDROID-VID] csd({Len}B) hex={Hex}",
            csd.Length, Convert.ToHexString(csd.Span));
        int frameW = settings.Width ?? 0, frameH = settings.Height ?? 0;
        // 【仅诊断】手写 SPS 位流解析不可信（实测把 1080x1920 的样例解析成 16x32）——
        // 真值以解码器 OutputFormat 为准（c2 软解对本样例导出 width=1080 height=1920 crop(0,0,1079,1919)，
        // 即容器声明正确）。绝不可参与 configure 决策（旧教训：错误尺寸喂高通硬解 → 0 帧产出）。
        if (csd.Length > 0 && AndroidCodecMaps.TryParseH264WidthHeight(csd.ToArray(), out int pw, out int ph))
            _logger.LogInformation("[ANDROID-VID] SPS 诊断解析 {W}x{H}（容器声明 {DW}x{DH}；仅供对照，不参与决策）",
                pw, ph, frameW, frameH);

        // ── ByteBuffer + 灵活 YUV420 单一路径（平台契约保证的 CPU 输出）──
        // 平台契约（MediaCodec「原始视频缓冲区」节）明文：灵活 YUV 缓冲（COLOR_FormatYUV420Flexible）
        // 既可用于输入/输出 Surface，也可用于 ByteBuffer 模式经 getOutputImage 访问；且自
        // LOLLIPOP_MR1 起「所有视频编解码器均支持灵活 YUV 4:2:0 缓冲」——即本路径对硬件与软件
        // 解码器同等有效，平面语义标准化（plane0=Y、plane1=U、plane2=V），无需按厂商私有布局猜测。
        //
        // 不采用「输出到 ImageReader(YUV_420_888) 再 CPU 读」：Surface 输出的原生格式是不透明的
        // COLOR_FormatSurface，平台只承诺其可用于呈现/GL 采样，从不承诺该缓冲能被 CPU 按
        // YUV_420_888 语义正确读出（是否可读、色度是否落盘取决于 gralloc 用途位与厂商实现）。
        // 真机实测即命中该缺口：Y/U 有效而 V 平面恒 ≈0，而色度平面为 0（非 128）正是画面整体
        // 泛绿的成因。GPU 零拷贝的正确形态是 SurfaceTexture/AHardwareBuffer→GL/Vulkan 采样，
        // 而非经 ImageReader 回读 CPU；该形态另行推进（见设计文档 §5.2），不在本路径内。
        var codecObj = CreateVideoCodec(mime, codec, preferSoftwareDecoder: false);
        try
        {
            ConfigureFlexibleYuv(ref codecObj, mime, codec, csd, frameW, frameH);
            codecObj.Start();

            _outputFormat?.Dispose();
            _outputFormat = codecObj.OutputFormat; // getOutputFormat 无参重载 → OutputFormat 属性
            ReadOutputFormat(_outputFormat);
        }
        catch
        {
            codecObj.Release();
            throw;
        }

        _codec = codecObj;
        _initialized = true;
        _logger.LogInformation("[ANDROID-VID] 初始化完成: {Codec} → {Mime}, 输出像素格式 {Fmt}, 解码器={Name}, 硬件={Hw}, csd长度={CsdLen}",
            codec, mime, PixelFormat.YUV420P, _codecName, _hardwareDecoder,
            settings.CodecConfiguration.Length);
    }

    /// <summary>以「灵活 YUV420 + 显式尺寸」配置 ByteBuffer 输出；被拒时降级到软件解码器重试一次。</summary>
    /// <remarks>显式 width/height 用容器声明值：硬件解码器普遍拒绝 csd-only 配置，而真实可见尺寸
    /// 由输出格式的 crop 矩形给出（见 <see cref="ReadOutputFormat"/>），故此处无需也不应依赖
    /// 手写 SPS 解析（该解析器已证伪，会把 1080x1920 解成 16x32）。</remarks>
    private void ConfigureFlexibleYuv(ref AndroidMediaCodec codecObj, string mime, VideoCodec codec,
        ReadOnlyMemory<byte> csd, int frameW, int frameH)
    {
        try
        {
            codecObj.Configure(BuildFormat(mime, csd, frameW, frameH), null, null, 0);
            return;
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
        {
            _logger.LogWarning("[ANDROID-VID] 解码器 {Name} 配置被拒（{Reason}），降级软件解码器重试",
                _codecName, ex.Message);
        }

        // 硬件解码器拒绝该配置：换软件解码器（c2/OMX 阶梯）重试。
        codecObj.Release();
        codecObj = CreateVideoCodec(mime, codec, preferSoftwareDecoder: true);
        codecObj.Configure(BuildFormat(mime, csd, frameW, frameH), null, null, 0);
    }

    /// <summary>构造 ByteBuffer 输出的输入格式（每次 configure 需新实例：MediaFormat 被 configure 消费后不应复用）。</summary>
    private static MediaFormat BuildFormat(string mime, ReadOnlyMemory<byte> csd, int frameW, int frameH)
    {
        var fmt = new MediaFormat();
        fmt.SetString(MediaFormat.KeyMime, mime);
        if (frameW > 0) fmt.SetInteger(MediaFormat.KeyWidth, frameW);
        if (frameH > 0) fmt.SetInteger(MediaFormat.KeyHeight, frameH);
        // 灵活 YUV420：令 getOutputImage 返回标准化三平面，屏蔽厂商私有 NV12/NV21 布局差异。
        fmt.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
        if (csd.Length > 0)
            fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(csd.ToArray())); // 键 "csd-0"（AOSP KEY_CSD0）
        // 显式输入缓冲上限：解码器按 SPS 推出的 max-input-size 可能过小，大关键帧会被截断喂入 → 码流损坏。
        fmt.SetInteger(MediaFormat.KeyMaxInputSize, 2 << 20);
        return fmt;
    }

    /// <summary>创建视频解码器。默认按 MIME 取系统首选（通常为硬件解码器，实时性最佳）；
    /// <paramref name="preferSoftwareDecoder"/> 为真时走软件阶梯
    /// c2.android.avc.decoder（API 29+）→ OMX.google.h264.decoder（旧机型）→ 按类型任选。</summary>
    /// <remarks>ByteBuffer 输出对硬件解码器同样安全：灵活 YUV420 由平台契约保证全解码器支持，
    /// 且等价于灵活格式的厂商私有格式仍可经 getOutputImage 取到标准化平面。</remarks>
    private AndroidMediaCodec CreateVideoCodec(string mime, VideoCodec codec, bool preferSoftwareDecoder)
    {
        if (preferSoftwareDecoder && codec == VideoCodec.H264)
        {
            foreach (string name in new[] { "c2.android.avc.decoder", "OMX.google.h264.decoder" })
            {
                try
                {
                    var sw = AndroidMediaCodec.CreateByCodecName(name);
                    _codecName = name;
                    _hardwareDecoder = false;
                    return sw;
                }
                catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
                {
                    // 该软解在本机不存在（旧/裁剪机型）：试下一个，最后按类型任选。
                }
            }
        }

        var obj = AndroidMediaCodec.CreateDecoderByType(mime);
        _codecName = obj.Name ?? "unknown";
        // 名称约定判定软/硬（CodecInfo.IsSoftwareOnly 为 API 29+；名称前缀是全版本可用的稳定判据）。
        _hardwareDecoder = !(_codecName.StartsWith("c2.android.", StringComparison.Ordinal)
            || _codecName.StartsWith("OMX.google.", StringComparison.Ordinal));
        return obj;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        // 实际初始化已在 Initialize 完成（同步托管调用）。
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ReadOutputFormat(MediaFormat fmt)
    {
        // 帧由 ExtractI420FromImage 按 plane 的 pixelStride/rowStride 统一产出标准紧凑 I420，
        // 与底层是 NV12/semiplanar 还是 I420/planar 无关（灵活 YUV420 二者皆可能）。

        // ── 可见区（crop 矩形）──
        // 平台契约：输出格式的 width/height 是「视频帧」尺寸（常按 16 对齐做了填充），真正的可见图像
        // 只占其中一部分，由 crop 矩形界定，且右/下坐标是「减 1」语义：
        //   width  = crop-right  + 1 - crop-left
        //   height = crop-bottom + 1 - crop-top
        // 键不存在时视频占满整帧。常量 KEY_CROP_* 直到 API 33 才公开，但字符串键自 Lollipop 起即有效，
        // 故统一用字符串键读取以覆盖全版本。
        int fw = fmt.ContainsKey(MediaFormat.KeyWidth) ? fmt.GetInteger(MediaFormat.KeyWidth) : 0;
        int fh = fmt.ContainsKey(MediaFormat.KeyHeight) ? fmt.GetInteger(MediaFormat.KeyHeight) : 0;
        int cl = 0, ctop = 0, vw = fw, vh = fh;
        if (fmt.ContainsKey("crop-left") && fmt.ContainsKey("crop-right"))
        {
            cl = fmt.GetInteger("crop-left");
            vw = fmt.GetInteger("crop-right") + 1 - cl;
        }
        if (fmt.ContainsKey("crop-top") && fmt.ContainsKey("crop-bottom"))
        {
            ctop = fmt.GetInteger("crop-top");
            vh = fmt.GetInteger("crop-bottom") + 1 - ctop;
        }
        if (vw > 0 && vh > 0)
        {
            _cropLeft = cl;
            _cropTop = ctop;
            _visibleWidth = vw;
            _visibleHeight = vh;
        }

        // 显示旋转（KEY_ROTATION = "rotation-degrees"，由 MediaExtractor 从容器 tkhd 旋转矩阵填入输出格式）。
        // 平台契约：MediaCodec 输出 buffer 的 width/height 永远是「旋转前（编码）」尺寸，真实显示尺寸
        // 在 90/270° 时需交换宽高。当前 VideoFrame 不携带 rotation，渲染端按编码尺寸呈现——
        // 若视频带 90/270° 旋转（竖屏拍摄的横屏内容），将出现方向错乱/溢出观感。此处先读取诊断，
        // 供真机确认旋转角度，再决定是否在解码端交换显示宽高 + 透传 rotation 给渲染端旋转。
        // 注：KEY_ROTATION 常量 MS Learn 标注仅 API 23+ 受支持（CA1416），故统一用字符串键读取以覆盖全版本。
        int rotationDeg = fmt.ContainsKey("rotation-degrees") ? fmt.GetInteger("rotation-degrees") : 0;
        _rotationDegrees = rotationDeg;

        // 色彩空间（可选键，API 24+）：渲染端据以选择 YUV→RGB 矩阵。低版本/缺失时回退 Unspecified。
        int cs = -1, cr = -1, ctr = -1;
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            if (fmt.ContainsKey(MediaFormat.KeyColorStandard)) cs = fmt.GetInteger(MediaFormat.KeyColorStandard);
            if (fmt.ContainsKey(MediaFormat.KeyColorRange)) cr = fmt.GetInteger(MediaFormat.KeyColorRange);
            if (fmt.ContainsKey(MediaFormat.KeyColorTransfer)) ctr = fmt.GetInteger(MediaFormat.KeyColorTransfer);
        }
        _colorInfo = new VideoColorInfo(
            AndroidCodecMaps.ColorStandardFromNdk(cs),
            AndroidCodecMaps.ColorRangeFromNdk(cr),
            AndroidCodecMaps.ColorTransferFromNdk(ctr));

        _logger.LogInformation(
            "[ANDROID-VID] 输出格式: 帧={FW}x{FH} 可见={VW}x{VH} crop=({CL},{CT}) 旋转={Rot}° 色彩={Color}",
            fw, fh, _visibleWidth, _visibleHeight, _cropLeft, _cropTop, _rotationDegrees, _colorInfo);
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> DecodeAsync(MediaPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        if (packet is null) return new ValueTask<VideoFrame?>(ReadOutput());

        // 诊断节流：收包节奏
        if ((_packetsFed % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-VID] 收包 #{Count} size={Size} pts={Pts:g} key={Key}",
                _packetsFed, packet.Data.Length, packet.Timestamp, packet.KeyFrame);
        _packetsFed++;

        _pendingInput.Enqueue(packet);
        FeedInput();
        return new ValueTask<VideoFrame?>(ReadOutput());
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        // 先排空待喂入队列
        FeedInput();

        // EOS 入队：输入槽满时必须先排空输出（释放输出→解码器继续消费输入→槽释放），带重试。
        // 旧实现单次 DequeueInputBuffer(1ms)：槽满即放弃 EOS → 解码器尾段重排缓冲永不排空
        // → 上层 DecodeLoop 的 EOS DRAIN 过早拿到 null 退出 → 剩余帧全部滞留（真机实测 770 帧卡死）。
        if (!_eosQueued)
        {
            for (int attempt = 0; attempt < 16 && !_eosQueued; attempt++)
            {
                FeedInput();
                int inIdx = _codec!.DequeueInputBuffer(2_000);
                if (inIdx >= 0)
                {
                    _codec.QueueInputBuffer(inIdx, 0, 0, 0, (MediaCodecBufferFlags)FlagEndOfStream);
                    _eosQueued = true;
                    break;
                }
                // 槽仍满：排空输出解锁解码器（产帧入 FIFO，不影响返回值语义）
                _ = DrainOutput(5_000);
            }
        }

        // 排空输出取帧：EOS 已入队后解码仍需 ~20-40ms/帧，10ms 会间歇性 TRY_AGAIN
        // 提前返回 null 让上层误判 DRAIN 完成。给足等待；见到输出 EOS 后 FIFO 排尽即真正完成。
        return new ValueTask<VideoFrame?>(DrainOutput(_eosQueued && !_eosOutputSeen ? 40_000 : 10_000));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_codec is null) return;
        _codec.Flush();
        _eosQueued = false;
        _eosOutputSeen = false;
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
    }

    /// <summary>尽可能把待喂入包拷入解码器输入槽。</summary>
    private void FeedInput()
    {
        while (_pendingInput.Count > 0)
        {
            int idx = _codec!.DequeueInputBuffer(0);
            if (idx < 0)
            {
                // 输入槽暂满：保留包待下次。诊断判定「未入队」而非常见的「入队不出帧」。
                _inputDequeueBlocked++;
                if (_inputDequeueBlocked == 1 || (_inputDequeueBlocked % 256) == 0)
                    _logger.LogWarning("[ANDROID-VID] 输入槽满，喂入被阻（pending={Pending}, 累计阻 {Blk}）, dequeueOut={Out}",
                        _pendingInput.Count, _inputDequeueBlocked, _drainDequeued);
                break;
            }

            var pkt = _pendingInput.Dequeue();
            try
            {
                ByteBuffer? buf = _codec.GetInputBuffer(idx);
                if (buf is null) continue;

                int len = Math.Min(pkt.Data.Length, buf.Remaining());
                if (len != pkt.Data.Length)
                    _logger.LogWarning("[ANDROID-VID] 输入 buffer 容量({Cap})小于包大小({Len})，截断喂入",
                        buf.Remaining(), pkt.Data.Length);

                var mem = pkt.Data;
                if (MemoryMarshal.TryGetArray(mem, out ArraySegment<byte> seg) && seg.Array is not null)
                    buf.Put(seg.Array, seg.Offset, len);
                else
                    buf.Put(mem.ToArray(), 0, len);

                long ptsUs = pkt.Timestamp.Ticks > 0 ? pkt.Timestamp.Ticks / 10 : 0;
                _codec.QueueInputBuffer(idx, 0, len, ptsUs, (MediaCodecBufferFlags)0);
                _inputQueued++;
            }
            finally
            {
                pkt.Dispose();
            }
        }
    }

    /// <summary>读出一个已解出帧（先返回 FIFO 余帧，再尝试从解码器申领）。</summary>
    private VideoFrame? ReadOutput()
    {
        if (_pendingFrames.Count > 0) return _pendingFrames.Dequeue();
        return DrainOutput(0);
    }

    /// <summary>排空解码器输出，将可用帧入 FIFO，返回队首（超时内无帧返回 null）。
    /// 单一 ByteBuffer 路径：dequeueOutputBuffer → getOutputImage → 按 stride/crop 提取 → release。</summary>
    private VideoFrame? DrainOutput(long timeoutUs)
    {
        _drainCalls++;
        while (true)
        {
            var info = new AndroidMediaCodec.BufferInfo();
            int idx = _codec!.DequeueOutputBuffer(info, timeoutUs);
            if (idx == InfoTryAgainLater) { _drainTryAgain++; break; }
            if (idx == InfoOutputFormatChanged)
            {
                _outputFormat?.Dispose();
                _outputFormat = _codec.OutputFormat;
                ReadOutputFormat(_outputFormat);
                continue;
            }
            if (idx == InfoOutputBuffersChanged) continue;
            if (idx < 0) { _drainTryAgain++; break; }

            if (((int)info.Flags & FlagEndOfStream) != 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                _eosOutputSeen = true; // DRAIN 完成判据：此后 FIFO 排尽即无更多帧
                break;
            }
            if (info.Size <= 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }

            // getOutputImage 取标准化平面（已设 Formatyuv420flexible，平台契约保证可用），
            // 统一经 ExtractI420FromImage 提紧凑 I420——不按 stride 猜测厂商私有布局。
            var image = _codec.GetOutputImage(idx);
            if (image is null)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }
            VideoFrame? frame;
            try
            {
                frame = ExtractI420FromImage(image, info.PresentationTimeUs,
                    ((int)info.Flags & FlagKeyFrame) != 0);
            }
            finally
            {
                // Image 与输出缓冲同生命周期：先关 Image 再释放缓冲，避免解码器复用缓冲时
                // 仍有存活的 Image 视图（数据此刻已拷入托管内存，关闭无损）。
                image.Close();
                _codec.ReleaseOutputBuffer(idx, false);
            }
            if (frame is null) continue;

            if ((_framesProduced % LogInterval) == 0)
                _logger.LogInformation("[ANDROID-VID] 产帧 #{Count} {W}x{H} {Fmt} pts={Pts:g}",
                    _framesProduced, frame.Width, frame.Height, frame.Format, frame.Timestamp);
            _framesProduced++;
            _drainDequeued++;
            _pendingFrames.Enqueue(frame);
        }

        // 周期性诊断（定位 dequeue 是否恒 TRY_AGAIN）
        if ((_drainCalls % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-VID] 诊断: 排空={Calls} dequeue成功={Deq} tryAgain={Try} 喂入={Fed} 累计产帧={Frames}",
                _drainCalls, _drainDequeued, _drainTryAgain, _inputQueued, _framesProduced);

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <summary>从 <see cref="Image"/>（YUV_420_888，灵活 YUV420 经 getOutputImage 取得）用托管 CPU 平面提取
    /// 产出标准紧凑帧。全设备通用：plane 顺序（#0=Y、#1=U、#2=V）；Y pixelStride 恒为 1；U/V 的
    /// pixelStride 为 1（planar I420）或 2（semiplanar NV12，U 在偶字节、V 在奇字节，MediaCodec 输出通常如此）。
    /// 可见区由 <see cref="Image.CropRect"/>（= 输出格式 crop-* 矩形）界定，跳过 16 对齐填充；缺失时退回
    /// <c>_visibleWidth/_visibleHeight</c> 字段（由 ReadOutputFormat 解析）。调用方负责关闭 Image 与释放输出缓冲。</summary>
    /// <remarks>官方 AOSP CTS getDataFromImage 范式：frame 尺寸取 crop 宽高（非 image.Width/Height，后者含对齐填充），
    /// 按 (cropTop+row)*rowStride + cropLeft*pixelStride 索引逐行提取；半平面 U 偶 V 奇。
    /// <para><b>性能优化</b>：半平面输入直接产出 <see cref="PixelFormat.NV12"/>，避免先拆成 I420 再被 SkiaVideoPresenter
    /// 重新按 NV12 语义读取的二次拆分/合并；planar 输入仍产出 <see cref="PixelFormat.YUV420P"/>。</para>
    /// 色彩顺序若个别设备为 NV21（U 奇 V 偶）将偏色，此时交换 U/V 即可（本实现默认 NV12）。</remarks>
    private VideoFrame? ExtractI420FromImage(Image image, long infoPtsUs, bool keyFrame)
    {
        var planes = image.GetPlanes();
        if (planes is null || planes.Length < 3) return null;

        // 可见区：优先 image.CropRect（官方权威 per-frame），回退到输出格式解析的 _visible* 字段。
        // 关键：image.Width/Height 是含 16 对齐填充的缓冲尺寸，可见像素只占 crop 矩形内一部分。
        int cl, ct, vw, vh;
        var crop = image.CropRect;
        if (crop is not null && crop.Width() > 0 && crop.Height() > 0)
        {
            cl = crop.Left; ct = crop.Top; vw = crop.Width(); vh = crop.Height();
        }
        else if (_visibleWidth > 0 && _visibleHeight > 0)
        {
            cl = _cropLeft; ct = _cropTop; vw = _visibleWidth; vh = _visibleHeight;
        }
        else
        {
            cl = 0; ct = 0; vw = image.Width; vh = image.Height;
        }
        if (vw <= 0 || vh <= 0) return null;

        var yPlane = planes[0];
        var uPlane = planes[1];
        var vPlane = planes[2];
        var yBuf = yPlane.Buffer;
        var uBuf = uPlane.Buffer;
        var vBuf = vPlane.Buffer;
        if (yBuf is null || uBuf is null || vBuf is null) return null;

        int yRowStride = yPlane.RowStride;
        int yPixelStride = yPlane.PixelStride; // 恒为 1
        int uvRowStride = uPlane.RowStride;
        int uvPixelStride = uPlane.PixelStride; // 1=planar(I420) / 2=semiplanar(NV12)
        int cw = (vw + 1) / 2;
        int ch = (vh + 1) / 2;
        int ySize = vw * vh;

        // 半平面（NV12）且 crop-left/width 均为偶数：直接输出紧凑 NV12（Y + UV 交错），避免拆成 I420 再被 presenter 重拼。
        // crop-left 为奇数或宽度为奇数时，UV 对无法字节对齐，回退到逐像素 I420 拆分（保证正确性）。
        bool fastNv12 = uvPixelStride == 2 && (cl & 1) == 0 && (vw & 1) == 0;
        PixelFormat outFmt = fastNv12 ? PixelFormat.NV12 : PixelFormat.YUV420P;
        int uvRowBytes = fastNv12 ? (vw + 1) & ~1 : cw; // NV12 紧凑行须为偶字节（UV 对），I420 行 = cw
        int uvPlaneBytes = uvRowBytes * ch;
        int totalBytes = fastNv12 ? ySize + uvPlaneBytes : ySize + 2 * cw * ch;
        var resource = new SoftwareFrameResource(vw, vh, outFmt, checked(totalBytes));
        Span<byte> dst = resource.Data.Span;

        // ── 一锤定音诊断（仅首帧）：统计可见区 Y/U/V 平面均值/范围/非零占比 ──
        bool diag = _framesProduced == 0;
        long extractStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Y 平面：整平面拷入 _extractRaw，再逐行按可见区拷贝（Y 与 UV 布局无关，共用）。
        int yCap = yBuf.Capacity();
        if (_extractRaw.Length < yCap) _extractRaw = new byte[yCap];
        yBuf.Rewind();
        yBuf.Get(_extractRaw, 0, yCap);
        long ySum = 0; byte yMin = 255, yMax = 0; int yNonZero = 0;
        int yDst = 0;
        for (int row = 0; row < vh; row++)
        {
            int srcOff = (ct + row) * yRowStride + cl * yPixelStride;
            CopyRow(_extractRaw, srcOff, vw, dst, yDst);
            if (diag)
                for (int x = 0; x < vw; x++)
                {
                    byte b = dst[yDst + x];
                    ySum += b; if (b < yMin) yMin = b; if (b > yMax) yMax = b; if (b != 0) yNonZero++;
                }
            yDst += vw;
        }

        int chromaRow0 = ct / 2;
        int chromaCol0 = cl / 2;

        long uSum = 0; byte uMin = 255, uMax = 0; int uNonZero = 0;
        long vSum = 0; byte vMin = 255, vMax = 0; int vNonZero = 0;

        if (fastNv12)
        {
            // 半平面 NV12：plane[1] 已含 UV 交错（U 偶 V 奇），直接按行拷入 destination UV 区。
            // 源行可能有行尾填充（uvRowStride），目标紧凑行 uvRowBytes；copy 后不足处填 128。
            int uCap = uBuf.Capacity();
            if (_extractRaw.Length < uCap) _extractRaw = new byte[uCap];
            uBuf.Rewind();
            uBuf.Get(_extractRaw, 0, uCap);

            int uvDst = ySize;
            for (int cy = 0; cy < ch; cy++)
            {
                int srcOff = (chromaRow0 + cy) * uvRowStride + chromaCol0 * 2;
                int dstOff = uvDst + cy * uvRowBytes;
                int avail = _extractRaw.Length - srcOff;
                int copy = avail >= uvRowBytes ? uvRowBytes : avail > 0 ? avail : 0;
                if (copy > 0)
                    _extractRaw.AsSpan(srcOff, copy).CopyTo(dst.Slice(dstOff, copy));
                if (copy < uvRowBytes)
                    dst.Slice(dstOff + copy, uvRowBytes - copy).Fill(128);

                if (diag && copy > 0)
                {
                    // 统计：U 在偶字节、V 在奇字节（标准 NV12）。
                    int statLen = copy & ~1;
                    for (int i = 0; i < statLen; i += 2)
                    {
                        byte u = _extractRaw[srcOff + i];
                        byte v = _extractRaw[srcOff + i + 1];
                        uSum += u; if (u < uMin) uMin = u; if (u > uMax) uMax = u; if (u != 0) uNonZero++;
                        vSum += v; if (v < vMin) vMin = v; if (v > vMax) vMax = v; if (v != 0) vNonZero++;
                    }
                }
            }
        }
        else if (uvPixelStride == 1)
        {
            // planar：U/V 各自独立平面，逐行拷贝 cw 字节（须按 uvRowStride 步进，含行填充）。
            int uCap = uBuf.Capacity();
            if (_extractRaw.Length < uCap) _extractRaw = new byte[uCap];
            uBuf.Rewind();
            uBuf.Get(_extractRaw, 0, uCap);

            int uDst = ySize;
            int vDst = ySize + cw * ch;
            for (int cy = 0; cy < ch; cy++)
            {
                int srcRow = (chromaRow0 + cy) * uvRowStride + chromaCol0;
                CopyRow(_extractRaw, srcRow, cw, dst, uDst + cy * cw);
                if (diag) AccumPlaneStats(dst, uDst + cy * cw, cw, ref uSum, ref uMin, ref uMax, ref uNonZero);
            }
            int vCap = vBuf.Capacity();
            if (_extractRaw.Length < vCap) _extractRaw = new byte[vCap];
            vBuf.Rewind();
            vBuf.Get(_extractRaw, 0, vCap);
            for (int cy = 0; cy < ch; cy++)
            {
                int srcRow = (chromaRow0 + cy) * uvRowStride + chromaCol0;
                CopyRow(_extractRaw, srcRow, cw, dst, vDst + cy * cw);
                if (diag) AccumPlaneStats(dst, vDst + cy * cw, cw, ref vSum, ref vMin, ref vMax, ref vNonZero);
            }
        }
        else
        {
            // semiplanar 但 crop-left 为奇数：无法字节对齐直接 copy，回退逐像素拆成 I420。
            int uCap = uBuf.Capacity();
            if (_extractRaw.Length < uCap) _extractRaw = new byte[uCap];
            uBuf.Rewind();
            uBuf.Get(_extractRaw, 0, uCap);

            int uDst = ySize;
            int vDst = ySize + cw * ch;
            int elemCap = _extractRaw.Length;
            for (int cy = 0; cy < ch; cy++)
            {
                int srcRow = (chromaRow0 + cy) * uvRowStride + chromaCol0 * 2;
                for (int cx = 0; cx < cw; cx++)
                {
                    int s = srcRow + cx * 2;
                    byte u, v;
                    if (s + 1 < elemCap && s >= 0) { u = _extractRaw[s]; v = _extractRaw[s + 1]; }
                    else { u = 128; v = 128; }
                    dst[uDst + cy * cw + cx] = u;
                    dst[vDst + cy * cw + cx] = v;
                    if (diag)
                    {
                        uSum += u; if (u < uMin) uMin = u; if (u > uMax) uMax = u; if (u != 0) uNonZero++;
                        vSum += v; if (v < vMin) vMin = v; if (v > vMax) vMax = v; if (v != 0) vNonZero++;
                    }
                }
            }
        }

        if (diag)
        {
            int yTot = vw * vh, cTot = cw * ch;
            _logger.LogInformation(
                "[ANDROID-VID] 首帧平面统计 可见={VW}x{VH} fmt={Fmt} crop=({CL},{CT}) | " +
                "Y 均值={YM:g} 范围[{Ymin},{Ymax}] 非零{Ynz}% | " +
                "U 均值={UM:g} 范围[{Umin},{Umax}] 非零{Unz}% | " +
                "V 均值={VM:g} 范围[{Vmin},{Vmax}] 非零{Vnz}%",
                vw, vh, fastNv12 ? "NV12(semiplanar,U-even,V-odd)" : uvPixelStride == 1 ? "I420(planar)" : "I420(fallback,semiplanar)",
                cl, ct,
                (double)ySum / yTot, yMin, yMax, yNonZero * 100 / yTot,
                (double)uSum / cTot, uMin, uMax, uNonZero * 100 / cTot,
                (double)vSum / cTot, vMin, vMax, vNonZero * 100 / cTot);
            _logger.LogInformation(
                "[ANDROID-VID] 诊断判读: V均值≈128→色度已填充(绿屏应消除); V≈0→解码端未填V(需换软件解码器)。解码器={Name} 硬件={Hw}",
                _codecName, _hardwareDecoder);
        }

        // 周期性性能诊断：每 60 帧报告一次平均提取耗时，用于定位卡顿瓶颈。
        _extractSamples++;
        _extractTicks += System.Diagnostics.Stopwatch.GetTimestamp() - extractStart;
        if (_framesProduced > 0 && (_framesProduced % LogInterval) == 0)
        {
            double avgMs = System.Diagnostics.Stopwatch.GetElapsedTime(0, _extractTicks).TotalMilliseconds / _extractSamples;
            _logger.LogInformation("[ANDROID-VID] 提取性能: 样本={Samples} 平均={AvgMs:F2}ms/帧 解码器={Name}",
                _extractSamples, avgMs, _codecName);
            _extractSamples = 0;
            _extractTicks = 0;
        }

        // PTS：优先 image.Timestamp（ns）→ ticks；缺失则退回 dequeue 的 presentationTimeUs（us）→ ticks。
        TimeSpan pts = image.Timestamp > 0
            ? TimeSpan.FromTicks(image.Timestamp / 100)
            : infoPtsUs >= 0 ? TimeSpan.FromTicks(infoPtsUs * 10) : TimeSpan.Zero;

        resource.ColorInfo = _colorInfo;
        return new VideoFrame(vw, vh, outFmt, resource, pts, TimeSpan.Zero, keyFrame, _colorInfo);
    }

    /// <summary>累计一行的平面统计（均值/范围/非零占比），供首帧诊断使用。</summary>
    private static void AccumPlaneStats(Span<byte> data, int off, int count,
        ref long sum, ref byte min, ref byte max, ref int nonZero)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = data[off + i];
            sum += b; if (b < min) min = b; if (b > max) max = b; if (b != 0) nonZero++;
        }
    }

    // 平面提取统一由 ExtractI420FromImage（Image.Plane，按 pixelStride/rowStride + CropRect 可见区）承担，见上方。

    private static void CopyRow(byte[] src, int srcOff, int count, Span<byte> dst, int dstOff)
    {
        if (srcOff < 0 || count <= 0) return;
        // 越界时只拷贝可用部分（不整行丢弃），避免对齐填充误差导致整行变 0（黑/绿线）。
        int n = Math.Min(count, src.Length - srcOff);
        if (n <= 0) return;
        new ReadOnlySpan<byte>(src, srcOff, n).CopyTo(dst.Slice(dstOff, n));
    }

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
        DrainAndDispose(_pendingInput);
        DrainAndDispose(_pendingFrames);
        // 先释放编解码器，再释放 reader（窗口须活得比编解码器久）。
        _codec?.Release();
        _codec = null;
        _outputFormat?.Dispose();
        _outputFormat = null;

        // ByteBuffer 单一路径汇总（确证 DrainOutput 是否被调用、各分支分布）
        if (_drainCalls > 0)
        {
            _logger.LogWarning(
                "[ANDROID-VID] 取帧汇总：drainCalls={Calls} dequeue成功={Dq} tryAgain={Ta} 喂入={Fed} 累计产帧={Frames} 解码器={Name} 硬件={Hw}",
                _drainCalls, _drainDequeued, _drainTryAgain, _inputQueued, _framesProduced, _codecName, _hardwareDecoder);
        }
    }
}