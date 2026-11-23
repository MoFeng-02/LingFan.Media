using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// D3D11VA 硬解输出的 NV12 纹理 → RGBA32 纹理的 GPU 转换（ID3D11VideoProcessor.VideoProcessorBlt）。
/// </summary>
/// <remarks>
/// <para>背景：Vulkan / OpenGL 渲染器经互操作导入 D3D11 共享纹理时，WGL_NV_DX_interop2 无法可移植地采样
/// NV12 双平面纹理，Vulkan 导入 NV12 也需双平面着色器支持。故在解码侧把 NV12 硬解帧经
/// GPU 视频处理器（硬件）转成 RGBA32，三渲染器统一收 RGBA 走零拷贝，达成 Windows「全通」。</para>
/// <para>零拷贝性质：NV12 纹理 → VideoProcessorBlt → RGBA 纹理全程在 GPU 内完成，无 CPU 回读。</para>
/// <para>归属：本类位于中性互操作模块 <c>LingFan.Media.GPUShare.D3D11</c>，解码后端（FFmpeg/MF）与
/// 渲染器（Vulkan/OpenGL/D3D11）均可引用，不引 Platforms / Renderers / Backends，符合依赖倒置。</para>
/// <para>生命周期：本类持有的 <see cref="_devicePtr"/> / <see cref="_contextPtr"/> 仅为共享设备的裸指针
/// （由 <c>IGpuDeviceContext</c> 拥有，本类不 AddRef、不 Release，避免提前释放共享设备）；
/// 通过 QueryInterface 取得的视频设备/上下文及创建的处理器/枚举器在 <see cref="Dispose"/> 中 Release。
/// 每帧产出的 RGBA 纹理经 <c>out</c> 参数交由调用方，在生产者成功导入共享句柄后由调用方 Release（共享引用已转移）。</para>
/// <para>AOT 兼容：全手写原生 vtable P/Invoke（见 <see cref="D3D11Interop"/>），零反射、零 [ComImport]、零 Vortice。</para>
/// <para>分部划分：VideoProcessor 分部管处理器/枚举器生命周期；Staging 分部管中转纹理；
/// Diagnostics 分部管失败诊断。本文件为转换主流程与类生命周期。</para>
/// </remarks>
public sealed partial class D3D11Nv12ToRgbaConverter : IDisposable
{
    private readonly IntPtr _devicePtr;        // 共享设备裸指针（不 Release）
    private readonly IntPtr _contextPtr;       // 共享上下文裸指针（不 Release）
    private readonly IntPtr _videoDevicePtr;   // QI 取得（本类 Release）
    private readonly IntPtr _videoContextPtr;  // QI 取得（本类 Release）
    private bool _disposed;

    // 跨设备共享纹理（SharedKeyedMutex）的 KeyedMutex 超时（毫秒）。
    // 转换器在解码线程执行，写入后须立即释放锁供渲染器（跨 API）消费；超时即认为消费方阻塞，丢帧回落软解。
    private const int KeyedMutexTimeoutMs = 5000;

    /// <summary>
    /// 以共享 D3D11 设备/上下文的裸指针构造转换器。
    /// </summary>
    /// <param name="deviceHandle">共享 D3D11 设备指针（由 IGpuDeviceContext 拥有，本类不接管）。</param>
    /// <param name="contextHandle">共享 D3D11 设备上下文指针（由 IGpuDeviceContext 拥有，本类不接管）。</param>
    /// <exception cref="ArgumentException">句柄为 Zero。</exception>
    /// <exception cref="COMException">QI 视频设备/上下文失败或 vtable 校准失败。</exception>
    public D3D11Nv12ToRgbaConverter(IntPtr deviceHandle, IntPtr contextHandle)
    {
        if (deviceHandle == IntPtr.Zero || contextHandle == IntPtr.Zero)
            throw new ArgumentException("device/context 句柄不可为 Zero", nameof(deviceHandle));

        // 仅持有裸指针；共享设备不 AddRef（不接管所有权），避免提前释放。
        _devicePtr = deviceHandle;
        _contextPtr = contextHandle;

        // QI 取得视频设备/上下文（引用计数 +1，由本类负责 Release）。
        _videoDevicePtr = D3D11Interop.QueryInterface(deviceHandle, D3D11Interop.IID_ID3D11VideoDevice);
        _videoContextPtr = D3D11Interop.QueryInterface(contextHandle, D3D11Interop.IID_ID3D11VideoContext);

        // vtable 运行时校准：槽位错 → 立即可诊断失败，绝不带野指针继续调（调用方捕获后回落软解）。
        // 调试层（D3D11_CREATE_DEVICE_DEBUG）启用时，vtable 被调试层包装——函数指针指向
        // 调试层模块而非 d3d11.dll，CheckSlotModule 会判越界。槽位本身仍正确（调试层透明转发），
        // 故此处 catch 跳过校验，继续初始化（Blt 失败后 DumpDebugMessages 会看到驱动精确拒绝原因）。
        try
        {
            D3D11Interop.VerifyVtableLayout(_devicePtr, _contextPtr);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[D3D11-DEBUG] VerifyVtableLayout 跳过（调试层包装 vtable，模块归属变化属正常）：{ex.Message}");
        }
    }

