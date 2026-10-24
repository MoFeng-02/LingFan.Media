using System;
using System.Collections.Generic;
using LingFan.Media.GPUShare.Android;
using LingFan.Media.GPUShare.Android.Egl;
using LingFan.Media.Renderers.OpenGL.Context;

namespace LingFan.Media.Renderers.OpenGL.SharedSurface;

/// <summary>
/// OpenGL / GL ES 共享表面源（AHB → EGLImage 零拷贝直采）。
/// 解码侧产出的 <c>AHardwareBuffer</c> 经 EGLImage 导入为普通 GL_TEXTURE_2D 采样，
/// 以 <see cref="SharedGpuHandleKind.GlTexture"/> 交付渲染器宿主呈现适配器直采上屏。
/// </summary>
/// <remarks>
/// <para><b>同步模型自报</b>：<see cref="SharedGpuSyncMode.None"/>——纹理内容在交付前已完成
/// GL 写入，跨上下文采样可见性由共享机制保证（见下方已知限制）。</para>
/// <para><b>只接受 AHB 帧</b>：<see cref="SharedGpuHandleKind.GlTexture"/> 的描述符约定
/// <c>NativeImage</c> 承载 <c>EGLImageKHR</c>，消费方据此直接做
/// <c>glEGLImageTargetTexture2DOES</c>。CPU 帧无法产出 EGLImage，因此本源的
/// <see cref="TryWriteFrame"/> 对非 AHB 帧返回 <see langword="false"/>，交由渲染器的
/// 健康回退机制切换到其它源或 CPU 上屏——不可用普通纹理名冒充 EGLImage，
/// 消费方会把它当 display 级句柄解引用。</para>
/// <para><b>帧所有权</b>：上下文与共享对象生命周期归本源所有，消费方只借用 <c>EGLImageKHR</c>
/// （经描述符 <c>NativeImage</c> 交付，<b>不是</b> GL 纹理对象名），绝不释放。</para>
/// <para><b>异步策略</b>：同步（纯 GPU 命令提交与状态更新，无 I/O，补 async 即伪异步）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，裸 P/Invoke（GLNative），无反射。</para>
/// </remarks>
internal sealed unsafe class OpenGLSharedSurfaceSource : ISharedGpuSurfaceSource
{
    // GL 内部格式（GLES 3.0 核心 GL_RGBA8）：描述符 GlTexture 语义下承载纹理内部格式。
    private const int GlRgba8 = 0x8058;

    private readonly EglContext _context;
    private readonly ILogger<OpenGLSharedSurfaceSource> _logger;
    private readonly int _declaredWidth;
    private readonly int _declaredHeight;

    private ulong _version;
    private bool _announcedZeroCopy;
    private bool _disposed;

    // EGLImage 缓存（键 = AHB 句柄）：BufferQueue 复用同句柄时复用同一 EGLImage 视图。
    private readonly Dictionary<IntPtr, nint> _eglImages = new();

    /// <summary>初始化共享表面源：创建离屏 EGL 上下文（GL ES 3.0）。</summary>
    /// <param name="width">宿主申报的期望宽（仅用于诊断；实际交付尺寸取自每帧 AHB）。</param>
    /// <param name="height">宿主申报的期望高（仅用于诊断；实际交付尺寸取自每帧 AHB）。</param>
    /// <param name="logger">日志。</param>
    public OpenGLSharedSurfaceSource(int width, int height, ILogger<OpenGLSharedSurfaceSource> logger)
    {
        _declaredWidth = width;
        _declaredHeight = height;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = EglContext.CreateOffscreen(logger);
        _logger.LogDebug("[GL-SHARED] 离屏上下文就绪，宿主申报尺寸 {W}x{H}（交付尺寸以每帧 AHB 为准）。", _declaredWidth, _declaredHeight);
    }

    /// <inheritdoc/>
    public SharedGpuHandleKind HandleKind => SharedGpuHandleKind.GlTexture;

    /// <inheritdoc/>
    public SharedGpuSyncMode SyncMode => SharedGpuSyncMode.None;

    /// <inheritdoc/>
    public ulong ConsumerAcquireKey => 0;

    /// <inheritdoc/>
    public ulong ConsumerReleaseKey => 0;

    /// <inheritdoc/>
    public SharedGpuSemaphorePair? Semaphores => null;

