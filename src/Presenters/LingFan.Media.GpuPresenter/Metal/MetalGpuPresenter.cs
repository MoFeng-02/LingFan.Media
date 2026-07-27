using LingFan.Media.Abstractions;

namespace LingFan.Media.Presenters.Metal;

/// <summary>
/// Metal GPU 视频呈现器占位。待 <c>LingFan.Media.Renderers.Metal</c> 落地后实现真实逻辑。
/// </summary>
/// <remarks>
/// 当前为占位（NotImplementedException），仅用于确立 GpuPresenter 的多后端二级目录骨架。
/// 实现时需：注入 IVideoRendererFactory（Renderers.Metal），Initialize 解析 Pointer HWND 并 Attach，
/// Present 委托 IVideoRenderer.Present（GPU 纹理路径），且方法加锁保护非线程安全的渲染器。
/// </remarks>
public sealed class MetalGpuPresenter : IGpuPresenter
{
    /// <inheritdoc/>
    public void Initialize(IRenderTarget target) =>
        throw new NotImplementedException("MetalGpuPresenter 待 LingFan.Media.Renderers.Metal 落地后实现。");

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        // 防御性释放帧，避免误调用导致帧泄漏
        frame.Dispose();
        throw new NotImplementedException("MetalGpuPresenter 待 LingFan.Media.Renderers.Metal 落地后实现。");
    }

    /// <inheritdoc/>
    public void Clear() =>
        throw new NotImplementedException("MetalGpuPresenter 待 LingFan.Media.Renderers.Metal 落地后实现。");

    /// <inheritdoc/>
    public void Resize(int width, int height, float scale) =>
        throw new NotImplementedException("MetalGpuPresenter 待 LingFan.Media.Renderers.Metal 落地后实现。");

    /// <inheritdoc/>
    public void Dispose() { }
}
