using LingFan.Media.Abstractions;
using System.Buffers;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// 跨 API 零拷贝 GL 纹理帧资源（Windows：WGL_NV_DX_interop2 待导入的 D3D11 共享纹理）。
/// </summary>
/// <remarks>
/// <para>本类本身<b>不</b>持有已注册的 GL 纹理，而是把解码侧打开的每帧 D3D11 共享纹理引用 + 桥接 D3D11 设备
/// 交给 <see cref="OpenGLShaderPipeline.PresentGpuTexture"/>；管线在绘制时于当前 on-screen GL 上下文上执行
/// <c>wglDXOpenDeviceNV / wglDXRegisterObjectNV / wglDXLockObjectsNV</c>，绘制后立即 unregister/delete/close。
/// 这样避免 owner 离屏上下文与 on-screen 渲染上下文对同一 WGL 互操作对象的跨上下文歧义（共享组虽可共享纹理名，
/// 但 WGL 互操作对象由 NVIDIA 实现强关联注册上下文；在 on-screen 上下文上重注册是可靠做法）。</para>
/// <para><b>生命周期</b>：Dispose 仅释放本类拥有的每帧 D3D11 共享纹理引用；GL/WGL 资源由管线在单帧内用完即释。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed unsafe class GLD3D11InteropTexture : IFrameResource, IGpuTextureResource
{
    private readonly ID3D11Texture2D _d3dTexture;
    private readonly ID3D11Device _bridgeDevice;
    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly int _subresourceIndex;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }
    /// <inheritdoc/>
    public int Height { get; }
    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <summary>用于在 on-screen 上下文上打开 WGL 互操作设备的桥接 D3D11 设备。</summary>
    internal ID3D11Device BridgeDevice => _bridgeDevice;

    /// <summary>解码侧经 <c>OpenSharedResource1</c> 打开的每帧 D3D11 共享纹理。</summary>
    internal ID3D11Texture2D D3dTexture => _d3dTexture;

    /// <summary>初始化 <see cref="GLD3D11InteropTexture"/> 的新实例。</summary>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="d3dTexture">每帧 D3D11 共享纹理（本类拥有，Dispose 释放）。</param>
    /// <param name="bridgeDevice">桥接 D3D11 设备（用于管线在 on-screen 上下文打开 WGL 互操作设备）。</param>
    /// <param name="glContext">共享组所有者离屏上下文（Linux EGL / 未来回读路径所需）。</param>
    /// <param name="subresourceIndex">子资源索引（默认 0）。</param>
    public GLD3D11InteropTexture(
        int width, int height, PixelFormat format,
        ID3D11Texture2D d3dTexture, ID3D11Device bridgeDevice,
        OpenGLOffscreenDeviceContext glContext, int subresourceIndex = 0)
    {
        Width = width;
        Height = height;
        Format = format;
        _d3dTexture = d3dTexture;
        _bridgeDevice = bridgeDevice;
        _glContext = glContext;
        _subresourceIndex = subresourceIndex;
    }

    /// <summary>
    /// 返回 <see cref="IntPtr.Zero"/>：本帧没有可长期持有的 GL 纹理名。
    /// </summary>
    /// <remarks>
    /// 零句柄的含义是「本帧无长期句柄」，不是「不支持」——D3D11 互操作纹理的 GL 侧纹理由管线在
    /// 每次绘制时现场注册（WGL_NV_DX_interop 的 lock/unlock 与注册生命周期都绑在那一帧的绘制上），
    /// 因此没有可跨帧交出去的纹理名。消费方不得据此判不支持、也不得把它当作错误码；
    /// 需要像素时走 <see cref="ReadbackToCpu"/>。
    /// </remarks>
    IntPtr IGpuTextureResource.NativeTextureHandle => IntPtr.Zero;

    int IGpuTextureResource.SubresourceIndex => _subresourceIndex;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _d3dTexture.Dispose();
    }

    /// <inheritdoc/>
    public GpuTextureReadback ReadbackToCpu()
    {
        // 回读唯一实现收敛于 GPUShare.D3D11.D3D11TextureReadback（staging + Map + NV12→BGRA）。
        // 纹理 COM 引用归本类所有（构造 AddRef / Dispose Release），共享实现不改变引用计数。
        return LingFan.Media.GPUShare.D3D11.Readback.D3D11TextureReadback.Read(_d3dTexture.NativePointer, _subresourceIndex, Format);
    }
}
