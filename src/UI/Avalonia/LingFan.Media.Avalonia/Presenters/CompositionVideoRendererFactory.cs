using System;
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
    private readonly SharedGpuSurfaceSourceSelector _selector;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="CompositionVideoRendererFactory"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入）。</param>
    /// <param name="selector">共享表面源工厂选择记忆（进程级，避免每次从头逐个回退）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public CompositionVideoRendererFactory(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories,
        SharedGpuSurfaceSourceSelector selector,
        ILoggerFactory loggerFactory)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        // 零拷贝硬解硬渲染路径（Android 同其他平台）：优先无空域 GPU 合成
        // （CompositionVideoRenderer → VulkanSharedSurfaceSource 消费 AndroidHardwareBufferFrameResource），
        // 解码侧零 CPU 拷贝、渲染侧 GPU 内 YCbCr 转换上屏。
        // VideoView.EnsurePresenter 在 Attach 失败或运行期 unhealthy（健康监控自愈，见 CompositionVideoRenderer）
        // 时自动回退 Skia 软渲染；此处无需抛异常或特殊分支——零拷贝为首选，回退由框架兜底。
        // 注意：MediaPlayer.OpenAsync 经单值 DI 解析本工厂创建的「管线渲染器」仅作 A/V 同步时延参考，
        // 不参与显示（管线绝不对其 Present），故此处返回何种渲染器不影响打开成败。
        return new CompositionVideoRenderer(_surfaceFactories, _selector, _loggerFactory.CreateLogger<CompositionVideoRenderer>());
    }
}
