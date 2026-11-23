using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Skia 视频呈现器（Avalonia UI 层）。IVideoPresenter 的默认实现，复用 Avalonia 渲染管线的 SkiaSharp 实例。
/// </summary>
/// <remarks>
/// <para><b>关键原则</b>：不独立引用 SkiaSharp NuGet 包，通过 Avalonia 的 WriteableBitmap
/// 复用底层 SkiaSharp 渲染实例，避免版本冲突、原生库冲突和 AOT 部署问题。</para>
/// <para><b>线程模型</b>：</para>
/// <list type="bullet">
/// <item>WriteableBitmap 有线程亲缘性——<b>创建 / Lock 写入 / DrawImage 必须发生在同一线程</b>（Avalonia 渲染线程）。
/// 跨线程访问会抛 InvalidOperationException 致渲染线程崩溃，故位图绝不能在管线线程创建或写入。</item>
/// <item>本实现将帧像素拷贝与位图绘制解耦：<see cref="Present"/>（管线线程）仅把帧像素转换/拷贝进线程安全的
/// <c>_staging</c> 缓冲（跨线程只读借用，不碰 WriteableBitmap）；<see cref="Render"/>（渲染线程）在锁定内
/// 于<b>渲染线程</b>创建/更新 WriteableBitmap 并 DrawImage。两者经 <c>_gate</c> 同步，规避跨线程亲缘性。</item>
/// </list>
/// <para><b>异步策略</b>：全部同步（Present/Clear/Resize/Render 纯内存 + 像素拷贝；帧所有权归管线，
/// Present 完成后由管线 ReturnFrame，本类绝不 Dispose 帧）。无伪异步——void 方法体内无 await。</para>
/// <para><b>容错</b>：单帧异常（如 GPU 纹理回读失败）在 Present 内吞掉，绝不向上抛到管线线程击杀播放；
/// 不支持的帧格式跳过该帧。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射；YUV→BGRA 用预计算只读 LUT（short[]，类型初始化期构造）。</para>
/// </remarks>
public sealed partial class SkiaVideoPresenter : IVideoPresenter
{
    private readonly object _gate = new();

    /// <summary>
    /// staging 缓冲池（<b>多缓冲 + 归还式所有权</b>）：
    /// <c>_free</c> 是空闲缓冲队列，<c>_pending</c> 是已写好、等待渲染线程取走的那一帧。
    /// 管线线程（Present）只从 <c>_free</c> 取缓冲写入；渲染线程（Render）取走 <c>_pending</c>
    /// 后<b>独占</b>它，直到像素拷贝完成才归还 <c>_free</c>。
    /// </summary>
    /// <remarks>
    /// <b>为何不能沿用双缓冲 ping-pong</b>：位图 <c>Lock()</c> 与像素拷贝必须留在 <c>_gate</c> 之外
    /// （<c>Lock()</c> 在 Android 上会等 GPU/合成器完成上一帧，被 vsync 节流，耗时可达一个刷新周期量级以上；
    /// 留在锁内会让管线线程阻塞等 vsync，呈现耗时显著抬高、帧率明显下降）。
    /// 但只有两块缓冲时，锁外的拷贝期间管线线程足足可以写满两帧 ——
    /// <b>Present #(n+2) 写的正是 Render #n 还在读的那一块</b>，于是半帧被覆写，画面花屏。
    /// <para>归还式所有权把缓冲的生命周期与「拷贝是否结束」绑定：渲染线程未归还前，
    /// Present 在结构上不可能拿到它。多缓冲（3+1）确保管线线程领先渲染线程时也只是丢帧，
    /// 而绝不会写坏正在上屏的像素。</para>
    /// </remarks>
    private readonly Queue<byte[]> _free = new();
    private byte[]? _pending;
    private int _pendingW;
    private int _pendingH;
    /// <summary>自由队列容量上限（不含正被渲染线程持有的那一块）。池容量有界，避免囤积显存级大数组。</summary>
    private const int MaxFreeBuffers = 3;
    /// <summary>尚未被渲染线程取走就被下一帧顶掉的帧数（渲染线程落后的直接度量）。</summary>
    private int _framesOverwritten;
    /// <summary>因上一帧未被取走而<b>跳过转换直接丢弃</b>的帧数（省下的转换次数）。</summary>
    private int _framesSkipped;
    /// <summary><c>_pending</c> 落定的时刻，用于判定它是否已"太旧"必须刷新。</summary>
    private long _pendingTicks;
    /// <summary><c>_pending</c> 的保鲜窗口：在此窗口内未被取走则跳过本帧转换。
    /// 取 50ms（≈1.5 个 30fps 帧距）：渲染线程按 ~66ms 间隔取帧，稳态可省掉约一半转换；
    /// 超过窗口则强制刷新，保证渲染线程卡住时画面不会永久定格。</summary>
    private static readonly long PendingFreshWindowTicks = Stopwatch.Frequency / 20;

