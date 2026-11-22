using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 后端无关的「无空域、纯控件级」GPU 上屏渲染器（Avalonia <see cref="Avalonia.Rendering.Composition.ICompositionGpuInterop"/>）。
/// </summary>
/// <remarks>
/// <para>经 <see cref="ISharedGpuSurfaceSource"/>（D3D11 适配器等 GPU 适配层）把视频帧写入
/// 跨设备共享纹理，再用 Avalonia 合成器直接导入、作为控件子视觉合成——<b>无独占 HWND、无空域</b>，
/// 不被 UI 内容遮挡/裁剪，且 Skia 仍可作为末级兜底。</para>
/// <para><b>解耦</b>：本类只碰 Avalonia Composition API，<b>不引用任何 GPU 库</b>；中立
/// <see cref="SharedGpuHandleKind"/> 经一次 <see cref="MapHandleKind"/> switch 映射到
/// <see cref="KnownPlatformGraphicsExternalImageHandleTypes"/>，渲染器层零 GPU 耦合。</para>
    /// <para><b>回退</b>：在 <c>VideoView</c> 回退链中位于 D3D11 SwapChain 之后、Skia 之前；
    /// <see cref="Attach"/> 失败时（合成器不可用 / 无匹配句柄类型 / 共享表面源 Create 或导入自检失败）
    /// 抛 <see cref="NotSupportedException"/>，由 <c>VideoView</c> 自动回退 Skia。</para>
    /// <para><b>有序回退 + 记忆</b>：候选工厂按注册序（Vulkan→D3D11→…）逐家 <c>Create</c> + 导入自检，
    /// 任一家失败即跳过试下一家，全失败才回退 Skia；成功者经 <see cref="SharedGpuSurfaceSourceSelector"/>
    /// 进程级记忆，后续挂载优先命中、不再每次从头部逐个探测（对标后端 <c>Lazy&lt;*Backend&gt;</c> 模式）。
    /// OpenGL 等未实现后端自然不在候选内，不阻塞链。</para>
    /// <para><b>线程</b>：Attach/Detach 在 UI 线程（合成器/子视觉创建、事件订阅）；
    /// <see cref="Present"/> 在管线线程被调用，仅负责把帧渲染进独立共享纹理（GPU 拷贝，与解码帧解耦）；
    /// 真正的导入与上屏（<c>ImportImage</c> / <c>UpdateWithKeyedMutexAsync</c> / <c>Visual</c> 属性）必须经
    /// <c>Dispatcher.UIThread</c> 封送到 UI（Compositor 拥有者）线程执行，否则抛 VerifyAccess 异常。</para>
/// <para><b>AOT 兼容</b>：无反射。</para>
/// </remarks>
internal sealed partial class CompositionVideoRenderer : IVideoRenderer, IRendererHealth
{
    // ≈ 1 个 60Hz 帧刷新周期；VideoPipeline 据其做音画对齐提前量。
    private static readonly TimeSpan PresentLatency = TimeSpan.FromMilliseconds(16);

    private readonly IEnumerable<ISharedGpuSurfaceSourceFactory> _surfaceFactories;
    private readonly SharedGpuSurfaceSourceSelector _selector;
    private readonly ILogger<CompositionVideoRenderer> _logger;

    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _drawingSurface;
    private CompositionSurfaceVisual? _surfaceVisual;
    private Visual? _attachedVisual;
    private Compositor? _compositor;
    private ISharedGpuSurfaceSource? _source;
    private ICompositionImportedGpuImage? _imported;
    private ICompositionImportedGpuSemaphore? _waitSem;
    private ICompositionImportedGpuSemaphore? _signalSem;
    private string? _handleType;
    private ulong _lastVersion;
    /// <summary>合成器 GPU 上下文重建标记。Android 初始化期合成器会重建 RenderInterface，使旧 interop
    /// 在创建时捕获的 Context 快照与当前 compositor.Server.RenderInterface.Value 不再是同一实例，
    /// 其导入图像 IsUsable 永久 false、UpdateAsync 抛 PlatformGraphicsContextLostException。置位后，
    /// 下一帧经 <see cref="EnsureInteropAndImportAsync"/> 重新 TryGetCompositionGpuInterop() 以刷新
    /// Context 快照并重导。跨程序集无法直接读取 Avalonia 内部 IsUsable，故以本标记驱动重拉。</summary>
    private volatile bool _interopStale;
    /// <summary>首次导入参数是否已打点（仅打一次，避免每帧刷屏）。</summary>
    private bool _importPropsLogged;
    private Task? _lastPresent;
    private bool _disposed;

