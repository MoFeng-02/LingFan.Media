using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Direct3D;

namespace LingFan.Media.Platforms.Windows;

/// <summary>
/// D3D11 与其他 GPU API 互操作。
/// </summary>
/// <remarks>
/// <para>职责：管理 D3D11 资源跨 API 共享（D3D11 → Vulkan → OpenGL），
/// 实现硬解纹理到渲染器纹理的零拷贝传递。</para>
/// <para>GPU 零拷贝路径：DXVA2 / D3D11VA → ID3D11Texture2D → D3D11Renderer → SwapChain → DirectComposition → Display。</para>
/// <para><b>异步策略</b>：全部同步（sync 分类）——GPU 资源创建/共享/KeyedMutex 是同步 native 操作，无 I/O await。
/// 若改为 <c>async</c> 方法体内无 <c>await</c> 则为伪异步，故保持同步。</para>
/// <para><b>分层</b>：本类位于 Platforms 层，仅依赖 Vortice（第三方）与 Abstractions，不引用 Renderers 模块具体类型；
/// 设备由调用方（Renderer 层）通过参数传入。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class D3D11Interop
{
    /// <summary>
    /// 创建可跨 API 共享的 D3D11 纹理（<see cref="ResourceOptionFlags.SharedKeyedMutex"/> + <see cref="ResourceOptionFlags.SharedNTHandle"/>）。
    /// </summary>
    /// <remarks>NT 句柄模式：与 <see cref="GetSharedHandle"/>（IDXGIResource1.CreateSharedHandle）配对，
    /// 句柄可安全 CloseHandle；ID3D11Device1.OpenSharedResource1 仅接受 NT 句柄。</remarks>
    /// <param name="device">共享 D3D11 设备。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">DXGI 纹理格式。</param>
    /// <returns>共享纹理（调用方负责释放）。</returns>
    public ID3D11Texture2D CreateSharedTexture(ID3D11Device device, int width, int height, Format format)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正数。");

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1u,
            ArraySize = 1u,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            // 注意：Shared 与 SharedKeyedMutex 互斥；OpenSharedResource1 要求 NT 句柄 → SharedNTHandle。
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex | ResourceOptionFlags.SharedNTHandle,
        };
        return device.CreateTexture2D(desc);
    }

    /// <summary>
    /// 从 D3D11 纹理创建 DXGI 共享 NT 句柄（用于跨进程 / 跨 API 共享）。
    /// </summary>
    /// <remarks>用 <c>IDXGIResource1.CreateSharedHandle</c> 创建真 NT 句柄（可 CloseHandle 释放），
    /// 而非 legacy <c>GetSharedHandle</c>（返回非 NT 伪句柄，不可 Close 且 OpenSharedResource1 不接受）。</remarks>
    /// <param name="texture">源纹理（须以 <see cref="ResourceOptionFlags.SharedNTHandle"/> 创建）。</param>
    /// <returns>DXGI 共享 NT 句柄（建议交由 <see cref="DxgiSharedHandle"/> 管理生命周期）。</returns>
    public nint GetSharedHandle(ID3D11Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        using var dxgiResource1 = texture.QueryInterface<IDXGIResource1>();
        return dxgiResource1.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
    }

    /// <summary>
    /// 打开跨进程共享纹理（D3D11 ↔ D3D11）。
    /// </summary>
    /// <param name="device">目标 D3D11 设备。</param>
    /// <param name="sharedHandle">DXGI 共享句柄。</param>
    /// <returns>打开的共享纹理。</returns>
    public ID3D11Texture2D OpenSharedTexture(ID3D11Device device, nint sharedHandle)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (sharedHandle == IntPtr.Zero) throw new ArgumentException("共享句柄无效。", nameof(sharedHandle));

        var device1 = device.QueryInterface<ID3D11Device1>();
        try
        {
            device1.OpenSharedResource1<ID3D11Texture2D>(sharedHandle, out var texture);
            return texture ?? throw new InvalidOperationException("打开共享纹理失败。");
        }
        finally
        {
            device1.Dispose();
        }
    }

    /// <summary>
    /// 将 Vulkan 外部内存句柄导入为 D3D11 共享纹理（跨 API 零拷贝）。
    /// </summary>
    /// <param name="device">目标 D3D11 设备。</param>
    /// <param name="vulkanExternalHandle">Vulkan 导出的外部内存句柄（HANDLE / fd 经 Windows 共享）。</param>
    /// <returns>导入的 D3D11 纹理。</returns>
    public ID3D11Texture2D OpenSharedTextureFromVulkan(ID3D11Device device, nint vulkanExternalHandle)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (vulkanExternalHandle == IntPtr.Zero) throw new ArgumentException("Vulkan 外部句柄无效。", nameof(vulkanExternalHandle));

        // Vulkan 侧已通过 vkGetMemoryWin32HandleKHR 导出 Windows 共享句柄，
        // D3D11 侧用 ID3D11Device1.OpenSharedResource1 打开（与跨进程同路径）。
        var device1 = device.QueryInterface<ID3D11Device1>();
        try
        {
            device1.OpenSharedResource1<ID3D11Texture2D>(vulkanExternalHandle, out var texture);
            return texture ?? throw new InvalidOperationException("导入 Vulkan 共享纹理失败。");
        }
        finally
        {
            device1.Dispose();
        }
    }

    /// <summary>
    /// 将 OpenGL 纹理导入为 D3D11 共享纹理（跨 API 零拷贝）。
    /// </summary>
    /// <remarks>需要外部已导出 Windows 共享句柄（如 WGL_NV_DX_interop 或 EGL 互操作导出的 HANDLE）。</remarks>
    /// <param name="device">目标 D3D11 设备。</param>
    /// <param name="glSharedHandle">OpenGL 侧导出的共享句柄。</param>
    /// <returns>导入的 D3D11 纹理。</returns>
    public ID3D11Texture2D OpenSharedTextureFromOpenGL(ID3D11Device device, nint glSharedHandle)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (glSharedHandle == IntPtr.Zero) throw new ArgumentException("OpenGL 共享句柄无效。", nameof(glSharedHandle));

        var device1 = device.QueryInterface<ID3D11Device1>();
        try
        {
            device1.OpenSharedResource1<ID3D11Texture2D>(glSharedHandle, out var texture);
            return texture ?? throw new InvalidOperationException("导入 OpenGL 共享纹理失败。");
        }
        finally
        {
            device1.Dispose();
        }
    }

    /// <summary>
    /// 从硬解输出创建可共享 D3D11 纹理（DXVA2 / D3D11VA 路径占位）。
    /// </summary>
    /// <remarks>真实硬解由 FFmpeg DXVA 后端产出 ID3D11Texture2D；此处提供共享纹理封装供跨模块传递。</remarks>
    /// <param name="device">共享 D3D11 设备。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <returns>共享纹理与数组索引（DXVA 共享纹理数组通常为 0）。</returns>
    public (ID3D11Texture2D texture, int index) CreateTextureFromHardwareDecoder(ID3D11Device device, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(device);
        var texture = CreateSharedTexture(device, width, height, Format.B8G8R8A8_UNorm);
        return (texture, 0);
    }

    /// <summary>
    /// 使用 KeyedMutex 获取跨 API 纹理访问权（同步访问锁）。
    /// </summary>
    /// <param name="texture">共享纹理。</param>
    /// <param name="key">同步键值。</param>
    /// <param name="timeoutMs">超时（毫秒，默认无限等待）。</param>
    public void AcquireSync(ID3D11Texture2D texture, ulong key, uint timeoutMs = uint.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(texture);
        using var keyedMutex = texture.QueryInterface<IDXGIKeyedMutex>();
        keyedMutex.AcquireSync(key, unchecked((int)timeoutMs));
    }

    /// <summary>
    /// 释放 KeyedMutex 同步锁。
    /// </summary>
    /// <param name="texture">共享纹理。</param>
    /// <param name="key">同步键值。</param>
    public void ReleaseSync(ID3D11Texture2D texture, ulong key)
    {
        ArgumentNullException.ThrowIfNull(texture);
        using var keyedMutex = texture.QueryInterface<IDXGIKeyedMutex>();
        keyedMutex.ReleaseSync(key);
    }
}
