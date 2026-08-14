using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGLES;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 OpenGL ES 实现（Android 兜底上屏后端）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。<see cref="Create"/> 返回 <see cref="OpenGLESRenderer"/> 实例。</para>
/// <para><b>与桌面 OpenGL / Vulkan / D3D11 的差异</b>：本工厂<b>不注册</b> <see cref="IGpuDeviceContext"/> 与
/// <see cref="IGpuFrameProducer"/>——GLES 当前无工厂级离屏设备上下文（零拷贝共享组尚未建立，属 C 线未来增强），
/// 故解码后端经此接口获取不到 GLES 设备句柄，仅走软帧上屏（非"假绿"：软解回退是明确契约行为，非 S_OK≠被接受的误判）。</para>
/// <para>宽高比缩放：<see cref="ScaleMode"/>（契约层 <see cref="AspectRatioMode"/>）下传创建的渲染器，默认 <see cref="AspectRatioMode.Uniform"/>（信箱）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class OpenGLESRendererFactory : IVideoRendererFactory
{
    private readonly ILogger<OpenGLESRenderer> _logger;

    /// <summary>软帧缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（保持比例，留黑边）。</summary>
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>初始化 <see cref="OpenGLESRendererFactory"/> 的新实例。</summary>
    public OpenGLESRendererFactory(ILogger<OpenGLESRenderer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        return new OpenGLESRenderer(_logger) { ScaleMode = this.ScaleMode };
    }
}