    // 运行期健康：连续无法呈现达到阈值即触发 Unhealthy → 宿主（VideoView）拉黑本工厂并回退 Skia，
    // 确保 Composition 永不静默空白（Attach 成功但运行期持续出不了画时有兜底）。
    // 阈值取 10（30fps 下约 0.33s）：主时钟是音频驱动，回退越晚音画起点错位越大——
    // 导入失败若连续多帧才恢复，会被感知为「音频比画面先出」。
    private int _consecutiveSkips;
    private bool _unhealthyFired;
    private const int SkipThreshold = 10;
    private readonly object _healthLock = new();

    // 布局状态：CompositionSurfaceVisual 无 Stretch 属性，须手动根据 VideoView.Stretch 计算目标尺寸与偏移。
    private Stretch _stretch = Stretch.Uniform;
    private Vector _controlSize;
    private Vector _frameSize;
    private IDisposable? _stretchSubscription;

    /// <summary>
    /// 初始化 <see cref="CompositionVideoRenderer"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入，按合成器支持筛选）。</param>
    /// <param name="logger">日志。</param>
    internal CompositionVideoRenderer(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories,
        SharedGpuSurfaceSourceSelector selector,
        ILogger<CompositionVideoRenderer> logger)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public TimeSpan PresentationLatency => PresentLatency;

    /// <inheritdoc/>
    public event Action? Unhealthy;

    /// <inheritdoc/>
    public void Attach(IRenderTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 共享表面源不需要 HWND；仅需控件的 Visual 以挂载子视觉（无空域）。
        if (target.NativeHandle is not Visual visual)
            throw new NotSupportedException("CompositionVideoRenderer 需要 IRenderTarget.NativeHandle 为 Avalonia Visual。");

        var compositor = ElementComposition.GetElementVisual(visual)?.Compositor
            ?? throw new NotSupportedException("无法从控件取得 Compositor（当前非组合渲染后端）。");

        _compositor = compositor;
        _attachedVisual = visual;

        // 轻量同步挂载：仅完成不依赖 GPU 互操作的子视觉创建与布局订阅。
        // 真正的 ICompositionGpuInterop 解析（含共享表面源 Create + 导入自检）必须延迟到渲染循环
        // 起来之后、于 UI 线程异步完成——TryGetCompositionGpuInterop 内部走 Dispatcher.VerifyAccess +
        // PostServerJob：既要求 UI 线程、又不可在 UI 线程阻塞（否则 server job 永无机会执行 → 死锁，
        // 表现为应用卡在启动 logo）。故同步 Attach 阶段只挂载空子视觉并返回成功；互操作就绪后由
        // ResolveAsync 补齐，失败则经既有 IRendererHealth.Unhealthy 机制让 VideoView 回退 Skia，
        // 任意阶段失败均有兜底、绝不静默空白或挂起。
        _drawingSurface = compositor.CreateDrawingSurface();
        _surfaceVisual = compositor.CreateSurfaceVisual();
        _surfaceVisual.Surface = _drawingSurface;
        ElementComposition.SetElementChildVisual(visual, _surfaceVisual);

        _controlSize = new Vector(visual.Bounds.Width, visual.Bounds.Height);
        _frameSize = new Vector(0, 0);
        if (visual is Control control)
        {
            _stretch = control.GetValue(VideoView.StretchProperty);
            control.SizeChanged += OnControlSizeChanged;
            _stretchSubscription = control.GetObservable(VideoView.StretchProperty)
                .Subscribe(new StretchObserver(this));
        }
        UpdateSurfaceLayout();

        // 初次尺寸：OnAttachedToVisualTree 时布局可能尚未完成（Bounds 仍 0），
        // 延迟到下一 UI 循环读取 post-layout 尺寸，避免子视觉尺寸为 0 不可见。
        // 后续尺寸变化由 OnControlSizeChanged 持续同步。
        Dispatcher.UIThread.Post(() =>
        {
            if (_surfaceVisual is not null && !_disposed && visual is not null)
            {
                _controlSize = new Vector(visual.Bounds.Width, visual.Bounds.Height);
                UpdateSurfaceLayout();
            }
        });

        _lastVersion = 0;

        // 延迟解析 GPU 互操作（UI 线程、渲染循环已启动，无死锁 / VerifyAccess 风险）。
        Dispatcher.UIThread.Post(() => _ = ResolveAsync());
    }

