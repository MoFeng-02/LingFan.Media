using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LingFan.Media.Avalonia;

/// <summary>
/// 同 device Skia GPU 直绘视频渲染器（Avalonia UI 层）。
/// </summary>
/// <remarks>
/// <para><b>定位</b>：Android 上「无空域 + 零拷贝 + 无 ByteBuffer」的 GPU 上屏路径——消费
/// <see cref="ISharedGpuSurfaceSource"/> 交付的 <see cref="SharedGpuHandleKind.VulkanNativeImage"/>
/// 描述符（原生 VkImage，与宿主 Skia 上下文共用同一 VkDevice/VkQueue），在渲染回调里经
/// <see cref="GRBackendTexture"/> + <c>SKImage.FromTexture</c> 直接采样绘制。</para>
/// <para><b>线程</b>：Attach/Detach 在 UI 线程；Present 在管线线程（只调
/// <see cref="ISharedGpuSurfaceSource.TryWriteFrame"/> 并存描述符快照）；Render 在 Avalonia 渲染线程
///（经 <see cref="IAvaloniaRenderAware"/>）。跨线程交接仅为一个 readonly record struct 快照（lock 保护）。</para>
/// <para><b>生命周期</b>：VkImage/VkDeviceMemory 归表面源所有，本渲染器只借用——每次 Render 现场
/// 创建包装（GRBackendTexture/SKImage）并在绘制后立即释放；Ganesh 对已记录命令持有的代理引用
/// 会保活纹理直至帧冲刷，属 Skia 标准用法。不跨帧缓存包装，规避渲染线程亲和与上下文重建悬挂。</para>
/// <para><b>回退</b>：持续失败（写入/绘制）达阈值触发 <see cref="IRendererHealth.Unhealthy"/>，
/// VideoView 拉黑本工厂并回退 CPU Skia 软渲染，保证总有路径出画。</para>
/// <para><b>异步策略</b>：全部同步（Present 纯 GPU 提交、Render 纯绘制，无 I/O 可 await）。</para>
/// <para><b>AOT 兼容</b>：sealed 类、无反射、全程静态调用（public Skia/Avalonia API）。</para>
/// </remarks>
internal sealed class SkiaGpuVideoRenderer : IVideoRenderer, IAvaloniaRenderAware, IRendererHealth
{
    // ≈ 1 个 60Hz 帧刷新周期；VideoPipeline 据其做音画对齐提前量。
    private static readonly TimeSpan PresentLatency = TimeSpan.FromMilliseconds(16);

    private readonly IEnumerable<ISharedGpuSurfaceSourceFactory> _surfaceFactories;
    private readonly ILogger _logger;

    private readonly object _gate = new();
    private SharedGpuSurfaceDescriptor _pending;
    private bool _hasPending;

    private Visual? _visual;
    private Control? _control;
    private IDisposable? _stretchSubscription;
    private Vector _controlSize;
    private Stretch _stretch = Stretch.Uniform;

    private ISharedGpuSurfaceSource? _source;
    private bool _disposed;

    // 运行期健康：连续失败达阈值触发 Unhealthy → VideoView 回退（与 CompositionVideoRenderer 同模式）。
    private int _consecutiveFailures;
    private bool _unhealthyFired;
    private const int FailureThreshold = 10;

    // 一次性诊断标志。
    private bool _firstFrameLogged;
    private bool _drawFailureLogged;

    /// <summary>渲染线程绘制几何诊断（DrawOp 调用，放大溢出排查用）。</summary>
    internal void LogDrawGeometry(string message)
        => _logger.LogInformation("[SKIA-GPU-DRAW] {Message}", message);

    // 渲染线程回调计数（冻结看门狗心跳）。
    private int _renderCallbacks;

    /// <summary>
    /// 初始化 <see cref="SkiaGpuVideoRenderer"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入）。</param>
    /// <param name="logger">日志。</param>
    internal SkiaGpuVideoRenderer(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories,
        ILogger<SkiaGpuVideoRenderer> logger)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
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

        if (target.NativeHandle is not Visual visual)
            throw new NotSupportedException("SkiaGpuVideoRenderer 需要 IRenderTarget.NativeHandle 为 Avalonia Visual。");

        _visual = visual;
        if (visual is Control control)
        {
            _control = control;
            _controlSize = new Vector(control.Bounds.Width, control.Bounds.Height);
            _stretch = control.GetValue(VideoView.StretchProperty);
            _stretchSubscription = control.GetObservable(VideoView.StretchProperty)
                .Subscribe(new StretchObserver(this));
        }

