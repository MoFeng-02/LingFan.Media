using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// ffmpeg D3D11VA 硬解 NV12 切片 → 自建可共享 NV12 纹理 → DXGI 共享 NT 句柄（NV12 零拷贝导出器）。
/// </summary>
/// <remarks>
/// <para>背景：Vulkan 渲染器可经互操作直接导入 NV12 共享纹理并在 shader 内转 RGBA（见
/// <c>VulkanGpuFrameProducer.TryImportNv12</c>），无需在解码侧经 VideoProcessorBlt 转 RGBA。
/// 但 ffmpeg D3D11VA 的硬解帧纹理（解码器私有、非 SharedNTHandle）无法直接导出共享句柄，
/// 故本导出器用 <c>ID3D11DeviceContext::CopySubresourceRegion</c>（绝对槽位 46，GPU 内拷贝、不需 SRV）
/// 把某一帧切片拷入<b>自建</b>的可共享 NV12 纹理（按可能性顺序探测 MiscFlags/BindFlags 组合，取首个 CT2D+CSH 均成功者；
/// 首选镜像 RGBA 零拷贝路径的 <c>SharedKeyedMutex|SharedNTHandle</c>+<c>BindShaderResource</c>，末位兜底纯 <c>SharedNTHandle</c>），
/// 再经 <c>D3D11SharedHandle.GetSharedHandle</c> 导出 NT 句柄交给 Vulkan 导入。</para>
/// <para>零拷贝性质：硬解 NV12 → 自建 NV12 全程 GPU 内拷贝，无 CPU 回读；NV12→RGBA 由 Vulkan shader 完成。</para>
/// <para>归属：位于中性互操作模块 <c>LingFan.Media.GPUShare.D3D11</c>，解码后端（FFmpeg/MF）与渲染器
/// （Vulkan/OpenGL/D3D11）均可引用，不引 Platforms / Renderers / Backends，符合依赖倒置。</para>
/// <para>生命周期：持有的 <see cref="_devicePtr"/> / <see cref="_contextPtr"/> 为共享设备裸指针
/// （由 <c>IGpuDeviceContext</c> 拥有，本类不 AddRef、不 Release）；每次导出新建的 NV12 纹理
/// 经 <c>out</c> 参数转移所有权给调用方（导入成功后由调用方 Release）。</para>
/// <para>AOT 兼容：全手写原生 vtable P/Invoke（见 <see cref="D3D11Interop"/>），零反射、零 [ComImport]、零 Vortice。</para>
/// <para>状态：NV12 共享纹理的可用性依赖驱动/硬件（部分环境拒绝 NV12 加共享标志），调用方应按
/// <c>ApiType</c> 决定启用本导出器还是回落 VideoProcessorBlt 转 RGBA 路径。本类保留供支持共享 NV12 的环境启用。</para>
/// </remarks>
public sealed class D3D11Nv12SharedHandleExporter : IDisposable
{
    private readonly IntPtr _devicePtr;        // 共享设备裸指针（不 Release）
    private readonly IntPtr _contextPtr;       // 共享上下文裸指针（不 Release）
    private bool _disposed;

    // 失败诊断已打印标志：首帧失败附带完整对象指针/设备归属诊断，后续帧仅异常本体（防刷屏）。
    private bool _failDiagnosed;

    /// <summary>以共享 D3D11 设备/上下文的裸指针构造导出器。</summary>
    /// <param name="deviceHandle">ffmpeg 自有 D3D11 设备指针（由 IGpuDeviceContext 拥有，本类不接管）。</param>
    /// <param name="contextHandle">ffmpeg 自有 D3D11 设备上下文指针（由 IGpuDeviceContext 拥有，本类不接管）。</param>
    /// <exception cref="ArgumentException">句柄为 Zero。</exception>
    /// <exception cref="COMException">vtable 校准失败。</exception>
    public D3D11Nv12SharedHandleExporter(IntPtr deviceHandle, IntPtr contextHandle)
    {
        if (deviceHandle == IntPtr.Zero || contextHandle == IntPtr.Zero)
            throw new ArgumentException("device/context 句柄不可为 Zero", nameof(deviceHandle));

        // 仅持有裸指针；共享设备不 AddRef（不接管所有权），避免提前释放。
        _devicePtr = deviceHandle;
        _contextPtr = contextHandle;

        // vtable 运行时校准：槽位错 → 立即可诊断失败，绝不带野指针继续调（调用方捕获后回落软解）。
        D3D11Interop.VerifyVtableLayout(_devicePtr, _contextPtr);
    }