    /// <summary>拦截式触发 <see cref="Unhealthy"/>：原子单次触发，避免与运行期跳过计数并发重复触发。</summary>
    private void MarkUnhealthy()
    {
        bool already;
        lock (_healthLock)
        {
            already = _unhealthyFired;
            _unhealthyFired = true;
        }
        if (already)
            return;
        _logger.LogError("CompositionVideoRenderer 解析/导入失败，触发渲染器回退（Skia）。");
        if (_surfaceVisual is not null)
            Dispatcher.UIThread.Post(() => { if (_surfaceVisual is not null) _surfaceVisual.Opacity = 0; });
        Unhealthy?.Invoke();
    }

    /// <summary>
    /// Android（SyncMode.None）专用：每帧重新解析 interop 并导入当前帧外部图像。
    /// 依据（Avalonia 12.1.1 源码）：CompositionImportedGpuImage 在导入时把
    /// <c>Context = compositor.Server.RenderInterface.Value</c> 一次性冻结；<see cref="IsUsable"/>
    /// 要求呈现时 <c>RenderInterface.Value</c> 与该 Context 为同一实例。Android 合成器 RenderInterface
    /// 跨帧重建，跨帧缓存导入必失配（PlatformGraphicsContextLostException）。每帧重解析+重导+await
    /// <c>ICompositionGpuImportedObject.ImportCompleted</c>，保证呈现瞬间 Context 与 Value 一致。
    /// </summary>
    /// <returns>本帧导入图像；任一环节失败返回 <see langword="null"/>（调用方应跳过本帧）。</returns>
    // 把生产者描述符携带的 SharedGpuSurfaceFormat 映射到合成器导入所需的 PlatformGraphicsExternalImageFormat。
    // Android 零拷贝路径生产者用 R8G8B8A8（AHB 唯一等价格式，B8G8R8A8 无 AHB 等价），须对应 R8G8B8A8UNorm，
    // 否则合成器以 BGRA 采样会致通道互换；Windows/Linux 仍走 B8G8R8A8UNorm（与 D3D/Vulkan 约定一致）。

    /// <summary>把出画失败记录封送到 UI 线程（<see cref="NoteSkip"/> 内会触碰 Visual 属性，必须 UI 线程）。</summary>
    private void PostNoteSkip() => Dispatcher.UIThread.Post(NoteSkip);

    /// <summary>记录一次出画失败；连续达到阈值即触发 <see cref="Unhealthy"/> 让宿主回退 Skia。</summary>
    private void NoteSkip()
    {
        if (_disposed)
            return;
        int n = Interlocked.Increment(ref _consecutiveSkips);
        if (n < SkipThreshold)
            return;
        // 原子化单次触发，避免管线线程与 UI 线程并发重复触发 Unhealthy。
        bool already;
        lock (_healthLock)
        {
            already = _unhealthyFired;
            _unhealthyFired = true;
        }
        if (already)
            return;

        _logger.LogError("CompositionVideoRenderer 连续 {N} 帧无法呈现，触发渲染器回退（Skia）。", SkipThreshold);
        // Visual 属性必须在 UI 线程设置。
        if (_surfaceVisual is not null)
            Dispatcher.UIThread.Post(() => { if (_surfaceVisual is not null) _surfaceVisual.Opacity = 0; });
        Unhealthy?.Invoke();
    }

    /// <summary>记录一次成功出画，清零连续失败计数（原子，跨线程安全）。</summary>
    private void NoteSuccess() => Interlocked.Exchange(ref _consecutiveSkips, 0);