    /// <summary>WriteableBitmap（仅在渲染线程创建/写入/绘制，规避线程亲缘性）。</summary>
    private WriteableBitmap? _bitmap;
    private int _bitmapW;
    private int _bitmapH;
    private global::Avalonia.Platform.PixelFormat _bitmapFormat = global::Avalonia.Platform.PixelFormat.Bgra8888;

    private int _targetW;
    private int _targetH;
    private float _scale = 1.0f;
    private bool _disposed;

    // 分体计时（仅诊断，不影响算法）：累计 YUV→BGRA 转换耗时，周期报平均，定位卡顿瓶颈。
    private int _convertSamples;
    private long _convertTicks;
    // 转换性能采样间隔。取小值 8：呈现帧率极低的场景（CPU 软渲）下大阈值永远达不到，
    // 诊断形同虚设；小间隔使早期即可
    // 拿到 YUV→BGRA 真实耗时（用于区分「转换慢」vs「等时钟/等队列」）。
    private const int ConvertLogInterval = 8;
    // 位图 Lock+拷贝耗时诊断（区分「像素转换慢」与「位图上传/等 vsync 慢」）。
    private int _uploadSamples;
    private long _uploadTicks;
    private const int UploadLogInterval = 30;
    // 渲染线程整帧计时：Render() 自身的耗时、DrawImage 耗时，以及两次 Render 的调用间隔。
    // 三者的关系直接回答「上屏 fps 是谁限的」：间隔大而自身耗时小 ⇒ 合成器/调度在等；
    // 自身耗时长 ⇒ 渲染线程自己就是瓶颈。
    private int _renderSamples;
    private long _renderTicks;
    private long _drawTicks;
    private long _renderGapTicks;
    private long _lastRenderTs;
    private const int RenderLogInterval = 30;
    private static bool _diagOnce;
    private static ILogger? _diagLogger;
    /// <summary>首帧渲染输出 BGRA 统计一次性标志（验证 YUV→BGRA 转换结果）。</summary>
    private static bool _renderDiagLogged;
    private readonly ILogger? _logger;

    // 全链路指纹诊断序号（定位「前段花屏、后段正常」时光途中哪一段被覆写）。
    private int _fpSeq;
    private int _fpRenderSeq;

    /// <summary>指纹采样节流：前 6 帧全采（覆盖起播瞬态），之后每 60 帧一采。</summary>
    private static bool FpDue(int seq) => seq < 6 || (seq % 60) == 0;

    // 帧水印诊断
    // 用途：把「帧序号 + 色标条 + 竖条纹」直接烧进 staging 左上角，肉眼截屏即可判定花屏归属：
    //   · 序号连续递增      ⇒ 时序正常，问题在**像素内容本身**
    //   · 序号回跳/重复/跳号 ⇒ 时序错乱（丢帧、乱序、缓冲轮转）
    //   · 色标条偏色        ⇒ YUV→RGB 矩阵或色彩区间错误
    //   · 竖条纹变斜/错位   ⇒ 行距（stride）处理错误
    // 诊断开关默认关闭 —— 水印会遮挡画面且每帧多写大量像素。
    // static readonly 而非 const：调用点保留完整分支（false 时运行时跳过），避免 const 折叠
    // 产生 CS0162 不可达代码（TreatWarningsAsErrors 下即编译错误），重开诊断只改这一处。
    private static readonly bool FrameWatermark = false;
    private int _presentSeq;

