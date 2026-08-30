using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Java.Nio;
using LingFan.Media.GPUShare.Vulkan; // AndroidHardwareBufferFrameResource（跨工程平台帧 DTO，消费方向：Backends→GPUShare.Vulkan）
// 本后端命名空间段为 ...MediaCodec，会遮蔽类型 Android.Media.MediaCodec → 用不撞名的别名。
using AndroidMediaCodec = Android.Media.MediaCodec;
// Android.Graphics.PixelFormat 与 Abstractions 全局冲突 → 别名锁定契约层像素格式。
using PixelFormat = LingFan.Media.Abstractions.PixelFormat;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// 基于托管 <see cref="AndroidMediaCodec"/> 的视频解码器，双路径：
/// <b>① GLES 桥接零拷贝（默认优先）</b>：解码器输出到 <see cref="AndroidAhbRgbaBridge"/> 的 SurfaceTexture，
/// 驱动在 GPU 内把 YUV→RGB 渲进 RGBA AHardwareBuffer，Vulkan 渲染器以普通 RGBA 纹理采样上屏
/// （绕开 Adreno 对「YUV AHB + Vulkan YCbCr 采样」的原生空指针崩溃）；
/// <b>② ByteBuffer + 灵活 YUV420 回退</b>：桥接不可用（API&lt;29 或 EGL/GLES 初始化失败）时，
/// 经 <c>getOutputImage</c> 取标准化三平面 I420 走 CPU。两条路径均走 net-android 托管绑定，
/// 仅桥接的 EGL/GLES/AHardwareBuffer 图形原语经 [LibraryImport]，符合 2026-08-22 架构裁定（Android 后端
/// 媒体 API 走托管绑定，仅图形原语例外，与解码器既有 carve-out 一致）。
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
/// 真机实测即命中该缺口：V 平面恒≈0 → 画面泛绿。GPU 零拷贝的正确形态是 SurfaceTexture→GL 在 GPU 内把
/// YUV→RGB 渲进 RGBA AHardwareBuffer，再交 Vulkan 以普通 RGBA 采样（<see cref="AndroidAhbRgbaBridge"/> 路径），
/// 已实现为本解码器默认优先的零拷贝路径，不可用时回退本 ByteBuffer CPU 路径。</para>
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

    private readonly Queue<MediaPacket> _pendingInput = new();
    private readonly Queue<VideoFrame> _pendingFrames = new();

    // 取帧诊断计数（Dispose 汇总，定位零产帧各分支分布）
    private long _drainCalls, _drainDequeued, _drainTryAgain;
    // UV 行读取越界警告一次性标志（稳态帧才会残留上一帧数据，首帧诊断覆盖不到，故需逐帧检测）
    private bool _uvClampWarned;
    private bool _eosQueued;    // EOS 已入队（FlushAsync 重试语义，Reset 清零）
    private bool _eosOutputSeen;// 解码器已回报输出 EOS（DRAIN 真正完成的判据）

    // 诊断节流：收包/产帧计数
    private int _packetsFed;
    private int _framesProduced;
    private int _inputQueued;              // 实际入队的输入缓冲数（vs 仅收包）
    private long _inputDequeueBlocked;     // dequeueInputBuffer 返回 -1 的次数（输入槽满）
    private long _inputBufferNull;         // GetInputBuffer 返回 null 的次数（异常，包保留重试）
    private long _inputTruncated;          // 输入槽装不下整包而被跳过的包数（绝不截断喂入）
    private long _postDrainFed;            // 「排空后补喂」实际入队的包数（>0 证明补喂路径生效）
    private long _postDrainCalls;          // 「排空后补喂」的调用次数
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

    // ── 帧几何变化探测器 ──
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
    // EOS 排空总超时（秒）兜底：正常 DRAIN 远快于此，仅用于解码器异常时防死锁，
    // 避免 FlushAsync 无限等待把呈现线程挂死。
    private const int DrainTimeoutSeconds = 5;
    // 单次 DrainOutput 最多提取的帧数。开播期解码器积压时，无上限的排空会把全部就绪帧
    // 一次性灌进 _pendingFrames，使 in-flight 帧数远超帧池每桶容量（16）；超额帧每帧都要
    // 新分配大数组、归还即弃 ⇒ 开播期垃圾风暴 ⇒ GC 停顿渲染线程，进而放大呈现侧抖动。
    // 加上限把 in-flight 峰值压回池容量内（未取完的帧留待下一轮，不丢帧）。
    private const int MaxFramesPerDrain = 4;

    // 本后端媒体 API 仅使用 net-android 托管的 Android.Media.* 绑定（MediaCodec / Image.Plane）；
    // 显式禁止手写 P/Invoke：Android/iOS/macOS 走 net-* workload 内置绑定，AOT 安全、零反射
    // （符合 2026-08-22 架构裁定）。GPU 零拷贝的图形原语（EGL/GLES/AHardwareBuffer）属 carve-out，
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

        // ── 双路径：GLES 桥接零拷贝（Surface 输出 → RGBA AHB，增强档按开关启用）与 ByteBuffer CPU 路径（默认）──
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
        // 而非经 ImageReader 回读 CPU；该形态即本解码器的 GLES 桥接零拷贝路径（AndroidAhbRgbaBridge），
        // 经 AndroidVideoDecodePolicy 启用，不可用时回退到本 ByteBuffer CPU 路径。
        //
        // 解码分档（见 AndroidVideoDecodePolicy）：
        // ① 硬解 + CPU 帧（能播档，默认）：OMX 硬件解码器 + ByteBuffer CPU 帧 + 渲染端 Skia 软渲。
        //    OMX 走旧 OMX 框架（非 Codec2），无 c2 的 numClientBuffers 僵死；此为真机 PASS 路径。
        // ② 桥接零拷贝（增强档，EnableHardwareZeroCopy）：软解（c2）+ GLES 桥接 → RGBA AHB。
        //    软解 + 桥接产帧稳定（绕开 OMX 首帧崩）；Vulkan AHB 采样在 Adreno 上驱动崩，待 GL 路线重做。
        // 桥接不可用时回落 ① 硬解 + ByteBuffer（绝不 c2 软解 + ByteBuffer——那是 numClientBuffers 僵死档）。
        bool zeroCopy = AndroidVideoDecodePolicy.EnableHardwareZeroCopy;
        _useAhbFrames = zeroCopy && TryCreateAhbOutputSurface(frameW, frameH);
        // 零拷贝档软解（c2）优先；能播档硬解（OMX）优先（避开 c2 僵死）。
        var codecObj = CreateVideoCodec(mime, codec, preferSoftwareDecoder: _useAhbFrames);
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

    /// <summary>以「灵活 YUV420 + 显式尺寸」配置 ByteBuffer 输出；被拒时降级到软件解码器重试一次。</summary>
    /// <remarks>显式 width/height 用容器声明值：硬件解码器普遍拒绝 csd-only 配置，而真实可见尺寸
    /// 由输出格式的 crop 矩形给出（见 <see cref="ReadOutputFormat"/>），故此处无需也不应依赖
    /// 手写 SPS 解析（该解析器已证伪，会把 1080x1920 解成 16x32）。</remarks>
    private void ConfigureFlexibleYuv(ref AndroidMediaCodec codecObj, string mime, VideoCodec codec,
        ReadOnlyMemory<byte> csd, int frameW, int frameH, Surface? outputSurface, bool useAhbFrames)
    {
        try
        {
            codecObj.Configure(BuildFormat(mime, csd, frameW, frameH, useAhbFrames),
                useAhbFrames ? outputSurface : null, null, 0);
            return;
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
        {
            _logger.LogWarning("[ANDROID-VID] 解码器 {Name} 配置被拒（{Reason}），降级软件解码器重试",
                _codecName, ex.Message);
        }

        // 硬件解码器拒绝该配置：换软件解码器（c2/OMX 阶梯）重试。软件路径不支持 AHB 零拷贝，
        // 释放桥接 Surface 并回退 ByteBuffer CPU 路径（useAhbFrames=false）。
        codecObj.Release();
        _bridge?.Dispose();
        _bridge = null;
        _outputSurface = null;
        _useAhbFrames = false;
        codecObj = CreateVideoCodec(mime, codec, preferSoftwareDecoder: true);
        codecObj.Configure(BuildFormat(mime, csd, frameW, frameH, false), null, null, 0);
    }

    /// <summary>构造解码器输入格式（每次 configure 需新实例：MediaFormat 被 configure 消费后不应复用）。
    /// 输出到 Surface（AHB 零拷贝路径）时颜色格式由 Surface 决定，不强制灵活 YUV420；回退 ByteBuffer 路径时才设。</summary>
    private static MediaFormat BuildFormat(string mime, ReadOnlyMemory<byte> csd, int frameW, int frameH, bool useAhbFrames)
    {
        var fmt = new MediaFormat();
        fmt.SetString(MediaFormat.KeyMime, mime);
        if (frameW > 0) fmt.SetInteger(MediaFormat.KeyWidth, frameW);
        if (frameH > 0) fmt.SetInteger(MediaFormat.KeyHeight, frameH);
        // 仅 ByteBuffer 回退路径强制灵活 YUV420（令 getOutputImage 返回标准化三平面，屏蔽厂商私有布局差异）；
        // Surface 输出路径（AHB 零拷贝）颜色格式由 Surface 决定，无需也不能设 KeyColorFormat。
        if (!useAhbFrames)
            fmt.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
        if (csd.Length > 0)
            fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(csd.ToArray())); // 键 "csd-0"（AOSP KEY_CSD0）
        // 显式输入缓冲上限：解码器按 SPS 推出的 max-input-size 可能过小，大关键帧会被截断喂入 → 码流损坏。
        fmt.SetInteger(MediaFormat.KeyMaxInputSize, 2 << 20);
        return fmt;
    }

    /// <summary>尝试建立 GLES/EGL 桥接的 AHB 输出 Surface（GPU 零拷贝前置）：构造
    /// <see cref="AndroidAhbRgbaBridge"/> 并初始化 EGL/GLES 上下文、SurfaceTexture 与输出 Surface。
    /// 失败（API&lt;29 或 EGL/GLES 不可用）则回退 ByteBuffer CPU 路径，返回 false。</summary>
    /// <remarks>DIP：桥接仅依赖 Abstractions + GPUShare.Vulkan，本解码器不引用任何 Renderer 或图形 API 绑定。
    /// 桥接初始化抛 <see cref="NotSupportedException"/> 视为「环境不支持」，catch 回退，不影响 ByteBuffer 主路径。</remarks>
    private bool TryCreateAhbOutputSurface(int frameW, int frameH)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            _logger.LogInformation("[ANDROID-AHB-DEC] API<29，GLES 桥接不可用，回退 ByteBuffer CPU 路径。");
            return false;
        }
        try
        {
            _bridge = new AndroidAhbRgbaBridge(frameW, frameH, _logger);
            _bridge.Initialize(); // 失败抛 NotSupportedException（EGL/GLES 配置缺失）
            _outputSurface = _bridge.OutputSurface;
            _logger.LogInformation(
                "[ANDROID-AHB-DEC] GLES/EGL 桥接 Surface 就绪（{W}x{H}），零拷贝路径启用。",
                _bridge.FrameWidth, _bridge.FrameHeight);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ANDROID-AHB-DEC] GLES 桥接初始化失败，回退 ByteBuffer CPU 路径。");
            _bridge?.Dispose();
            _bridge = null;
            _outputSurface = null;
            return false;
        }
    }

    /// <summary>创建视频解码器。默认按 MIME 取系统首选（通常为硬件解码器，实时性最佳）；
    /// <paramref name="preferSoftwareDecoder"/> 为真时走 AOSP 软件解码器阶梯
    /// （<see cref="SoftwareCodecCandidates"/>，c2 新栈优先、OMX.google 旧栈兜底）→ 按类型任选。</summary>
    /// <remarks>ByteBuffer 输出对硬件解码器同样安全：灵活 YUV420 由平台契约保证全解码器支持，
    /// 且等价于灵活格式的厂商私有格式仍可经 getOutputImage 取到标准化平面。</remarks>
    private AndroidMediaCodec CreateVideoCodec(string mime, VideoCodec codec, bool preferSoftwareDecoder)
    {
        if (preferSoftwareDecoder)
        {
            foreach (string name in SoftwareCodecCandidates(codec))
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

    /// <summary>AOSP 软件解码器候选（Codec2 新栈优先，OMX.google 旧栈兜底；均不存在时按类型任选）。
    /// 软件解码器随系统内置，输出灵活 YUV420 到 ByteBuffer，无额外原生依赖。</summary>
    private static string[] SoftwareCodecCandidates(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => new[] { "c2.android.avc.decoder", "OMX.google.h264.decoder" },
        VideoCodec.H265 => new[] { "c2.android.hevc.decoder", "OMX.google.hevc.decoder" },
        VideoCodec.AV1 => new[] { "c2.android.av1.decoder" },
        VideoCodec.VP9 => new[] { "c2.android.vp9.decoder", "OMX.google.vp9.decoder" },
        VideoCodec.MPEG2 => new[] { "c2.android.mpeg2.decoder", "OMX.google.mpeg2.decoder" },
        VideoCodec.MPEG4 => new[] { "c2.android.mpeg4.decoder", "OMX.google.mpeg4.decoder" },
        _ => Array.Empty<string>(),
    };

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

        if (packet is null)
        {
            VideoFrame? drained = ReadOutput();
            FeedAfterDrain(); // 排空后补喂（理由见 FeedAfterDrain 注释）
            return new ValueTask<VideoFrame?>(drained);
        }

        // 诊断节流：收包节奏
        if ((_packetsFed % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-VID] 收包 #{Count} size={Size} pts={Pts:g} key={Key}",
                _packetsFed, packet.Data.Length, packet.Timestamp, packet.KeyFrame);
        _packetsFed++;

        _pendingInput.Enqueue(packet);
        FeedInput();               // 尽力喂（此时槽位多半仍满）
        VideoFrame? frame = ReadOutput();  // 排空输出 → 输入槽位腾出
        FeedAfterDrain();          // 立刻补喂，否则要等下一个包到来（30 包/秒）
        return new ValueTask<VideoFrame?>(frame);
    }

    /// <inheritdoc/>
    public ValueTask<VideoFrame?> FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        // 先排空待喂入队列
        FeedInput();

        // 已有解码完成但尚未交出的帧：立即返回，绝不阻塞在 EOS 入队上
        // （否则 EOS 入队重试期间会把已解码帧白白拖住，末段帧被上层 Complete 截断）。
        if (_pendingFrames.Count > 0)
            return new ValueTask<VideoFrame?>(_pendingFrames.Dequeue());

        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
                        + System.Diagnostics.Stopwatch.Frequency * DrainTimeoutSeconds;

        // ── 阶段 1：EOS 入队（必须成功）──────────────────────────────────────
        // 输入槽满时（真机日志：输入槽满，喂入被阻 pending=135）须先排空输出腾出槽位再重试。
        // 旧实现「固定 16 次重试后放弃」+「随后 !_eosQueued 即返回 null」，会让上层 DecodeLoop
        // 立刻判定排空完成并 Complete 帧队列 —— 末段 GOP 全部滞留解码器。
        // 真机实证：32.8s 视频只呈现到 28.3~28.5s，末约 4.4s（~130 帧）永久丢失，
        // 表现为「画面卡在最后几秒、音频照常播完」。
        // 修正：重试到成功为止（受总 deadline 约束）；**排空取到的帧立即交还上层、绝不丢弃**
        // （旧代码 `_ = DrainOutput(5_000);` 直接丢弃返回值 = 每轮白丢一帧）。
        while (!_eosQueued)
        {
            FeedInput();

            // 【关键】待喂队列未排空前，绝不能入 EOS。
            // EOS 一旦入队，_pendingInput 里剩余的包就永远进不了解码器，其帧永久丢失。
            // 真机实证（2026-08-30）：32.8s 视频只产出 816/985 帧、喂入 844 包，
            // 画面冻在 27.2s 而音频照常播完 —— 正是此处提前入 EOS 所致。
            if (_pendingInput.Count > 0)
            {
                if (DrainOutput(5_000) is { } pending)
                {
                    FeedAfterDrain();
                    return new ValueTask<VideoFrame?>(pending);
                }
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    _logger?.LogWarning(
                        "[ANDROID-VID] EOS 入队受阻：待喂队列仍有 {Pending} 包未入解码器，末段帧将丢失",
                        _pendingInput.Count);
                    break;
                }
                continue;
            }

            int inIdx = _codec!.DequeueInputBuffer(2_000);
            if (inIdx >= 0)
            {
                _codec.QueueInputBuffer(inIdx, 0, 0, 0, (MediaCodecBufferFlags)FlagEndOfStream);
                _eosQueued = true;
                break;
            }
            // 槽仍满：排空输出解锁解码器。取到的帧直接返回上层（下一轮 FlushAsync 再继续入 EOS）。
            if (DrainOutput(5_000) is { } unlocked)
            {
                FeedAfterDrain();
                return new ValueTask<VideoFrame?>(unlocked);
            }

            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
            {
                _logger?.LogWarning(
                    "[ANDROID-VID] EOS 入队超时（{Sec}s，输入槽持续占满），末段重排帧可能丢失",
                    DrainTimeoutSeconds);
                break;
            }
        }

        // ── 阶段 2：排空输出，直到解码器回报输出 EOS ──────────────────────────
        // 【关键】单次 DrainOutput 的 TRY_AGAIN **绝不代表排空完成**：EOS 入队后，解码器内部
        // 仍持有末段 GOP 的 B 帧重排缓冲，需多轮 dequeue 才逐步吐出，期间必然穿插 TRY_AGAIN。
        // 修正：持续重试直到 `_eosOutputSeen`（DRAIN 真正的完成判据），总超时兜底防死锁。
        while (true)
        {
            var frame = DrainOutput(_eosQueued && !_eosOutputSeen ? 40_000 : 10_000);
            if (frame is not null)
                return new ValueTask<VideoFrame?>(frame);

            // 未取到帧：仅当已见到输出 EOS 时才算真正排空。
            // 注意：不再因 `!_eosQueued` 提前返回——EOS 未入队时仍应继续把解码器已有输出取干净。
            if (_eosOutputSeen)
                return new ValueTask<VideoFrame?>((VideoFrame?)null);

            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
            {
                _logger?.LogWarning(
                    "[ANDROID-VID] EOS 排空超时（{Sec}s，未见输出 EOS），末段重排帧可能丢失",
                    DrainTimeoutSeconds);
                return new ValueTask<VideoFrame?>((VideoFrame?)null);
            }
            // 未见 EOS：解码器仍在重排，继续等下一轮。
        }
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
        DrainAndDispose(_reorder);
        _reorder.Clear();
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

            // 先 Peek 不 Dequeue：只有确认能安全喂入才出队，避免任何分支静默丢包
            // （旧实现 `if (buf is null) continue;` 会把已出队的包在 finally 里 Dispose 掉，
            //  包就此消失且无任何日志 —— 末段丢帧的隐藏来源之一）。
            var pkt = _pendingInput.Peek();

            ByteBuffer? buf = _codec.GetInputBuffer(idx);
            if (buf is null)
            {
                _inputBufferNull++;
                if (_inputBufferNull <= 8)
                    _logger.LogWarning(
                        "[ANDROID-VID] GetInputBuffer({Idx}) 返回 null（共 {N} 次），保留该包待下次重试（绝不静默丢包）",
                        idx, _inputBufferNull);
                break;
            }

            // 装不下就必须整包跳过、绝不能截断喂入：截断的 H.264 NAL 会让解码器产出
            // 半帧/错帧，正是「花屏」的直接来源。宁可丢这一帧，也不能污染后续帧的参考。
            if (buf.Remaining() < pkt.Data.Length)
            {
                _inputTruncated++;
                if (_inputTruncated <= 8)
                    _logger.LogWarning(
                        "[ANDROID-VID] 输入 buffer 容量({Cap})小于包大小({Len})，跳过整包（绝不截断喂入，避免解码错帧）",
                        buf.Remaining(), pkt.Data.Length);
                _pendingInput.Dequeue();
                pkt.Dispose();
                continue;
            }

            _pendingInput.Dequeue();
            try
            {
                int len = pkt.Data.Length;
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

    /// <summary>排空输出后立即补喂输入。</summary>
    /// <remarks>
    /// <b>为什么必须有这一步（真机实证，2026-08-31）</b>：MediaCodec 的输入槽位只有在
    /// 输出缓冲被 <c>ReleaseOutputBuffer</c> 归还后才会腾出来。而调用链原本是
    /// 「收包 → <see cref="FeedInput"/> → <see cref="ReadOutput"/>」，即<b>先喂、后排空</b>：
    /// 喂的时候槽位还是满的（必失败），等排空把槽位腾出来了，<b>却再没有人补喂</b>，
    /// 只能干等下一个包到来（30 包/秒）才想起喂。
    /// 更糟的是 <see cref="ReadOutput"/> 与 <see cref="DrainOutput"/> 在
    /// <c>_pendingFrames.Count &gt; 0</c> 时直接返回队首、<b>根本不走 dequeue</b>，
    /// 于是约 3/4 的调用既没排空也没补喂。
    /// 后果：解码器长期半饥饿 —— 真机日志「前 256 次排空只喂入 9 个包、累计产帧=1」、
    /// 「输入槽满累计阻塞 1280 次」，开播约 10 秒只跑出 20~27fps（应为 30），
    /// 净少约 63 帧，<c>[SYNC]</c> 队列一度归零、窗口帧数跌到 36。
    /// 画面表现即「开播十几秒糊/拖影、之后完全正常」——<b>不是像素错，是喂不饱</b>。
    /// </remarks>
    private void FeedAfterDrain()
    {
        _postDrainCalls++;
        int before = _inputQueued;
        FeedInput();
        _postDrainFed += _inputQueued - before;
    }

    /// <summary>读出一个已解出帧（先返回 FIFO 余帧，再尝试从解码器申领）。</summary>
    private VideoFrame? ReadOutput()
    {
        if (_pendingFrames.Count > 0) return _pendingFrames.Dequeue();
        return DrainOutput(0);
    }

    // ── 自适应重排缓冲 ─────────────────────────────────────────────────────────
    // MediaCodec 契约要求按**呈现序**输出，但 OMX.qcom 实测按**解码序**（B 帧重排）吐帧：
    // 真机日志 pts回退=72 次，全部集中在开播约 10 秒内（I0 → P7 → B1 → B2…）。
    // 后果：管线只 Peek 队首，队首 pts 远在未来时整队被堵（队头阻塞），主时钟追上后
    // 其后各帧又集体超时被丢 —— 开播期时序错乱/花屏。
    //
    // 关键约束：解码器领先量实测恒为负（−0.07~−1.0s，从不跑在主时钟前面），
    // 所以**不能**用「固定深度重排缓冲」——那会原样变成延迟，被 200ms 丢帧阈值吃掉。
    // 本实现只在检测到「超前跳变」时才暂存，稳态（pts 连续）逐帧直通，零额外延迟。
    private readonly List<VideoFrame> _reorder = new();
    private TimeSpan _lastReleasedPts = TimeSpan.MinValue;
    /// <summary>超过已释放 pts 多少算「超前跳变」（判定为重排的 P 帧，需等中间 B 帧补齐）。
    /// 取 50ms：30fps 帧间隔 33ms，正常连续帧不会触发；观测到的回退最小跨度 33ms、最大 200ms。</summary>
    private static readonly TimeSpan ReorderHoldAhead = TimeSpan.FromMilliseconds(50);
    /// <summary>重排缓冲深度上限（防失控；超过即强制按序释放）。</summary>
    private const int MaxReorderHold = 8;
    private int _reorderCorrections;

    /// <summary>把新帧按 pts 升序插入重排缓冲，并释放已可确定的前缀。</summary>
    private void PushFrameOrdered(VideoFrame frame)
    {
        int i = _reorder.Count;
        while (i > 0 && _reorder[i - 1].Timestamp > frame.Timestamp)
            i--;
        if (i < _reorder.Count)
            _reorderCorrections++; // 真正发生了乱序插入（插到了已有帧之前）
        _reorder.Insert(i, frame);
        FlushReorder(force: false);
    }

    /// <summary>把重排缓冲中「已确定不会再有更小 pts 插到它前面」的帧按序交出。
    /// 判定：与上一次已释放 pts 的间隔 ≤ <see cref="ReorderHoldAhead"/> 即为自然顺序的下一帧；
    /// 差距过大说明是重排的 P 帧，中间 B 帧还没到，继续攒（受深度上限与 EOS 强制约束）。</summary>
    private void FlushReorder(bool force)
    {
        while (_reorder.Count > 0)
        {
            var head = _reorder[0];
            bool confident = force
                || _reorder.Count >= MaxReorderHold
                || _lastReleasedPts == TimeSpan.MinValue
                || head.Timestamp - _lastReleasedPts <= ReorderHoldAhead;
            if (!confident)
                break;

            _reorder.RemoveAt(0);
            _pendingFrames.Enqueue(head);
            if (_lastReleasedPts != TimeSpan.MinValue && head.Timestamp < _lastReleasedPts)
                _reorderCorrections++;
            _lastReleasedPts = head.Timestamp;
        }
    }

    /// <summary>排空解码器输出，将可用帧入 FIFO，返回队首（超时内无帧返回 null）。
    /// 双路径分发：<see cref="_useAhbFrames"/> 为真走 GLES 桥接零拷贝（<see cref="DrainOutputAhb"/>），
    /// 否则走 ByteBuffer CPU 提取。</summary>
    private VideoFrame? DrainOutput(long timeoutUs)
    {
        if (_useAhbFrames)
            return DrainOutputAhb(timeoutUs);

        // 优先吐出上次已解码、尚未取走的帧：否则每取一帧都要先走一次 DequeueOutputBuffer
        // 并白等整个超时窗口（EOS 排空阶段 timeoutUs=40ms），末段上百帧将耗时数秒，
        // 且极易在等待途中被上层误判为「已排空」而提前收尾。
        if (_pendingFrames.Count > 0)
            return _pendingFrames.Dequeue();

        _drainCalls++;
        int dequeuedThisCall = 0;
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
                // ByteBuffer 路径（render=false）必须显式关闭 Image，释放 native reader。
                // 真机实证（2026-08-28）：仅 ReleaseOutputBuffer 而不 Close 会导致系统警告
                // "A resource failed to call close"，且 CCodec 把未释放的 Image 计入 client-held
                // buffer，大量出现 pipelineRoom<=numClientBuffers，解码器异常复用/丢帧 → 画面
                // 大面积块状破碎、色块错位。此处用 try/catch 保护，确保即使 Close 异常也不会
                // 阻止缓冲归还。AHB 零拷贝路径（render=true）不在此分支。
                try { image.Close(); }
                catch (Exception closeEx)
                {
                    _logger.LogDebug(closeEx, "[ANDROID-VID] Image.Close 异常（忽略，继续归还缓冲）");
                }
                _codec.ReleaseOutputBuffer(idx, false);
            }
            if (frame is null) continue;

            // pts 单调性哨兵：MediaCodec 契约要求按呈现序输出，若解码器实际按解码序（B 帧重排）
            // 吐帧，队首会变成一个远未来的 pts，管线据此「等待」而把其后所有帧堵死（队头阻塞），
            // 待主时钟追上后这些帧又集体超时被丢 —— 表现为开播期花屏/时序错乱。
            long ptsUs = info.PresentationTimeUs;
            if (_lastOutputPtsUs >= 0 && ptsUs < _lastOutputPtsUs)
            {
                _ptsRegressions++;
                if (_ptsRegressions <= 8)
                    _logger.LogWarning(
                        "[ANDROID-VID] pts 回退 #{N}: 本帧={Cur}us 上一帧={Prev}us 回退={Back:F1}ms（解码序≠呈现序？）",
                        _ptsRegressions, ptsUs, _lastOutputPtsUs, (_lastOutputPtsUs - ptsUs) / 1000.0);
            }
            _lastOutputPtsUs = ptsUs;

            if ((_framesProduced % LogInterval) == 0)
                _logger.LogInformation("[ANDROID-VID] 产帧 #{Count} {W}x{H} {Fmt} pts={Pts:g}",
                    _framesProduced, frame.Width, frame.Height, frame.Format, frame.Timestamp);
            _framesProduced++;
            _drainDequeued++;
            PushFrameOrdered(frame);

            // 单轮预算：开播期解码器积压时，无上限的排空会把全部就绪帧一次性灌进 _pendingFrames，
            // 使 in-flight 帧数远超帧池每桶容量（maxArraysPerBucket=16）。超额帧每帧都要新分配
            // 大数组、归还即弃 ⇒ 开播期垃圾风暴 ⇒ GC 停顿渲染线程。加上限把峰值压回池容量内；
            // 未取完的帧留待下一轮，不丢帧（DequeueOutputBuffer 会立即再次返回就绪缓冲）。
            if (++dequeuedThisCall >= MaxFramesPerDrain)
                break;
        }

        // 重排缓冲兜底：EOS 已入队即强制排空，绝不让末帧滞留缓冲里被 Complete 截断。
        FlushReorder(force: _eosQueued);

        // 周期性诊断（定位 dequeue 是否恒 TRY_AGAIN）
        if ((_drainCalls % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-VID] 诊断: 排空={Calls} dequeue成功={Deq} tryAgain={Try} 喂入={Fed} 累计产帧={Frames} pts回退={Reg} 重排缓冲={Hold} 校正={Fix} 待喂={Pend} 补喂={PostFed}/{PostCalls} 阻塞={Blk}",
                _drainCalls, _drainDequeued, _drainTryAgain, _inputQueued, _framesProduced, _ptsRegressions,
                _reorder.Count, _reorderCorrections, _pendingInput.Count,
                _postDrainFed, _postDrainCalls, _inputDequeueBlocked);

        return _pendingFrames.Count > 0 ? _pendingFrames.Dequeue() : null;
    }

    /// <summary>排空解码器输出（GLES 桥接零拷贝路径）：dequeueOutputBuffer → ReleaseOutputBuffer(render:true)
    /// 把帧渲进桥接 SurfaceTexture（驱动在 GPU 内完成 YUV→RGB）→ <see cref="AndroidAhbRgbaBridge.ConvertLatest"/>
    /// 闩帧并渲进 RGBA AHardwareBuffer → 包 <see cref="AndroidHardwareBufferFrameResource"/>。
    /// 全程零 CPU 像素拷贝，绕开 Adreno 对「YUV AHB + Vulkan YCbCr 采样」的原生空指针崩溃。
    /// 渲染侧据 AHB 的 ExternalFormat==0 自动路由到 <c>VulkanRgbaToRgbaConverter</c> RGBA 直通。</summary>
    /// <remarks>一帧一产：每次调用最多渲染一帧到 SurfaceTexture 并转换一帧。
    /// <see cref="AndroidAhbRgbaBridge.ConvertLatest"/> 返回 0（GL 异常态）时清空
    /// <see cref="_pendingSurfaceTextureFrame"/> 并丢弃该帧，等下一帧渲入 SurfaceTexture 再试，避免异常态空转死循环。</remarks>
    private VideoFrame? DrainOutputAhb(long timeoutUs)
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
                _eosOutputSeen = true; // DRAIN 完成判据
                break;
            }
            if (info.Size <= 0)
            {
                _codec.ReleaseOutputBuffer(idx, false);
                continue;
            }

            // 把帧渲进桥接 SurfaceTexture（render:true → 驱动等解码 fence，完成 GPU 内 YUV→RGB），
            // 后续 ConvertLatest 经 SurfaceTexture.updateTexImage 闩取。
            _lastPresentationTimeUs = info.PresentationTimeUs;
            _codec.ReleaseOutputBuffer(idx, true);
            _pendingSurfaceTextureFrame = true;
            break; // 一帧已入 SurfaceTexture，跳出交给 ConvertLatest
        }

        // 周期性诊断
        if ((_drainCalls % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-AHB-DEC] 诊断: 排空={Calls} tryAgain={Try} 喂入={Fed} 累计产帧={Frames}",
                _drainCalls, _drainTryAgain, _inputQueued, _framesProduced);

        // 仅当 SurfaceTexture 有待闩帧时才触发 GL 转换（避免空跑）。ConvertLatest 失败则清空标志、
        // 丢弃该帧（GL 异常态，等下一帧渲入 SurfaceTexture 再试），避免 updateTexImage 异常后的空转重试死循环。
        if (!_pendingSurfaceTextureFrame)
            return null;

        nint ahb = _bridge!.ConvertLatest();
        if (ahb == nint.Zero)
        {
            _logger.LogWarning("[ANDROID-AHB-DEC] ConvertLatest 返回 0（GL 异常态），丢弃该帧待下帧重试。");
            _pendingSurfaceTextureFrame = false;
            return null;
        }
        _pendingSurfaceTextureFrame = false;

        int w = _bridge.FrameWidth;
        int h = _bridge.FrameHeight;
        TimeSpan pts = _lastPresentationTimeUs >= 0
            ? TimeSpan.FromTicks(_lastPresentationTimeUs * 10) // us → ticks（1 tick = 100ns）
            : TimeSpan.Zero;

        // AHB 引用所有权已由桥接移交本帧资源；帧 Dispose 时经 AHardwareBuffer_release 释放，
        // 渲染侧 Vulkan 导入持有独立引用，无悬挂/双释放。
        var resource = new AndroidHardwareBufferFrameResource(ahb, w, h, PixelFormat.RGBA32);
        if ((_framesProduced % LogInterval) == 0)
            _logger.LogInformation("[ANDROID-AHB-DEC] 产帧 #{Count} {W}x{H} {Fmt} pts={Pts:g}",
                _framesProduced, w, h, PixelFormat.RGBA32, pts);
        _framesProduced++;
        _drainDequeued++;
        return new VideoFrame(w, h, PixelFormat.RGBA32, resource, pts, TimeSpan.Zero, false, _colorInfo);
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

        // ── 几何变化探测：与上一帧逐项比对，变化即打点（开播前 8 帧无条件打点作基线）──
        if (!_geoInit
            || vw != _geoVw || vh != _geoVh || cl != _geoCl || ct != _geoCt
            || yRowStride != _geoYRow || uvRowStride != _geoUvRow || uvPixelStride != _geoUvPix)
        {
            bool first = !_geoInit;
            if (!first) _geoChanges++;
            // 变化/首帧/开播窗口内必打；稳态每 120 帧打一次心跳，证明确实「一直没变」。
            if (first || _framesProduced < 8 || (_framesProduced % 120) == 0 || _geoChanges <= 8)
            {
                _logger.LogInformation(
                    "[ANDROID-VID] 帧几何{Flag} seq={Seq} pts={Pts}ms 可见={VW}x{VH} crop=({CL},{CT}) " +
                    "yRow={YRow} yPix={YPix} uvRow={UvRow} uvPix={UvPix} 快路径={Fast} 累计变化={Changes}",
                    first ? "基线" : "变化", _framesProduced, infoPtsUs / 1000,
                    vw, vh, cl, ct, yRowStride, yPixelStride, uvRowStride, uvPixelStride,
                    fastNv12 ? "NV12" : "I420", _geoChanges);
            }
            _geoVw = vw; _geoVh = vh; _geoCl = cl; _geoCt = ct;
            _geoYRow = yRowStride; _geoUvRow = uvRowStride; _geoUvPix = uvPixelStride;
            _geoInit = true;
        }

        var resource = new SoftwareFrameResource(vw, vh, outFmt, checked(totalBytes));
        Span<byte> dst = resource.Data.Span;

        // ── 平面统计采样 ──
        // 采样策略：开播前 8 帧全采（对齐「花屏窗口」），之后每 120 帧一采作稳态基线。
        // 判读：若开播期 U/V 均值·范围与稳态显著不同 ⇒ 解码器早期输出本身异常（渲染侧无责）；
        //       若两者一致 ⇒ 解码输出正常，问题在下游（时序/缓冲/上屏）。
        bool diag = _framesProduced < 8 || (_framesProduced % 120) == 0;
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
            CopyRow(_extractRaw, srcOff, vw, dst, yDst, yCap);
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

        // 缓冲上界诊断（仅首帧）：_extractRaw 被 Y/U/V 轮流复用，数组长度 = 三者最大容量。
        // 若 uCap/vCap 远小于 _extractRaw.Length，说明 UV 行按数组长度取上界会读到残留 Y 数据
        // ——这正是画面下半色度错乱（块状破碎/色块错位）的成因。打印三值以便真机一锤定音。
        if (diag)
        {
            int uCapDiag = uBuf.Capacity();
            int vCapDiag = vBuf.Capacity();
            int lastUvSrcOff = (chromaRow0 + ch - 1) * uPlane.RowStride + chromaCol0 * 2;
            _logger.LogInformation(
                "[ANDROID-VID] 缓冲上界诊断: _extractRaw.Length={RawLen} yCap={YCap} uCap={UCap} vCap={VCap} " +
                "yRowStride={YRow} uvRowStride={UvRow} 末行UV源偏移={LastOff} 末行需要末字节={LastNeed} " +
                "按数组长度是否越界={OverByArray} 按uCap是否越界={OverByCap}",
                _extractRaw.Length, yCap, uCapDiag, vCapDiag, yRowStride, uvRowStride, lastUvSrcOff,
                lastUvSrcOff + (fastNv12 ? uvRowBytes : cw),
                lastUvSrcOff + (fastNv12 ? uvRowBytes : cw) > _extractRaw.Length,
                lastUvSrcOff + (fastNv12 ? uvRowBytes : cw) > uCapDiag);
        }

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
                // 上界必须是本平面实际装载长度 uCap：_extractRaw 为 Y 平面分配时远大于 UV 容量，
                // 按数组长度计算会让 UV 行越过有效边界读到残留 Y 像素（真机画面破碎根因）。
                int avail = uCap - srcOff;
                int copy = avail >= uvRowBytes ? uvRowBytes : avail > 0 ? avail : 0;
                // 越界检测：avail < 本行所需 ⇒ 该行尾部会读到残留数据（上一帧的 Y 像素），
                // 画面表现为块状破碎/色块错位。首帧诊断（[ANDROID-VID] 缓冲上界诊断）只在
                // _extractRaw 首次分配时打印、天然不越界，**覆盖不到稳态帧**，故在此逐帧检测。
                if (avail < uvRowBytes && !_uvClampWarned)
                {
                    _uvClampWarned = true;
                    // 注意：此处不用 `_logger?.`——_logger 为非空字段（构造函数已 `?? throw` 保证），
                    // 一旦使用 null 条件运算符，编译器会把后续所有不带 ? 的 _logger 调用判为 CS8604。
                    _logger.LogWarning(
                        "[ANDROID-VID] UV 行读取越界被截断（将读到残留数据→画面破碎）: " +
                        "帧=#{Frame} cy={Cy} srcOff={Off} uCap={Cap} 本行需要={Need} 实际可拷={Copy} " +
                        "uvRowStride={Stride} 可见={VW}x{VH}",
                        _framesProduced, cy, srcOff, uCap, uvRowBytes, copy, uvRowStride, vw, vh);
                }
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
                CopyRow(_extractRaw, srcRow, cw, dst, uDst + cy * cw, uCap);
                if (diag) AccumPlaneStats(dst, uDst + cy * cw, cw, ref uSum, ref uMin, ref uMax, ref uNonZero);
            }
            int vCap = vBuf.Capacity();
            if (_extractRaw.Length < vCap) _extractRaw = new byte[vCap];
            vBuf.Rewind();
            vBuf.Get(_extractRaw, 0, vCap);
            for (int cy = 0; cy < ch; cy++)
            {
                int srcRow = (chromaRow0 + cy) * uvRowStride + chromaCol0;
                CopyRow(_extractRaw, srcRow, cw, dst, vDst + cy * cw, vCap);
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
            int elemCap = uCap; // 同 fastNv12：上界取本平面装载长度，非数组长度
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
                "[ANDROID-VID] 平面统计 seq={Seq} 可见={VW}x{VH} fmt={Fmt} crop=({CL},{CT}) | " +
                "Y 均值={YM:g} 范围[{Ymin},{Ymax}] 非零{Ynz}% | " +
                "U 均值={UM:g} 范围[{Umin},{Umax}] 非零{Unz}% | " +
                "V 均值={VM:g} 范围[{Vmin},{Vmax}] 非零{Vnz}%",
                _framesProduced,
                vw, vh, fastNv12 ? "NV12(semiplanar,U-even,V-odd)" : uvPixelStride == 1 ? "I420(planar)" : "I420(fallback,semiplanar)",
                cl, ct,
                (double)ySum / yTot, yMin, yMax, yNonZero * 100 / yTot,
                (double)uSum / cTot, uMin, uMax, uNonZero * 100 / cTot,
                (double)vSum / cTot, vMin, vMax, vNonZero * 100 / cTot);
            if (_framesProduced == 0)
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

        // 全链路指纹（与 SkiaVideoPresenter 的 [FP-PRESENT] 同口径）：开播前 6 帧全采，之后每 60 帧一采。
        // 同 PTS 下 [FP-DEC].fp 与 [FP-PRESENT].src 必须逐字节相同；不同 ⇒ 帧数据在管线途中被覆写
        // （池化复用 / 缓冲提前归还），正是「开播激流期花屏、稳态正常」的典型特征。
        if (_framesProduced < 6 || (_framesProduced % 60) == 0)
        {
            var d2 = resource.Data.Span;
            int fpYRow = Math.Min(500, Math.Max(0, vh - 1));
            int fpCRow = Math.Min(250, Math.Max(0, vh / 2 - 1));
            var sb = new System.Text.StringBuilder(40);
            for (int i = 0; i < 8; i++)
            {
                int ix = fpYRow * vw + 16 + i;
                sb.Append(ix >= 0 && ix < d2.Length ? d2[ix].ToString("X2") : "--");
            }
            sb.Append('/');
            for (int i = 0; i < 8; i++)
            {
                int ix = vw * vh + fpCRow * vw + 16 + i;
                sb.Append(ix >= 0 && ix < d2.Length ? d2[ix].ToString("X2") : "--");
            }
            _logger.LogInformation("[FP-DEC] seq={Seq} pts={Pts} fmt={Fmt} {W}x{H} fp={Fp}",
                _framesProduced, pts, outFmt, vw, vh, sb.ToString());
        }

        return new VideoFrame(vw, vh, outFmt, resource, pts, TimeSpan.Zero, keyFrame, _colorInfo);
    }

    /// <summary>按源有效长度拷贝一行（越界时只拷可用部分，不整行丢弃，避免对齐填充误差导致整行变 0）。</summary>
    /// <param name="src">源缓冲（被 Y/U/V 三个平面轮流复用，故 <c>src.Length</c> 不等于本平面有效长度）。</param>
    /// <param name="srcOff">本行在源缓冲内的字节偏移。</param>
    /// <param name="count">期望拷贝字节数。</param>
    /// <param name="dst">目标紧凑平面。</param>
    /// <param name="dstOff">目标偏移。</param>
    /// <param name="srcValid">本平面实际装载的有效字节数（= plane.Buffer.Capacity()）。
    /// <b>必须</b>用它而非 <c>src.Length</c> 做上界：源缓冲为 Y 平面分配时远大于 UV 容量，
    /// 若按 <c>src.Length</c> 计算可用量，UV 行会越过有效数据边界读到残留的 Y 像素，
    /// 导致画面下半色度错乱（真机实证：大面积块状破碎、色块错位、拖影）。</param>
    private static void CopyRow(byte[] src, int srcOff, int count, Span<byte> dst, int dstOff, int srcValid)
    {
        if (srcOff < 0 || count <= 0) return;
        int limit = Math.Min(src.Length, srcValid);
        int n = Math.Min(count, limit - srcOff);
        if (n <= 0) return;
        new ReadOnlySpan<byte>(src, srcOff, n).CopyTo(dst.Slice(dstOff, n));
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