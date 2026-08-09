namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 视频渲染器。桩实现。
/// </summary>
/// <remarks>
/// <para>桩——所有方法抛出 <see cref="NotSupportedException"/>。
/// OpenGL 渲染器为桌面兼容备用（Windows/Linux/macOS 遗留），
/// Apple 平台已废弃 OpenGL，使用 Metal。Phase 2-3 目标。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="InitializeAsync"/>：抛出 <see cref="NotSupportedException"/>。</item>
/// <item><see cref="Attach"/>/<see cref="Detach"/>/<see cref="Present"/>/<see cref="Clear"/>：抛出 <see cref="NotSupportedException"/>。</item>
/// <item><see cref="Dispose"/>/<see cref="DisposeAsync"/>：安全 no-op（无资源可释放）。</item>
/// </list>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class OpenGLRenderer : IVideoRenderer
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "OpenGL 渲染器尚未实现。OpenGL 为桌面兼容备用（Phase 2-3 目标），Apple 平台请使用 Metal。");

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
        => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <inheritdoc/>
    public void Detach()
        => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
        => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <inheritdoc />
    public TimeSpan PresentationLatency => TimeSpan.Zero;

    /// <inheritdoc/>
    public void Clear()
        => throw new NotSupportedException("OpenGL 渲染器尚未实现。");

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