        // 选择 VulkanNativeImage 共享表面源（同 device 直采样）。无匹配（其他平台 / 未注入外部 device）
        // 时抛 NotSupportedException，VideoView 回退链继续（Composition / Skia）。
        foreach (var factory in _surfaceFactories)
        {
            if (factory.HandleKind != SharedGpuHandleKind.VulkanNativeImage || !factory.IsAvailable)
                continue;
            try
            {
                _source = factory.Create();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "VulkanNativeImage 共享表面源工厂（{Factory}）Create 失败，尝试下一个。",
                    factory.GetType().Name);
            }
        }
        if (_source is null)
            throw new NotSupportedException(
                "当前环境无 VulkanNativeImage 共享表面源（须与宿主共用同一 Vulkan device），Skia GPU 直绘不可用。");

        _logger.LogInformation("[SKIA-GPU] SkiaGpuVideoRenderer 挂载成功，源={Source}。",
            _source.GetType().Name);
    }

    /// <inheritdoc/>
    public void Detach()
    {
        UnsubscribeControl();
    }

    /// <inheritdoc/>
    public void Present(VideoFrame frame)
    {
        if (_disposed || _source is null)
        {
            RegisterFailure();
            return;
        }

        try
        {
            if (!_source.TryWriteFrame(frame, out SharedGpuSurfaceDescriptor desc) || !desc.IsValid)
            {
                RegisterFailure();
                return;
            }

            lock (_gate)
            {
                _pending = desc;
                _hasPending = true;
            }

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                _logger.LogInformation(
                    "[SKIA-GPU] 首帧已写入共享表面 {W}x{H} version={Version}，等待渲染线程直绘。",
                    desc.Width, desc.Height, desc.Version);
            }
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SkiaGpuVideoRenderer Present 失败。");
            RegisterFailure();
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_gate)
        {
            _hasPending = false;
            _pending = default;
        }
    }

    /// <summary>通知目标尺寸变化（VideoView 经 IAvaloniaRenderAware 调用，DIP 尺寸）。</summary>
    /// <remarks>GPU 直绘以 DIP 计算目标矩形（与 CPU Skia 路径同约定），scale 不参与几何。</remarks>
    public void Resize(int width, int height, float scale)
    {
        _controlSize = new Vector(width, height);
    }

    /// <summary>将缓存的表面快照绘制到 Avalonia DrawingContext（Avalonia 渲染线程调用）。</summary>
    public void Render(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_disposed)
            return;

        SharedGpuSurfaceDescriptor desc;
        double cw, ch;
        lock (_gate)
        {
            if (!_hasPending)
                return;
            desc = _pending;
            cw = _controlSize.X;
            ch = _controlSize.Y;
        }
        if (cw <= 0 || ch <= 0)
            return;

        // 冻结看门狗（渲染线程心跳）：每 60 次回调打一条，证明渲染线程仍活跃（冻结排查）。
        _renderCallbacks++;
        if ((_renderCallbacks % 60) == 1)
            _logger.LogInformation("[SKIA-GPU] 渲染回调 #{N}（渲染线程活跃）", _renderCallbacks);

        var op = new SkiaGpuVideoDrawOp(this, desc, cw, ch, _stretch)
        {
            Bounds = new Rect(0, 0, cw, ch),
        };
        drawingContext.Custom(op);
    }

    /// <summary>渲染线程回调：绘制失败计数（达到阈值触发 Unhealthy）。</summary>
    internal void OnDrawFailure(string reason)
    {
        if (!_drawFailureLogged)
        {
            _drawFailureLogged = true;
            _logger.LogWarning("[SKIA-GPU] 渲染线程直绘失败：{Reason}（持续失败将回退软渲染）。", reason);
        }
        RegisterFailure();
    }

    private void RegisterFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) < FailureThreshold)
            return;
        if (_unhealthyFired)
            return;
        _unhealthyFired = true;
        _logger.LogWarning(
            "[SKIA-GPU] 连续 {N} 帧无法写入/绘制，触发渲染器回退（软渲染）。", _consecutiveFailures);
        Unhealthy?.Invoke();
    }

    private void UnsubscribeControl()
    {
        _control = null;
        _stretchSubscription?.Dispose();
        _stretchSubscription = null;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>同步快速释放：共享表面源 Dispose 内含 DeviceWaitIdle，无 I/O 可 await，非伪异步。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        UnsubscribeControl();
        lock (_gate)
        {
            _hasPending = false;
            _pending = default;
        }
        _source?.Dispose();
        _source = null;
    }

    /// <summary>Stretch 变化观察者（UI 线程回调，仅更新快照）。</summary>
    private sealed class StretchObserver(SkiaGpuVideoRenderer owner) : IObserver<Stretch>
    {
        public void OnNext(Stretch value) => owner._stretch = value;
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}

