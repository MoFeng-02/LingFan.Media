using System.Collections.Generic;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// <see cref="CompositionVideoRenderer"/> 的工厂（无空域 GPU 上屏渲染器）。
/// </summary>
/// <remarks>
/// <para>类名须包含 <c>CompositionVideoRenderer</c>，以支持 <c>VideoView.RendererType</c>
/// 前置匹配；否则由 <c>VideoView.EnsurePresenter</c> 按 DI 顺序尝试（位于 D3D11 SwapChain 之后、
/// Skia 之前），Attach 失败自动回退。</para>
/// <para>依赖 <see cref="IEnumerable{T}"/> 注入全部 <see cref="ISharedGpuSurfaceSourceFactory"/>
/// （D3D11 适配器等，由各自渲染器模块注册）；渲染器据此挑选被合成器支持的工厂。</para>
/// <para>AOT 兼容：sealed 类，构造函数自动解析，无反射。</para>
/// </remarks>
public sealed class CompositionVideoRendererFactory : IVideoRendererFactory
{
    private readonly IEnumerable<ISharedGpuSurfaceSourceFactory> _surfaceFactories;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="CompositionVideoRendererFactory"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public CompositionVideoRendererFactory(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories, ILoggerFactory loggerFactory)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoRenderer Create() =>
        new CompositionVideoRenderer(_surfaceFactories, _loggerFactory.CreateLogger<CompositionVideoRenderer>());
}