    /// <summary>5×7 点阵数字字形（'1' 为亮点），行优先，每个数字 35 个字符（7 行 × 5 列）。</summary>
    private static readonly string[] DigitFont =
    {
        "01110" + "10001" + "10001" + "10001" + "10001" + "10001" + "01110", // 0
        "00100" + "01100" + "00100" + "00100" + "00100" + "00100" + "01110", // 1
        "01110" + "10001" + "00001" + "00010" + "00100" + "01000" + "11111", // 2
        "11111" + "00010" + "00100" + "00010" + "00001" + "10001" + "01110", // 3
        "00010" + "00110" + "01010" + "10010" + "11111" + "00010" + "00010", // 4
        "11111" + "10000" + "11110" + "00001" + "00001" + "10001" + "01110", // 5
        "00110" + "01000" + "10000" + "11110" + "10001" + "10001" + "01110", // 6
        "11111" + "00001" + "00010" + "00100" + "01000" + "01000" + "01000", // 7
        "01110" + "10001" + "10001" + "01110" + "10001" + "10001" + "01110", // 8
        "01110" + "10001" + "10001" + "01111" + "00001" + "00010" + "01100", // 9
    };

    private static readonly int[] Pow10 = { 1, 10, 100, 1000 };

    /// <summary>初始化 Skia 呈现器。可选注入日志器，用于暴露帧转换失败（避免静默白屏）。</summary>
    /// <param name="logger">可选日志器；为 null 时不记录。</param>
    public SkiaVideoPresenter(ILogger? logger = null)
    {
        _logger = logger;
        _diagLogger = logger;
    }

