using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 后端无关的「无空域、纯控件级」GPU 上屏渲染器（Avalonia <see cref="ICompositionGpuInterop"/>）。
/// </summary>
/// <remarks>
/// <para>经 <see cref="ISharedGpuSurfaceSource"/>（D3D11 适配器等 GPU 适配层）把视频帧写入
/// 跨设备共享纹理，再用 Avalonia 合成器直接导入、作为控件子视觉合成——<b>无独占 HWND、无空域</b>，
/// 不被 UI 内容遮挡/裁剪，且 Skia 仍可作为末级兜底。</para>
/// <para><b>解耦</b>：本类只碰 Avalonia Composition API，<b>不引用任何 GPU 库</b>；中立
/// <see cref="SharedGpuHandleKind"/> 经一次 <see cref="MapHandleKind"/> switch 映射到
/// <see cref="KnownPlatformGraphicsExternalImageHandleTypes"/>，渲染器层零 GPU 耦合。</para>
/// <para><b>回退</b>：在 <c>VideoView</c> 回退链中位于 D3D11 SwapChain 之后、Skia 之前；
/// <see cref="Attach"/> 失败（合成器不可用 / 无匹配句柄类型 / 共享表面源创建失败）即抛
/// <see cref="NotSupportedException"/>，由 <c>VideoView</c> 自动回退 Skia。</para>
/// <para><b>线程</b>：Attach/Detach 在 UI 线程（合成器/子视觉创建、事件订阅）；
/// <see cref="Present"/> 在管线线程（ImportImage / UpdateWithKeyedMutexAsync，与官方 GpuInterop 样例同线程模型）。</para>
/// <para><b>AOT 兼容</b>：无反射。</para>
/// </remarks>
internal sealed class CompositionVideoRenderer : IVideoRenderer
{
    // ≈ 1 个 60Hz 帧刷新周期；VideoPipeline 据其做音画对齐提前量。
    private static readonly TimeSpan PresentLatency = TimeSpan.FromMilliseconds(16);

    private readonly IEnumerable<ISharedGpuSurfaceSourceFactory> _surfaceFactories;
    private readonly ILogger<CompositionVideoRenderer> _logger;

    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _drawingSurface;
    private CompositionSurfaceVisual? _surfaceVisual;
    private Visual? _attachedVisual;
    private ISharedGpuSurfaceSource? _source;
    private ICompositionImportedGpuImage? _imported;
    private string? _handleType;
    private ulong _lastVersion;
    private Task? _lastPresent;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="CompositionVideoRenderer"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入，按合成器支持筛选）。</param>
    /// <param name="logger">日志。</param>
    internal CompositionVideoRenderer(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories,
        ILogger<CompositionVideoRenderer> logger)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public TimeSpan PresentationLatency => PresentLatency;

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 共享表面源不需要 HWND；仅需控件的 Visual 以挂载子视觉（无空域）。
        if (target.NativeHandle is not Visual visual)
            throw new NotSupportedException("CompositionVideoRenderer 需要 IRenderTarget.NativeHandle 为 Avalonia Visual。");

        var compositor = ElementComposition.GetElementVisual(visual)?.Compositor
            ?? throw new NotSupportedException("无法从控件取得 Compositor（当前非组合渲染后端）。");

        // 🔴 受 IVideoRenderer.Attach 同步 void 契约约束，必须同步解析 ValueTask。
        // Avalonia 12 的 TryGetCompositionGpuInterop 实现为同步判定（仅查询当前渲染后端是否支持
        // GPU 互操作，无真实 I/O/await），ValueTask 退化为即时完成，无死锁风险。此为契约强制的同步点。
        ICompositionGpuInterop? interop = compositor.TryGetCompositionGpuInterop().GetAwaiter().GetResult();
        if (interop is null)
            throw new NotSupportedException("当前 Avalonia 渲染后端不支持 ICompositionGpuInterop（回退 Skia）。");

        // 选定首个可用且句柄类型被合成器支持的共享表面源工厂（UI 层无「优先 D3D11」硬编码分支）。
        ISharedGpuSurfaceSourceFactory? selected = null;
        string? handleType = null;
        foreach (var f in _surfaceFactories)
        {
            if (!f.IsAvailable)
                continue;
            string? ht = MapHandleKind(f.HandleKind);
            if (ht is not null && interop.SupportedImageHandleTypes.Contains(ht))
            {
                selected = f;
                handleType = ht;
                break;
            }
        }

        if (selected is null)
            throw new NotSupportedException("无可用且被合成器支持的共享表面源工厂（回退 Skia）。");

        // 创建绘制表面 + 子视觉（无空域，挂在控件 Visual 下），并铺满控件边界。
        _drawingSurface = compositor.CreateDrawingSurface();
        _surfaceVisual = compositor.CreateSurfaceVisual();
        _surfaceVisual.Surface = _drawingSurface;
        _surfaceVisual.Size = new Vector(visual.Bounds.Width, visual.Bounds.Height);
        ElementComposition.SetElementChildVisual(visual, _surfaceVisual);

        // 初次尺寸：OnAttachedToVisualTree 时布局可能尚未完成（Bounds 仍 0），
        // 延迟到下一 UI 循环读取 post-layout 尺寸，避免子视觉尺寸为 0 不可见。
        // 后续尺寸变化由 OnControlSizeChanged 持续同步。
        Dispatcher.UIThread.Post(() =>
        {
            if (_surfaceVisual is not null && !_disposed)
                _surfaceVisual.Size = new Vector(visual.Bounds.Width, visual.Bounds.Height);
        });

