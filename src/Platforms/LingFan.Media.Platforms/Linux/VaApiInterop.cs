namespace LingFan.Media.Platforms.Linux;

/// <summary>
/// VA-API（Video Acceleration API）硬件解码互操作。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>职责：通过 VA-API 创建硬件解码器，输出 VA Surface，
/// 再通过 dmabuf 导出为 VkImage 或 GLTexture 实现零拷贝。</para>
/// <para><b>GPU 零拷贝路径</b>：
/// VAAPI → VA Surface → dmabuf → VkImage → VulkanRenderer → Swapchain → Wayland subsurface → Display
/// VAAPI → VA Surface → dmabuf → GLTexture → OpenGLRenderer → GLX/EGL → Display</para>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// VAAPI 硬解属 Phase 2 目标（Linux / Steam Deck）。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——原生 VA-API 调用是同步边界，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class VaApiInterop
{
    /// <summary>
    /// 创建 VA Display（从 DRM 文件描述符）。
    /// </summary>
    /// <param name="drmFd">DRM 设备文件描述符（如 /dev/dri/renderD128）。</param>
    /// <returns>VADisplay 原生句柄。</returns>
    public nint CreateVaDisplay(nint drmFd)
        => throw new NotSupportedException("VA-API 互操作尚未实现。VAAPI 为 Phase 2 目标。");

    /// <summary>
    /// 从 VA Surface 导出 dmabuf 文件描述符（用于 Vulkan / OpenGL 导入）。
    /// </summary>
    /// <param name="vaDisplay">VADisplay 句柄。</param>
    /// <param name="surface">VASurfaceID。</param>
    /// <returns>dmabuf 文件描述符。</returns>
    public int ExportToDmaBuf(nint vaDisplay, uint surface)
        => throw new NotSupportedException("VA-API dmabuf 导出尚未实现。");

    /// <summary>
    /// 创建 VA Surface（硬件解码输出目标）。
    /// </summary>
    /// <param name="vaDisplay">VADisplay 句柄。</param>
    /// <param name="width">表面宽度。</param>
    /// <param name="height">表面高度。</param>
    /// <param name="format">VA 表面格式（如 VA_FOURCC_NV12）。</param>
    /// <returns>VASurfaceID。</returns>
    public uint CreateSurface(nint vaDisplay, int width, int height, uint format)
        => throw new NotSupportedException("VA-API Surface 创建尚未实现。");
}
