using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.Models;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

// FFmpegVideoDecoder 的 HwInit 职责段（部分类，由 FFmpegVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeCore/FlushCore/Reset/Dispose）在主文件；硬解/软帧路径与平台初始化。
internal sealed partial class FFmpegVideoDecoder
{
    /// <summary>
    /// 初始化 D3D11VA 硬件解码设备上下文（使用渲染器共享的 D3D11 设备，实现零拷贝）。
    /// </summary>
    /// <remarks>
    /// <para>同步操作（sync 分类）：FFmpeg hwdevice_ctx API 和 COM 操作均为同步原生调用，无 I/O await。</para>
    /// <para>共享设备零拷贝链路：渲染器 ID3D11Device → FFmpeg D3D11VA → 硬解纹理 → D3D11Renderer CopySubresourceRegion。</para>
    /// <para><b>引用计数所有权</b>：FFmpeg 在销毁 AVHWDeviceContext 时<b>必定</b> Release
    /// <c>device</c> 与 <c>device_context</c>（官方文档明示，与是否用户提供无关）。因本方法借用的是
    /// 渲染器工厂持有的共享设备，故写入 hwctx 前对两者各 <c>Marshal.AddRef</c> 一次；否则工厂
    /// Dispose 时将在已销毁对象上 Release，产生确定性 AccessViolation。详见方法内注释。</para>
    /// </remarks>
    /// <param name="ctx">FFmpeg 编解码上下文（设置其 hw_device_ctx 字段）。</param>
    private unsafe void InitializeD3D11VA(AVCodecContext* ctx)
    {
        // 统一读 _vaDeviceHandle/_vaContextHandle（渲染器共享设备场景 / ffmpeg 自有设备场景）。
        // 绝不可直接用 _gpuContext.DeviceHandle：ffmpeg 自有设备场景下渲染器为 GL/Vulkan 时它是 GL/Vk 设备，非 ID3D11Device。
        if (_vaDeviceHandle == IntPtr.Zero)
            throw new InvalidOperationException("GPU 设备句柄无效");
        if (_vaContextHandle == IntPtr.Zero)
            throw new InvalidOperationException("GPU 设备上下文句柄无效");

        // 1. 分配 D3D11VA 硬件设备上下文
        AVBufferRef* hwRef = FF.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (hwRef == null)
            throw new InvalidOperationException("av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA) 返回 null");

        try
        {
            // 2. 设置共享 D3D11 设备（通过原始指针操作 AVD3D11VADeviceContext）
            // AVBufferRef.data → AVHWDeviceContext → hwctx → AVD3D11VADeviceContext
            // AVD3D11VADeviceContext 布局：device(void*), device_context(void*), lock_ctx(uint), lock(fn*), unlock(fn*)
            AVHWDeviceContext* hwCtx = (AVHWDeviceContext*)hwRef->data;
            IntPtr* hwctxPtrs = (IntPtr*)hwCtx->hwctx;

            // FFmpeg 所有权契约（hwcontext_d3d11va.h 官方文档，device / device_context 两字段逐字相同）：
            //   "Deallocating the AVHWDeviceContext will always release this interface,
            //    and it does not matter whether it was user-allocated."
            //   即 av_buffer_unref 引用归零时 d3d11va_device_free() 会对 device 与 device_context
            //   各调用一次 Release —— 无论指针是不是用户塞进来的。
            //
            //   渲染器共享设备场景：这里塞的是渲染器工厂（D3D11RendererFactory，Singleton）持有的共享设备，工厂自己
            //   在 Dispose 时还要 Release 一次。ffmpeg 自有设备场景：这里塞的是本解码器 _vaOwnedDevice（ffmpeg 自有，
            //   我方 Dispose 时 Release 一次）。两种情形若不补 AddRef，ffmpeg 那次 Release 会吃掉那份引用
            //   ⇒ 设备/上下文提前销毁 ⇒ 确定性 AccessViolation。故写入 hwctx 前各 AddRef 一次。
            //
            //   配对性：AddRef 紧贴写入（其间无抛出点）。此后任何失败路径都走 catch 内的
            //   av_buffer_unref(hwRef) → free 回调 Release 两者，与本处 AddRef 精确配对，不泄漏、不多释放。
            //   video_device / video_context 由 ffmpeg 在 init 内自行 QI（自带 AddRef），无需我方干预。
            Marshal.AddRef(_vaDeviceHandle);
            Marshal.AddRef(_vaContextHandle);

            hwctxPtrs[0] = _vaDeviceHandle;    // device (ID3D11Device*)
            hwctxPtrs[1] = _vaContextHandle;    // device_context (ID3D11DeviceContext*)

            // 3. 初始化设备上下文（FFmpeg 检测 device 已设 → 使用共享设备，创建默认 lock/unlock）
            int ret = FF.av_hwdevice_ctx_init(hwRef);
            if (ret < 0)
                throw new InvalidOperationException($"av_hwdevice_ctx_init 失败: {GetErrorString(ret)} (code={ret})");

            // 4. 设置到编解码上下文（av_buffer_ref 增加引用计数）
            AVBufferRef* ctxRef = FF.av_buffer_ref(hwRef);
            if (ctxRef == null)
                throw new InvalidOperationException("av_buffer_ref 返回 null（内存不足）");
            ctx->hw_device_ctx = ctxRef;

            // 5. 创建 SafeHandle 管理 hwRef 生命周期（在 try 内——OOM 时 catch 可释放）
            _hwDeviceCtx = new SafeAVBufferRefHandle((IntPtr)hwRef);
        }
        catch
        {
            // 失败时释放 hwRef（av_buffer_unref 通过 SafeHandle 在 Dispose 中处理）
            AVBufferRef* p = hwRef;
            FF.av_buffer_unref(&p);
            throw;
        }

        _logger.LogInformation("D3D11VA 硬件解码已初始化（共享 D3D11 设备）");
    }

