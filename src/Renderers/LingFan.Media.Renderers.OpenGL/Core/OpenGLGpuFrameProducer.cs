using System;
using Vortice.Direct3D11;

namespace LingFan.Media.Renderers.OpenGL;

/// <summary>
/// OpenGL 零拷贝帧生产者：把解码后端原生输出（Windows D3D11 共享句柄 / Linux VAAPI dma_buf）
/// 经跨 API 互操作导入为 GL 纹理（<see cref="IGpuTextureResource"/>），实现零拷贝上屏。
/// </summary>
/// <remarks>
/// <para>注册为 <see cref="IGpuFrameProducer"/>，供解码器经中立桥调用，严守依赖倒置（后端不感知 OpenGL 绑定细节）。</para>
/// <para><b>导入机制</b>：
/// <list type="bullet">
/// <item><b>Windows</b>：WGL_NV_DX_interop2——生产者自建桥接 D3D11 设备（<c>D3D11CreateDevice</c>），
/// 把解码侧 DXGI 共享 NT 句柄经 <c>ID3D11Device1.OpenSharedResource1</c> 打开为本设备纹理，
/// 再经 <c>wglDXOpenDeviceNV</c> + <c>wglDXRegisterObjectNV</c> 注册为 GL 纹理；
/// 着色器管线的 <see cref="OpenGLShaderPipeline.PresentGpuTexture"/> 直接采样（零拷贝）。</item>
/// <item><b>Linux</b>：EGL_EXT_image_dma_buf_import——经 <c>eglCreateImageKHR</c> 把 VAAPI dma_buf 绑为 EGLImage，
/// 再 <c>glEGLImageTargetTexture2DOES</c> 绑为 GL 纹理（零拷贝）。</item>
/// </list></para>
/// <para><b>能力自报 + 行为副作用双判据（S_OK≠被接受）</b>：扩展不可用 / 句柄无效 / 注册失败 →
/// <see cref="TryImport"/> 返回 <see langword="false"/>，调用方回落软解并计入 CPU 拷贝统计，绝不报"已就绪"假绿。</para>
/// <para><b>共享组</b>：导入在工厂级离屏 GL 上下文（共享组所有者，on-screen 上下文以 shareContext 接入）下执行，
/// 注册的 GL 纹理对渲染器可见——零拷贝链路与 D3D11/Vulkan 完全同源。跨上下文 D3D 绑定共享依赖 GL share-list，
/// 属运行期验收项（设计假设，由宿主 probe 验证）。</para>
/// <para><b>AOT</b>：GL/WGL/EGL 互操作函数指针经 <see cref="GLNative"/> 零反射解析；D3D11 桥接设备经 Vortice 类型安全 API；
/// 无 [DllImport]/[ComImport]/反射；跨平台经 OperatingSystem.IsXxx() 运行时分发，无 #if。</para>
/// <para><b>支持范围</b>：Windows(D3D11→GL) 与 Linux(VAAPI→GL) 均为已启用主路径。
/// Linux 路径：VAAPI 解出 VA Surface → <c>vaExportSurfaceHandle(DRM_PRIME_2, COMPOSED_LAYERS)</c> 导出单 fd 双平面 NV12 dma_buf
/// → 拆为 Y(R8) / UV(GR88) 两 EGLImage → 两 GL 纹理 → NV12 shader 零拷贝上屏。Android(AHardwareBuffer)/Apple(IOSurface) 为后续端点，当前返回 false。</para>
/// <para><b>异步策略</b>：<see cref="TryImport"/> 为同步（native 分类）——GPU 纹理导入是同步原生调用，无 I/O await；
/// 实现保持同步，不补 async（补即伪异步）。</para>
/// <para><b>句柄所有权契约（单一责任人）</b>：原生共享句柄（NT HANDLE / dma_buf fd）的所有权自
/// <see cref="TryImport"/> 调用起转移至本生产者；无论导入成功或失败，生产者均在返回前
/// 经 <c>CloseHandle</c>（NT HANDLE）/ close（fd）关闭句柄（导入成功后资源引用已由 GL 纹理 / EGLImage 持有，
/// 关闭句柄不销毁资源）。调用方（解码器）导出句柄后<b>不得</b>再关闭，避免双关。</para>
/// <para><b>分部划分</b>：Win32 分部管 WGL 导入、桥接设备与 GPU 一致性守卫；Linux 分部管 dma_buf 导入与平面属性表。
/// 本文件为平台分发入口与类生命周期。</para>
/// </remarks>
public sealed partial class OpenGLGpuFrameProducer : IGpuFrameProducer, IDisposable
{
    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.OpenGL;

    private readonly OpenGLOffscreenDeviceContext _glContext;
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>初始化 <see cref="OpenGLGpuFrameProducer"/> 的新实例。</summary>
    /// <param name="glContext">工厂级离屏 GL 设备上下文（共享组所有者；导入时 MakeCurrent 所需）。</param>
    /// <param name="logger">可选日志器。</param>
    public OpenGLGpuFrameProducer(OpenGLOffscreenDeviceContext glContext, ILogger? logger = null)
    {
        _glContext = glContext ?? throw new ArgumentNullException(nameof(glContext));
        _logger = logger;
    }

    /// <inheritdoc/>
    public unsafe bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        try
        {
            if (source.Handle == IntPtr.Zero)
                return false;
            if (source.Width <= 0 || source.Height <= 0)
            {
                CloseUnconsumedHandle(source); // 早退出口：句柄未被消费，须关闭防泄漏（单一责任人）
                return false;
            }

            if (OperatingSystem.IsWindows() && source.Kind == GpuFrameImportKind.D3D11SharedHandle)
                return TryImportWin32D3D11(source, out texture);
            if (OperatingSystem.IsLinux() && source.Kind == GpuFrameImportKind.LinuxDmaBufFd)
                return TryImportLinuxVaApi(source, out texture);

            // Android / iOS：后续端点（AHardwareBuffer / IOSurface），当前回落软解。
            CloseUnconsumedHandle(source); // 不支持端点出口：句柄未被消费，须关闭防泄漏（单一责任人）
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "OpenGL 零拷贝导入失败，回落软件解码（S_OK≠被接受：导入行为副作用未成立）。");
            texture?.Dispose();
            texture = null;
            return false;
        }
    }

    /// <summary>
    /// 关闭早退/不支持端点路径上未被消费的原生共享句柄（单一责任人契约的兜底出口）。
    /// 仅关闭语义明确的句柄形态：Windows NT HANDLE（D3D11SharedHandle）/ Linux dma_buf fd（LinuxDmaBufFd）；
    /// AHardwareBuffer 为借用指针（所有权不转移），绝不在此释放；其余未知形态不触碰。
    /// 平台导入分部（Win32/Linux）内部各出口已自行关闭句柄，本方法不与其重叠，避免双关。
    /// </summary>
    private static void CloseUnconsumedHandle(GpuFrameImportSource source)
    {
        if (source.Handle == IntPtr.Zero) return;
        if (source.Kind == GpuFrameImportKind.AndroidHardwareBuffer) return;
        if (OperatingSystem.IsWindows() && source.Kind == GpuFrameImportKind.D3D11SharedHandle)
            CloseHandle(source.Handle);
        else if (OperatingSystem.IsLinux() && source.Kind == GpuFrameImportKind.LinuxDmaBufFd)
            CloseFd((int)source.Handle);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 桥接 D3D11 设备直接释放；WGL 互操作句柄由 on-screen 渲染上下文（OpenGLShaderPipeline）持有并在其 Dispose 时关闭。
        _bridgeDevice?.Dispose();
        _bridgeDevice = null;
    }
}