    /// <summary>
    /// 把 NV12 纹理（指定切片）转换为 RGBA32 纹理，返回其 DXGI 共享句柄与原始纹理指针。
    /// </summary>
    /// <param name="nv12TexturePtr">NV12 D3D11 纹理指针（avFrame->data[0]）。</param>
    /// <param name="subresourceIndex">NV12 纹理数组切片索引（avFrame->data[1]）。</param>
    /// <param name="width">帧宽。</param>
    /// <param name="height">帧高。</param>
    /// <param name="rgbaSharedHandle">成功时为 RGBA 纹理的 DXGI 共享句柄；失败为 Zero。</param>
    /// <param name="rgbaTexture">成功时为 RGBA 纹理指针（调用方在生产者导入后负责 Release）；失败为 Zero。</param>
    /// <param name="failure">失败时承载具体异常（含失败步骤与 HRESULT）；成功为 <c>null</c>。</param>
    /// <returns>转换是否成功。</returns>
    public unsafe bool TryConvert(
        IntPtr nv12TexturePtr, int subresourceIndex, int width, int height,
        out IntPtr rgbaSharedHandle, out IntPtr rgbaTexture, out Exception? failure)
    {
        failure = null;
        rgbaSharedHandle = IntPtr.Zero;
        rgbaTexture = IntPtr.Zero;

        if (nv12TexturePtr == IntPtr.Zero || width <= 0 || height <= 0)
        {
            failure = new ArgumentException($"无效输入：texture=0x{nv12TexturePtr:X} W={width} H={height}");
            return false;
        }

        // output 由本方法创建；成功经 out 转移给调用方（不 Release），失败路径才 Release。
        IntPtr output = IntPtr.Zero;
        // 输入 NV12 纹理 DESC（内容描述 Input 尺寸 + 诊断用；GetTexture2DDesc 为只读方法，不影响 ffmpeg 所有权）。
        // 须在 EnsureProcessor 之前读：内容描述的 InputWidth/InputHeight 用纹理实际尺寸（含宏块对齐填充），
        // 用帧尺寸会让驱动在 Blt 校验维度不匹配 → E_INVALIDARG。
        D3D11Texture2DDesc inputTexDesc = default;
        try
        {
            inputTexDesc = D3D11Interop.GetTexture2DDesc(nv12TexturePtr);
            EnsureProcessor((int)inputTexDesc.Width, (int)inputTexDesc.Height, width, height);

            // 1. 创建 RGBA 输出纹理（RenderTarget + 共享，供渲染器跨 API 导入）。
            var rgbaDesc = new D3D11Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = D3D11Interop.FormatB8G8R8A8Unorm,
                SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
                Usage = 0, // D3D11_USAGE_DEFAULT
                BindFlags = D3D11Interop.BindRenderTarget,
                CPUAccessFlags = 0,
                // NT 句柄：渲染器侧经 ID3D11Device1.OpenSharedResource1 打开（仅接受 NT 句柄）。
                MiscFlags = D3D11Interop.RgbaTextureMiscFlags, // SharedKeyedMutex | SharedNTHandle
            };
            output = D3D11Interop.CreateTexture2D(_devicePtr, rgbaDesc);

            // 2. 输入/输出视图 + KeyedMutex + VideoProcessorBlt + Flush。
            // 视图每帧释放（finally 中 Release），避免每帧泄漏 2 个视频处理器视图；
            // blt+Flush 后 GPU 在途命令由 D3D11 运行时保活，可安全释放视图。
            IntPtr inputView = IntPtr.Zero;
            IntPtr outputView = IntPtr.Zero;
            IntPtr keyedMutexPtr = IntPtr.Zero;
            bool acquired = false;
            try
            {
                // VP 输入改为普通 NV12 纹理（绕过 DECODER 绑定 + 数组纹理的驱动限制）：
                // 1) 确保普通 NV12 中转纹理（ArraySize=1, BindFlags=0, MiscFlags=0）存在。
                EnsureSingleNv12((int)inputTexDesc.Width, (int)inputTexDesc.Height);

                // 2) 把 ffmpeg 硬解切片的两个平面（Y + UV）拷到普通纹理。
                // NV12 Texture2DArray 的子资源布局：Y 面 sub=S，UV 面 sub=S+ArraySize（MipLevels=1 时）。
                uint srcArraySize = inputTexDesc.ArraySize;
                // Y 面：解码器切片 subresourceIndex → 普通 sub 0
                D3D11Interop.CopySubresourceRegion(
                    _contextPtr, _singleNv12Ptr, 0, 0, 0, 0, nv12TexturePtr, (uint)subresourceIndex, IntPtr.Zero);
                // UV 面：解码器切片 subresourceIndex+ArraySize → 普通 sub 1
                D3D11Interop.CopySubresourceRegion(
                    _contextPtr, _singleNv12Ptr, 1, 0, 0, 0, nv12TexturePtr,
                    (uint)subresourceIndex + srcArraySize, IntPtr.Zero);

                // 3) 在普通 NV12 纹理上建 VP 输入视图（ArraySlice=0，因为普通纹理 ArraySize=1）。
                var inputDesc = new D3D11VideoProcessorInputViewDesc
                {
                    FourCC = 0,
                    ViewDimension = 1, // D3D11_VPIV_DIMENSION_TEXTURE_2D
                    MipSlice = 0,
                    ArraySlice = 0, // 普通纹理，只有 1 个切片
                };
                inputView = D3D11Interop.CreateVideoProcessorInputView(_videoDevicePtr, _singleNv12Ptr, _enumeratorPtr, inputDesc);

                var outputDesc = new D3D11VideoProcessorOutputViewDesc
                {
                    ViewDimension = 1, // D3D11_VPOV_DIMENSION_TEXTURE2D
                    MipSlice = 0,
                    // FirstArraySlice/ArraySize 未用（Texture2D 维度），恒 0。
                };
                outputView = D3D11Interop.CreateVideoProcessorOutputView(_videoDevicePtr, output, _enumeratorPtr, outputDesc);

                // 输出纹理以 SharedKeyedMutex 创建，跨设备共享须先 Acquire 再写入。
                if (D3D11Interop.TryQueryInterface(output, D3D11Interop.IID_IDXGIKeyedMutex, out keyedMutexPtr))
                {
                    D3D11Interop.AcquireSync(keyedMutexPtr, 0, KeyedMutexTimeoutMs);
                    acquired = true;
                }

                // 3. VideoProcessorBlt：NV12 → BGRA。先试直写共享输出纹理；
                //    部分驱动上 VP 拒绝直写 SharedKeyedMutex|SharedNTHandle 纹理（E_INVALIDARG），
                //    失败则自动切换 staging 中转：Blt→非共享纹理→CopySubresourceRegion→共享纹理
                //    （全程 GPU 内，零 CPU 回读，仅多一次 GPU 内 BGRA 拷贝）。
                var stream = new D3D11VideoProcessorStream
                {
                    Enable = 1,
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    ppPastFrames = IntPtr.Zero,
                    pInputSurface = inputView,
                    ppFutureFrames = IntPtr.Zero,
                };
                bool bltDone = false;
                if (!_directBltFailed)
                {
                    try
                    {
                        D3D11Interop.VideoProcessorBlt(_videoContextPtr, _processorPtr, outputView, 0, stream);
                        bltDone = true;
                    }
                    catch (COMException)
                    {
                        // 直写共享纹理失败（非致命）：置标志，本帧及后续帧走 staging 中转。
                        _directBltFailed = true;
                    }
                }
                if (!bltDone)
                {
                    EnsureStaging(width, height);
                    IntPtr stagingView = IntPtr.Zero;
                    try
                    {
                        stagingView = D3D11Interop.CreateVideoProcessorOutputView(
                            _videoDevicePtr, _stagingPtr, _enumeratorPtr, outputDesc);
                        D3D11Interop.VideoProcessorBlt(_videoContextPtr, _processorPtr, stagingView, 0, stream);
                        // staging 写入成功 → GPU 内拷入共享输出纹理（CopySubresourceRegion，绝对槽位 46）。
                        D3D11Interop.CopySubresourceRegion(
                            _contextPtr, output, 0, 0, 0, 0, _stagingPtr, 0, IntPtr.Zero);
                        if (!_stagingAnnounced)
                        {
                            _stagingAnnounced = true;
                            Console.Error.WriteLine(
                                "[NV12-BLT] 直写共享纹理 Blt 失败(E_INVALIDARG)，已切换 staging 中转路径" +
                                "（Blt→非共享BGRA→CopySubresourceRegion→共享BGRA，全程 GPU 内零 CPU 回读）");
                        }
                    }
                    finally
                    {
                        if (stagingView != IntPtr.Zero)
                            D3D11Interop.Release(stagingView);
                    }
                }

                // 4. 确保 GPU 命令提交（绝对槽位 111）。
                D3D11Interop.Flush(_contextPtr);
            }
            finally
            {
                // 写入完成即释放锁，使跨 API 消费者（GL/Vulkan）可安全采样。
                if (acquired && keyedMutexPtr != IntPtr.Zero)
                    D3D11Interop.ReleaseSync(keyedMutexPtr, 0);
                if (keyedMutexPtr != IntPtr.Zero)
                {
                    D3D11Interop.Release(keyedMutexPtr);
                    keyedMutexPtr = IntPtr.Zero;
                }

                // 视图每帧释放（无论成功/异常）。
                if (inputView != IntPtr.Zero)
                {
                    D3D11Interop.Release(inputView);
                    inputView = IntPtr.Zero;
                }

                if (outputView != IntPtr.Zero)
                {
                    D3D11Interop.Release(outputView);
                    outputView = IntPtr.Zero;
                }
            }

            // 5. 取 DXGI 共享句柄（output 纹理仍由本方法持有并 out 返回）。
            rgbaSharedHandle = D3D11SharedHandle.GetSharedHandle(output);
            if (rgbaSharedHandle == IntPtr.Zero)
            {
                failure = new InvalidOperationException("IDXGIResource1::CreateSharedHandle 返回空句柄");
                D3D11Interop.Release(output);
                return false;
            }

            rgbaTexture = output;
            return true;
        }
        catch (Exception ex)
        {
            // 任意失败（VideoProcessorBlt / 视图创建 / 共享句柄等）→ 释放本方法创建的 output，回落 CPU 传输。
            failure = DiagnoseFailure(ex, output, nv12TexturePtr, subresourceIndex, width, height, inputTexDesc);
            if (output != IntPtr.Zero)
                D3D11Interop.Release(output);
            return false;
        }
    }

    /// <summary>
    /// 释放本转换器持有的 COM 引用（VideoProcessor / 枚举器 / staging 纹理 / 单平面 NV12 纹理 / 视频上下文与设备；幂等）。
    /// 共享 device/context 为借用裸指针，不 Release（所有权归外部共享设备）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_processorPtr != IntPtr.Zero)
        {
            D3D11Interop.Release(_processorPtr);
            _processorPtr = IntPtr.Zero;
        }

        if (_enumeratorPtr != IntPtr.Zero)
        {
            D3D11Interop.Release(_enumeratorPtr);
            _enumeratorPtr = IntPtr.Zero;
        }

        if (_stagingPtr != IntPtr.Zero)
        {
            D3D11Interop.Release(_stagingPtr);
            _stagingPtr = IntPtr.Zero;
        }

        if (_singleNv12Ptr != IntPtr.Zero)
        {
            D3D11Interop.Release(_singleNv12Ptr);
            _singleNv12Ptr = IntPtr.Zero;
        }

        if (_videoContextPtr != IntPtr.Zero)
            D3D11Interop.Release(_videoContextPtr);
        if (_videoDevicePtr != IntPtr.Zero)
            D3D11Interop.Release(_videoDevicePtr);

        // _devicePtr / _contextPtr 为共享设备裸指针，不 Release（避免提前释放共享设备）。
    }
}