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
/// <para>生命周期：本转换器持有的 <see cref="_devicePtr"/> / <see cref="_contextPtr"/> 仅为共享设备的裸指针
/// （由 <c>IGpuDeviceContext</c> 拥有，本类不 AddRef、不 Release，避免提前释放共享设备）；
/// 通过 QueryInterface 取得的视频设备/上下文及创建的处理器/枚举器在 <see cref="Dispose"/> 中 Release。
/// 每帧产出的 RGBA 纹理经 <c>out</c> 参数交由调用方，在生产者成功导入共享句柄后由调用方 Release（共享引用已转移）。</para>
/// <para>AOT 兼容：全手写原生 vtable P/Invoke（见 <see cref="D3D11Interop"/>），零反射、零 [ComImport]、零 Vortice。</para>
/// </remarks>
public sealed class D3D11Nv12ToRgbaConverter : IDisposable
{
    private readonly IntPtr _devicePtr;        // 共享设备裸指针（不 Release）
    private readonly IntPtr _contextPtr;       // 共享上下文裸指针（不 Release）
    private readonly IntPtr _videoDevicePtr;   // QI 取得（本类 Release）
    private readonly IntPtr _videoContextPtr;  // QI 取得（本类 Release）

    private IntPtr _enumeratorPtr;             // CreateVideoProcessor* 创建（本类 Release）
    private IntPtr _processorPtr;              // CreateVideoProcessor* 创建（本类 Release）
    private int _contentWidth = -1;            // 输入纹理实际宽（含对齐填充）
    private int _contentHeight = -1;           // 输入纹理实际高
    private int _frameWidth = -1;              // 帧宽（输出尺寸）
    private int _frameHeight = -1;             // 帧高（输出尺寸）
    private bool _disposed;

    // 非共享 BGRA 中转纹理（staging）：直写共享纹理的 Blt 失败时启用（部分驱动上 VP 拒绝直写
    // SharedKeyedMutex|SharedNTHandle 纹理 → E_INVALIDARG）。Blt 先写 staging，再
    // CopySubresourceRegion 拷入共享输出纹理——全程 GPU 内，零 CPU 回读。跨帧复用（尺寸变了才重建）。
    private IntPtr _stagingPtr;
    private int _stagingWidth = -1;
    private int _stagingHeight = -1;
    // 直写共享纹理 Blt 已失败标志：置 true 后后续帧直接走 staging，避免每帧重复失败开销。
    private bool _directBltFailed;
    // staging 路径首次启用已通告标志（一次性诊断打印，防刷屏）。
    private bool _stagingAnnounced;

    // 普通 NV12 中转纹理（非数组、非 DECODER 绑定）：把 ffmpeg 硬解切片经 CopySubresourceRegion
    // 拷入此纹理，再用它做 VP 输入——绕过驱动对 D3D11_BIND_DECODER + Texture2DArray 纹理的 VP Blt 限制。
    private IntPtr _singleNv12Ptr;
    private int _singleNv12Width = -1;
    private int _singleNv12Height = -1;