    /// <summary>
    /// 初始化 VAAPI 硬件解码设备上下文（Linux 真实零拷贝路径）。
    /// </summary>
    /// <remarks>
    /// <para>同步操作（sync 分类）：av_hwdevice_ctx_create + 原生指针读取均为同步原生调用，无 I/O await。</para>
    /// <para><c>av_hwdevice_ctx_create(AV_HWDEVICE_TYPE_VAAPI, device, ...)</c> 内部经 libva 打开 VADisplay
    /// （X11：取 $DISPLAY；或 DRM render 节点）并 vaInitialize，返回已初始化的 hw 设备上下文。
    /// 解码器随后自动把 hw 像素格式（AV_PIX_FMT_VAAPI）前置到 pix_fmts，收帧时
    /// <c>format==AV_PIX_FMT_VAAPI</c>、<c>data[3]=VASurfaceID</c>，经 <see cref="IVaApiExport"/> 导出 dma_buf
    /// 直投 GPU（零拷贝）。复用 D3D11VA 范式：不自定义 get_format（FFmpeg 自动前置 hw 格式）。</para>
    /// </remarks>
    private unsafe void InitializeVAAPI(AVCodecContext* ctx)
    {
        if (_vaApiExport is null)
            throw new InvalidOperationException("IVaApiExport 未注入（VAAPI 不可用）");

        // 1) 默认设备：ffmpeg 据 $DISPLAY(X11) 或默认 DRM render 节点自动选；失败回退显式 render 节点。
        AVBufferRef* hwRef = null;
        int ret = FF.av_hwdevice_ctx_create(
            &hwRef, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, null, null, 0);
        if (ret < 0)
        {
            _logger.LogWarning(
                "av_hwdevice_ctx_create(VAAPI, 默认设备) 失败({Error})，回退 /dev/dri/renderD128",
                GetErrorString(ret));
            ret = FF.av_hwdevice_ctx_create(
                &hwRef, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, "/dev/dri/renderD128", null, 0);
        }
        if (ret < 0 || hwRef == null)
            throw new InvalidOperationException($"av_hwdevice_ctx_create(VAAPI) 失败: {GetErrorString(ret)} (code={ret})");

        try
        {
            // 2) 取 VADisplay：AVHWDeviceContext.hwctx -> AVVAAPIDeviceContext.display（首字段，VADisplay=void*）
            var hwCtx = (AVHWDeviceContext*)hwRef->data;
            nint display = hwCtx->hwctx == null ? nint.Zero : *(nint*)hwCtx->hwctx;
            if (display == nint.Zero)
                throw new InvalidOperationException("VAAPI display 为空（vaInitialize 未成功）");
            _vaDisplay = display;

            // 3. 设置到编解码上下文（av_buffer_ref 增加引用计数）
            AVBufferRef* ctxRef = FF.av_buffer_ref(hwRef);
            if (ctxRef == null)
                throw new InvalidOperationException("av_buffer_ref 返回 null（内存不足）");
            ctx->hw_device_ctx = ctxRef;

            // 4. SafeHandle 管理 hwRef 生命周期（在 try 内——OOM 时 catch 可释放）
            _hwDeviceCtx = new SafeAVBufferRefHandle((nint)hwRef);
        }
        catch
        {
            AVBufferRef* p = hwRef;
            FF.av_buffer_unref(&p);
            throw;
        }

        _logger.LogInformation(
            "VAAPI 硬件解码已初始化（display={Display}, 零拷贝导入={ZeroCopy}）",
            _vaDisplay, _gpuProducer is not null);
    }

