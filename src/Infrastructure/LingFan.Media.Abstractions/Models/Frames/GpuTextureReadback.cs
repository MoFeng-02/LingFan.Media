namespace LingFan.Media.Abstractions;

/// <summary>
/// GPU 纹理 CPU 回读结果（中立、零外部引用）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="IGpuTextureResource.ReadbackToCpu"/> 返回，承载紧凑 BGRA32 像素数据
/// （stride = 宽 × 4，无行内填充），供 SkiaVideoPresenter 直接写入 WriteableBitmap。</para>
/// <para><b>生命周期</b>：回读数据为托管 <see cref="byte"/>[] 快照，与原生 GPU 纹理解耦；
/// 调用方以 <c>using</c> 释放即可。设计上不持有原生句柄，避免与帧资源（D3D11TextureResource）双重释放——
/// 帧所有权始终由 VideoPipeline 的 ReturnFrame 闭环。</para>
/// <para><b>B-CTR2 池化支持</b>：生产者可经池化构造传入 <see cref="System.Buffers.ArrayPool{T}"/> 租借数组，
/// <see cref="Dispose"/> 时自动归还——消除每帧回读的大数组分配（1080p ≈ 8MB/帧 → LOH 压力）。
/// <see cref="System.Buffers.ArrayPool{T}"/> 为 BCL 中立类型，契约层零外部引用约束不受影响。
/// 消费方必须在 <c>using</c> 作用域内完成对 <see cref="Data"/> 的读取（归还后数据失效）。</para>
/// </remarks>
public sealed class GpuTextureReadback : IDisposable
{
    private byte[]? _pooledArray;
    /// <summary>帧宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>帧高度（像素）。</summary>
    public int Height { get; }

    /// <summary>回读像素格式（始终 BGRA32）。</summary>
    public PixelFormat Format { get; }

    /// <summary>BGRA32 像素数据（紧凑，stride = Width × 4）。</summary>
    public Memory<byte> Data { get; }

    /// <summary>每行字节数（= Width × 4）。</summary>
    public int Stride { get; }

    /// <summary>初始化回读结果。</summary>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <param name="format">回读格式（应为 BGRA32）。</param>
    /// <param name="data">BGRA32 像素数据（管理型 byte[]）。</param>
    /// <param name="stride">每行字节数。</param>
    public GpuTextureReadback(int width, int height, PixelFormat format, byte[] data, int stride)
    {
        Width = width;
        Height = height;
        Format = format;
        Data = data;
        Stride = stride;
    }

    /// <summary>
    /// B-CTR2: 池化构造——包装 <see cref="System.Buffers.ArrayPool{T}"/> 租借数组
    /// （租借数组通常比所需更长，经 <paramref name="dataLength"/> 截取有效区间），
    /// <see cref="Dispose"/> 时自动归还池。
    /// </summary>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <param name="format">回读格式（应为 BGRA32）。</param>
    /// <param name="pooledData">自 <see cref="System.Buffers.ArrayPool{T}"/> 租借的数组（本实例接管归还责任）。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="dataLength">有效数据长度（字节）。</param>
    public GpuTextureReadback(int width, int height, PixelFormat format, byte[] pooledData, int stride, int dataLength)
    {
        ArgumentNullException.ThrowIfNull(pooledData);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataLength, pooledData.Length);

        Width = width;
        Height = height;
        Format = format;
        Data = new Memory<byte>(pooledData, 0, dataLength);
        Stride = stride;
        _pooledArray = pooledData;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // 非池化：数据为托管 byte[]，GC 自动回收；本类不持有原生句柄，无双重释放风险。
        // 池化（B-CTR2）：归还 ArrayPool，幂等（二次 Dispose 为 no-op）。
        var arr = _pooledArray;
        if (arr is not null)
        {
            _pooledArray = null;
            System.Buffers.ArrayPool<byte>.Shared.Return(arr);
        }
    }
}