/// <summary>
/// Skia GPU 直绘的帧绘制操作：把 <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 描述符包装为
/// Vulkan 后端纹理并绘制（Avalonia 渲染线程，经 <see cref="DrawingContext.Custom"/> 挂入合成树）。
/// </summary>
/// <remarks>
/// <para>包装生命周期：Render 内现场创建、绘制后立即释放——Ganesh 持有已记录命令的代理引用，
/// 释放 SKImage 不影响本帧已记录的采样；不跨帧缓存，规避上下文重建悬挂与线程亲和问题。</para>
/// <para>采样同步契约（生产者保证，见 <c>VulkanSharedSurfaceSource.CopyToSharedImage</c>）：
/// 交付布局 = ShaderReadOnlyOptimal、写入已对后续采样可见、生产/消费共用同一 VkQueue
///（同队列提交按序串行）。本侧不做任何布局转换或同步原语。</para>
/// </remarks>
internal sealed class SkiaGpuVideoDrawOp : ICustomDrawOperation
{
    private readonly SkiaGpuVideoRenderer _owner;
    private readonly SharedGpuSurfaceDescriptor _descriptor;
    private readonly double _controlW;
    private readonly double _controlH;
    private readonly Stretch _stretch;
    private static bool _drawGeomLogged; // 一次性几何对账打点标志（static：DrawOp 每帧新建实例，实例字段会每帧打点）
    private static uint _dbgFrameSeq; // 【定格对照实验】绘制帧序号（跨 DrawOp 实例单调）

    /// <summary>初始化帧绘制操作。</summary>
    /// <param name="owner">宿主渲染器（失败计数回调用）。</param>
    /// <param name="descriptor">共享表面快照（VulkanNativeImage）。</param>
    /// <param name="controlW">控件宽（DIP）。</param>
    /// <param name="controlH">控件高（DIP）。</param>
    /// <param name="stretch">拉伸模式。</param>
    public SkiaGpuVideoDrawOp(
        SkiaGpuVideoRenderer owner,
        in SharedGpuSurfaceDescriptor descriptor,
        double controlW,
        double controlH,
        Stretch stretch)
    {
        _owner = owner;
        _descriptor = descriptor;
        _controlW = controlW;
        _controlH = controlH;
        _stretch = stretch;
    }

    /// <summary>脏区边界（控件可视区）。</summary>
    public Rect Bounds { get; set; }

