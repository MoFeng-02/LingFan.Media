using LingFan.Media.Apple.Shared;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// Metal 上屏上下文（MTLDevice + MTLCommandQueue + CAMetalLayer）。
/// </summary>
/// <remarks>
/// <para>职责类比 <see cref="LingFan.Media.Renderers.OpenGLES.Context.AndroidEglContext"/>（Android EGL/GLES 上下文）：
/// 持有 GPU 设备与命令队列，并管理 CoreAnimation 图层（CAMetalLayer）的生命周期。</para>
/// <para>创建流程：优先复用宿主 CAMetalLayer 既有 device（避免硬绑定系统默认 GPU 后端），无则 <c>MTLCreateSystemDefaultDevice()</c> 回退
/// → 配置 CAMetalLayer（pixelFormat=BGRA8Unorm / opaque / drawableSize）→ <c>newCommandQueue</c>。</para>
/// <para>每帧上屏：<see cref="NextDrawable"/> 取得当前可绘制层与其纹理（作为渲染目标），由
/// <see cref="MetalShaderPipeline"/> 完成渲染后提交 <c>presentDrawable:</c> + <c>commit</c>。</para>
/// <para><b>无头域渲染</b>：macOS/iOS 上屏走 CAMetalLayer + CoreAnimation（来自总记忆：Apple 无空域=CAMetalLayer），
/// 本上下文不提供离屏设备上下文（GPU 纹理零拷贝属 C 线未来增强，故不注册 <see cref="LingFan.Media.Abstractions.IGpuDeviceContext"/>）。</para>
    /// <para><b>所有权</b>：device 来源有二——复用宿主图层既有 device（本层另 <see cref="AppleRuntime.objc_retain"/> 取 +1）或回退 <c>MTLCreateSystemDefaultDevice</c>（本层所有 +1）；
    /// queue(来自 newCommandQueue) 均按 Cocoa 规则返回 +1（本层所有）。二者均在 <see cref="Dispose"/> 中 <see cref="AppleRuntime.objc_release"/> 一次即平衡（无需额外 retain）；
    /// layer 为宿主借入对象，经 <see cref="AppleRuntime.objc_retain"/> 取得本层 +1，<see cref="Dispose"/> 释放。CAMetalLayer 同时被宿主视图（NSView/UIView）强引用，释放仅解除本层引用。</para>
/// <para><b>跨平台</b>：仅在 <see cref="OperatingSystem.IsMacOS"/> / <see cref="OperatingSystem.IsIOS"/> 下被构造；
/// 构造前由 <see cref="MetalRenderer.Attach"/> 守卫，非 Apple 平台永不被实例化。</para>
/// </remarks>
internal sealed class MetalContext : IDisposable
{
    private nint _device;
    private nint _queue;
    private nint _layer;
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>初始化 Metal 上屏上下文。</summary>
    /// <param name="layer">宿主提供的 CAMetalLayer*（已交由本层强引用）。</param>
    /// <param name="width">渲染目标宽度（像素），用于设置 drawableSize。</param>
    /// <param name="height">渲染目标高度（像素），用于设置 drawableSize。</param>
    /// <param name="logger">日志器（可为 null）。</param>
    internal MetalContext(nint layer, int width, int height, ILogger? logger = null)
    {
        if (layer == nint.Zero)
            throw new ArgumentNullException(nameof(layer));
        _logger = logger;
        _layer = layer;
        AppleRuntime.objc_retain(_layer);

        // —— 设备来源策略：避免硬绑定系统默认 GPU 后端 ——
        // 优先复用宿主 CAMetalLayer 已配置的 device：宿主（SwiftUI/UIKit 或上层共享 / 多 GPU / eGPU 上下文）
        // 可能已为图层设定 device；渲染器不得强制覆盖为系统默认设备，否则在跨设备场景（drawable 纹理属于图层既有
        // device，而本层命令队列却建在系统默认 device 上）会直接崩溃。仅当图层未配置 device（例如本层自建的
        // macOS NSView 路径）时，才回退 MTLCreateSystemDefaultDevice() 并 setDevice:。
        // 此策略与未来零拷贝 C 线兼容：解码器经 IGpuDeviceContext 共享同一设备时，本层直接复用，无需自建。
        nint hostDevice = AppleRuntime.objc_msgSend(_layer, AppleRuntime.Sel("device"));
        if (hostDevice != nint.Zero)
        {
            // 图层已持有该 device（+1）；本层另取自有 +1，Dispose 释放一次即平衡，且不覆盖宿主选择。
            _device = hostDevice;
            AppleRuntime.objc_retain(_device);
        }
        else
        {
            _device = AppleRuntime.MTLCreateSystemDefaultDevice();
            if (_device == nint.Zero)
                throw new InvalidOperationException("Metal：MTLCreateSystemDefaultDevice 返回 null（无可用 Metal 设备，可能运行于虚拟机或非 Apple 硬件）。");
            // MTLCreateSystemDefaultDevice 已返回 +1（本层所有）。图层尚未配置 device，由本层设定。
            AppleRuntime.objc_msgSend(_layer, AppleRuntime.Sel("setDevice:"), _device);
        }

        // 配置 CAMetalLayer：渲染目标像素格式固定 BGRA8Unorm（与着色器输出一致）。device 已就绪（复用宿主或回退默认）。
        AppleRuntime.objc_msgSend(_layer, AppleRuntime.Sel("setPixelFormat:"), MetalConstants.BGRA8Unorm);
        AppleRuntime.objc_msgSend(_layer, AppleRuntime.Sel("setOpaque:"), (byte)1);
        SetDrawableSize(width, height);

        _queue = AppleRuntime.objc_msgSend(_device, AppleRuntime.Sel("newCommandQueue"));
        if (_queue == nint.Zero)
            throw new InvalidOperationException("Metal：newCommandQueue 返回 null。");
        // newCommandQueue 已返回 +1（本层所有），Dispose 释放一次即平衡，无需额外 retain。

        _logger?.LogInformation(
            "[METAL-CONTEXT] 设备={Dev} 队列={Q} 图层={Layer} 尺寸={W}x{H}",
            _device, _queue, _layer, width, height);
    }

