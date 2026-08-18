namespace LingFan.Media.Abstractions;

/// <summary>
/// VA-API 导出抽象（依赖倒置）：解码后端经此把 VA Surface 导出为 dma_buf 描述符，
/// 具体实现由 <c>LingFan.Media.Platforms</c> 的 <c>VaApiInterop</c> 注册（Linux 专属），
/// 后端绝不可反向引用平台程序集。
/// </summary>
/// <remarks>
/// <para>真实零拷贝链路（Linux）：FFmpeg 自建 VAAPI 硬解设备 → 产出 <c>AV_PIX_FMT_VAAPI</c> 帧
/// → <see cref="TryExportSurfaceToDmaBuf"/> 经 libva <c>vaExportSurfaceHandle(DRM_PRIME_2)</c>
/// 导出 dma_buf fd + 多平面布局 → 渲染侧生产者（GL/Vulkan）零拷贝导入上屏。</para>
/// <para><b>句柄所有权</b>：导出产生的 dma_buf fd 由本方法写入 <see cref="VaApiDmaBufDescriptor"/>，
/// 所有权在导出后即转移给调用方（解码器 → 生产者），由生产者经 EGL/Vulkan 导入消费后关闭，本抽象不负责关闭。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——原生 VA-API 调用是同步边界，无 I/O await；实现保持同步。</para>
/// <para>AOT 兼容：接口 + 托管描述符，无反射。</para>
/// </remarks>
public interface IVaApiExport
{
    /// <summary>
    /// 从 VAAPI 表面导出 dma_buf 描述符（<c>DRM_PRIME_2</c>）。
    /// </summary>
    /// <param name="vaDisplay">VADisplay（由 FFmpeg VAAPI hwdevice 内部持有，解码器从 hwdevice 上下文提取后传入）。</param>
    /// <param name="surfaceId">VASurfaceID（<c>avFrame->data[3]</c>）。</param>
    /// <param name="descriptor">成功时为本表面导出的 dma_buf 描述符（fd / 修饰符 / 多平面布局），调用方取得所有权；失败为 <see langword="null"/>。</param>
    /// <returns><see langword="true"/>=导出成功；<see langword="false"/>=VAAPI 不可用 / 表面无效，调用方回落软解。</returns>
    bool TryExportSurfaceToDmaBuf(nint vaDisplay, uint surfaceId, out VaApiDmaBufDescriptor? descriptor);
}

/// <summary>
/// VAAPI 表面导出的 dma_buf 描述符（<c>VADRMPRIMESurfaceDescriptor</c> 托管载体，零外部引用）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="IVaApiExport.TryExportSurfaceToDmaBuf"/> 填充。composed NV12 典型布局：
/// <c>ObjectCount=1</c>（单 fd）、<c>LayerCount=1</c>、单层 <c>PlaneCount=2</c>（Y / UV），
/// 两平面共享同一对象索引、各自带 offset / pitch、共享一个 DRM 修饰符。</para>
/// <para>纯托管结构（不跨原生边界封送），供解码器在托管侧 flatten 为 <see cref="GpuFrameImportSource"/> 的逐平面视图。</para>
/// </remarks>
public sealed class VaApiDmaBufDescriptor
{
    /// <summary>表面像素宽（像素）。</summary>
    public int Width;

    /// <summary>表面像素高（像素）。</summary>
    public int Height;

    /// <summary>DRM fourcc（如 <c>0x3231564E</c> = 'NV12'）。</summary>
    public uint DrmFourcc;

    /// <summary>每对象 DRM 格式修饰符（composed 时单对象，所有平面共享）。</summary>
    public ulong Modifier;

    /// <summary>对象数（dma_buf fd 数）。composed NV12 = 1；separate layers = 2。</summary>
    public int ObjectCount;

    /// <summary>每对象的 dma_buf fd（长度 = <see cref="ObjectCount"/>）。</summary>
    public int[]? ObjectFds;

    /// <summary>层数（NV12 composed = 1 单层含 2 平面；separate = 2）。</summary>
    public int LayerCount;

    /// <summary>每平面所属对象索引（长度随层数 × 4，实际有效数见各层 PlaneCount）。</summary>
    public uint[]? PlaneObjectIndices;

    /// <summary>每平面在对象内的字节偏移（长度随层数 × 4）。</summary>
    public uint[]? PlaneOffsets;

    /// <summary>每平面的行字节步幅（长度随层数 × 4）。</summary>
    public uint[]? PlanePitches;
}