    /// <summary>宽高比模式。</summary>
    public AspectRatioMode AspectRatioMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>诊断访问：当前 WriteableBitmap（无帧时为 null）。</summary>
    internal WriteableBitmap? DebugBitmap => _bitmap;

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targetW = target.Width;
        _targetH = target.Height;
        _scale = target.Scale;
    }

    /// <summary>从自由队列取一块容量 ≥ <paramref name="need"/> 的缓冲；无可用则新分配。
    /// 只在 <c>_gate</c> 内调用。容量不足的旧缓冲直接丢弃（尺寸变更才发生，罕见）。</summary>
    private byte[] RentBuffer(int need)
    {
        while (_free.Count > 0)
        {
            byte[] candidate = _free.Dequeue();
            if (candidate.Length >= need)
                return candidate;
        }
        return new byte[need];
    }

    /// <summary>把缓冲还给自由队列；超出 <see cref="MaxFreeBuffers"/> 即丢弃交由 GC，保证池容量有界。
    /// 只在 <c>_gate</c> 内调用，且调用者必须确保该缓冲已无任何读者。</summary>
    private void ReturnBuffer(byte[] buffer)
    {
        if (_free.Count < MaxFreeBuffers)
            _free.Enqueue(buffer);
    }

    /// <inheritdoc/>
    public void Render(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_disposed)
            return;

        long renderStart = Stopwatch.GetTimestamp();
        long nowTs = renderStart;
        long gap = _lastRenderTs == 0 ? 0 : nowTs - _lastRenderTs;
        _lastRenderTs = nowTs;

        // 阶段 1（临界区，必须极短）
        // 仅做「取走待渲染帧」。绝不在锁内调用 WriteableBitmap.Lock()：
        // 它在 Android 上会等待 GPU/合成器完成上一帧（被 vsync 节流，耗时可达刷新周期量级以上），
        // 一旦留在锁内，管线线程的 Present 就会阻塞等 vsync —— 呈现耗时会大幅抬高、
        // 帧率显著下降。
        byte[]? snapshot = null;
        int sw = 0, sh = 0;
        lock (_gate)
        {
            if (_pending is not null && _pendingW > 0 && _pendingH > 0)
            {
                // 取走待渲染帧：此后渲染线程独占该缓冲，Present 拿不到它（只能从 _free 取），
                // 直到阶段 2 结束显式归还。这是消除半帧覆写的根本保证。
                snapshot = _pending;
                _pending = null;
                sw = _pendingW;
                sh = _pendingH;
            }
        }

        // 阶段 2（锁外）
        // 位图创建、Lock、像素拷贝均在此完成。此时 Present 不受阻塞，可继续写后续帧到
        // _free 提供的其它缓冲。（渲染线程独占 snapshot，Present 结构上取不到它，故无需加锁。）
        if (snapshot is not null)
        {
            // FP-RENDER：渲染线程实际拷进位图的 snapshot 字节。与同帧 [FP-PRESENT] 的 stg 逐字节比对：
            // 一致 ⇒ 渲染线程拿到的就是本帧刚写出的内容（缓冲所有权独占，未被任何人覆写）；
            // 不一致 ⇒ 该缓冲在渲染线程取走前已被后续 Present 顶掉（渲染线程落后于管线，表现为跳帧）。
            // 注意：在新模型下失配只可能是「跳帧」，绝不再是「拷贝到一半被覆写」——后者已由
            // 归还式所有权从结构上消除。
            if (FpDue(_fpRenderSeq))
                _logger?.LogInformation("[FP-RENDER] seq={Seq} {W}x{H} snap={Snap}",
                    _fpRenderSeq, sw, sh, FpBgra(snapshot, sw, sh));
            _fpRenderSeq++;

            EnsureBitmap(sw, sh, _bitmapFormat);
            if (_bitmap is not null)
            {
                long lockStart = Stopwatch.GetTimestamp();
                using (var locked = _bitmap.Lock())
                {
                    // staging 为紧凑 BGRA（stride = w*4）；位图 RowBytes 由平台/后端决定，
                    // 可能含对齐填充而**不等于** w*4。必须按行拷贝、各自按自身 stride 步进：
                    // 整块拷贝会让每行累积错位（第 n 行偏 n×(RowBytes−w×4) 字节），
                    // 表现为画面块状破碎、色块错位、拖影（此类破碎的成因）。
                    int srcStride = sw * 4;
                    int dstStride = locked.RowBytes;

                    if (!_strideDiagLogged)
                    {
                        _strideDiagLogged = true;
                        _logger?.LogInformation(
                            "[SKIA-STRIDE] staging stride={SrcStride} bitmap RowBytes={DstStride} " +
                            "尺寸={W}x{H} 行距是否一致={Same}（不一致时按行拷贝，整块拷贝会导致行错位）",
                            srcStride, dstStride, sw, sh, srcStride == dstStride);
                    }

                    if (srcStride == dstStride)
                    {
                        // 行距一致：整块拷贝（**单次** P/Invoke，最快）。
                        System.Runtime.InteropServices.Marshal.Copy(
                            snapshot, 0, locked.Address, sh * srcStride);
                    }
                    else
                    {
                        // 行距不一致（位图含对齐填充）：必须按行拷贝，否则每行累积错位 → 画面块状破碎。
                        // 用 Span 逐行拷贝而非逐行 Marshal.Copy —— 纯托管内存复制，规避逐行 P/Invoke 开销。
                        int copyBytes = Math.Min(srcStride, dstStride);
                        unsafe
                        {
                            var dst = new Span<byte>((void*)locked.Address, sh * dstStride);
                            for (int y = 0; y < sh; y++)
                            {
                                snapshot.AsSpan(y * srcStride, copyBytes)
                                        .CopyTo(dst.Slice(y * dstStride, copyBytes));
                            }
                        }
                    }
                }

                // 位图上传耗时诊断（每 60 帧一次）：区分「转换慢」与「位图 Lock/拷贝慢」。
                _uploadSamples++;
                _uploadTicks += Stopwatch.GetElapsedTime(lockStart).Ticks;
                if (_uploadSamples >= UploadLogInterval)
                {
                    double avgMs = Stopwatch.GetElapsedTime(0, _uploadTicks).TotalMilliseconds / _uploadSamples;
                    int freeDepth;
                    int overwritten;
                    int skipped;
                    lock (_gate)
                    {
                        freeDepth = _free.Count;
                        overwritten = _framesOverwritten;
                        _framesOverwritten = 0;
                        skipped = _framesSkipped;
                        _framesSkipped = 0;
                    }
                    _logger?.LogInformation(
                        "[SKIA-UPLOAD] 位图 Lock+拷贝性能: 样本={N} 平均={AvgMs:F2}ms/帧 尺寸={W}x{H} " +
                        "缓冲池自由={Free} 未渲染被顶掉={Ovr} 跳过转换={Skip}（后两者>0 即渲染线程落后于管线）",
                        _uploadSamples, avgMs, sw, sh, freeDepth, overwritten, skipped);
                    _uploadSamples = 0;
                    _uploadTicks = 0;
                }
            }

            // 首帧渲染输出统计：验证 YUV→BGRA 转换结果。对比解码侧 Y 均值（本轮=110），
            // 中性灰/正常画面下 B/G/R 均值应彼此接近且 ≈Y；若显著偏色（R≫G/B 或整体过暗/过亮）
            // 即转换 bug（「糊」在渲染侧）；若 RGB 正常则「糊」在解码输出（需 dump 解码 YUV 对比）。
            if (!_renderDiagLogged)
            {
                _renderDiagLogged = true;
                int n = sw * sh;
                long bSum = 0, gSum = 0, rSum = 0;
                int px = 0;
                for (int i = 0; i + 3 < snapshot.Length && px < n; i += 4, px++)
                {
                    bSum += snapshot[i];
                    gSum += snapshot[i + 1];
                    rSum += snapshot[i + 2];
                }
                _logger?.LogInformation(
                    "[SKIA-RENDER-DIAG] 渲染输出 BGRA 统计 尺寸={W}x{H} | B均值={B:g} G均值={G:g} R均值={R:g}",
                    sw, sh, (double)bSum / n, (double)gSum / n, (double)rSum / n);
            }

            // 像素已全部拷进位图，snapshot 不再有读者 —— 此刻才归还自由队列。
            // 归还前 Present 只能从 _free 取缓冲，拿不到这块，因此不存在「拷贝到一半被覆写」。
            lock (_gate) { ReturnBuffer(snapshot); }
        }

        if (_bitmap is not null && _bitmapW > 0 && _bitmapH > 0)
        {
            // 单位统一为 DIP（设备无关像素）：
            // - WriteableBitmap 的 DPI 已固定为 96（见 EnsureBitmap），故其逻辑尺寸 = 物理像素尺寸，
            //   即 _bitmapW/_bitmapH 可直接当作 DIP 帧尺寸参与布局计算。
            // - _targetW/_targetH 来自 OnSizeChanged 的 e.NewSize（DIP），无需再乘 scale。
            // 禁止把「物理像素帧」与「DIP target」混算、或把 bitmap DPI 设为 96*scale：
            // DrawImage 会再按 DPI 缩放一次 → 实际绘制放大 scale 倍、只显示左上角局部（溢出）。
            var destRect = CalculateDestRect(_bitmapW, _bitmapH, _targetW, _targetH, AspectRatioMode);

            // 一次性诊断（溢出定位）：目标区/位图/目标矩形/模式/scale。destRect 必须落在
            // [0,0,_targetW,_targetH] 内（Uniform 模式数学上不溢出）。
            if (!_destRectLogged)
            {
                _destRectLogged = true;
                _logger?.LogInformation(
                    "[SKIA-PRESENT] 首帧绘制 target={TW}x{TH}(DIP) bitmap={BW}x{BH}(DIP) scale={Scale} mode={Mode} destRect={Rect} 设备缩放比={Ratio:F3} 插值={Interp}",
                    _targetW, _targetH, _bitmapW, _bitmapH, _scale, AspectRatioMode, destRect,
                    DeviceScaleRatio(destRect), ChooseInterpolation(destRect));
            }

            // 硬裁剪保险：画面绘制永远不越过控件边界（防御 destRect 异常导致的"溢出屏幕"）。
            using var clip = drawingContext.PushClip(new Rect(0, 0, _targetW, _targetH));
            // 插值模式按**设备缩放比**选择，不能一律 HighQuality：
            // HighQuality（三次插值，逐像素 16 taps）只在重度缩小时才需要（抑制摩尔纹），
            // 而在 1:1 贴块时是纯粹的浪费——位图设备像素与 destRect 设备像素相等（比值=1.0）时，
            // 每帧为全分辨率像素白付三次插值开销，可把渲染线程拖到远低于管线能力。
            using var opts = drawingContext.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = ChooseInterpolation(destRect),
            });
            long drawStart = Stopwatch.GetTimestamp();
            drawingContext.DrawImage(_bitmap, destRect);
            _drawTicks += Stopwatch.GetElapsedTime(drawStart).Ticks;
        }

        // 渲染线程整帧诊断：区分「渲染线程自己慢」与「合成器调用频率低」。
        // 未渲染被顶掉的计数持续为正 ⇒ 上屏 fps 低于管线 fps，须结合本组计时定位瓶颈归属。
        _renderSamples++;
        _renderTicks += Stopwatch.GetElapsedTime(renderStart).Ticks;
        _renderGapTicks += gap;
        if (_renderSamples >= RenderLogInterval)
        {
            double selfMs = Stopwatch.GetElapsedTime(0, _renderTicks).TotalMilliseconds / _renderSamples;
            double drawMs = Stopwatch.GetElapsedTime(0, _drawTicks).TotalMilliseconds / _renderSamples;
            double gapMs = Stopwatch.GetElapsedTime(0, _renderGapTicks).TotalMilliseconds / _renderSamples;
            _logger?.LogInformation(
                "[SKIA-RENDER] 渲染线程: 样本={N} 调用间隔={Gap:F1}ms(⇒{Fps:F1}fps) Render自身={Self:F2}ms DrawImage={Draw:F2}ms",
                _renderSamples, gapMs, gapMs > 0 ? 1000.0 / gapMs : 0, selfMs, drawMs);
            _renderSamples = 0;
            _renderTicks = 0;
            _drawTicks = 0;
            _renderGapTicks = 0;
        }
    }

    // 溢出诊断一次性标志（[SKIA-PRESENT] 首帧绘制）
    private bool _destRectLogged;
    // 行距诊断一次性标志（[SKIA-STRIDE] staging stride vs bitmap RowBytes）
    private bool _strideDiagLogged;

    /// <summary>
    /// 确保 WriteableBitmap 存在且尺寸/格式匹配（不匹配则重建）。始终在渲染线程调用。
    /// </summary>
    /// <remarks>
    /// <b>DPI 固定 96（=1.0 逻辑缩放）</b>：Avalonia 的 <see cref="WriteableBitmap"/> 逻辑尺寸 =
    /// <c>PixelSize / Dpi * 96</c>；DPI 设为 96 时逻辑尺寸 == 物理像素尺寸，与
    /// <see cref="CalculateDestRect"/> 传入的 DIP 帧/目标尺寸单位完全一致，<c>DrawImage</c>
    /// 不会再二次缩放。禁止把 DPI 设为 <c>96 * _scale</c>：那会使 bitmap 逻辑尺寸缩小为 1/scale，与 DIP 目标
    /// 混算后被 DrawImage 再放大 scale 倍，画面只显示左上角局部（溢出观感）。
    /// </remarks>
    private void EnsureBitmap(int width, int height, global::Avalonia.Platform.PixelFormat format)
    {
        if (_bitmap is null || _bitmapW != width || _bitmapH != height || _bitmapFormat != format)
        {
            _bitmap?.Dispose();
            _bitmapW = width;
            _bitmapH = height;
            _bitmapFormat = format;
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96.0, 96.0),
                format);
        }
    }

    // YUV → BGRA 转换（BT.601 全范围 JFIF 矩阵，预计算 LUT）

    private static readonly short[] Rv = BuildYuvLut(d => 1.402f * d);
    private static readonly short[] Gu = BuildYuvLut(d => -0.344136f * d);
    private static readonly short[] Gv = BuildYuvLut(d => -0.714136f * d);
    private static readonly short[] Bu = BuildYuvLut(d => 1.772f * d);

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_gate)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }

    /// <inheritdoc/>
    public void Resize(int width, int height, float scale)
    {
        _targetW = width;
        _targetH = height;
        _scale = scale;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _free.Clear();
            _pending = null;
        }
    }
}