    /// <inheritdoc/>
    public void Clear()
    {
        // 隐藏表面视觉（保留子视觉挂载，避免重建开销）；下次 Present 恢复 Opacity=1。
        // Visual 属性须 UI 线程设置。
        if (_surfaceVisual is not null)
            Dispatcher.UIThread.Post(() => { if (_surfaceVisual is not null) _surfaceVisual.Opacity = 0; });
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

        _stretchSubscription?.Dispose();
        _stretchSubscription = null;

        // 移除子视觉挂载
        if (_attachedVisual is not null)
            ElementComposition.SetElementChildVisual(_attachedVisual, null!);
        _attachedVisual = null;

        _drawingSurface?.Dispose();
        _drawingSurface = null;

        // CompositionVisual 无公开 Dispose：解除子视觉挂载后由合成器自行回收。
        _surfaceVisual = null;

        // 合成器资源释放本身是「排入合成批次队列」的操作，与之前提交的
        // UpdateWithKeyedMutexAsync / UpdateWithSemaphoresAsync 在同一队列上串行，故无需阻塞等待其完成。
        // 需要确定性等待（例如宿主退出前）请改用 DisposeAsync。
        var imported = _imported;
        _imported = null;
        if (imported is not null)
            _ = imported.DisposeAsync();

        // 信号量（Semaphores 模型）与源同生命周期，随源一并释放导入实例。
        var waitSem = _waitSem;
        _waitSem = null;
        if (waitSem is not null)
            _ = waitSem.DisposeAsync();
        var signalSem = _signalSem;
        _signalSem = null;
        if (signalSem is not null)
            _ = signalSem.DisposeAsync();

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

    /// <summary>订阅 VideoView.Stretch 变化的简单 IObserver 实现（避免引入 System.Reactive）。</summary>
    private sealed class StretchObserver : IObserver<Stretch>
    {
        private readonly CompositionVideoRenderer _owner;
        public StretchObserver(CompositionVideoRenderer owner) => _owner = owner;
        public void OnNext(Stretch value) => _owner.OnStretchChanged();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>
    /// 中立 <see cref="SharedGpuHandleKind"/> → Avalonia <see cref="KnownPlatformGraphicsExternalImageHandleTypes"/> 的一次映射。
    /// </summary>
    // Gate0（Android）：Avalonia 的 KnownPlatformGraphicsExternalImageHandleTypes 无 AndroidHardwareBuffer 成员，
    // 故 Android AHB 句柄类型字符串需运行时确认。下方候选循环会在合成器实际支持列表中自动认领
    // 含 "HardwareBuffer"/"android" 的入口（不靠硬编码猜测）；Attach 处的诊断日志会打印真实列表供核对。
    private const string AndroidHardwareBufferHandleType = "androidHardwareBuffer";

    private static string? MapHandleKind(SharedGpuHandleKind kind) => kind switch
    {
        SharedGpuHandleKind.D3D11TextureGlobalSharedHandle => KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle,
        SharedGpuHandleKind.D3D11TextureNtHandle => KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle,
        SharedGpuHandleKind.VulkanOpaqueNtHandle => KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle,
        SharedGpuHandleKind.VulkanOpaquePosixFileDescriptor => KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor,
        SharedGpuHandleKind.IOSurfaceRef => KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef,
        SharedGpuHandleKind.AndroidHardwareBuffer => AndroidHardwareBufferHandleType,
        _ => null,
    };

    /// <summary>
    /// 中立 <see cref="SharedGpuSemaphoreKind"/> → Avalonia
    /// <see cref="KnownPlatformGraphicsExternalSemaphoreHandleTypes"/> 的一次映射。
    /// </summary>
    private static string MapSemaphoreKind(SharedGpuSemaphoreKind kind) => kind switch
    {
        SharedGpuSemaphoreKind.VulkanOpaqueNtHandle => KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle,
        SharedGpuSemaphoreKind.VulkanOpaquePosixFileDescriptor => KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor,
        SharedGpuSemaphoreKind.MetalSharedEvent => KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent,
        _ => throw new NotSupportedException($"不支持的共享 GPU 信号量句柄类型：{kind}。"),
    };
}
