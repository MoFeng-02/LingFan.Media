using System.Diagnostics.CodeAnalysis;
using LingFan.Media.GPUShare.Android.Egl;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LingFan.Media.Avalonia;

/// <summary>
/// OpenGL / GL ES 纹理的 Skia 直采适配器：把 <see cref="SharedGpuHandleKind.GlTexture"/>
/// 描述符承载的 <c>EGLImageKHR</c> 导入为当前（宿主渲染）上下文的 GL 纹理，包装为
/// <see cref="GRGlTextureInfo"/> + <c>SKImage.FromTexture</c>。
/// </summary>
/// <remarks>
/// <para><b>导入模型</b>：描述符 NativeImage 承载 <c>EGLImageKHR</c>（display 级共享对象）——
/// 本适配器在宿主渲染上下文（lease 作用域）内 <c>glGenTextures</c> +
/// <c>glEGLImageTargetTexture2DOES(GL_TEXTURE_2D)</c> 导入为普通 2D 纹理（非 external OES），
/// 可被 Skia 直采。</para>
/// <para>包装生命周期：每次绘制现场创建、绘制后立即释放——Ganesh 对已记录命令持有的代理引用
/// 会保活纹理直至帧冲刷；纹理名按 GL 延迟删除语义释放（已记录命令不受影响），不跨帧缓存，
/// 规避渲染线程亲和与上下文重建悬挂。</para>
/// <para><b>AOT 兼容</b>：sealed 无状态类，public ctor（DI 直接激活），零反射。</para>
/// </remarks>
public sealed class SkiaGlTexturePresenter : IHostSurfacePresenter
{
    // GL_TEXTURE_2D / GL_RGBA8 常量（GLES 3.0 核心，与 Skia 的 GL 采样约定一致）。
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlRgba8 = 0x8058;

    private readonly ILogger<SkiaGlTexturePresenter> _logger;
    private bool _displayProbeLogged;

    /// <summary>初始化适配器。</summary>
    public SkiaGlTexturePresenter(ILogger<SkiaGlTexturePresenter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool CanPresent(in SharedGpuSurfaceDescriptor descriptor)
        => descriptor.Kind == SharedGpuHandleKind.GlTexture;

    /// <inheritdoc/>
    public bool TryWrap(
        GRContext grContext,
        in SharedGpuSurfaceDescriptor descriptor,
        [NotNullWhen(true)] out SkiaWrappedSurface? surface,
        out string? failureReason)
    {
        surface = null;
        failureReason = null;

        // GlTexture 语义：描述符 NativeImage 承载 EGLImageKHR 句柄（display 级共享）。
        // 在当前（宿主渲染）上下文内创建纹理并导入 EGLImage——普通 GL_TEXTURE_2D，可被 Skia 直采；
        // 绘制后释放：先图像/后端纹理包装，纹理对象名经 GL 延迟删除语义保活已记录命令。
        // display 对表探针（一次性）：与生产者日志的「生产者 EGLDisplay」比对——不同值即跨 display 导入失败根因。
        if (!_displayProbeLogged)
        {
            _displayProbeLogged = true;
            // ILogger 通道：渲染线程的 Console 输出在 LogCatTextWriter 下可能丢失，诊断必须走 ILogger。
            _logger.LogInformation(
                "[GL-SHARED] 消费者当前 EGLDisplay=0x{Display:X}（宿主渲染上下文，与生产者 display 对表）",
                EglImageInterop.QueryCurrentDisplay());
        }

        uint texture = EglImageInterop.CreateTextureFromEglImage(descriptor.NativeImage);
        if (texture == 0)
        {
            failureReason = "GL 纹理创建失败（glGenTextures 返回 0）";
            return false;
        }

        var glInfo = new GRGlTextureInfo
        {
            Id = texture,
            Target = GlTexture2D,
            Format = GlRgba8,
        };

        // TopLeft：本链路图像行 0 = 画面顶部（GPU 四边形渲染 + 行对齐拷贝），Skia 画布同 y 向下；
        // 视频帧无透明通道 → Opaque。
        var backendTexture = new GRBackendTexture(descriptor.Width, descriptor.Height, false, glInfo);
        var image = SKImage.FromTexture(
            grContext, backendTexture, GRSurfaceOrigin.TopLeft,
            SKColorType.Rgba8888, SKAlphaType.Opaque);
        if (image is null)
        {
            backendTexture.Dispose();
            EglImageInterop.DeleteTexture(texture);
            failureReason = "SKImage.FromTexture 返回 null（GL 纹理包装失败）";
            return false;
        }

        surface = new SkiaWrappedSurface(image, backendTexture, () => EglImageInterop.DeleteTexture(texture));
        return true;
    }
}
