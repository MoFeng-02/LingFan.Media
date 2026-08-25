using System.Buffers;

namespace LingFan.Media.Abstractions;

/// <summary>
/// CPU 内存帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>放在 Abstractions 的原因：Backends.FFmpeg 只引用 Abstractions，
/// FFmpegVideoDecoder 在软件解码路径中需要创建 SoftwareFrameResource。
/// 如果放在 Renderers 模块，Backends 无法访问。</para>
/// <para>内存所有权：<see cref="Data"/> 表示资源拥有该内存。</para>
/// <list type="bullet">
/// <item>FFmpeg 软解拷贝路径: av_frame_get_buffer 分配 → 拷贝到 Memory&lt;byte&gt; → av_frame_free 释放原生帧</item>
/// <item>ArrayPool 租借/归还内存，减少 GC 压力（60fps 每秒约 60 个帧）</item>
/// <item>零拷贝路径：<see cref="Data"/> 直接映射原生引用计数 buffer，
/// 生命周期由中立 <see cref="IDisposable"/> 所有者控制（本层不依赖任何后端类型）</item>
/// <item>Dispose 时: 归还 ArrayPool buffer 或释放零拷贝所有者（原生引用计数减一）</item>
/// </list>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，ArrayPool 为运行时内置 API。</para>
/// </remarks>
public sealed class SoftwareFrameResource : IFrameResource
{
    // ── 有界私有池（headless 默认场景的关键内存治理点）──
    // 原用 ArrayPool<byte>.Shared：4K 软解每帧租 12–33MB，归还后 Shared 会囤积数十个大 buffer 不还 GC，
    // 造成「队列只 ~6 帧、进程内存却卡在 500MB~1GB」的 ghost 内存（帧在队列里很少，多的是池里囤的）。
    // 私有有界池把每桶囤积数锁死：超出的 buffer 在 Return 时即丢弃、交由 GC 回收 —— 内存有界、可预测，
    // 不再随 4K 软解爆发。in-flight 帧数由 FrameQueue 容量封顶，本池仅保留少量热 buffer 复用。
    // maxArrayLength 覆盖最大单帧（8K BGRA32≈132MB，留 256MB 余量）。
    //
    // 【2026-08-23 vivo/Qualcomm 实证修正】maxArraysPerBucket 必须覆盖 in-flight 峰值：
    // 帧队列容量 8（8×3MB=24MB in-flight）+ 解码器内部缓冲 + 渲染侧持有 ≈10 块同尺寸并发需求。
    // 旧值 4 时池形同虚设——每次池空必走 new byte[3MB]（LOH），1080x1920@30fps ≈180MB/s 的
    // LOS 分配率触发 Mono GC 每 25~90ms 一次，呈现线程被反复抢占 → 画面 1fps（每 2s 只呈 2 帧）。
    // 提到 16（覆盖峰值 + 余量）：内存上界 16×3MB=48MB/尺寸桶，可预测；GC 压力趋零。
    private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(
        maxArrayLength: 256 * 1024 * 1024,
        maxArraysPerBucket: 16);

    private byte[]? _rentedBuffer;
    private IDisposable? _dataOwner;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>CPU 内存数据（拥有所有权，底层由 ArrayPool、外部或原生引用计数 buffer 提供）。</summary>
    public Memory<byte> Data { get; }

    /// <summary>
    /// 帧的色彩空间描述（可空；由解码器透传，渲染端据此选 YUV→RGB 矩阵，null 时用默认）。
    /// </summary>
    public VideoColorInfo? ColorInfo { get; set; }

    /// <summary>
    /// 平面 0 的行字节数（stride）。<c>0</c> 表示未指定——数据为紧凑布局（宽度 × 每像素字节数），
    /// 兼容历史构造函数。零拷贝路径传入原生 buffer 的实际 stride，
    /// 可能因对齐填充大于紧凑行宽，渲染方须按行拷贝。
    /// </summary>
    public int Stride { get; }

    /// <inheritdoc/>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 从 <see cref="ArrayPool{T}"/> 租借内存创建实例，减少 GC 压力。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="dataLength">所需数据长度（字节）。实际租借的数组可能更长，但 <see cref="Data"/> 仅包含前 <paramref name="dataLength"/> 字节。</param>
    /// <remarks>
    /// 调用方通过 <see cref="Data"/>.<see cref="Memory{T}.Span"/> 写入数据。
    /// Dispose 时自动归还数组到 <see cref="ArrayPool{T}"/>。
    /// </remarks>
    public SoftwareFrameResource(int width, int height, PixelFormat format, int dataLength)
    {
        Width = width;
        Height = height;
        Format = format;
        _rentedBuffer = _pool.Rent(dataLength);
        Data = _rentedBuffer.AsMemory(0, dataLength);
    }

    /// <summary>
    /// 使用外部提供的 <see cref="Memory{T}"/> 创建实例（不租借 ArrayPool）。
    /// 保留此构造函数兼容既有调用方（如 TestFrameFactory）。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="data">外部提供的数据（调用方拥有所有权，Dispose 时不释放）。</param>
    public SoftwareFrameResource(int width, int height, PixelFormat format, Memory<byte> data)
    {
        Width = width;
        Height = height;
        Format = format;
        Data = data;
        // _rentedBuffer = null，Dispose 时不归还 ArrayPool
    }

    /// <summary>
    /// 零拷贝构造：<paramref name="data"/> 直接映射原生引用计数 buffer，
    /// 生命周期由 <paramref name="dataOwner"/> 控制。
    /// </summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="data">映射原生内存的数据视图（不经拷贝）。</param>
    /// <param name="stride">平面 0 的行字节数（原生 buffer 实际 stride，可能含对齐填充）。</param>
    /// <param name="dataOwner">
    /// 原生内存所有者（中立 <see cref="IDisposable"/>，后端传入引用计数句柄）。
    /// Dispose 时释放该所有者，原生引用计数减一。所有者释放后不得再访问 <see cref="Data"/>。
    /// </param>
    public SoftwareFrameResource(int width, int height, PixelFormat format,
        Memory<byte> data, int stride, IDisposable dataOwner)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        Width = width;
        Height = height;
        Format = format;
        Data = data;
        Stride = stride;
        _dataOwner = dataOwner ?? throw new ArgumentNullException(nameof(dataOwner));
    }

    /// <summary>释放内存资源（归还 ArrayPool buffer 或释放零拷贝所有者）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 归还租借的 buffer 到私有有界池（仅 ArrayPool 构造函数创建的实例；超额囤积即由 GC 回收）
        if (_rentedBuffer != null)
        {
            _pool.Return(_rentedBuffer);
            _rentedBuffer = null;
        }

        // 释放零拷贝所有者（原生引用计数减一；仅零拷贝构造函数创建的实例）
        if (_dataOwner != null)
        {
            _dataOwner.Dispose();
            _dataOwner = null;
        }
    }
}