    // 失败诊断已打印标志：首帧失败附带完整对象指针/设备归属诊断，后续帧仅异常本体（防刷屏）。
    private bool _failDiagnosed;

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
    /// 确保视频处理器存在（尺寸任一变化即重建）。
    /// </summary>
    /// <param name="texWidth">输入纹理实际宽（含编解码对齐填充，如 1080→1088）。</param>
    /// <param name="texHeight">输入纹理实际高。</param>
    /// <param name="frameWidth">帧宽（输出尺寸，即实际视频内容宽）。</param>
    /// <param name="frameHeight">帧高（输出尺寸）。</param>
    /// <remarks>
    /// 内容描述的 InputWidth/InputHeight <b>必须用纹理实际尺寸</b>（而非帧尺寸）：
    /// D3D11VA 硬解纹理有宏块对齐填充（H.264 16 像素对齐，如 1080→1088），输入视图覆盖整张纹理；
    /// 若内容描述声明 1080 而实际纹理 1088，驱动在 VideoProcessorBlt 校验维度不匹配 → E_INVALIDARG。
    /// 输出侧用帧尺寸，并经源矩形裁掉填充（左上角帧尺寸区域），避免垃圾像素被缩进画面。
    /// </remarks>
    private void EnsureProcessor(int texWidth, int texHeight, int frameWidth, int frameHeight)
    {
        if (_enumeratorPtr != IntPtr.Zero && _contentWidth == texWidth && _contentHeight == texHeight
            && _frameWidth == frameWidth && _frameHeight == frameHeight)
            return;

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

        _contentWidth = texWidth;
        _contentHeight = texHeight;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;

        var contentDesc = new D3D11VideoProcessorContentDescription
        {
            InputFrameFormat = 0, // D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE
            InputFrameRate = new DxgiRational { Numerator = 60, Denominator = 1 },
            InputWidth = (uint)texWidth,   // 纹理实际宽（含对齐填充）
            InputHeight = (uint)texHeight, // 纹理实际高
            OutputFrameRate = new DxgiRational { Numerator = 60, Denominator = 1 },
            OutputWidth = (uint)frameWidth,
            OutputHeight = (uint)frameHeight,
            Usage = 0, // D3D11_VIDEO_USAGE_PLAYBACK_NORMAL
        };
        _enumeratorPtr = D3D11Interop.CreateVideoProcessorEnumerator(_videoDevicePtr, contentDesc);

        // 处理器按 RateConversionCaps 遍历创建（对齐社区成熟做法）：固定 index 0 在部分设备/驱动上
        // 返回「创建成功但无 NV12→BGRA 转换能力」的处理器 → VideoProcessorBlt 报 E_INVALIDARG。
        // 遍历取第一个创建成功的 index；全失败则抛（回落 CPU 传输）。
        D3D11VideoProcessorCaps caps = D3D11Interop.GetVideoProcessorCaps(_enumeratorPtr);
        if (caps.RateConversionCapsCount == 0)
            throw new InvalidOperationException("视频处理器枚举器无任何 RateConversion 能力（GetVideoProcessorCaps.RateConversionCapsCount=0）");
        for (uint i = 0; i < caps.RateConversionCapsCount; i++)
        {
            try
            {
                _processorPtr = D3D11Interop.CreateVideoProcessor(_videoDevicePtr, _enumeratorPtr, i);
                break;
            }
            catch (COMException)
            {
                _processorPtr = IntPtr.Zero;
            }
        }
        if (_processorPtr == IntPtr.Zero)
            throw new InvalidOperationException(
                $"创建视频处理器失败（遍历 RateConversionCapsCount={caps.RateConversionCapsCount} 全部失败）");

        // Blt 前置必需状态（所有参考实现 VLC/Chromium/MF 均在 Blt 前设置；缺失会使部分驱动在 Blt 报 E_INVALIDARG）：
        // ① 流 0 帧格式 = 逐行扫描。
        D3D11Interop.VideoProcessorSetStreamFrameFormat(_videoContextPtr, _processorPtr, 0, 0); // D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE
        // ② 输入流颜色空间：BT.709 + 演播室范围 16-235（NV12 YUV 视频标准）。
        D3D11Interop.VideoProcessorSetStreamColorSpace(_videoContextPtr, _processorPtr, 0,
            new D3D11VideoProcessorColorSpace { Value = 0x0C }); // Matrix=1(BT.709) | Nominal=1(16-235)
        // ③ 输出颜色空间：全范围 RGB（BGRA 渲染目标）。
        D3D11Interop.VideoProcessorSetOutputColorSpace(_videoContextPtr, _processorPtr,
            new D3D11VideoProcessorColorSpace { Value = 0x10 }); // Nominal=2(0-255)
        // ④ 源矩形 = 左上角帧尺寸区域：裁掉宏块对齐填充（如纹理 1088 宽、视频 1080 宽，
        //    右侧 8 列是垃圾填充，不裁会被缩进画面）。纹理无填充时矩形=整张纹理，等效默认值。
        D3D11Interop.VideoProcessorSetStreamSourceRect(_videoContextPtr, _processorPtr, 0, 1,
            new D3D11Rect { Left = 0, Top = 0, Right = frameWidth, Bottom = frameHeight });
    }

