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
internal sealed class CompositionVideoRenderer : IVideoRenderer, IRendererHealth
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
    // 阈值取 10（30fps 下约 0.33s）：主时钟是音频驱动，回退越晚音画起点错位越大——真机曾因
    // 导入失败连吃 30 帧（约 1s）后画面才出现，被感知为「音频比画面先出约 1 秒」。
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

    /// <summary>
    /// 延迟解析 GPU 互操作并选定共享表面源。在 UI 线程、渲染循环已启动后由
    /// <see cref="Attach"/> 经 <see cref="Dispatcher"/> 调度执行；解析失败即触发
    /// <see cref="Unhealthy"/> 让宿主回退 Skia。
    /// </summary>
    private async Task ResolveAsync()
    {
        if (_disposed || _compositor is null || _attachedVisual is not Visual visual)
            return;

        // UI 线程 + await（非阻塞）：渲染循环已启动，server job 可被调度执行，无死锁；
        // continuation 经 ConfigureAwait(true) 回到 UI 线程，后续 ImportImage 等合成器操作满足 VerifyAccess。
        ICompositionGpuInterop? interop;
        try
        {
            interop = await _compositor.TryGetCompositionGpuInterop().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompositionVideoRenderer 解析 GPU 互操作失败，回退 Skia。");
            MarkUnhealthy();
            return;
        }

        if (interop is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 当前 Avalonia 渲染后端不支持 ICompositionGpuInterop，回退 Skia。");
            MarkUnhealthy();
            return;
        }

        // 诊断：打印合成器实际支持的句柄类型集合，用于确认零拷贝路径是否被接受
        // （legacy 全局共享句柄不支持跨 GPU 适配器；若合成器在其他适配器上则导入会静默黑屏）。
        _logger.LogInformation(
            "CompositionVideoRenderer 合成器支持句柄类型=[{HandleTypes}]。",
            string.Join(", ", interop.SupportedImageHandleTypes));

        // _interop 须在导入自检（选择循环内调用）前就绪。
        _interop = interop;

        // 把宿主合成器所在的物理 GPU 身份透传给工厂，供生产者优选同一设备
        // （跨设备共享纹理/信号量导入要求生产者与合成器位于同一物理 GPU，否则静默黑屏）。
        ReadOnlyMemory<byte> uuidMem = interop.DeviceUuid is { Length: 16 } ? interop.DeviceUuid : default(ReadOnlyMemory<byte>);
        ReadOnlyMemory<byte> luidMem = interop.DeviceLuid is { Length: 8 } ? interop.DeviceLuid : default(ReadOnlyMemory<byte>);
        SharedGpuAdapterIdentity? identity = (uuidMem.IsEmpty && luidMem.IsEmpty)
            ? null
            : new SharedGpuAdapterIdentity(uuidMem, luidMem);

        // —— 选定共享表面源：逐工厂 Create + 导入自检，失败即跳过试下一个（Vulkan→D3D11→…→软渲）——
        // 进程级记忆（SharedGpuSurfaceSourceSelector）：命中缓存胜出厂优先尝试，避免每次从注册序头部
        // 逐个探测；缓存项本次失败则失效，强制下次全扫描。OpenGL 等未实现后端自然不在候选内。
        string selectionKey = BuildSelectionKey(interop);
        _selector.TryGet(selectionKey, _surfaceFactories, out var cached);
        var candidates = new List<ISharedGpuSurfaceSourceFactory>(_surfaceFactories);
        if (cached is not null && candidates.Contains(cached))
        {
            candidates.Remove(cached);
            candidates.Insert(0, cached);
        }

        ISharedGpuSurfaceSourceFactory? selected = null;
        ISharedGpuSurfaceSource? source = null;
        string? handleType = null;
        bool cachedFailed = false;

        foreach (var f in candidates)
        {
            if (!f.IsAvailable)
                continue;
            string? ht = MapHandleKind(f.HandleKind);
            if (ht is null || !interop.SupportedImageHandleTypes.Contains(ht))
            {
                // Gate0 自修复：Android AHB 候选常量未命中时，从合成器实际支持的句柄类型中认领
                // 含 "HardwareBuffer"/"android" 的入口（运行时确认，不靠猜测）。认领成功即继续，
                // 失败则跳过本工厂（回退下一个 / Skia）。
                if (f.HandleKind == SharedGpuHandleKind.AndroidHardwareBuffer)
                    ht = interop.SupportedImageHandleTypes.FirstOrDefault(t =>
                        t.Contains("HardwareBuffer", StringComparison.OrdinalIgnoreCase) ||
                        t.Contains("android", StringComparison.OrdinalIgnoreCase));
                if (ht is null || !interop.SupportedImageHandleTypes.Contains(ht))
                {
                    // 静默跳过是零拷贝排查最大的盲区：合成器句柄类型为空集时（典型：Android 跑在
                    // EGL 后端 —— Avalonia 的 GL 后端不实现外部图像导入，SupportedImageHandleTypes 为 []），
                    // 每个工厂都会走到这里而不留任何痕迹，最终只看到一句笼统的「无可用工厂」。
                    // 必须逐工厂打点：句柄类型 + 映射结果 + 合成器实际支持列表，一眼看出是后端选错还是映射缺项。
                    _logger.LogWarning(
                        "CompositionVideoRenderer 跳过工厂 {Factory}：句柄类型 {HandleKind}（映射={Mapped}）" +
                        "不在合成器支持列表内。合成器后端支持=[{Supported}]。" +
                        "若列表为空，多半是 Avalonia 渲染后端选错（Android 需 AndroidRenderingMode.Vulkan）。",
                        f.GetType().Name, f.HandleKind, ht ?? "<无法映射>",
                        string.Join(", ", interop.SupportedImageHandleTypes));
                    continue;
                }
                _logger.LogInformation("CompositionVideoRenderer 已自动认领 Android AHB 句柄类型={HandleType}。", ht);
            }

            ISharedGpuSurfaceSource? candidate = null;
            try
            {
                candidate = f.Create(identity);
                // 临时落字段，供循环内 SelfTestImport 读取；成功后保留。
                _source = candidate;
                _handleType = ht;
                if (!SelfTestImport())
                {
                    _logger.LogWarning(
                        "CompositionVideoRenderer 共享表面源工厂 {Factory} 导入自检失败，尝试下一个候选。",
                        f.GetType().Name);
                    if (cached is not null && ReferenceEquals(f, cached))
                        cachedFailed = true;
                    continue;
                }

                selected = f;
                source = candidate;
                handleType = ht;
                _selector.Record(selectionKey, f);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "CompositionVideoRenderer 共享表面源工厂 {Factory} 创建失败，尝试下一个候选。",
                    f.GetType().Name);
                if (cached is not null && ReferenceEquals(f, cached))
                    cachedFailed = true;
                continue;
            }
            finally
            {
                // 失败分支：释放候选并清空临时字段，避免残留指向已弃用源。
                if (selected is null)
                {
                    candidate?.Dispose();
                    _source = null;
                    _handleType = null;
                }
            }
        }

        if (cachedFailed)
            _selector.Invalidate(selectionKey);

        if (selected is null || source is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 无可用且被合成器支持的共享表面源工厂，回退 Skia。");
            MarkUnhealthy();
            return;
        }

        _logger.LogInformation(
            "CompositionVideoRenderer 选定共享表面源工厂={Factory}，句柄类型={HandleType}（缓存命中={Cached}）。",
            selected.GetType().Name, handleType, cached is not null && ReferenceEquals(selected, cached));

        // _source / _handleType 在成功分支落定，供后续 Present 导入上屏。
        _source = source;
        _handleType = handleType;

        _logger.LogInformation(
            "CompositionVideoRenderer 导入自检通过，启用零拷贝上屏路径（句柄类型={HandleType}）。",
            _handleType);
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

    private void OnControlSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _controlSize = new Vector(e.NewSize.Width, e.NewSize.Height);
        UpdateSurfaceLayout();
    }

    private void OnStretchChanged()
    {
        if (_attachedVisual is Control control)
            _stretch = control.GetValue(VideoView.StretchProperty);
        UpdateSurfaceLayout();
    }

    /// <summary>
    /// 根据 <see cref="VideoView.Stretch"/> 模式计算 <see cref="CompositionSurfaceVisual"/> 的目标尺寸与偏移。
    /// CompositionSurfaceVisual 没有内置 Stretch，须手动实现 Fill/Uniform/UniformToFill/None。
    /// </summary>
    private void UpdateSurfaceLayout()
    {
        if (_surfaceVisual is null)
            return;

        if (_frameSize.X <= 0 || _frameSize.Y <= 0 || _controlSize.X <= 0 || _controlSize.Y <= 0)
        {
            // 尚未拿到帧尺寸：先按控件大小 Fill，避免 0 尺寸不可见。
            _surfaceVisual.Size = _controlSize;
            _surfaceVisual.Offset = new Vector3D(0, 0, 0);
            return;
        }

        double frameW = _frameSize.X;
        double frameH = _frameSize.Y;
        double ctrlW = _controlSize.X;
        double ctrlH = _controlSize.Y;
        double targetW = ctrlW;
        double targetH = ctrlH;

        switch (_stretch)
        {
            case Stretch.None:
                targetW = frameW;
                targetH = frameH;
                break;
            case Stretch.Uniform:
                double uniformScale = Math.Min(ctrlW / frameW, ctrlH / frameH);
                targetW = frameW * uniformScale;
                targetH = frameH * uniformScale;
                break;
            case Stretch.UniformToFill:
                double fillScale = Math.Max(ctrlW / frameW, ctrlH / frameH);
                targetW = frameW * fillScale;
                targetH = frameH * fillScale;
                break;
            case Stretch.Fill:
            default:
                // targetW/H 已等于控件尺寸
                break;
        }

        double offsetX = (ctrlW - targetW) / 2.0;
        double offsetY = (ctrlH - targetH) / 2.0;

        _surfaceVisual.Size = new Vector(targetW, targetH);
        _surfaceVisual.Offset = new Vector3D(offsetX, offsetY, 0);
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        if (_disposed || _source is null || _interop is null || _drawingSurface is null || _handleType is null)
        {
            PostNoteSkip();
            return;
        }

        // ① 管线线程：把解码帧 GPU 内容渲染进独立的共享 D3D11 纹理（keyed mutex 握手）。
        //    必须在 frame 仍存活时（本线程，Emit 的 ReturnFrame 之前）完成——共享纹理内容被固化拷贝，
        //    与解码帧纹理解耦，故后续封送到 UI 线程导入时 frame 是否已释放均安全。
        //    共享 D3D11 设备已开启多线程保护，跨线程调用安全。
        if (!_source.TryWriteFrame(frame, out var desc) || !desc.IsValid)
        {
            PostNoteSkip();
            return;
        }

        // ② 导入 + 上屏必须在 UI 线程（Compositor 拥有者）：Avalonia 的 ImportImage /
        //    UpdateWithKeyedMutexAsync / Visual 属性全部要求 UI 线程，否则抛 VerifyAccess 异常
        //    （之前在管线线程直接调用即暴露为「连续 30 帧无法呈现 → 回退 Skia」）。
        //    desc 为值类型，拷贝后跨线程安全传递到 UI 线程。
        var d = desc;
        Dispatcher.UIThread.Post(() => { _ = PresentUi(d); });
    }

    /// <summary>在 UI（Compositor 拥有者）线程执行共享纹理导入与上屏。</summary>
    private async Task PresentUi(SharedGpuSurfaceDescriptor desc)
    {
        // Detach 后可能有挂起的封送调用：字段已被 DetachCore 置空，直接退出避免触碰已释放对象。
        if (_disposed || _source is null || _interop is null || _compositor is null || _drawingSurface is null || _handleType is null)
        {
            PostNoteSkip();
            return;
        }

        try
        {
            // Android（SyncMode.None）路径：合成器 RenderInterface.Value 跨帧不稳定，而 Avalonia 的
            // CompositionImportedGpuImage.IsUsable 要求「导入时捕获的 Context」与「呈现时
            // Compositor.Server.RenderInterface.Value」为同一实例（Avalonia 12.1.1 源码证实：
            // Context 在 CompositionInterop 构造时一次性冻结）。跨帧缓存导入会因上下文重建而永久失配
            // （PlatformGraphicsContextLostException）。故每帧重新解析 interop（捕获当前 Value）+ 重新
            // 导入，并在呈现前 await ImportCompleted 确保服务端导入完成、Context 与 Value 一致。
            // 非 None 路径（Windows KeyedMutex / Linux Semaphores）上下文稳定，沿用缓存导入。
            ICompositionImportedGpuImage imported;
            if (_source.SyncMode == SharedGpuSyncMode.None)
            {
                var fresh = await ImportFreshAsync(desc).ConfigureAwait(true);
                if (fresh is null)
                {
                    PostNoteSkip();
                    return;
                }
                var old = _imported;
                _imported = fresh;
                old?.DisposeAsync();
                imported = fresh;
            }
            else
            {
                if (!await EnsureInteropAndImportAsync(desc).ConfigureAwait(true))
                    return;
                imported = _imported!;
            }

            // 信号量（仅 Semaphores 模型）：与源同生命周期，导入一次（非每帧），供 UpdateWithSemaphoresAsync 使用。
            if (_source.SyncMode == SharedGpuSyncMode.Semaphores && _waitSem is null)
            {
                var sem = _source.Semaphores;
                if (sem is not { IsValid: true })
                {
                    _logger.LogWarning("CompositionVideoRenderer Semaphores 模型但信号量无效，跳过本帧。");
                    PostNoteSkip();
                    return;
                }
                string semType = MapSemaphoreKind(sem.Value.Kind);
                _waitSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerWaitHandle, semType));
                _signalSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerSignalHandle, semType));
                if (_waitSem is null || _signalSem is null)
                {
                    _logger.LogWarning("CompositionVideoRenderer 信号量导入失败（句柄类型={SemType}），跳过本帧。", semType);
                    _waitSem?.DisposeAsync();
                    _signalSem?.DisposeAsync();
                    _waitSem = null;
                    _signalSem = null;
                    PostNoteSkip();
                    return;
                }
            }

            // 帧尺寸变化或首次呈现时，按 VideoView.Stretch 重新计算子视觉目标尺寸与偏移。
            // 必须在 UpdateWithKeyedMutexAsync 之前完成，确保合成器采样到正确布局。
            var newFrameSize = new Vector(desc.Width, desc.Height);
            if (_frameSize != newFrameSize)
            {
                _frameSize = newFrameSize;
                UpdateSurfaceLayout();
            }

            // Size=0 兜底：若子视觉尺寸仍为 0（Attach 时控件布局尚未完成、SizeChanged 尚未触发），
            // 按控件当前边界重设，避免「内容已提交但显示尺寸为 0 的空白」。
            if (_surfaceVisual is { } sv && (sv.Size.X == 0 || sv.Size.Y == 0) && _attachedVisual is { } v)
            {
                _controlSize = new Vector(v.Bounds.Width, v.Bounds.Height);
                UpdateSurfaceLayout();
            }

            _surfaceVisual!.Opacity = 1;

            // 按源的同步模型选择提交方式：各后端只用自己 API 的原生机制，互不跨界、不伪造句柄。
            Task present = _source.SyncMode == SharedGpuSyncMode.Semaphores && _waitSem is not null && _signalSem is not null
                ? _drawingSurface.UpdateWithSemaphoresAsync(imported, _waitSem, _signalSem)
                : _source.SyncMode == SharedGpuSyncMode.None
                    // 无显式同步（Android）：由 Avalonia 合成器平台层（UpdateAsync）自管跨端同步，
                    // 走 UpdateWithAutomaticSync 路径（sync=None）。
                    ? _drawingSurface.UpdateAsync(imported)
                    // 消费者（Avalonia 合成线程）以 ConsumerAcquireKey 取锁采样、以 ConsumerReleaseKey 归还。
                    : _drawingSurface.UpdateWithKeyedMutexAsync(
                        imported, (uint)_source.ConsumerAcquireKey, (uint)_source.ConsumerReleaseKey);

            // 治根（Android 真机教训）：上屏任务必须被健康监控观测。原实现把 _lastPresent 存入即弃、
            // 无条件 NoteSuccess()，导致「写入共享纹理成功但合成器从未采样 / 导入失效」被误判为成功，
            // 连续成功计数永不归零 → Unhealthy 永不触发 → 永不回退 Skia → 永久空白且无任何日志。
            // 现改为：任务成功完成才记成功；任务失败/取消记一次跳过，连续达阈值即触发回退。
            _lastPresent = present;
            _ = present.ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    // 数据先行：上屏任务真实异常此前被静默吞掉（仅计一次跳过 → 30 次触发回退），
                    // 导致「UpdateAsync 为何失败」完全不可见。此处把 AggregateException 展开记录，
                    // 真机 run 的 3.txt 即可看到 Android 合成器对导入 AHB/OPAQUE_FD 图像的真实拒绝原因。
                    if (t.Exception is { } ex)
                        _logger.LogError(ex, "CompositionVideoRenderer UpdateAsync 上屏失败（帧跳过，累计将触发回退）。HandleType={HandleType} Version={Version}",
                            _handleType, _lastVersion);
                    // Android 路径每帧重新解析 interop+重导（ImportFreshAsync），故此处无需置 _interopStale；
                    // 下一帧会自动以当前 RenderInterface.Value 重新导入。仅记一次跳过，连续达阈值即触发回退。
                    PostNoteSkip();
                }
                else
                    NoteSuccess();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // 单帧提交异常（如跨设备导入失败）吞掉，绝不击杀管线线程；下一帧重试。
            // 用 Warning 而非 Trace：跨设备纹理导入失败是「无空域上屏空白」的首要嫌疑，必须可见以便诊断。
            _logger.LogWarning(ex, "CompositionVideoRenderer 提交帧失败（跳过本帧）。");
            PostNoteSkip();
        }
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
    private static PlatformGraphicsExternalImageFormat ToExternalFormat(SharedGpuSurfaceFormat fmt) =>
        fmt == SharedGpuSurfaceFormat.R8G8B8A8UNorm
            ? PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm
            : PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm;

    private async Task<ICompositionImportedGpuImage?> ImportFreshAsync(SharedGpuSurfaceDescriptor desc)
    {
        var interop = await _compositor!.TryGetCompositionGpuInterop().ConfigureAwait(true);
        if (interop is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 获取 GPU 互操作失败（合成器未就绪），跳过本帧。");
            return null;
        }
        var handleType = ResolveHandleType(interop);
        if (handleType is null)
        {
            _logger.LogWarning("CompositionVideoRenderer 合成器不支持任何 Vulkan/AHardwareBuffer 句柄类型，跳过本帧。");
            return null;
        }
        _handleType = handleType;
        var props = new PlatformGraphicsExternalImageProperties
        {
            Width = desc.Width,
            Height = desc.Height,
            Format = ToExternalFormat(desc.Format),
            // 【必须透传】OPAQUE_FD 不携带内存元数据，Avalonia 的
            // VulkanExternalObjectsFeature.ImportedImage.CreateMemory 会拿这两个值与它自己
            // vkGetImageMemoryRequirements(导入图像) 的结果做严格相等校验，不符即抛
            // "Invalid memory size"（真机实证：漏传 → 每帧导入失败 → 整段播放不出画）。
            // MemorySize 必须是生产者侧 vkGetImageMemoryRequirements().size（含驱动 tile/对齐扩容），
            // 不是 w*h*4；MemoryOffset 恒为 0。
            MemorySize = desc.MemorySize,
            MemoryOffset = desc.MemoryOffset,
        };
        if (!_importPropsLogged)
        {
            _importPropsLogged = true;
            _logger.LogInformation(
                "CompositionVideoRenderer 首次导入参数: 句柄类型={HandleType} {W}x{H} 格式={Fmt} " +
                "MemorySize={MemSize} MemoryOffset={MemOff}（MemorySize=0 说明生产者未上报，导入必失败）",
                handleType, desc.Width, desc.Height, desc.Format, desc.MemorySize, desc.MemoryOffset);
        }
        var imported = interop.ImportImage(new PlatformHandle(desc.Handle, handleType), props);
        // 关键：await 服务端导入完成。若 Context 与当前 RenderInterface.Value 失配，导入阶段即抛
        // PlatformGraphicsContextLostException（ImportCompleted 变 Faulted），此处 await 直接抛出，
        // 由 PresentUi 的 try/catch 捕获并跳过本帧——比呈现时才发现失配更早、更明确。
        await imported.ImportCompleted.ConfigureAwait(true);
        return imported;
    }

    /// <summary>从合成器支持的句柄类型中选出本机可用者：优先 OpaqueFd（Vulkan 导出跨 GL/VK 通用），
    /// 其次 AHardwareBuffer / android 相关类型，最后兜底取第一个。</summary>
    private static string? ResolveHandleType(ICompositionGpuInterop interop)
    {
        var types = interop.SupportedImageHandleTypes;
        return types.FirstOrDefault(t => t.Contains("OpaqueFd", StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault(t => t.Contains("HardwareBuffer", StringComparison.OrdinalIgnoreCase)
                                         || t.Contains("android", StringComparison.OrdinalIgnoreCase))
            ?? types.FirstOrDefault();
    }

    /// <summary>
    /// 确保导入图像可用，处理两类重建场景：
    /// ① 共享纹理重建（生产者 <see cref="SharedGpuSurfaceDescriptor.Version"/> 变化）→ 丢弃旧导入，按新句柄/尺寸重导；
    /// ② 合成器 GPU 上下文重建（Android 初始化期 RenderInterface 被重建）→ 旧 interop 在创建时捕获的 Context 快照
    ///    与当前 <c>compositor.Server.RenderInterface.Value</c> 不再是同一实例，其导入图像
    ///    <c>IsUsable</c> 永久 false、<c>UpdateAsync</c> 抛 <see cref="PlatformGraphicsContextLostException"/>。
    ///    此时须重新 <c>TryGetCompositionGpuInterop()</c>（其构造函数会重新捕获当前 RenderInterface.Value 作为新 Context），
    ///    并丢弃旧导入重导。一次重拉即收敛（上下文已稳定）。
    /// 跨程序集无法直接读取 Avalonia 内部 <c>CompositionImportedGpuImage.IsUsable</c>，故由 <see cref="_interopStale"/>
    /// 标记驱动重拉（上屏续体在捕获 context-lost 异常时置位）。
    /// </summary>
    /// <returns>导入图像就绪可用返回 <see langword="true"/>；不可用（应跳过本帧或回退）返回 <see langword="false"/>。</returns>
    private async Task<bool> EnsureInteropAndImportAsync(SharedGpuSurfaceDescriptor desc)
    {
        // ② 合成器上下文重建：重拉 interop 以刷新 Context 快照，并强制下方重导旧导入。
        if (_interopStale || _interop is null)
        {
            _interopStale = false;
            var interop = await _compositor!.TryGetCompositionGpuInterop().ConfigureAwait(true);
            if (interop is null)
            {
                _logger.LogWarning("CompositionVideoRenderer 重新解析 GPU 互操作失败，回退 Skia。");
                MarkUnhealthy();
                return false;
            }
            _interop = interop;

            // 句柄类型在新 interop 下仍应受支持（设备/驱动不变）；逐个认领逻辑同 Setup 选择循环。
            if (_handleType is null || !_interop.SupportedImageHandleTypes.Contains(_handleType))
            {
                _handleType = _interop.SupportedImageHandleTypes.FirstOrDefault(t =>
                    t.Contains("HardwareBuffer", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("android", StringComparison.OrdinalIgnoreCase));
            }
            if (_handleType is null || !_interop.SupportedImageHandleTypes.Contains(_handleType))
            {
                _logger.LogWarning("CompositionVideoRenderer 重解析后句柄类型不被合成器支持，回退 Skia。");
                MarkUnhealthy();
                return false;
            }

            // 旧导入基于失效 Context，必须丢弃，强制重导。
            _imported?.DisposeAsync();
            _imported = null;
            _lastVersion = 0;
            _waitSem?.DisposeAsync();
            _waitSem = null;
            _signalSem?.DisposeAsync();
            _signalSem = null;
        }

        // ① 共享纹理重建或强制重导：按当前句柄/尺寸导入。
        if (_imported is null || _lastVersion != desc.Version)
        {
            _imported?.DisposeAsync();
            var props = new PlatformGraphicsExternalImageProperties
            {
                Width = desc.Width,
                Height = desc.Height,
                Format = ToExternalFormat(desc.Format),
            };
            _imported = _interop.ImportImage(new PlatformHandle(desc.Handle, _handleType!), props);
            if (_imported is null)
            {
                _logger.LogWarning(
                    "CompositionVideoRenderer 导入共享纹理失败（句柄类型={HandleType}），跳过本帧。",
                    _handleType);
                return false;
            }
            _lastVersion = desc.Version;
        }
        return true;
    }

    /// <summary>
    /// 挂载期导入能力自检：用 1×1 软件探针帧走完整「写入共享纹理 → 合成器导入」路径，
    /// 验证本共享 D3D11 设备纹理能被 Avalonia 合成器跨设备导入。
    /// </summary>
    /// <returns>自检通过返回 <see langword="true"/>；任一环节失败返回 <see langword="false"/>。</returns>
    private bool SelfTestImport()
    {
        // 探针资源：VideoFrame / SoftwareFrameResource 仅实现 IDisposableFrame（非 System.IDisposable），
        // 故手动 new + finally 释放；导入图像为 IAsyncDisposable，与文件既有 _imported?.DisposeAsync() 一致。
        SoftwareFrameResource? probe = null;
        VideoFrame? frame = null;
        ICompositionImportedGpuImage? imported = null;
        ICompositionImportedGpuSemaphore? waitSem = null;
        ICompositionImportedGpuSemaphore? signalSem = null;
        try
        {
            // 1×1 BGRA 探针（4 字节）——仅验证导入可达性，尺寸任意。
            // 完全限定 PixelFormat 以消除与 Avalonia.Platform.PixelFormat 的歧义（CS0104/CS1503）。
            probe = new SoftwareFrameResource(1, 1, LingFan.Media.Abstractions.PixelFormat.BGRA32, new Memory<byte>(new byte[4]));
            frame = new VideoFrame(1, 1, LingFan.Media.Abstractions.PixelFormat.BGRA32, probe, TimeSpan.Zero, TimeSpan.Zero, true);
            if (!_source!.TryWriteFrame(frame, out var desc) || !desc.IsValid)
                return false;

            var props = new PlatformGraphicsExternalImageProperties
            {
                Width = desc.Width,
                Height = desc.Height,
                Format = ToExternalFormat(desc.Format),
            };
            imported = _interop!.ImportImage(new PlatformHandle(desc.Handle, _handleType!), props);
            if (imported is null)
                return false;

            // Semaphores 模型：同步导入信号量，验证完整「写入 → 信号量 → 合成器导入」路径。
            if (_source.SyncMode == SharedGpuSyncMode.Semaphores)
            {
                var sem = _source.Semaphores;
                if (sem is not { IsValid: true })
                    return false;
                string semType = MapSemaphoreKind(sem.Value.Kind);
                waitSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerWaitHandle, semType));
                signalSem = _interop.ImportSemaphore(new PlatformHandle(sem.Value.ConsumerSignalHandle, semType));
                if (waitSem is null || signalSem is null)
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompositionVideoRenderer 导入自检异常（回退 Skia）。");
            return false;
        }
        finally
        {
            // 级联释放探针 CPU 资源（释放顺序：先 VideoFrame 再其 Resource）。
            frame?.Dispose();
            probe?.Dispose();
            // 探针导入图像/信号量为一次性探测，释放即可；失败回退链不依赖这些导入实例。
            if (imported is not null)
                _ = imported.DisposeAsync();
            if (waitSem is not null)
                _ = waitSem.DisposeAsync();
            if (signalSem is not null)
                _ = signalSem.DisposeAsync();
        }
    }

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
    /// 构建选择记忆键：合成器支持的句柄类型集合 + 合成器所在 GPU 身份（UUID/LUID）。
    /// 同一机器同一合成器恒定命中；跨 GPU / 远程桌面合成器切换则自然重新探测。
    /// </summary>
    private static string BuildSelectionKey(ICompositionGpuInterop interop)
    {
        var sb = new System.Text.StringBuilder();
        var handles = interop.SupportedImageHandleTypes;
        if (handles is not null)
            foreach (var h in handles)
                sb.Append(h).Append(';');
        if (interop.DeviceUuid is { } uuid && uuid.Length == 16)
            sb.Append("U:").Append(Convert.ToHexString(uuid));
        if (interop.DeviceLuid is { } luid && luid.Length == 8)
            sb.Append("L:").Append(Convert.ToHexString(luid));
        return sb.ToString();
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