    /// <summary>GPU 设备（MTLDevice*）。</summary>
    internal nint Device => _device;

    /// <summary>命令队列（MTLCommandQueue*）。</summary>
    internal nint Queue => _queue;

    /// <summary>CoreAnimation 图层（CAMetalLayer*）。</summary>
    internal nint Layer => _layer;

    /// <summary>设置 CAMetalLayer 的 drawableSize（渲染分辨率，像素）。CGSize 按两个 double 值传参（ABI 安全）。</summary>
    internal void SetDrawableSize(int width, int height)
    {
        AppleRuntime.objc_msgSend(_layer, AppleRuntime.Sel("setDrawableSize:"), (double)width, (double)height);
    }

    /// <summary>
    /// 取得当前可绘制层及其纹理（作为本帧渲染目标）。
    /// </summary>
    /// <returns>可绘制层、其纹理、纹理像素宽高。</returns>
    /// <exception cref="InvalidOperationException">图层不可见或设备丢失导致 nextDrawable 返回 null。</exception>
    internal (nint drawable, nint texture, int width, int height) NextDrawable()
    {
        nint drawable = AppleRuntime.objc_msgSend(_layer, AppleRuntime.Sel("nextDrawable"));
        if (drawable == nint.Zero)
            throw new InvalidOperationException("Metal：nextDrawable 返回 null（图层不可见、被移出窗口或设备丢失）。");

        nint texture = AppleRuntime.objc_msgSend(drawable, AppleRuntime.Sel("texture"));
        if (texture == nint.Zero)
            throw new InvalidOperationException("Metal：可绘制层纹理为 null（图层尺寸为 0，请确认 drawableSize 已设置）。");

        int w = (int)AppleRuntime.objc_msgSend(texture, AppleRuntime.Sel("width"));
        int h = (int)AppleRuntime.objc_msgSend(texture, AppleRuntime.Sel("height"));
        return (drawable, texture, w, h);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_queue != nint.Zero)
        {
            AppleRuntime.objc_release(_queue);
            _queue = nint.Zero;
        }
        if (_device != nint.Zero)
        {
            AppleRuntime.objc_release(_device);
            _device = nint.Zero;
        }
        if (_layer != nint.Zero)
        {
            AppleRuntime.objc_release(_layer);
            _layer = nint.Zero;
        }
        _logger?.LogDebug("Metal 上屏上下文已释放");
    }
}