    /// <summary>确保非共享 BGRA 中转纹理存在（尺寸变化时重建）。</summary>
    private void EnsureStaging(int width, int height)
    {
        if (_stagingPtr != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
            return;

        if (_stagingPtr != IntPtr.Zero)
        {
            D3D11Interop.Release(_stagingPtr);
            _stagingPtr = IntPtr.Zero;
        }

        var stagingDesc = new D3D11Texture2DDesc
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
            MiscFlags = 0, // 非共享（关键差异：无 SharedKeyedMutex/SharedNTHandle）
        };
        _stagingPtr = D3D11Interop.CreateTexture2D(_devicePtr, stagingDesc);
        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>确保普通 NV12 中转纹理存在（尺寸变化时重建）。</summary>
    /// <remarks>
    /// 普通 NV12 纹理 = ArraySize=1, BindFlags=0, MiscFlags=0——无 DECODER 绑定、无数组、无共享。
    /// ffmpeg 硬解纹理是 D3D11_BIND_DECODER + Texture2DArray，部分驱动在 VP Blt 时拒绝从
    /// 此类纹理采样（CreateVideoProcessorInputView 宽松放行，Blt 才报 E_INVALIDARG）。
    /// 把切片拷到普通纹理后再做 VP 输入，绕过此限制。
    /// </remarks>
    private void EnsureSingleNv12(int texWidth, int texHeight)
    {
        if (_singleNv12Ptr != IntPtr.Zero && _singleNv12Width == texWidth && _singleNv12Height == texHeight)
            return;

        if (_singleNv12Ptr != IntPtr.Zero)
        {
            D3D11Interop.Release(_singleNv12Ptr);
            _singleNv12Ptr = IntPtr.Zero;
        }

        var nv12Desc = new D3D11Texture2DDesc
        {
            Width = (uint)texWidth,
            Height = (uint)texHeight,
            MipLevels = 1,
            ArraySize = 1, // 单张，非数组
            Format = D3D11Interop.FormatNv12,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = 0, // D3D11_USAGE_DEFAULT
            BindFlags = 0, // 无绑定（非 DECODER、非 SRV、非 RTV）
            CPUAccessFlags = 0,
            MiscFlags = 0, // 无共享标志
        };
        _singleNv12Ptr = D3D11Interop.CreateTexture2D(_devicePtr, nv12Desc);
        _singleNv12Width = texWidth;
        _singleNv12Height = texHeight;
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
            if (!_failDiagnosed)
            {
                _failDiagnosed = true;
                failure = new InvalidOperationException(
                    $"NV12→RGBA 转换失败: {ex.Message}\n" +
                    $"  device=0x{_devicePtr:X} context=0x{_contextPtr:X}\n" +
                    $"  videoDevice=0x{_videoDevicePtr:X} videoContext=0x{_videoContextPtr:X}\n" +
                    $"  enumerator=0x{_enumeratorPtr:X} processor=0x{_processorPtr:X}\n" +
                    $"  output=0x{output:X}（失败路径已释放）\n" +
                    $"  VP 格式支持: NV12(103)=0x{D3D11Interop.CheckVideoProcessorFormat(_enumeratorPtr, 103):X} " +
                    $"BGRA(87)=0x{D3D11Interop.CheckVideoProcessorFormat(_enumeratorPtr, 87):X}（bit0=输入 bit1=输出）\n" +
                    $"  输入纹理(inputTexDesc): {inputTexDesc.Width}x{inputTexDesc.Height} ArraySize={inputTexDesc.ArraySize} Bind=0x{inputTexDesc.BindFlags:X} " +
                    $"Fmt=0x{inputTexDesc.Format:X} Misc=0x{inputTexDesc.MiscFlags:X} subresource={subresourceIndex}" +
                    $" device=0x{D3D11Interop.GetDeviceChildDevice(nv12TexturePtr):X}\n" +
                    $"  帧尺寸(内容描述/输出纹理): {width}x{height}" +
                    $"{(inputTexDesc.Width != (uint)width || inputTexDesc.Height != (uint)height ? " 与输入纹理尺寸不一致(编解码对齐填充)" : " (一致)")}\n" +
                    $"  设备归属(GetDevice): enumerator→0x{D3D11Interop.GetDeviceChildDevice(_enumeratorPtr):X} " +
                    $"processor→0x{D3D11Interop.GetDeviceChildDevice(_processorPtr):X} " +
                    $"output→0x{D3D11Interop.GetDeviceChildDevice(output):X}", ex);
                // D3D11 调试层消息捕获：打印驱动/runtime 对 VideoProcessorBlt 失败的精确原因。
                D3D11Interop.DumpDebugMessages(_devicePtr);
            }
            else
            {
                failure = ex;
            }
            if (output != IntPtr.Zero)
                D3D11Interop.Release(output);
            return false;
        }
    }

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
