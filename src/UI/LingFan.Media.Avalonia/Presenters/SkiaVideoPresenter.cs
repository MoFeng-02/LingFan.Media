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
/// <para><b>线程模型修正（修复不出画/冻屏成因）</b>：</para>
/// <list type="bullet">
/// <item>WriteableBitmap 有线程亲缘性——<b>创建 / Lock 写入 / DrawImage 必须发生在同一线程</b>（Avalonia 渲染线程）。
/// 旧实现在管线线程创建并写入位图、渲染线程 DrawImage，跨线程访问抛 InvalidOperationException 致渲染线程崩溃，
/// 表现为「不出画 + 应用冻死（播几秒音乐就没动静）」。</item>
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
public sealed class SkiaVideoPresenter : IVideoPresenter
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
    /// （<c>Lock()</c> 在 Android 上会等 GPU/合成器完成上一帧，被 vsync 节流，实测可达数十 ms；
    /// 留在锁内会让管线线程阻塞等 vsync，真机实证呈现耗时 35~45ms/帧、仅 ~20fps）。
    /// 但只有两块缓冲时，锁外的拷贝期间管线线程足足可以写满两帧 ——
    /// <b>Present #(n+2) 写的正是 Render #n 还在读的那一块</b>，于是半帧被覆写，画面花屏。
    /// 真机日志佐证：<c>[FP-PRESENT] seq=5 stg=…4C5562FF</c> 与
    /// <c>[FP-RENDER] seq=5 snap=…4B5461FF</c> 同址不同值（B/G/R 各差 1），即渲染线程读到的
    /// 已不是本帧写入的内容。
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
    // 转换性能采样间隔。原为 64，但真机实测呈现帧率极低（1080x1920 CPU 软渲 ≈1fps，
    // 整段播放仅呈现 20 余帧）→ 64 帧阈值永远达不到，诊断形同虚设。降到 8，使早期即可
    // 拿到 YUV→BGRA 真实耗时（用于区分「转换慢」vs「等时钟/等队列」）。
    private const int ConvertLogInterval = 8;
    // 位图 Lock+拷贝耗时诊断（区分「像素转换慢」与「位图上传/等 vsync 慢」）。
    private int _uploadSamples;
    private long _uploadTicks;
    private const int UploadLogInterval = 30;
    // 渲染线程整帧计时：Render() 自身的耗时、DrawImage 耗时，以及两次 Render 的调用间隔。
    // 三者的关系直接回答「上屏 fps 是谁限的」：间隔大而自身耗时小 ⇒ 合成器/调度在等；
    // 自身耗时长 ⇒ 渲染线程自己就是瓶颈（此前只测了 Lock+拷贝，DrawImage 一直没测）。
    private int _renderSamples;
    private long _renderTicks;
    private long _drawTicks;
    private long _renderGapTicks;
    private long _lastRenderTs;
    private const int RenderLogInterval = 30;
    private static bool _diagOnce;
    private static ILogger? _diagLogger;
    /// <summary>首帧渲染输出 BGRA 统计一次性标志（验证 YUV→BGRA 转换结果，二分定位「糊」在解码侧还是渲染侧）。</summary>
    private static bool _renderDiagLogged;
    private readonly ILogger? _logger;

    // 全链路指纹诊断序号（定位「前段花屏、后段正常」时光途中哪一段被覆写）。
    private int _fpSeq;
    private int _fpRenderSeq;

    /// <summary>指纹采样节流：前 6 帧全采（覆盖「开播花屏期」），之后每 60 帧一采。</summary>
    private static bool FpDue(int seq) => seq < 6 || (seq % 60) == 0;

    // ── 帧水印诊断 ─────────────────────────────────────────────────────────────
    // 用途：把「帧序号 + 色标条 + 竖条纹」直接烧进 staging 左上角，肉眼截屏即可判定花屏归属：
    //   · 序号连续递增      ⇒ 时序正常，问题在**像素内容本身**
    //   · 序号回跳/重复/跳号 ⇒ 时序错乱（丢帧、乱序、缓冲轮转）
    //   · 色标条偏色        ⇒ YUV→RGB 矩阵或色彩区间错误
    //   · 竖条纹变斜/错位   ⇒ 行距（stride）处理错误
    // 排查完成后务必改回 false —— 水印会遮挡画面且每帧多写约 30 万像素。
    private const bool FrameWatermark = true;
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

    /// <summary>在紧凑 BGRA 缓冲上填充矩形（staging 格式恒定 Bgra8888，步长 = 宽×4）。</summary>
    private static void FillRect(byte[] buf, int w, int h, int x, int y, int rw, int rh, byte b, byte g, byte r)
    {
        int x1 = Math.Max(0, x), y1 = Math.Max(0, y);
        int x2 = Math.Min(w, x + rw), y2 = Math.Min(h, y + rh);
        for (int yy = y1; yy < y2; yy++)
        {
            int off = (yy * w + x1) * 4;
            for (int xx = x1; xx < x2; xx++, off += 4)
            {
                buf[off] = b;
                buf[off + 1] = g;
                buf[off + 2] = r;
                buf[off + 3] = 255;
            }
        }
    }

    /// <summary>烧入帧水印：4 位十进制序号 + 五色标条 + 竖条纹（三者分别检验时序/色彩/行距）。</summary>
    private static void DrawFrameWatermark(byte[] buf, int w, int h, int index)
    {
        const int scale = 16;
        const int digits = 4;
        int dw = 5 * scale;          // 单字宽 80
        int dh = 7 * scale;          // 单字高 112
        int gap = scale;             // 字间距 16
        int textW = digits * dw + (digits - 1) * gap;  // 368
        int panelW = textW + 24;     // 392
        int panelH = 24 + dh + 12 + 48 + 12 + 64 + 12; // 284
        if (w < panelW + 24 || h < panelH + 24) return;

        // 不透明黑底板：任何画面内容下水印都清晰可读
        FillRect(buf, w, h, 12, 12, panelW, panelH, 0, 0, 0);

        // 1) 帧序号（白色数字；个位在最右，取模 10000 循环）
        int x0 = 24, y0 = 24;
        int v = index % 10000;
        for (int d = 0; d < digits; d++)
        {
            int digit = (v / Pow10[digits - 1 - d]) % 10;
            string glyph = DigitFont[digit];
            int dx = x0 + d * (dw + gap);
            for (int row = 0; row < 7; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    if (glyph[row * 5 + col] != '1') continue;
                    FillRect(buf, w, h, dx + col * scale, y0 + row * scale, scale, scale, 255, 255, 255);
                }
            }
        }

        // 2) 五色标条：蓝/绿/红/白/黑 —— 一眼看出通道是否互换或色彩矩阵错误
        int barY = y0 + dh + 12;
        int stripeW = textW / 5;
        FillRect(buf, w, h, x0 + stripeW * 0, barY, stripeW, 48, 255, 0, 0);    // B
        FillRect(buf, w, h, x0 + stripeW * 1, barY, stripeW, 48, 0, 255, 0);    // G
        FillRect(buf, w, h, x0 + stripeW * 2, barY, stripeW, 48, 0, 0, 255);    // R
        FillRect(buf, w, h, x0 + stripeW * 3, barY, stripeW, 48, 255, 255, 255);// W
        FillRect(buf, w, h, x0 + stripeW * 4, barY, textW - stripeW * 4, 48, 0, 0, 0); // K

        // 3) 竖条纹：若行距（stride）处理错误，竖条会呈现斜纹或整块错位
        int stripeY = barY + 48 + 12;
        for (int i = 0; i < 32; i++)
        {
            bool on = (i & 1) == 0;
            FillRect(buf, w, h, x0 + i * 12, stripeY, 12, 64,
                on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0, on ? (byte)255 : (byte)0);
        }
    }

    private static readonly int[] Pow10 = { 1, 10, 100, 1000 };

    private static void AppendHex(System.Text.StringBuilder sb, ReadOnlySpan<byte> d, int off, int n)
    {
        for (int i = 0; i < n; i++)
        {
            int idx = off + i;
            sb.Append(idx >= 0 && idx < d.Length ? d[idx].ToString("X2") : "--");
        }
    }

    /// <summary>YUV 帧指纹：Y 第 500 行列 16..23 + 色度区第 250 行偏移 16..23（两侧口径必须一致）。</summary>
    private static string FpYuv(ReadOnlySpan<byte> d, int w, int h)
    {
        int yRow = Math.Min(500, Math.Max(0, h - 1));
        int cRow = Math.Min(250, Math.Max(0, h / 2 - 1));
        var sb = new System.Text.StringBuilder(40);
        AppendHex(sb, d, yRow * w + 16, 8);
        sb.Append('/');
        AppendHex(sb, d, w * h + cRow * w + 16, 8);
        return sb.ToString();
    }

    /// <summary>紧凑 BGRA 缓冲指纹：第 500 行像素 16..23（字节 64..71）。</summary>
    private static string FpBgra(ReadOnlySpan<byte> d, int w, int h)
    {
        int row = Math.Min(500, Math.Max(0, h - 1));
        var sb = new System.Text.StringBuilder(40);
        AppendHex(sb, d, row * w * 4 + 64, 8);
        return sb.ToString();
    }

    /// <summary>初始化 Skia 呈现器。可选注入日志器，用于暴露帧转换失败（避免静默白屏）。</summary>
    /// <param name="logger">可选日志器；为 null 时不记录。</param>
    public SkiaVideoPresenter(ILogger? logger = null)
    {
        _logger = logger;
        _diagLogger = logger;
    }

    /// <summary>宽高比模式。</summary>
    public AspectRatioMode AspectRatioMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>测试可见：当前 WriteableBitmap（无帧时为 null）。</summary>
    internal WriteableBitmap? DebugBitmap => _bitmap;

    /// <inheritdoc/>
    public void Initialize(IRenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _targetW = target.Width;
        _targetH = target.Height;
        _scale = target.Scale;
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed)
            return;

        try
        {
            if (frame.Resource is SoftwareFrameResource sw)
            {
                int w = sw.Width, h = sw.Height;
                bool fpOn = FpDue(_fpSeq);
                string fpSrc = fpOn ? FpYuv(sw.Data.Span, w, h) : string.Empty;
                lock (_gate)
                {
                    // 【先判定、后转换】上一帧还没被渲染线程取走时，本帧转换完几乎必然被顶掉
                    // （真机：未渲染被顶掉 18~43 次 / 每 30 次渲染，即一半以上帧从未上过屏，
                    //  而每次转换实测约 26ms，全白花在管线线程上，还会挤压解码喂入的节拍）。
                    // 与其转换再丢弃，不如直接跳过本帧、留住已经转换好的那一帧：
                    // 视觉效果等价（反正两帧里只有一帧会上屏，最多相差一个帧距的延迟），
                    // 但能省下整次 YUV→BGRA。超过保鲜窗口则强制刷新，避免渲染线程卡住时定格。
                    if (_pending is not null && _pendingTicks != 0
                        && (Stopwatch.GetTimestamp() - _pendingTicks) < PendingFreshWindowTicks)
                    {
                        _framesSkipped++;
                        return;
                    }

                    byte[] buf = RentBuffer(w * h * 4);
                    long convStart = Stopwatch.GetTimestamp();
                    unsafe
                    {
                        fixed (byte* p = buf)
                        {
                            IntPtr dest = (IntPtr)p;
                            int destStride = w * 4; // staging 始终紧凑 BGRA
                            switch (sw.Format)
                            {
                                case LingFan.Media.Abstractions.PixelFormat.BGRA32:
                                case LingFan.Media.Abstractions.PixelFormat.RGBA32:
                                    WriteBgra(sw.Data.Span, w, h, sw.Stride, dest, destStride);
                                    break;
                                case LingFan.Media.Abstractions.PixelFormat.RGB24:
                                    WriteRgb24ToBgra(sw, dest, destStride);
                                    break;
                                default:
                                    WriteYuvToBgra(sw, dest, destStride);
                                    break;
                            }
                        }
                    }

                    if (fpOn)
                    {
                        // FP-SRC 与 FP-STG 同源同序号：前者是管线收到的源帧字节，后者是本帧写出的
                        // staging 字节。与解码侧 [FP-DEC] 同 PTS 比对即可判定数据是否在途中被覆写。
                        _logger?.LogInformation(
                            "[FP-PRESENT] seq={Seq} pts={Pts} fmt={Fmt} {W}x{H} src={Src} stg={Stg}",
                            _fpSeq, frame.Timestamp, sw.Format, w, h, fpSrc,
                            FpBgra(buf, w, h));
                    }
                    _fpSeq++;

                    // 帧水印：烧进 staging 左上角（行 12~296，不影响第 500 行的指纹采样点）。
                    if (FrameWatermark)
                    {
                        DrawFrameWatermark(buf, w, h, _presentSeq);
                        if ((_presentSeq % 60) == 0)
                            _logger?.LogInformation(
                                "[WM] 帧水印已烧入 seq={Seq} pts={Pts}（画面左上角 4 位序号；连续⇒内容错，跳号/回跳⇒时序错）",
                                _presentSeq, frame.Timestamp);
                    }
                    _presentSeq++;

                    // 转换完成后才把缓冲交给渲染线程；渲染线程拷贝完毕后才会归还 _free。
                    // 若上一帧还没被取走（渲染线程落后），直接回收复用 —— 等价于丢帧（跳一帧），
                    // 但绝不会覆写渲染线程正在读取的缓冲，而这正是旧双缓冲半帧撕裂的根因。
                    if (_pending is not null)
                    {
                        _framesOverwritten++;
                        ReturnBuffer(_pending);
                    }
                    _pending = buf;
                    _pendingW = w;
                    _pendingH = h;
                    _pendingTicks = Stopwatch.GetTimestamp();

                    // 分体计时：仅 YUV→BGRA 转换本身（不含锁/拷贝外的渲染开销）。
                    _convertSamples++;
                    _convertTicks += Stopwatch.GetElapsedTime(convStart).Ticks;
                    if (_convertSamples >= ConvertLogInterval)
                    {
                        double avgMs = Stopwatch.GetElapsedTime(0, _convertTicks).TotalMilliseconds / _convertSamples;
                        _logger?.LogInformation(
                            "[SKIA-CONVERT] YUV→BGRA 转换性能: 样本={N} 平均={AvgMs:F2}ms/帧 格式={Fmt} 尺寸={W}x{H}",
                            _convertSamples, avgMs, sw.Format, w, h);
                        _convertSamples = 0;
                        _convertTicks = 0;
                    }
                }
            }
            else if (frame.Resource is IGpuTextureResource gpu)
            {
                // GPU 纹理回退：经中立 IGpuTextureResource 桥回读为 BGRA（与 Avalonia Bgra8888 位图一致）。
                // 回读在管线线程执行（与最简播放 GPU 工作同线程），仅把结果像素拷进 staging；
                // 位图本身仍在渲染线程创建/绘制。
                using var rb = gpu.ReadbackToCpu();
                int w = rb.Width, h = rb.Height;
                lock (_gate)
                {
                    byte[] buf = RentBuffer(w * h * 4);
                    unsafe
                    {
                        fixed (byte* p = buf)
                        {
                            // rb.Data 为 BGRA（最简播放 FrameDumper 的 ReorderBgraToRgba 是 PNG(RGBA) 专用）；
                            // 此处直接整块拷进 staging（BGRA），匹配 Bgra8888 位图，无需重排。
                            rb.Data.Span.CopyTo(new Span<byte>(p, w * h * 4));
                        }
                    }

                    // 与软件帧分支同规则：未取走的旧帧回收复用（丢帧），绝不覆写渲染线程持有的缓冲。
                    if (_pending is not null)
                    {
                        _framesOverwritten++;
                        ReturnBuffer(_pending);
                    }
                    _pending = buf;
                    _pendingW = w;
                    _pendingH = h;
                }
            }
            // 其它资源类型：跳过该帧（不抛，避免击杀管线），但必须记录——静默跳过是此前白屏的元凶。
            else
            {
                _logger?.LogWarning(
                    "SkiaVideoPresenter 收到不支持的帧资源类型 {ResourceType}，跳过该帧（视频将不出画）。" +
                    "如需支持该类型，请在 Present 中补充其像素拷贝/回读分支。",
                    frame.Resource?.GetType().Name ?? "<null>");
            }
        }
        catch (Exception ex)
        {
            // 单帧异常（如 GPU 回读失败）记录但不向上抛到管线线程击杀播放——此前 ReadbackToCpu 抛 NotSupported 即被此处吞掉导致白屏。
            _logger?.LogWarning(ex, "SkiaVideoPresenter 帧转位图失败（跳过本帧）。");
        }
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

    /// <summary>帧位图绘进 <paramref name="destRect"/> 后的**设备像素**缩放比
    /// （destRect 是 DIP，乘 <c>_scale</c> 才是设备像素）。1.0 表示 1:1 贴块，&lt;1 表示缩小。</summary>
    private double DeviceScaleRatio(Rect destRect)
        => _bitmapW > 0 && destRect.Width > 0 ? (destRect.Width * _scale) / (double)_bitmapW : 1.0;

    /// <summary>按设备缩放比挑选位图插值模式：
    /// 1:1（≥0.995）用 None（最近邻，1:1 时零误差且最快）；
    /// 轻中度缩小（≥0.5）用 LowQuality（双线性，足够且便宜）；
    /// 重度缩小（&lt;0.5）才用 HighQuality（三次插值）抑制摩尔纹与欠采样发糊。</summary>
    private BitmapInterpolationMode ChooseInterpolation(Rect destRect)
    {
        double ratio = DeviceScaleRatio(destRect);
        return ratio >= 0.995 ? BitmapInterpolationMode.None
             : ratio >= 0.5 ? BitmapInterpolationMode.LowQuality
             : BitmapInterpolationMode.HighQuality;
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

        // ── 阶段 1（临界区，必须极短）──────────────────────────────────────────────
        // 仅做「取走待渲染帧」。绝不在锁内调用 WriteableBitmap.Lock()：
        // 它在 Android 上会等待 GPU/合成器完成上一帧（被 vsync 节流，实测数十 ms），
        // 一旦留在锁内，管线线程的 Present 就会阻塞等 vsync —— 真机实证呈现耗时 35~45ms/帧、
        // 仅 ~20fps（≈60Hz/3，每 3 个 vsync 才出一帧），瓶颈正源于此。
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

        // ── 阶段 2（锁外）────────────────────────────────────────────────────────
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
                    // 表现为画面块状破碎、色块错位、拖影（真机实证的破碎根因）。
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
            // 旧实现把「物理像素帧」与「DIP target」直接混算，且 bitmap DPI 设 96*scale，
            // 导致 DrawImage 再按 DPI 缩放一次 → 实际绘制放大 scale 倍、只显示左上角局部（溢出）。
            var destRect = CalculateDestRect(_bitmapW, _bitmapH, _targetW, _targetH, AspectRatioMode);

            // 一次性诊断（溢出排查）：目标区/位图/目标矩形/模式/scale。destRect 必须落在
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
            // 而在 1:1 贴块时是纯粹的浪费 —— 实测 1080x1920 位图以 scale=3 绘进 360x640 DIP 的
            // destRect，设备像素正好 1080x1920，比值=1.0，此时每帧为 2M 像素白付三次插值开销，
            // 是渲染线程只能跑到 ~15fps（管线的一半）的主要嫌疑。
            using var opts = drawingContext.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = ChooseInterpolation(destRect),
            });
            long drawStart = Stopwatch.GetTimestamp();
            drawingContext.DrawImage(_bitmap, destRect);
            _drawTicks += Stopwatch.GetElapsedTime(drawStart).Ticks;
        }

        // 渲染线程整帧诊断：区分「渲染线程自己慢」与「合成器调用频率低」。
        // 实测未渲染被顶掉持续为正 ⇒ 上屏 fps 只有管线的一半（~15 vs 30），必须先定位瓶颈归属。
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
    /// 写入打包 4 字节像素（BGRA32/RGBA32），按行拷贝并处理源 stride 对齐填充。
    /// </summary>
    private static unsafe void WriteBgra(ReadOnlySpan<byte> src, int width, int height, int srcStride, IntPtr dest, int destStride)
    {
        int srcStride2 = srcStride > 0 ? srcStride : destStride;
        byte* d = (byte*)dest;

        if (srcStride2 == destStride)
        {
            var copyLength = Math.Min(src.Length, destStride * height);
            src.Slice(0, copyLength).CopyTo(new Span<byte>(d, copyLength));
        }
        else
        {
            var rowBytes = Math.Min(srcStride2, destStride);
            for (int y = 0; y < height; y++)
            {
                int srcOffset = y * srcStride2;
                int available = src.Length - srcOffset;
                if (available <= 0) break;
                int n = Math.Min(rowBytes, available);
                src.Slice(srcOffset, n).CopyTo(new Span<byte>(d + (nuint)(y * destStride), n));
            }
        }
    }

    /// <summary>
    /// 确保 WriteableBitmap 存在且尺寸/格式匹配（不匹配则重建）。始终在渲染线程调用。
    /// </summary>
    /// <remarks>
    /// <b>DPI 固定 96（=1.0 逻辑缩放）</b>：Avalonia 的 <see cref="WriteableBitmap"/> 逻辑尺寸 =
    /// <c>PixelSize / Dpi * 96</c>；DPI 设为 96 时逻辑尺寸 == 物理像素尺寸，与
    /// <see cref="CalculateDestRect"/> 传入的 DIP 帧/目标尺寸单位完全一致，<see cref="DrawImage"/>
    /// 不会再二次缩放。旧实现用 <c>96 * _scale</c> 使 bitmap 逻辑尺寸缩小为 1/scale，与 DIP 目标
    /// 混算后被 DrawImage 再放大 scale 倍，导致画面只显示左上角局部（溢出观感）。
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

    /// <summary>
    /// RGB24（打包 R,G,B 各 1 字节）→ BGRA32。AOT 友好，纯 Span 逐像素写。
    /// </summary>
    internal static unsafe void WriteRgb24ToBgra(SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        int srcStride = sw.Stride > 0 ? sw.Stride : sw.Width * 3;
        byte* dst = (byte*)dest;
        for (int y = 0; y < sw.Height; y++)
        {
            int srcRow = y * srcStride;
            byte* dstRow = dst + (nuint)(y * destStride);
            for (int x = 0; x < sw.Width; x++)
            {
                int s = srcRow + x * 3;
                byte r = sw.Data.Span[s], g = sw.Data.Span[s + 1], b = sw.Data.Span[s + 2];
                int di = x * 4;
                dstRow[di] = b;
                dstRow[di + 1] = g;
                dstRow[di + 2] = r;
                dstRow[di + 3] = 255;
            }
        }
    }

    // ── YUV → BGRA 转换（BT.601 全范围 JFIF 矩阵，预计算 LUT）──

    private static readonly short[] Rv = BuildYuvLut(d => 1.402f * d);
    private static readonly short[] Gu = BuildYuvLut(d => -0.344136f * d);
    private static readonly short[] Gv = BuildYuvLut(d => -0.714136f * d);
    private static readonly short[] Bu = BuildYuvLut(d => 1.772f * d);

    private static short[] BuildYuvLut(Func<float, float> coeff)
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int v = (int)Math.Round(coeff(i - 128));
            t[i] = v < short.MinValue ? short.MinValue : v > short.MaxValue ? short.MaxValue : (short)v;
        }
        return t;
    }

    /// <summary>
    /// YUV 平面/半平面（YUV420P/YUV422P/YUV444P/NV12/NV21）→ BGRA32。
    /// 假设紧凑平面布局（解码器 av_image_copy_to_buffer align=1 产出）：平面无行内填充、连续排列，
    /// 平面偏移仅由宽高与色度子采样推导（与 av_image_copy_to_buffer 的打包语义一致）。
    /// </summary>
    internal static unsafe void WriteYuvToBgra(SoftwareFrameResource sw, IntPtr dest, int destStride)
    {
        byte* dstBase = (byte*)dest;
        int w = sw.Width, h = sw.Height;
        // 源行 stride：优先用 SoftwareFrameResource 报告的 Stride（支持对齐/padding），
        // 未设置 (>0 视为有效) 时回退到 width。NV12/YUV420P 的 Y 与 UV 共用同一行 stride。
        int srcStride = sw.Stride > 0 ? sw.Stride : w;
        bool isNv = sw.Format is LingFan.Media.Abstractions.PixelFormat.NV12 or LingFan.Media.Abstractions.PixelFormat.NV21;

        int chromaW, chromaH;
        bool hSub, vSub;
        switch (sw.Format)
        {
            case LingFan.Media.Abstractions.PixelFormat.YUV444P:
                chromaW = w; chromaH = h; hSub = false; vSub = false; break;
            case LingFan.Media.Abstractions.PixelFormat.YUV422P:
                chromaW = (w + 1) / 2; chromaH = h; hSub = true; vSub = false; break;
            case LingFan.Media.Abstractions.PixelFormat.NV12:
            case LingFan.Media.Abstractions.PixelFormat.NV21:
                chromaW = (w + 1) / 2; chromaH = (h + 1) / 2; hSub = true; vSub = true; break;
            default: // YUV420P
                chromaW = (w + 1) / 2; chromaH = (h + 1) / 2; hSub = true; vSub = true; break;
        }

        int ySize = w * h;
        int uOff = ySize;
        int vOff = ySize + chromaW * chromaH;
        int uvOff = ySize;

        // 一次性诊断：打 UV 前 8 字节 + Y 前 2 字节 + sw.Stride + sw.Format，定位花屏根因。
        if (!_diagOnce)
        {
            _diagOnce = true;
            var d = sw.Data.Span;
            var sb = new System.Text.StringBuilder();
            sb.Append("[SKIA-CONVERT-DIAG] fmt=").Append(sw.Format)
              .Append(" w=").Append(w).Append(" h=").Append(h)
              .Append(" sw.Stride=").Append(sw.Stride)
              .Append(" srcStrideUsed=").Append(srcStride)
              .Append(" chromaW=").Append(chromaW).Append(" chromaH=").Append(chromaH)
              .Append(" ySize=").Append(ySize)
              .Append(" uvOff=").Append(uvOff).Append(" vOff=").Append(vOff)
              .Append(" uvPlen=").Append(d.Length - uvOff)
              .Append(" | Y[0,1]=").Append(d[0]).Append(',').Append(d[1])
              .Append(" | UV[0..7]=");
            for (int i = 0; i < 8 && uvOff + i < d.Length; i++) sb.Append(d[uvOff + i]).Append(i < 7 ? "," : "");
            _diagLogger?.LogInformation(sb.ToString());
        }

        // 色彩矩阵选择：默认（未指定 / BT.601-Full）走既有 LUT（BT.601-Full），其余按色彩空间取 float 系数。
        // limited range 需对 Y 做 (Y-16)×1.1644 补偿；系数来源见行 70-84 文档依据。
        var ci = sw.ColorInfo;
        bool useLut = ci is null || !ci.Value.IsSpecified
            || (ci.Value.Standard == LingFan.Media.Abstractions.ColorStandard.Bt601
                && ci.Value.Range == LingFan.Media.Abstractions.ColorRange.Full);
        float kR = 0f, kU = 0f, kV = 0f, kUB = 0f, yScale = 1f;
        int yOff = 0;
        if (!useLut)
        {
            // 此时 useLut=false ⇒ ci 非空（见上方短路条件）；用 ! 抑制可空告警。
            var std = ci!.Value.Standard;
            var range = ci.Value.Range;
            yScale = range == LingFan.Media.Abstractions.ColorRange.Limited ? 1.1644f : 1f;
            yOff = range == LingFan.Media.Abstractions.ColorRange.Limited ? 16 : 0;
            switch (std)
            {
                case LingFan.Media.Abstractions.ColorStandard.Bt709:
                    kR = 1.5748f; kU = 0.1873f; kV = 0.4681f; kUB = 1.8556f; break;
                case LingFan.Media.Abstractions.ColorStandard.Bt2020:
                    kR = 1.6787f; kU = 0.1873f; kV = 0.6504f; kUB = 2.1418f; break;
                default: // BT.601 limited
                    kR = 1.5960f; kU = 0.3918f; kV = 0.8130f; kUB = 2.0173f; break;
            }
        }

        // 【性能关键】内层热循环：裸指针（消除 Span 边界检查）+ 每像素对步进（U/V 共享一次读取）
        // + 指针递增写。真机实测（vivo iQOO10 / Mono Debug）：旧实现 Span 逐像素索引在
        // 1080x1920 每帧 ~1800ms（画面 1fps）；指针化 + 双像素步进为其数分之一，Release JIT 下更低。
        // CPU 逐像素转换终究是 Tier0 兜底路径；1080p+ 的正解是 Tier2 硬解硬渲（GPU 采样 YUV）。
        var srcSpan = sw.Data.Span;
        fixed (byte* srcBase = srcSpan)
        fixed (short* pRv = Rv, pGu = Gu, pGv = Gv, pBu = Bu)
        {
            byte* yPlane = srcBase;
            byte* uPlane = srcBase + uOff;
            byte* vPlane = srcBase + vOff;
            byte* uvPlane = srcBase + uvOff;

            for (int y = 0; y < h; y++)
            {
                byte* dstRow = dstBase + (nuint)(y * destStride);
                byte* yRow = yPlane + (nuint)(y * srcStride);
                int cRow = vSub ? (y >> 1) : y;
                byte* uRow = uPlane + (nuint)(cRow * chromaW);
                byte* vRow = vPlane + (nuint)(cRow * chromaW);
                byte* uvRow = uvPlane + (nuint)(cRow * srcStride);
                // 【治根AC：色度垂直双线性上采样】消除 NV12 4:2:0 在 y 方向的"水平边"色度块。
                // y 奇数时 cRow0 != cRow1，U/V = (uvRow0 + uvRow1) / 2；y 偶数走最近邻（uvRow1 = uvRow0）。
                // 水平方向仍为 2:1 共享（每 2 个 Y 像素 1 个 UV 对）——完整消除需水平+垂直双线性，
                // 但垂直一维插值已消除 2x2 方格的"水平边"（3x 缩放下从 6x6 方格→6x3 横条，视觉改善明显）。
                int cRow1 = cRow + 1;
                bool vertChromaInterp = vSub && (y & 1) == 1 && cRow1 < chromaH;
                byte* uvRow1 = vertChromaInterp ? uvPlane + (nuint)(cRow1 * srcStride) : uvRow;
                byte* uRow1 = vertChromaInterp ? uPlane + (nuint)(cRow1 * chromaW) : uRow;
                byte* vRow1 = vertChromaInterp ? vPlane + (nuint)(cRow1 * chromaW) : vRow;

                // 双像素步进：4:2:0/4:2:2 色度水平共享（hSub），偶数列取一次 U/V 写两像素。
                int x = 0;
                for (; x + 1 < w; x += 2)
                {
                    int yv0 = yRow[x];
                    int yv1 = yRow[x + 1];

                    int cu, cv;
                    if (isNv)
                    {
                        // 【治根AD：水平+垂直完整双线性色度上采样】
                        // 取 4 邻 UV 像素对：(cRow0, cCol0), (cRow0, cCol1), (cRow1, cCol0), (cRow1, cCol1)
                        // x 偶数 + y 偶数：U = U00 (最近邻)
                        // x 奇数 + y 偶数：U = (U00 + U01) / 2
                        // x 偶数 + y 奇数：U = (U00 + U10) / 2
                        // x 奇数 + y 奇数：U = (U00 + U01 + U10 + U11) / 4
                        // V 同理。彻底消除 NV12 4:2:0 在 x 方向（每 2 个 Y 共享 1 个 UV）的色度"垂直边"块。
                        int cCol0_off = x & ~1;          // (x&~1)/2 == cCol0 索引，U 在偶字节
                        int cCol1_off = Math.Min(cCol0_off + 2, Math.Max(0, srcStride - 2)); // 右侧边界 clamp，防读到下一行
                        byte* p00 = uvRow + cCol0_off;
                        byte* p01 = uvRow + cCol1_off;
                        int cu00, cv00, cu01, cv01;
                        if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                        {
                            cu00 = p00[0]; cv00 = p00[1];
                            cu01 = p01[0]; cv01 = p01[1];
                        }
                        else // NV21: V 在前
                        {
                            cv00 = p00[0]; cu00 = p00[1];
                            cv01 = p01[0]; cu01 = p01[1];
                        }
                        int cu10 = 0, cv10 = 0, cu11 = 0, cv11 = 0;
                        if (vertChromaInterp)
                        {
                            byte* p10 = uvRow1 + cCol0_off;
                            byte* p11 = uvRow1 + cCol1_off;
                            if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                            {
                                cu10 = p10[0]; cv10 = p10[1];
                                cu11 = p11[0]; cv11 = p11[1];
                            }
                            else
                            {
                                cv10 = p10[0]; cu10 = p10[1];
                                cv11 = p11[0]; cu11 = p11[1];
                            }
                        }
                        // 4 邻加权平均（权重取决于 x 和 y 的奇偶性）
                        if (vertChromaInterp)
                        {
                            if ((x & 1) == 0) // x 偶数
                            {
                                cu = (cu00 + cu10 + 1) >> 1;
                                cv = (cv00 + cv10 + 1) >> 1;
                            }
                            else // x 奇数
                            {
                                cu = (cu00 + cu01 + cu10 + cu11 + 2) >> 2;
                                cv = (cv00 + cv01 + cv10 + cv11 + 2) >> 2;
                            }
                        }
                        else
                        {
                            if ((x & 1) == 0) // x 偶数
                            {
                                cu = cu00;
                                cv = cv00;
                            }
                            else // x 奇数
                            {
                                cu = (cu00 + cu01 + 1) >> 1;
                                cv = (cv00 + cv01 + 1) >> 1;
                            }
                        }
                    }
                    else
                    {
                        int cCol = hSub ? (x >> 1) : x;
                        cu = uRow[cCol];
                        cv = vRow[cCol];
                    }

                    int r, g, b, du, dv, yl;
                    if (useLut)
                    {
                        int rv = pRv[cv], gu = pGu[cu], gv = pGv[cv], bu = pBu[cu];

                        // 像素 0
                        r = yv0 + rv; g = yv0 + gu + gv; b = yv0 + bu;
                        byte* d = dstRow + (nuint)(x * 4);
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;

                        // 像素 1（共享 U/V）
                        r = yv1 + rv; g = yv1 + gu + gv; b = yv1 + bu;
                        d += 4;
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;
                    }
                    else
                    {
                        du = cu - 128; dv = cv - 128;
                        int kr = (int)(dv * kR), kg1 = (int)(du * kU), kg2 = (int)(dv * kV), kb = (int)(du * kUB);
                        yl = (int)((yv0 - yOff) * yScale);

                        byte* d = dstRow + (nuint)(x * 4);
                        r = yl + kr; g = yl - kg1 - kg2; b = yl + kb;
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;

                        yl = (int)((yv1 - yOff) * yScale);
                        d += 4;
                        r = yl + kr; g = yl - kg1 - kg2; b = yl + kb;
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;
                    }
                }

                // 奇数尾列（宽为奇数时）
                if (x < w)
                {
                    int yv = yRow[x];
                    int cu, cv;
                    if (isNv)
                    {
                        byte* uv = uvRow + (nuint)(x & ~1);
                        if (sw.Format == LingFan.Media.Abstractions.PixelFormat.NV12)
                        {
                            cu = uv[0];
                            cv = uv[1];
                        }
                        else
                        {
                            cv = uv[0];
                            cu = uv[1];
                        }
                    }
                    else
                    {
                        int cCol = hSub ? (x >> 1) : x;
                        cu = uRow[cCol];
                        cv = vRow[cCol];
                    }

                    int r, g, b;
                    if (useLut)
                    {
                        r = yv + pRv[cv]; g = yv + pGu[cu] + pGv[cv]; b = yv + pBu[cu];
                    }
                    else
                    {
                        int du = cu - 128, dv = cv - 128;
                        int yl = (int)((yv - yOff) * yScale);
                        r = yl + (int)(dv * kR);
                        g = yl - (int)(du * kU) - (int)(dv * kV);
                        b = yl + (int)(du * kUB);
                    }
                    byte* d = dstRow + (nuint)(x * 4);
                    d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    d[3] = 255;
                }
            }
        }
    }

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

    /// <summary>
    /// 根据宽高比模式计算目标矩形。
    /// </summary>
    private static Rect CalculateDestRect(
        int frameWidth, int frameHeight,
        int targetWidth, int targetHeight,
        AspectRatioMode mode)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
            return new Rect(0, 0, targetWidth, targetHeight);

        var scaleX = (double)targetWidth / frameWidth;
        var scaleY = (double)targetHeight / frameHeight;

        return mode switch
        {
            AspectRatioMode.Fill => new Rect(0, 0, targetWidth, targetHeight),
            AspectRatioMode.Uniform => CalculateUniformRect(scaleX, scaleY, targetWidth, targetHeight),
            AspectRatioMode.UniformToFill => CalculateUniformToFillRect(scaleX, scaleY, targetWidth, targetHeight),
            _ => new Rect(0, 0, targetWidth, targetHeight)
        };
    }

    /// <summary>
    /// Uniform: 保持宽高比，缩放到完全包含在目标内，居中（留黑边）。
    /// </summary>
    private static Rect CalculateUniformRect(double scaleX, double scaleY, int targetWidth, int targetHeight)
    {
        var scale = Math.Min(scaleX, scaleY);
        var destW = targetWidth / scaleX * scale;
        var destH = targetHeight / scaleY * scale;
        var destX = (targetWidth - destW) / 2.0;
        var destY = (targetHeight - destH) / 2.0;
        return new Rect(destX, destY, destW, destH);
    }

    /// <summary>
    /// UniformToFill: 保持宽高比，缩放到完全填充目标，居中（裁剪溢出）。
    /// </summary>
    private static Rect CalculateUniformToFillRect(double scaleX, double scaleY, int targetWidth, int targetHeight)
    {
        var scale = Math.Max(scaleX, scaleY);
        var destW = targetWidth / scaleX * scale;
        var destH = targetHeight / scaleY * scale;
        var destX = (targetWidth - destW) / 2.0;
        var destY = (targetHeight - destH) / 2.0;
        return new Rect(destX, destY, destW, destH);
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
