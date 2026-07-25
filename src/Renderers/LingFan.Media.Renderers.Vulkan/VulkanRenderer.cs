namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 视频渲染器。V1 桩实现。
/// </summary>
/// <remarks>
/// <para>V1 桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// Vulkan 渲染器为 Phase 2 目标（Linux / Android / Steam Deck）。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：抛出 <see cref="NotSupportedException"/>。</item>
/// <item><see cref="Attach"/>/<see cref="Detach"/>/<see cref="Present"/>/<see cref="Clear"/>：抛出 <see cref="NotSupportedException"/>。</item>
/// <item><see cref="Dispose"/>/<see cref="DisposeAsync"/>：安全 no-op（无资源可释放）。</item>
/// </list>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class VulkanRenderer : IVideoRenderer
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "Vulkan 渲染器尚未实现。Vulkan 为 Phase 2 目标（Linux / Android / Steam Deck）。");

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
        => throw new NotSupportedException("Vulkan 渲染器尚未实现。");

    /// <inheritdoc/>
    public void Detach()
        => throw new NotSupportedException("Vulkan 渲染器尚未实现。");

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
        => throw new NotSupportedException("Vulkan 渲染器尚未实现。");

    /// <inheritdoc/>
    public void Clear()
        => throw new NotSupportedException("Vulkan 渲染器尚未实现。");

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