    /// <summary>
    /// 初始化 MediaCodec 硬件设备上下文（宿主注入 Surface → 表面直渲染；未注入 → 缓冲模式）。
    /// </summary>
    /// <remarks>
    /// <para>同步操作（sync 分类）：FFmpeg hwdevice_ctx API 均为同步原生调用，无 I/O await。</para>
    /// <para>MediaCodec 设备上下文无高层封装，故直接走通用 <c>av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_MEDIACODEC)</c> + 原始指针设置
    /// <c>AVMediaCodecDeviceContext</c>（与 D3D11VA 同款手法）。</para>
    /// <para>AVMediaCodecDeviceContext 布局（FFmpeg 8 hwcontext_mediacodec.h）：
    /// <c>surface(void*)</c>, <c>native_window(void*)</c>, <c>create_window(int)</c>。</para>
    /// <para><b>前置条件</b>：宿主须已调用 <see cref="MediaCodecInterop.SetJavaVM"/> 注入 JavaVM。</para>
    /// </remarks>
    /// <param name="ctx">FFmpeg 编解码上下文（设置其 hw_device_ctx 字段）。</param>
    private unsafe void InitializeMediaCodec(AVCodecContext* ctx)
    {
        IntPtr surface = _options?.MediaCodecSurface ?? IntPtr.Zero;
        IntPtr nativeWindow = _options?.MediaCodecNativeWindow ?? IntPtr.Zero;

        // 1. 分配 MediaCodec 硬件设备上下文
        AVBufferRef* hwRef = FF.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC);
        if (hwRef == null)
            throw new InvalidOperationException("av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_MEDIACODEC) 返回 null");

        try
        {
            // 2. 设置宿主注入的 Surface / ANativeWindow（Surface 优先；均为空 = 缓冲模式）
            AVHWDeviceContext* hwCtx = (AVHWDeviceContext*)hwRef->data;
            IntPtr* mcCtx = (IntPtr*)hwCtx->hwctx; // AVMediaCodecDeviceContext
            if (surface != IntPtr.Zero)
                mcCtx[0] = surface;        // surface (android/view/Surface jobject 全局引用)
            else if (nativeWindow != IntPtr.Zero)
                mcCtx[1] = nativeWindow;   // native_window (ANativeWindow*)

            // 3. 初始化设备上下文
            int ret = FF.av_hwdevice_ctx_init(hwRef);
            if (ret < 0)
                throw new InvalidOperationException($"av_hwdevice_ctx_init(MEDIACODEC) 失败: {GetErrorString(ret)} (code={ret})");

            // 4. 设置到编解码上下文（av_buffer_ref 增加引用计数）
            AVBufferRef* ctxRef = FF.av_buffer_ref(hwRef);
            if (ctxRef == null)
                throw new InvalidOperationException("av_buffer_ref 返回 null（内存不足）");
            ctx->hw_device_ctx = ctxRef;

            // 5. SafeHandle 管理 hwRef 生命周期
            _hwDeviceCtx = new SafeAVBufferRefHandle((IntPtr)hwRef);
        }
        catch
        {
            AVBufferRef* p = hwRef;
            FF.av_buffer_unref(&p);
            throw;
        }

        _logger.LogInformation("MediaCodec 硬件解码已初始化（{Mode}）",
            surface != IntPtr.Zero || nativeWindow != IntPtr.Zero ? "表面直渲染" : "缓冲模式");
    }

    /// <summary>获取 FFmpeg MediaCodec 专用解码器名（无对应硬解则返回 null）。</summary>
    private static string? GetMediaCodecDecoderName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264_mediacodec",
        VideoCodec.H265 => "hevc_mediacodec",
        VideoCodec.AV1 => "av1_mediacodec",
        VideoCodec.VP9 => "vp9_mediacodec",
        VideoCodec.MPEG2 => "mpeg2_mediacodec",
        VideoCodec.MPEG4 => "mpeg4_mediacodec",
        _ => null
    };
}
