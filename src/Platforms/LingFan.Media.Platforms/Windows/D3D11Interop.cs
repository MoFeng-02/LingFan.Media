namespace LingFan.Media.Platforms.Windows;

/// <summary>
/// D3D11 与其他 GPU API 互操作。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>职责：管理 D3D11 资源跨 API 共享（D3D11 → Vulkan → OpenGL），
/// 实现硬解纹理到渲染器纹理的零拷贝传递。</para>
/// <para><b>GPU 零拷贝路径</b>：DXVA2 / D3D11VA → ID3D11Texture2D → D3D11Renderer → SwapChain → DirectComposition（IDCompositionVisual 合成到窗口 Visual 树）→ Display</para>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// 未来实现将使用 Vortice.Direct3D11 + DXGI KeyedMutex 实现跨进程/跨 API 共享。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——GPU 资源创建/共享是原生同步操作，无 I/O。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class D3D11Interop
{
    /// <summary>
    /// 创建可跨 API 共享的 D3D11 纹理。
    /// </summary>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">DXGI 纹理格式（如 DXGI_FORMAT_B8G8R8A8_UNORM = 87）。</param>
    /// <returns>共享 ID3D11Texture2D 的原生句柄。</returns>
    public nint CreateSharedTexture(int width, int height, int format)
        => throw new NotSupportedException("D3D11 互操作尚未实现。V1 由 D3D11Renderer 直接管理纹理。");

    /// <summary>
    /// 将 Vulkan VkImage 导入为 D3D11 纹理（跨 API 零拷贝）。
    /// </summary>
    /// <param name="vkDevice">VkDevice 句柄。</param>
    /// <param name="vkImage">VkImage 句柄（需带 VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT_KHR）。</param>
    /// <returns>导入的 ID3D11Texture2D 原生句柄。</returns>
    public nint OpenSharedTextureFromVulkan(nint vkDevice, nint vkImage)
        => throw new NotSupportedException("D3D11-Vulkan 互操作尚未实现。");

    /// <summary>
    /// 将 OpenGL 纹理导入为 D3D11 纹理（跨 API 零拷贝）。
    /// </summary>
    /// <param name="glTexture">OpenGL 纹理 ID。</param>
    /// <returns>导入的 ID3D11Texture2D 原生句柄。</returns>
    public nint OpenSharedTextureFromOpenGL(int glTexture)
        => throw new NotSupportedException("D3D11-OpenGL 互操作尚未实现。");

    /// <summary>
    /// 从硬解输出创建 D3D11 纹理（DXVA2 / D3D11VA 路径）。
    /// </summary>
    /// <param name="decoderOutput">硬解器输出缓冲（AVD3D11VAContext 或 IMFTransform 输出）。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <returns>ID3D11Texture2D 原生句柄 + 数组索引。</returns>
    public (nint texture, int index) CreateTextureFromHardwareDecoder(nint decoderOutput, int width, int height)
        => throw new NotSupportedException("DXVA2 / D3D11VA 硬解纹理提取尚未实现。");
}