        if (visual is Control control)
            control.SizeChanged += OnControlSizeChanged;

        // 创建共享表面源（延迟到此处以确保共享 D3D11 设备就绪；失败抛异常触发回退）。
        ISharedGpuSurfaceSource source;
        try
        {
            source = selected.Create();
        }
        catch (Exception ex)
        {
            throw new NotSupportedException("共享表面源创建失败（回退 Skia）。", ex);
        }

        _attachedVisual = visual;
        _interop = interop;
        _source = source;
        _handleType = handleType;
        _lastVersion = 0;
    }

    private void OnControlSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_surfaceVisual is not null)
            _surfaceVisual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        if (_disposed || _source is null || _interop is null || _drawingSurface is null || _handleType is null)
            return;

        // 解码帧经 GPU 适配层写入共享纹理（内部 keyed mutex 握手，超时即丢帧）。
        if (!_source.TryWriteFrame(frame, out var desc) || !desc.IsValid)
            return;

        try
        {
            // 纹理重建（Version 变化）→ 丢弃旧导入图像，按新句柄/尺寸重新导入。
            if (_imported is null || _lastVersion != desc.Version)
            {
                _imported?.DisposeAsync();
                _imported = null;

                var props = new PlatformGraphicsExternalImageProperties
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                };
                _imported = _interop.ImportImage(new PlatformHandle(desc.Handle, _handleType), props);
                if (_imported is null)
                {
                    _logger.LogWarning(
                        "CompositionVideoRenderer 导入共享纹理失败（句柄类型={HandleType}），跳过本帧。",
                        _handleType);
                    return;
                }
                _lastVersion = desc.Version;
            }

            // 消费者（Avalonia 合成线程）以 ConsumerAcquireKey 取锁采样、以 ConsumerReleaseKey 归还。
            _surfaceVisual!.Opacity = 1;
            _lastPresent = _drawingSurface.UpdateWithKeyedMutexAsync(
                _imported, (uint)_source.ConsumerAcquireKey, (uint)_source.ConsumerReleaseKey);
        }
        catch (Exception ex)
        {
            // 单帧提交异常（如跨设备导入失败）吞掉，绝不击杀管线线程；下一帧重试。
            // 用 Warning 而非 Trace：跨设备纹理导入失败是「无空域上屏空白」的首要嫌疑，必须可见以便诊断。
            _logger.LogWarning(ex, "CompositionVideoRenderer 提交帧失败（跳过本帧）。");
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        // 隐藏表面视觉（保留子视觉挂载，避免重建开销）；下次 Present 恢复 Opacity=1。
        if (_surfaceVisual is not null)
            _surfaceVisual.Opacity = 0;
    }

    /// <inheritdoc/>
    public void Detach()
    {
        DetachCore();
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void DetachCore()
    {
        if (_attachedVisual is Control control)
            control.SizeChanged -= OnControlSizeChanged;

        // 移除子视觉挂载
        if (_attachedVisual is not null)
            ElementComposition.SetElementChildVisual(_attachedVisual, null!);
        _attachedVisual = null;

        _drawingSurface?.Dispose();
        _drawingSurface = null;

        // CompositionVisual 无公开 Dispose：解除子视觉挂载后由合成器自行回收。
        _surfaceVisual = null;

        // 合成器资源释放本身是「排入合成批次队列」的操作，与之前提交的
        // UpdateWithKeyedMutexAsync 在同一队列上串行，故无需阻塞等待其完成。
        // 需要确定性等待（例如宿主退出前）请改用 DisposeAsync。
        var imported = _imported;
        _imported = null;
        if (imported is not null)
            _ = imported.DisposeAsync();

        _lastPresent = null;
        _source?.Dispose();
        _source = null;
        _interop = null;
        _handleType = null;
        _lastVersion = 0;
    }

    /// <inheritdoc/>
    /// <remarks>同步释放：不阻塞等待挂起的合成提交（禁止 <c>.GetAwaiter().GetResult()</c>）。
    /// 需要「最后一帧提交完成后再拆除」的确定性语义时用 <see cref="DisposeAsync"/>。</remarks>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DetachCore();
    }

    /// <inheritdoc/>
    /// <remarks>真异步释放：先 await 最后一次合成提交与导入图像释放，再拆除子视觉与共享表面源，
    /// 避免宿主退出时释放仍被合成线程引用的跨设备资源。</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        Task? pending = _lastPresent;
        _lastPresent = null;
        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "CompositionVideoRenderer 释放时末帧提交异常（忽略）。");
            }
        }

        ICompositionImportedGpuImage? imported = _imported;
        _imported = null;
        if (imported is not null)
        {
            try
            {
                await imported.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "CompositionVideoRenderer 释放导入图像异常（忽略）。");
            }
        }

        DetachCore();
    }

    /// <summary>
    /// 中立 <see cref="SharedGpuHandleKind"/> → Avalonia <see cref="KnownPlatformGraphicsExternalImageHandleTypes"/> 的一次映射。
    /// </summary>
    private static string? MapHandleKind(SharedGpuHandleKind kind) => kind switch
    {
        SharedGpuHandleKind.D3D11TextureGlobalSharedHandle => KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle,
        SharedGpuHandleKind.D3D11TextureNtHandle => KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle,
        SharedGpuHandleKind.VulkanOpaqueNtHandle => KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle,
        SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor => KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor,
        SharedGpuHandleKind.IOSurfaceRef => KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef,
        _ => null,
    };
}