    /// <inheritdoc/>
    public void Render(ImmediateDrawingContext context)
    {
        // 【定格对照实验】绘制阶段心跳：DrawOp.Render 才是真正往画布画东西的回调（渲染线程）。
        // 冻结窗口内本心跳持续 ⇒ 绘制在跑但未上屏（提交/交换链停摆）；
        // 本心跳停而「渲染回调 #N」仍在 ⇒ 绘制线程被楔死（GPU 等待永不返回）。
        // logcat 的 tid 同时暴露绘制线程身份，与管线/UI 线程 tid 对比可判定同队列并发提交竞争。
        uint n = Interlocked.Increment(ref _dbgFrameSeq);
        if ((n % 60) == 1)
            _owner.LogDrawGeometry(
                $"[DRAW-OP] #{n} desc={_descriptor.Width}x{_descriptor.Height} layout={_descriptor.NativeImageLayout}");
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
        {
            _owner.OnDrawFailure("无 ISkiaSharpApiLeaseFeature（非 Skia 渲染后端）");
            return;
        }

        using ISkiaSharpApiLease lease = leaseFeature.Lease();
        GRContext? gr = lease.GrContext;
        SKCanvas? canvas = lease.SkCanvas;
        if (gr is null || canvas is null)
        {
            _owner.OnDrawFailure("Lease 缺 GrContext/SkCanvas");
            return;
        }

        GRBackendTexture? backendTexture = null;
        SKImage? image = null;
        try
        {
            // GRVkImageInfo 各字段 = 描述符 Native* 如实回填（生产者侧 vkCreateImage 的真实参数）。
            // Image/Memory 句柄按位转换为 ulong（Skia 的 Vulkan 句柄类型）。
            GRVkImageInfo vkInfo = new()
            {
                Image = unchecked((ulong)_descriptor.NativeImage),
                Alloc = new GRVkAlloc
                {
                    Memory = unchecked((ulong)_descriptor.NativeDeviceMemory),
                    Offset = _descriptor.MemoryOffset,
                    Size = _descriptor.MemorySize,
                },
                ImageTiling = _descriptor.NativeImageTiling,
                ImageLayout = _descriptor.NativeImageLayout,
                Format = _descriptor.NativeVkFormat,
                ImageUsageFlags = _descriptor.NativeImageUsage,
                SampleCount = 1,
                LevelCount = 1,
                CurrentQueueFamily = _descriptor.NativeQueueFamilyIndex,
                Protected = false,
                SharingMode = 0, // VK_SHARING_MODE_EXCLUSIVE（同队列族独占）
            };

            backendTexture = new GRBackendTexture(_descriptor.Width, _descriptor.Height, vkInfo);
            // TopLeft：本链路图像行 0 = 画面顶部（GPU 四边形渲染 + 行对齐拷贝），Skia 画布同 y 向下。
            // 颜色型别与 VkFormat R8G8B8A8_UNORM 对应；视频帧无透明通道 → Opaque。
            image = SKImage.FromTexture(
                gr, backendTexture, GRSurfaceOrigin.TopLeft,
                SKColorType.Rgba8888, SKAlphaType.Opaque);
            // 【包装遥测】每 60 帧报一次 wrap 结果（与 DRAW-OP 心跳同 n 对齐）。失败警告只打一次
            // 会被日志捕获窗口错过（direct3 教训），周期遥测让包装成败在任何窗口都可见。
            if ((n % 60) == 1)
                _owner.LogDrawGeometry(
                    $"[DRAW-OP] #{n} wrap={(image is null ? "FAIL" : "OK")} layout={_descriptor.NativeImageLayout} usage=0x{_descriptor.NativeImageUsage:X}");
            if (image is null)
            {
                _owner.OnDrawFailure("SKImage.FromTexture 返回 null（VkImage 包装失败）");
                return;
            }

            // 旋转呈现：容器声明的显示旋转（90/180/270）。源纹理像素本身不旋转，
            // 以 canvas 变换把已算好的 dest 矩形（按旋转后的显示宽高适配）转回源方向绘制。
            int rot = ((int)_descriptor.RotationDegrees % 360 + 360) % 360;
            SKRect dest = ComputeDestRect();
            // 【诊断】一次性对账坐标单位（放大溢出排查）：desc/控件(DIP)/dest/画布本地与设备裁剪盒。
            // LocalClipBounds=canvas 当前变换下的本地单位；DeviceClipBounds=物理像素。
            // 两者比值 = 画布有效缩放。dest 落在 Local 内 = 单位一致（DIP）；溢出 Device = 单位错位。
            if (!_drawGeomLogged)
            {
                _drawGeomLogged = true;
                _owner.LogDrawGeometry(
                    $"desc={_descriptor.Width}x{_descriptor.Height} control={_controlW:F0}x{_controlH:F0} " +
                    $"dest={dest} localClip={canvas.LocalClipBounds} deviceClip={canvas.DeviceClipBounds}");
            }
            if (rot == 90 || rot == 180 || rot == 270)
            {
                int save = canvas.Save();
                canvas.Translate(dest.MidX, dest.MidY);
                canvas.RotateDegrees(rot);
                // 旋转后源图占据的矩形：宽高随角度互换。
                float rw = (rot == 90 || rot == 270) ? dest.Height : dest.Width;
                float rh = (rot == 90 || rot == 270) ? dest.Width : dest.Height;
                canvas.DrawImage(image, new SKRect(-rw / 2, -rh / 2, rw / 2, rh / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                canvas.RestoreToCount(save);
            }
            else
            {
                // 线性过滤（视频缩放平滑）；无 Mipmap（单级纹理）。
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            }

            // 【定格对照实验】帧计数水印（Skia 直接绘制，与 VkImage 内容无关）：
            // 数字在屏幕上跳动而视频冻结 ⇒ 合成/提交正常、VkImage 采样陈旧；
            // 数字也定格 ⇒ 合成器/交换链停更。诊断期保留。
            // SkiaSharp 3.x：SKPaint.TextSize 与 4 参 DrawText 已废弃（TreatWarningsAsErrors 升为
            // 错误），改用 SKFont 现代重载。帧序号 n 已在 Render 顶部统一递增（供心跳复用）。
            using var dbg = new SKPaint { Color = SKColors.Red, IsAntialias = true };
            using var dbgFont = new SKFont(SKTypeface.Default, 48);
            canvas.DrawText($"F{n}", dest.Left + 16, dest.Top + 56, SKTextAlign.Left, dbgFont, dbg);
        }
        catch (Exception ex)
        {
            _owner.OnDrawFailure($"包装/绘制异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 释放包装（借用的 VkImage/VkDeviceMemory 不受影响——Ganesh 不拥有外来纹理；
            // 已记录的本帧采样命令由 Ganesh 代理引用保活）。
            image?.Dispose();
            backendTexture?.Dispose();
        }
    }

    /// <summary>按拉伸模式计算目标矩形（DIP），画面恒不越控件边界。
    /// 90/270° 旋转时以「旋转后的显示宽高」（互换）参与适配——dest 矩形是画面最终呈现区域。</summary>
    private SKRect ComputeDestRect()
    {
        double fw = _descriptor.Width;
        double fh = _descriptor.Height;
        int rot = ((int)_descriptor.RotationDegrees % 360 + 360) % 360;
        if (rot == 90 || rot == 270)
            (fw, fh) = (fh, fw);
        if (fw <= 0 || fh <= 0 || _controlW <= 0 || _controlH <= 0)
            return SKRect.Empty;

        double scaleX = _controlW / fw;
        double scaleY = _controlH / fh;
        double scale, dx, dy;
        switch (_stretch)
        {
            case Stretch.Fill:
                return new SKRect(0, 0, (float)_controlW, (float)_controlH);
            case Stretch.UniformToFill:
                scale = Math.Max(scaleX, scaleY);
                dx = (_controlW - fw * scale) / 2;
                dy = (_controlH - fh * scale) / 2;
                break;
            default: // Uniform（默认）
                scale = Math.Min(scaleX, scaleY);
                dx = (_controlW - fw * scale) / 2;
                dy = (_controlH - fh * scale) / 2;
                break;
        }

        double w = fw * scale;
        double h = fh * scale;
        // 裁剪保险：UniformToFill 可能越界，夹回控件内。
        double left = Math.Max(0, Math.Min(dx, _controlW));
        double top = Math.Max(0, Math.Min(dy, _controlH));
        double right = Math.Max(left, Math.Min(dx + w, _controlW));
        double bottom = Math.Max(top, Math.Min(dy + h, _controlH));
        return new SKRect((float)left, (float)top, (float)right, (float)bottom);
    }

    /// <inheritdoc/>
    public bool HitTest(Point p) => false;

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    bool IEquatable<ICustomDrawOperation>.Equals(ICustomDrawOperation? other) => false;
}

/// <summary>
/// <see cref="SkiaGpuVideoRenderer"/> 的工厂（同 device Skia GPU 直绘）。
/// </summary>
/// <remarks>
/// <para>类名含 <c>SkiaGpuVideoRenderer</c> 以支持 <c>VideoView.RendererType</c> 前置匹配。
/// 回退链位置：在 Composition 渲染器之后、CPU Skia 之前——Android 上 Composition 因
/// <see cref="SharedGpuHandleKind.VulkanNativeImage"/> 不被合成器支持而让位，本工厂接手直绘；
/// Windows/Linux/Apple 上无 VulkanNativeImage 源，Attach 抛 <see cref="NotSupportedException"/>
/// 自动跳过，既有 Composition 路径不受影响。</para>
/// <para><b>AOT 兼容</b>：sealed 类，构造函数自动解析，无反射。</para>
/// </remarks>
public sealed class SkiaGpuVideoRendererFactory : IVideoRendererFactory
{
    private readonly IEnumerable<ISharedGpuSurfaceSourceFactory> _surfaceFactories;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="SkiaGpuVideoRendererFactory"/> 的新实例。
    /// </summary>
    /// <param name="surfaceFactories">共享表面源工厂集合（DI 注入）。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public SkiaGpuVideoRendererFactory(
        IEnumerable<ISharedGpuSurfaceSourceFactory> surfaceFactories,
        ILoggerFactory loggerFactory)
    {
        _surfaceFactories = surfaceFactories ?? throw new ArgumentNullException(nameof(surfaceFactories));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IVideoRenderer Create()
        => new SkiaGpuVideoRenderer(_surfaceFactories, _loggerFactory.CreateLogger<SkiaGpuVideoRenderer>());
}