    /// <inheritdoc/>
    public bool TryWriteFrame(VideoFrame frame, out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;
        if (_disposed)
            return false;
        if (frame.Resource is null)
            return false;

        // AHB 零拷贝路径：解码侧产出的 AHardwareBuffer 经 EGLImage 导入为普通 GL_TEXTURE_2D
        // 采样（EGLImage 为 display 级对象，与宿主 EGL 渲染上下文天然同 display，跨上下文共享成立）。
        if (frame.Resource is not AndroidHardwareBufferFrameResource ahb)
        {
            // CPU 帧没有 EGLImage 可交付。普通 GL 纹理名不是 display 级对象，
            // 消费方按契约当 EGLImageKHR 解引用会直接崩溃，因此如实返回失败，
            // 由渲染器的健康回退切换到其它源（或 CPU 上屏）。
            _logger.LogDebug("[GL-SHARED] 跳过非硬件缓冲帧（帧资源 {Kind}）：本源的 GlTexture 句柄语义要求 AHB。", frame.Resource.GetType().Name);
            return false;
        }

        if (!_announcedZeroCopy)
        {
            _announcedZeroCopy = true;
            _logger.LogInformation("[GL-SHARED] 路径锁定=ZERO-COPY(AHB→EGLImage→GL 采样)——GPU 出帧，无 CPU 像素拷贝。生产者 EGLDisplay=0x{Display:X}。", _context.PlatformDisplay);
        }
        return TryDeliverAhb(ahb, frame.RotationDegrees, out descriptor);
    }

    /// <summary>
    /// 把 AHB 帧经 EGLImage 导入并交付 GlTexture 描述符。EGLImage 按句柄缓存——
    /// AHB 由 BufferQueue 复用（同句柄内容更新）时复用同一 EGLImage 视图。
    /// </summary>
    private bool TryDeliverAhb(AndroidHardwareBufferFrameResource ahb, int rotationDegrees, out SharedGpuSurfaceDescriptor descriptor)
    {
        descriptor = default;
        try
        {
            if (!_eglImages.TryGetValue(ahb.AhbHandle, out var eglImage))
            {
                eglImage = EglImageInterop.CreateImageFromHardwareBuffer(_context.PlatformDisplay, ahb.AhbHandle);
                _eglImages[ahb.AhbHandle] = eglImage;
            }

            _version++;
            // 交付心跳（不依赖宿主渲染循环）：直接证明 AHB→EGLImage 真的成功、描述符真的产出。
            // 每 60 帧一条——「路径锁定」只证明收到 AHB 帧类型，本条才证明导入成功。
            if ((_version % 60) == 1)
                _logger.LogInformation(
                    "[GL-SHARED] 交付 GlTexture 描述符 v={Version} {W}x{H} ahb=0x{Ahb:X} eglImage=0x{Image:X}",
                    _version, ahb.Width, ahb.Height, ahb.AhbHandle, eglImage);
            descriptor = new SharedGpuSurfaceDescriptor(
                Handle: (IntPtr)ahb.AhbHandle,
                Kind: SharedGpuHandleKind.GlTexture,
                Width: ahb.Width,
                Height: ahb.Height,
                Format: SharedGpuSurfaceFormat.R8G8B8A8UNorm,
                Version: _version,
                SyncMode: SharedGpuSyncMode.None,
                // GlTexture 语义：NativeImage 承载 EGLImageKHR 句柄（消费方在宿主渲染上下文内
                // glGenTextures + glEGLImageTargetTexture2DOES 导入采样），NativeVkFormat 承载 GL 内部格式。
                NativeImage: eglImage,
                NativeDeviceMemory: IntPtr.Zero,
                NativeImageLayout: 0,
                NativeVkFormat: GlRgba8,
                NativeQueueFamilyIndex: 0,
                NativeImageUsage: 0,
                NativeImageTiling: 0,
                RotationDegrees: rotationDegrees);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GL-SHARED] AHB 经 EGLImage 导入失败，交回回退链。");
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _context.MakeCurrent();
            // EGLImage 逐个销毁（MakeCurrent 后 eglGetCurrentDisplay 有效；已导入纹理持引用，安全）。
            foreach (var eglImage in _eglImages.Values)
                EglImageInterop.DestroyImage(_context.PlatformDisplay, eglImage);
            _eglImages.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GL 共享表面源清理失败。");
        }
        finally
        {
            _context.ReleaseCurrent();
        }

        _context.Dispose();
    }
}
