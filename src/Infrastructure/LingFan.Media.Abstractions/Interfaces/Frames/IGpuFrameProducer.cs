namespace LingFan.Media.Abstractions;

/// <summary>
/// 解码原生输出类型，供 <see cref="IGpuFrameProducer"/> 判别如何导入为当前渲染器 GPU-API 纹理。
/// </summary>
public enum GpuFrameImportKind
{
    /// <summary>未知 / 不支持。</summary>
    Unknown = 0,

    /// <summary>Windows：ID3D11Texture2D 经 DXGI 共享句柄（HANDLE，legacy 或 NT）。</summary>
    D3D11SharedHandle,

    /// <summary>Linux：VAAPI / VDPAU / NVDEC surface 导出的 dma_buf 文件描述符（fd），跨 API 中性句柄。</summary>
    LinuxDmaBufFd,

    /// <summary>Android：AHardwareBuffer 指针。</summary>
    AndroidHardwareBuffer,

    /// <summary>Apple(macOS/iOS)：IOSurfaceRef。</summary>
    IOSurface,

    /// <summary>跨平台：Vulkan Video 解码产出的 VkImage，经 Vulkan 外部内存句柄（fd / HANDLE）导入，由 Vulkan 渲染器零拷贝消费（B4）。</summary>
    VulkanImage,
}

/// <summary>
/// 解码原生输出导入描述（中立，零外部引用）。
/// </summary>
/// <remarks>
/// 由解码后端填充：把其原生解码输出（D3D11 共享句柄 / Linux dma_buf / AHardwareBuffer / IOSurface）
/// 的句柄、尺寸与格式交给渲染器侧生产者，避免后端反向引用渲染器程序集。
/// </remarks>
public readonly struct GpuFrameImportSource
{
    /// <summary>显式无参构造函数（readonly struct 含字段初始值设定项所必需）。</summary>
    public GpuFrameImportSource() { }

    /// <summary>原生输出类型。</summary>
    public GpuFrameImportKind Kind { get; init; }

    /// <summary>原生共享句柄 / 指针（HANDLE / fd / AHardwareBuffer* / IOSurfaceRef），语义随 <see cref="Kind"/>。</summary>
    public nint Handle { get; init; }

    /// <summary>纹理宽度（像素）。</summary>
    public int Width { get; init; }

    /// <summary>纹理高度（像素）。</summary>
    public int Height { get; init; }

    /// <summary>像素格式（CPU 侧语义；生产者据此推导 GPU-API 纹理格式）。</summary>
    public PixelFormat Format { get; init; }

    /// <summary>子资源 / 切片索引（D3D11 纹理数组时为 <c>avFrame->data[1]</c>）。生产者导入整数组后，
    /// 出餐侧 Blit 据此选 <c>baseArrayLayer</c>。默认 0。</summary>
    public int SubresourceIndex { get; init; }

    /// <summary>导入纹理的数组层数（D3D11 纹理数组 = 切片总数）。生产者据此创建整数组 VkImage（单切片为 1）。
    /// 缺省 1：v1 渐进式 H.264/H.265 常见 arrayLayers=1；D3D11VA 纹理数组>1 时必须填真实层数，否则外部内存导入校验失败回落软解。</summary>
    public int ArrayLayers { get; init; } = 1;
}

/// <summary>
/// 中立 GPU 帧生产者桥：由渲染器程序集实现并注册为 Singleton，解码后端仅依赖此抽象。
/// </summary>
/// <remarks>
/// <para>解码器把其原生解码输出（D3D11 共享句柄 / Linux dma_buf / AHardwareBuffer / IOSurface）
/// 经本桥导入为当前渲染器 GPU-API 的纹理（<see cref="IGpuTextureResource"/>），实现零拷贝上屏。
/// 与 <see cref="IGpuDeviceContext"/> 同为 Abstractions 中立桥，严守依赖倒置——
/// 解码后端不反向引用渲染器程序集，亦不感知具体 GPU-API 绑定的创建细节（VkImage 创建由生产者完成）。</para>
/// <para><b>能力自报 + 行为副作用双判据（S_OK≠被接受）</b>：<see cref="TryImport"/> 在扩展不可用、
/// 原生句柄无效或导入失败时返回 <see langword="false"/>，调用方须回落软件解码并计入 [FRAMEPATH] 统计，
/// 绝不报"已就绪"假绿。</para>
/// <para><b>异步策略</b>：<see cref="TryImport"/> 为同步（native 分类）——GPU 纹理导入是同步原生调用，无 I/O await；
/// 实现保持同步，不补 async（补即伪异步）。</para>
/// </remarks>
public interface IGpuFrameProducer
{
    /// <summary>本生产者面向的 GPU API（须与激活渲染器一致，供解码器按 <see cref="IGpuDeviceContext.ApiType"/> 匹配）。</summary>
    GPUApiType ApiType { get; }

    /// <summary>
    /// 尝试将解码原生输出导入为本渲染器 GPU-API 纹理（零拷贝）。
    /// </summary>
    /// <param name="source">解码原生输出描述（句柄 / 尺寸 / 格式 / 切片）。</param>
    /// <param name="texture">成功时为本渲染器 GPU-API 纹理（<see cref="IGpuTextureResource"/>），调用方取得所有权；失败时 <see langword="null"/>。</param>
    /// <returns><see langword="true"/>=零拷贝导入成功；<see langword="false"/>=不可用，调用方回落软解。</returns>
    bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture);
}
