namespace LingFan.Media.Abstractions;

/// <summary>
/// 中立 GPU 纹理帧资源契约（跨层共享，零外部引用）。
/// </summary>
/// <remarks>
/// <para>由具体渲染器实现（如 <c>LingFan.Media.Renderers.D3D11.D3D11TextureResource</c>），
/// 供 Avalonia / Outputs 等层以平台无关方式消费 GPU 纹理（例如 CPU 回读后送入 Skia WriteableBitmap 回退路径），
/// 而无需引用具体渲染器模块，严守依赖倒置。与 <see cref="IGpuDeviceContext"/> 同为 Abstractions 中立桥。</para>
/// <para><b>异步策略</b>：<see cref="ReadbackToCpu"/> 为同步（native 分类）——D3D11/GL 纹理回读是同步 COM/原生调用，
/// 无真实 I/O await。实现保持同步，不补 async（补即伪异步）。</para>
/// </remarks>
public interface IGpuTextureResource : IFrameResource
{
    /// <summary>
    /// 原生 GPU 纹理句柄（如 ID3D11Texture2D* 的 COM 指针）。
    /// 供渲染器直接 GPU 拷贝（零拷贝路径），无需 CPU 回读。
    /// </summary>
    /// <remarks>
    /// 调用方负责确保使用期间资源不被释放（如渲染器持锁或帧引用计数）。
    /// 句柄所有权归 <see cref="IFrameResource"/> 实现的 <see cref="Dispose"/> 管理。
    /// </remarks>
    IntPtr NativeTextureHandle { get; }

    /// <summary>
    /// 子资源索引（纹理数组切片索引，非数组时为 0）。
    /// DXVA 硬解输出纹理数组时，需用此索引通过 CopySubresourceRegion 拷贝正确的切片。
    /// </summary>
    int SubresourceIndex { get; }

    /// <summary>
    /// 将 GPU 纹理回读到 CPU，返回紧凑 BGRA32 像素数据（供 Avalonia Skia 回退路径直接写入 WriteableBitmap）。
    /// 实现负责创建暂存纹理、拷贝、Map 读取，并按源格式转换为 BGRA32。
    /// </summary>
    /// <returns>CPU 回读结果（中立 <see cref="GpuTextureReadback"/>，由调用方 Dispose）。</returns>
    /// <exception cref="NotSupportedException">源纹理格式暂不支持回读时。</exception>
    GpuTextureReadback ReadbackToCpu();
}