    /// <summary>
    /// 把 ffmpeg NV12 硬解帧的指定切片经 GPU 拷贝导出为可共享 NV12 纹理的 DXGI NT 句柄。
    /// </summary>
    /// <param name="nv12TexturePtr">ffmpeg NV12 D3D11 纹理指针（avFrame->data[0]，与 _devicePtr 同设备）。</param>
    /// <param name="subresourceIndex">NV12 纹理数组切片索引（avFrame->data[1]）。</param>
    /// <param name="width">帧宽。</param>
    /// <param name="height">帧高。</param>
    /// <param name="nv12SharedHandle">成功时为 NV12 纹理的 DXGI 共享句柄；失败为 Zero。</param>
    /// <param name="nv12Texture">成功时为 NV12 纹理指针（调用方在生产者导入后负责 Release）；失败为 Zero。</param>
    /// <param name="failure">失败时承载具体异常；成功为 <c>null</c>。</param>
    /// <returns>导出是否成功。</returns>
    public unsafe bool TryExportNv12(
        IntPtr nv12TexturePtr, int subresourceIndex, int width, int height,
        out IntPtr nv12SharedHandle, out IntPtr nv12Texture, out Exception? failure)
    {
        failure = null;
        nv12SharedHandle = IntPtr.Zero;
        nv12Texture = IntPtr.Zero;

        if (nv12TexturePtr == IntPtr.Zero || width <= 0 || height <= 0)
        {
            failure = new ArgumentException($"无效输入：texture=0x{nv12TexturePtr:X} W={width} H={height}");
            return false;
        }

        // 自建可共享 NV12 纹理。NV12 视频格式对「共享纹理 MiscFlags/BindFlags 组合」极其敏感（驱动差异），
        // 故按可能性顺序运行时探测，取首个在 CreateTexture2D 与 CreateSharedHandle 两处均成功的组合：
        //  ① SharedKeyedMutex|SharedNTHandle + ShaderResource：镜像已验证的 RGBA 零拷贝组合（仅 BindFlags RTV→SRV，NV12 不可渲染）。
        //  ② 同 ① 但 BindFlags=0：兜底个别驱动拒绝 NV12 带任何绑定的情形。
        //  ③ 仅 SharedNTHandle + ShaderResource：去 keyed mutex（Vulkan 不经 keyed mutex 同步），个别环境要求此组合（作末位兜底）。
        var combos = new (uint Misc, uint Bind)[]
        {
            (D3D11Interop.RgbaTextureMiscFlags, D3D11Interop.BindShaderResource),
            (D3D11Interop.RgbaTextureMiscFlags, 0u),
            (D3D11Interop.Nv12TextureMiscFlags, D3D11Interop.BindShaderResource),
        };

        Exception? lastError = null;
        for (int ci = 0; ci < combos.Length; ci++)
        {
            var (misc, bind) = combos[ci];
            IntPtr output = IntPtr.Zero;
            try
            {
                var nv12Desc = new D3D11Texture2DDesc
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = D3D11Interop.FormatNv12, // DXGI_FORMAT_NV12 = 103（注意 41 是 NV11，勿混用）
                    SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
                    Usage = 0, // D3D11_USAGE_DEFAULT
                    BindFlags = bind,
                    CPUAccessFlags = 0,
                    MiscFlags = misc,
                };
                output = D3D11Interop.CreateTexture2D(_devicePtr, nv12Desc);

                // 条件 KeyedMutex 包裹：仅当纹理确实带 keyed mutex（QI 成功）才 Acquire/Release；
                // 纯 NT 句柄（0x800）纹理无 keyed mutex，跳过包裹（Vulkan 侧不经 keyed mutex 同步）。
                IntPtr keyedMutexPtr = IntPtr.Zero;
                bool acquired = false;
                try
                {
                    if (D3D11Interop.TryQueryInterface(output, D3D11Interop.IID_IDXGIKeyedMutex, out keyedMutexPtr))
                    {
                        D3D11Interop.AcquireSync(keyedMutexPtr, 0, 5000);
                        acquired = true;
                    }

                    // GPU 内拷贝：ffmpeg NV12 切片（srcSubresource=subresourceIndex）→ 自建 NV12（dstSubresource=0）。
                    // 绝对槽位 46，不需 SRV/RTV 绑定（NV12 硬解纹理不可绑 SRV，但可经此拷出）。
                    D3D11Interop.CopySubresourceRegion(
                        _contextPtr, output, 0, 0, 0, 0, nv12TexturePtr, (uint)subresourceIndex, IntPtr.Zero);

                    // 确保 GPU 命令提交（绝对槽位 111）。
                    D3D11Interop.Flush(_contextPtr);
                }
                finally
                {
                    if (acquired && keyedMutexPtr != IntPtr.Zero)
                        D3D11Interop.ReleaseSync(keyedMutexPtr, 0);
                    if (keyedMutexPtr != IntPtr.Zero)
                    {
                        D3D11Interop.Release(keyedMutexPtr);
                        keyedMutexPtr = IntPtr.Zero;
                    }
                }

                // 取 DXGI 共享句柄（output 纹理仍由本方法持有并 out 返回）。
                nv12SharedHandle = D3D11SharedHandle.GetSharedHandle(output);
                if (nv12SharedHandle == IntPtr.Zero)
                    throw new InvalidOperationException("IDXGIResource1::CreateSharedHandle 返回空句柄");

                if (!_failDiagnosed)
                    Console.Error.WriteLine(
                        $"[NV12-EXPORTER] 命中 DESC 组合 #{ci + 1}（misc=0x{misc:X} bind=0x{bind:X}）=> NV12 共享句柄导出成功");
                nv12Texture = output;
                return true;
            }
            catch (Exception ex)
            {
                // 本组合失败（CT2D / 拷贝 / CSH）→ 释放本组合创建的 output，尝试下一组合。
                lastError = ex;
                if (output != IntPtr.Zero)
                {
                    D3D11Interop.Release(output);
                    output = IntPtr.Zero;
                }
            }
        }

        // 所有组合均失败 → 回落 CPU 传输。
        if (!_failDiagnosed)
        {
            _failDiagnosed = true;
            failure = new InvalidOperationException(
                $"NV12 共享句柄导出失败: 已尝试 {combos.Length} 种 DESC 组合均失败，末次={lastError?.Message}\n" +
                $"  device=0x{_devicePtr:X} context=0x{_contextPtr:X}\n" +
                $"  srcTexture=0x{nv12TexturePtr:X} subresource={subresourceIndex}（失败路径已释放）",
                lastError);
        }
        else
        {
            failure = lastError;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // _devicePtr / _contextPtr 为共享设备裸指针，不 Release（避免提前释放共享设备）。
    }
}
