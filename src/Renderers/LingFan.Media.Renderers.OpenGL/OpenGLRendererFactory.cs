using LingFan.Media.Abstractions;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 OpenGL 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。<see cref="Create"/> 返回 <see cref="OpenGLRenderer"/> 实例。</para>
/// <para>此工厂为同步配置（config 分类），无 I/O，无共享 GPU 设备（GL 上下文为渲染器实例级，非工厂共享 Device 单例）。</para>
/// <para>宽高比缩放：<see cref="ScaleMode"/>（契约层 <see cref="AspectRatioMode"/>）下传创建的渲染器，默认 <see cref="AspectRatioMode.Uniform"/>（信箱）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class OpenGLRendererFactory : IVideoRendererFactory
{
    private readonly ILogger<OpenGLRenderer> _logger;

    /// <summary>软帧缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（保持比例，留黑边）。</summary>
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>
    /// 初始化 <see cref="OpenGLRendererFactory"/> 的新实例。
    /// </summary>
    public OpenGLRendererFactory(ILogger<OpenGLRenderer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        return new OpenGLRenderer(_logger) { ScaleMode = this.ScaleMode };
    }
}
