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
/// <para><b>M1 已知限制</b>：纹理在离屏上下文中创建，与宿主采样上下文<b>尚未建立共享组</b>
/// （待 M2 经 EGLImage 导入或共享上下文建立）。此阶段消费方包装采样的正确性不成立——
/// 本阶段判据为链路日志行为（包装成功/失败形态、回退链行为），不判画面正确性。</para>
/// <para><b>帧所有权</b>：纹理与上下文生命周期归本源所有，消费方只借用纹理名，绝不释放。</para>
/// <para><b>异步策略</b>：同步（纯 GPU 命令提交与状态更新，无 I/O，补 async 即伪异步）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，裸 P/Invoke（GLNative），无反射。</para>
/// </remarks>
internal sealed unsafe class OpenGLSharedSurfaceSource : ISharedGpuSurfaceSource
{
    // GL 常量（GLES 3.0 核心）：GL_TEXTURE_2D / GL_COLOR_BUFFER_BIT / GL_RGBA / GL_UNSIGNED_BYTE /
    // GL_RGBA8 / GL_LINEAR / GL_TEXTURE_MAG_FILTER / GL_TEXTURE_MIN_FILTER。
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlColorBufferBit = 0x4000;
    private const uint GlRgba = 0x1908;
    private const uint GlUnsignedByte = 0x1401;
    private const int GlRgba8 = 0x8058;
    private const int GlLinear = 0x2601;
    private const uint GlTextureMagFilter = 0x2801;
    private const uint GlTextureMinFilter = 0x2800;

    private readonly EglContext _context;
    private readonly ILogger<OpenGLSharedSurfaceSource> _logger;
    private readonly int _width;
    private readonly int _height;

    private uint _texture;
    private ulong _version;
    private long _writtenFrames;
    private bool _textureReady;
    private bool _announcedZeroCopy;
    private bool _disposed;

    // EGLImage 缓存（键 = AHB 句柄）：BufferQueue 复用同句柄时复用同一 EGLImage 视图。
    private readonly Dictionary<IntPtr, nint> _eglImages = new();

    /// <summary>初始化共享表面源：创建离屏 EGL 上下文（GL ES 3.0）。</summary>
    public OpenGLSharedSurfaceSource(int width, int height, ILogger<OpenGLSharedSurfaceSource> logger)
    {
        _width = width;
        _height = height;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _context = EglContext.CreateOffscreen(logger);
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
        if (frame.Resource is AndroidHardwareBufferFrameResource ahb)
        {
            if (!_announcedZeroCopy)
            {
                _announcedZeroCopy = true;
                _logger.LogInformation("[GL-SHARED] 路径锁定=ZERO-COPY(AHB→EGLImage→GL 采样)——GPU 出帧，无 CPU 像素拷贝。生产者 EGLDisplay=0x{Display:X}。", _context.PlatformDisplay);
            }
            return TryDeliverAhb(ahb, frame.RotationDegrees, out descriptor);
        }

        _context.MakeCurrent();
        try
        {
            if (!_textureReady)
                CreateTexture();

            // M1 测试内容：按帧序号变化的纯色（灰度随帧递增，肉眼可判持续出帧与重排）。
            var level = (float)((_writtenFrames % 120) / 120.0);
            GLNative.glClearColor(level, 0.25f, 1.0f - level, 1.0f);
            GLNative.glClear(GlColorBufferBit);
            GLNative.glFinish();

            _version++;
            _writtenFrames++;
            descriptor = new SharedGpuSurfaceDescriptor(
                Handle: (IntPtr)_texture,
                Kind: SharedGpuHandleKind.GlTexture,
                Width: _width,
                Height: _height,
                Format: SharedGpuSurfaceFormat.R8G8B8A8UNorm,
                Version: _version,
                SyncMode: SharedGpuSyncMode.None,
                NativeImage: (IntPtr)_texture,
                NativeDeviceMemory: IntPtr.Zero,
                NativeImageLayout: 0,
                NativeVkFormat: (uint)GlRgba8, // GlTexture 语义下此字段承载 GL 内部格式
                NativeQueueFamilyIndex: 0,
                NativeImageUsage: 0,
                NativeImageTiling: 0);
            return true;
        }
        finally
        {
            _context.ReleaseCurrent();
        }
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

    private void CreateTexture()
    {
        uint texture = 0;
        GLNative.glGenTextures(1, &texture);
        GLNative.glBindTexture(GlTexture2D, texture);
        GLNative.glTexImage2D(GlTexture2D, 0, GlRgba8, _width, _height, 0, GlRgba, GlUnsignedByte, null);
        GLNative.glTexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
        GLNative.glTexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
        _texture = texture;
        _textureReady = true;
        _logger.LogInformation("[GL-SHARED] 测试纹理就绪 {W}x{H} tex={Tex}（M1：跨上下文共享待 M2 建立）", _width, _height, _texture);
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
            if (_texture != 0)
            {
                uint texture = _texture;
                GLNative.glDeleteTextures(1, &texture);
                _texture = 0;
            }
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
