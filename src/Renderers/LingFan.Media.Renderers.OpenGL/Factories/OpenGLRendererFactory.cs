using System;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 OpenGL 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。<see cref="Create"/> 返回 <see cref="OpenGLRenderer"/> 实例。</para>
/// <para><b>工厂级离屏 GL 设备上下文</b>：持有 <see cref="OpenGLOffscreenDeviceContext"/>（实现 <see cref="IGpuDeviceContext"/>）
/// 作为共享组所有者——GL 上下文虽仍由渲染器实例在 <see cref="OpenGLRenderer.Attach"/> 按窗口建立 on-screen 上下文，
/// 但工厂层面额外维护一个离屏 GL 上下文单例，供解码后端在 decode-init 阶段即经
/// <see cref="IGpuDeviceContext"/> 拿到 OpenGL 设备句柄（启用硬解 interop + 零拷贝），
/// 与 D3D11/Vulkan 的"工厂级共享 GPU 设备"完全同源（依赖倒置，消除架构不对称）。</para>
/// <para>on-screen 上下文以离屏上下文为共享组（shareContext）接入，解码侧产出的 GL 纹理对渲染器可见。</para>
/// <para>宽高比缩放：<see cref="ScaleMode"/>（契约层 <see cref="AspectRatioMode"/>）下传创建的渲染器，默认 <see cref="AspectRatioMode.Uniform"/>（信箱）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class OpenGLRendererFactory : IVideoRendererFactory, IDisposable
{
    private readonly ILogger<OpenGLRenderer> _logger;
    private readonly object _deviceLock = new();
    private OpenGLOffscreenDeviceContext? _deviceContext;
    private bool _disposed;

    /// <summary>软帧缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（保持比例，留黑边）。</summary>
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>
    /// 初始化 <see cref="OpenGLRendererFactory"/> 的新实例。
    /// </summary>
    public OpenGLRendererFactory(ILogger<OpenGLRenderer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 工厂级离屏 GL 设备上下文（实现 <see cref="IGpuDeviceContext"/>，共享组所有者）。延迟创建单例。
    /// 解码后端经此接口在 decode-init 阶段获取 OpenGL 设备句柄，启用硬解 interop + 零拷贝。
    /// </summary>
    public OpenGLOffscreenDeviceContext DeviceContext
    {
        get
        {
            if (_deviceContext is null)
            {
                lock (_deviceLock)
                {
                    _deviceContext ??= new OpenGLOffscreenDeviceContext(_logger);
                }
            }
            return _deviceContext;
        }
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        return new OpenGLRenderer(_logger, DeviceContext) { ScaleMode = this.ScaleMode };
    }

    /// <summary>
    /// 创建中立 GPU 帧生产者（<see cref="IGpuFrameProducer"/> 具体实现），供解码后端经依赖倒置把原生解码输出
    /// 导入为 OpenGL 纹理（零拷贝上屏）。
    /// </summary>
    /// <remarks>
    /// <para>解析延迟到消费方（解码器）真正请求时才发生，且仅在 <see cref="IGpuDeviceContext.ApiType"/>==OpenGL 时被解码器选用——
    /// 与 <see cref="LingFan.Media.Renderers.Vulkan.Factories.VulkanRendererFactory.CreateFrameProducer"/> 同源守卫，
    /// 避免硬编码任一渲染器。</para>
    /// <para>生产者持有工厂级离屏 GL 设备上下文（共享组所有者）用于 MakeCurrent 与共享组根，自身不注册 IGpuDeviceContext。</para>
    /// </remarks>
    public OpenGLGpuFrameProducer CreateFrameProducer()
    {
        return new OpenGLGpuFrameProducer(DeviceContext, _logger);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deviceContext?.Dispose();
        _deviceContext = null;
    }
}
