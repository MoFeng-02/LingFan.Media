using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

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
/// <para>生命周期：本转换器持有的 <see cref="_device"/> / <see cref="_context"/> 仅为共享设备的零引用包装
/// （由 <c>IGpuDeviceContext</c> 拥有，本类不 AddRef、不 Dispose，避免提前释放共享设备）；
/// 通过 QueryInterface 取得的视频设备/上下文及创建的处理器/枚举器在 <see cref="Dispose"/> 中释放。
/// 每帧产出的 RGBA 纹理经 <c>out</c> 参数交由调用方，在生产者成功导入共享句柄后释放（共享引用已转移）。</para>
/// <para>AOT 兼容：使用 Vortice 源生成 COM 互操作（已在 TrimmerRoot 白名单），无反射、无 [ComImport]。</para>
/// </remarks>
public sealed class D3D11Nv12ToRgbaConverter : IDisposable
{
    private readonly ID3D11Device _device;        // 共享设备包装（不 Dispose）
    private readonly ID3D11DeviceContext _context; // 共享上下文包装（不 Dispose）
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _contentWidth = -1;
    private int _contentHeight = -1;
    private bool _disposed;

    // 跨设备共享纹理（SharedKeyedMutex）的 KeyedMutex 超时（毫秒）。
    // 转换器在解码线程执行，写入后须立即释放锁供渲染器（跨 API）消费；超时即认为消费方阻塞，丢帧回落软解。
    private const int KeyedMutexTimeoutMs = 5000;

    public D3D11Nv12ToRgbaConverter(IntPtr deviceHandle, IntPtr contextHandle)
    {
        // 包装既有共享设备指针（Vortice 构造不 AddRef；Dispose 时显式跳过本两项）。
        _device = new ID3D11Device(deviceHandle);
        _context = new ID3D11DeviceContext(contextHandle);
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
    }

    private void EnsureProcessor(int width, int height)
    {
        if (_enumerator is not null && _contentWidth == width && _contentHeight == height)
            return;

        _processor?.Dispose();
        _enumerator?.Dispose();
        _contentWidth = width;
        _contentHeight = height;

        var contentDesc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            InputFrameRate = new Vortice.DXGI.Rational(60u, 1u),
            OutputFrameRate = new Vortice.DXGI.Rational(60u, 1u),
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDesc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
    }

    /// <summary>
    /// 把 NV12 纹理（指定切片）转换为 RGBA32 纹理，返回其 DXGI 共享句柄与原始纹理。
    /// </summary>
    /// <param name="nv12TexturePtr">NV12 D3D11 纹理指针（avFrame->data[0]）。</param>
    /// <param name="subresourceIndex">NV12 纹理数组切片索引（avFrame->data[1]）。</param>
    /// <param name="width">帧宽。</param>
    /// <param name="height">帧高。</param>
    /// <param name="rgbaSharedHandle">成功时为 RGBA 纹理的 DXGI 共享句柄；失败为 Zero。</param>
    /// <param name="rgbaTexture">成功时为 RGBA 纹理（调用方在生产者导入后负责 Dispose）；失败为 null。</param>
    /// <returns>转换是否成功。</returns>
    public unsafe bool TryConvert(
        IntPtr nv12TexturePtr, int subresourceIndex, int width, int height,
        out IntPtr rgbaSharedHandle, out ID3D11Texture2D? rgbaTexture)
    {
        rgbaSharedHandle = IntPtr.Zero;
        rgbaTexture = null;

        if (nv12TexturePtr == IntPtr.Zero || width <= 0 || height <= 0)
            return false;

        try
        {
            EnsureProcessor(width, height);

            // 1. 创建 RGBA 输出纹理（RenderTarget + 共享，供渲染器跨 API 导入）
            var rgbaDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                // NT 句柄：渲染器侧经 ID3D11Device1.OpenSharedResource1 打开（仅接受 NT 句柄）。
                // 与 D3D11Interop 既有模式一致：SharedKeyedMutex | SharedNTHandle。
                MiscFlags = ResourceOptionFlags.SharedKeyedMutex | ResourceOptionFlags.SharedNTHandle,
            };
            ID3D11Texture2D output = _device.CreateTexture2D(rgbaDesc);

            try
            {
                // 2. 输入/输出视图
                // 注意：CreateVideoProcessor*View 返回的视图 COM 对象由本方法持有，必须显式 Dispose，
                // 否则每帧泄漏 2 个视频处理器视图。blt+Flush 后 GPU 在途命令由 D3D11 运行时保活，可安全释放视图。
                var inputDesc = new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView
                    {
                        ArraySlice = (uint)subresourceIndex,
                        MipSlice = 0,
                    },
                };

                // nv12TexturePtr 是 FFmpeg 拥有的 NV12 纹理；Vortice IntPtr 构造不 AddRef（不接管所有权），
                // 须抑制其包装器终结器，禁止 finalizer 对该纹理调用 Release（否则双重释放 / use-after-free）。
                var nv12Resource = new ID3D11Resource(nv12TexturePtr);
                IDXGIKeyedMutex? keyedMutex = null;
                bool acquired = false;
                try
                {
                    using ID3D11VideoProcessorInputView inputView =
                        _videoDevice.CreateVideoProcessorInputView(nv12Resource, _enumerator!, inputDesc);

                    var outputDesc = new VideoProcessorOutputViewDescription
                    {
                        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                    };
                    using ID3D11VideoProcessorOutputView outputView =
                        _videoDevice.CreateVideoProcessorOutputView(output, _enumerator!, outputDesc);

                    // 输出纹理以 SharedKeyedMutex 创建，跨设备共享须先 Acquire 再写入。
                    try { keyedMutex = output.QueryInterface<IDXGIKeyedMutex>(); }
                    catch (Exception) { keyedMutex = null; }
                    if (keyedMutex is not null)
                    {
                        keyedMutex.AcquireSync(0, KeyedMutexTimeoutMs);
                        acquired = true;
                    }

                    // 3. VideoProcessorBlt：NV12 → RGBA（GPU 硬件视频处理器）
                    var stream = new VideoProcessorStream
                    {
                        Enable = true,
                        InputSurface = inputView,
                        InputFrameOrField = 0,
                        OutputIndex = 0,
                    };
                    _videoContext.VideoProcessorBlt(_processor, outputView, 0, 1, new[] { stream });

                    // 4. 确保 GPU 命令提交
                    _context.Flush();
                }
                finally
                {
                    // 写入完成即释放锁，使跨 API 消费者（GL/Vulkan）可安全采样。
                    if (acquired && keyedMutex is not null)
                        keyedMutex.ReleaseSync(0);
                    keyedMutex?.Dispose();
                    GC.SuppressFinalize(nv12Resource);
                }

                // 5. 取 DXGI 共享句柄（Vortice 干净封装，不碰 raw vtable；output 纹理仍由本方法持有并 out 返回）
                rgbaSharedHandle = D3D11SharedHandle.GetSharedHandle(output);
                if (rgbaSharedHandle == IntPtr.Zero)
                {
                    output.Dispose();
                    return false;
                }

                rgbaTexture = output;
                return true;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        // _device / _context 为共享设备包装，不 Dispose（避免提前释放共享设备）
    }
}
