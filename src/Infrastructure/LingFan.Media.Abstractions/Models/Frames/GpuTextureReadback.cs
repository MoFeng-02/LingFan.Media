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
/// </remarks>
public sealed class GpuTextureReadback : IDisposable
{
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

    /// <inheritdoc/>
    public void Dispose()
    {
        // 数据为托管 byte[]，GC 自动回收；本类不持有原生句柄，无双重释放风险。
        // 留作对称性/未来原生扩展点。
    }
}
