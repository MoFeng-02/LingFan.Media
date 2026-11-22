using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg.Interop;
using LingFan.Media.Backends.FFmpeg.Models;
using LingFan.Media.Backends.FFmpeg.SafeHandles;
namespace LingFan.Media.Backends.FFmpeg.Decoders;

// FFmpegVideoDecoder 的 Init 职责段（部分类，由 FFmpegVideoDecoder.Core.cs 再拆，编译期等价）。
// 解码器初始化/编解码私有配置/extradata 设置；同步原生调用。
internal sealed partial class FFmpegVideoDecoder
{
    /// <inheritdoc/>
    /// <remarks>同步参数化配置：avcodec_find_decoder + avcodec_alloc_context3 + avcodec_open2。</remarks>
    public unsafe void Initialize(VideoCodec codec, VideoSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            throw new InvalidOperationException("视频解码器已初始化");

        Codec = codec;
        _lastCodec = codec;
        _lastSettings = settings;
        AVCodecID codecId = MapVideoCodecToFFmpeg(codec);

        // 硬解开关是「两级与」：会话级 VideoSettings.EnableHardwareAcceleration
        // 与后端级 FFmpegOptions.HardwareAcceleration 任一为 false 即禁用。
        // 此前 FFmpegOptions.HardwareAcceleration 声明了却从未被任何代码读取——宿主写
        // AddFFmpeg(o => o.HardwareAcceleration = false) 毫无效果（静默失效）。
        // options 为 null（直接 new 解码器、未走 DI）时按 true 处理，保持既有行为。
        bool hwEnabled = settings.EnableHardwareAcceleration && (_options?.HardwareAcceleration ?? true);

        // Android MediaCodec 硬解必须使用专用解码器（h264_mediacodec 等）——
        // avcodec_find_decoder 默认返回软件解码器，对其挂 hw_device_ctx 无效
        AVCodec* avCodec = null;
        bool useMediaCodec = false;
        if (hwEnabled && OperatingSystem.IsAndroid())
        {
            string? mcName = GetMediaCodecDecoderName(codec);
            if (mcName is not null)
            {
                avCodec = FF.avcodec_find_decoder_by_name(mcName);
                useMediaCodec = avCodec != null;
                if (!useMediaCodec)
                    _logger.LogWarning("FFmpeg 未编译 MediaCodec 解码器 {Name}，回退到软件解码", mcName);
            }
        }

        // 查找解码器（软件路径 / MediaCodec 未命中回退）
        if (avCodec == null)
            avCodec = FF.avcodec_find_decoder(codecId);
        if (avCodec == null)
            throw new NotSupportedException($"FFmpeg 未找到视频解码器: {codec} (codec_id={codecId})");

        // 分配上下文
        AVCodecContext* ctx = FF.avcodec_alloc_context3(avCodec);
        if (ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 失败");

        // 建立流时间基：解码帧 pts/dts 以流 time_base 为单位，须由调用方写入 ctx->pkt_timebase。
        // 解码后 avFrame->time_base / ctx->time_base 常为 0，直接用其换算会使帧时间戳全 0。
        // 故用 demuxer 透传的流 time_base 建立 pkt_timebase，并以同一秒值做时间戳换算。
        _timeBase = settings.TimeBase;
        _tbSeconds = _timeBase.ToDouble();
        if (_timeBase.Denominator > 0)
        {
            AVRational tb = ctx->pkt_timebase;
            tb.num = _timeBase.Numerator;
            tb.den = _timeBase.Denominator;
            ctx->pkt_timebase = tb;
        }

        _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);

        // 应用编解码器私有配置（extradata）：MP4 中 HEVC/H264 为 length-prefixed，解码器需 hvcC/avcC 作为
        // extradata 才能解析参数集；并需 hevc_mp4toannexb/h264_mp4toannexb 比特流过滤器将包转为 Annex-B。
        ApplyCodecConfiguration(ctx, codec, codecId, settings.CodecConfiguration);

        // 重入/重播清理：释放上一轮 ffmpeg 自有 D3D11 设备与 NV12→RGBA 转换器（避免泄漏/双重初始化）。
        // 首次 Initialize 时下述字段均为默认空值，调用无副作用；Reset 全量重建时会先到这里释放旧设备。
        _nv12ToRgbaConverter?.Dispose();
        _nv12ToRgbaConverter = null;
        _vaOwnedDevice?.Dispose();
        _vaOwnedDevice = null;
        _vaOwnedContext?.Dispose();
        _vaOwnedContext = null;
        _vaDeviceHandle = IntPtr.Zero;
        _vaContextHandle = IntPtr.Zero;
        _nv12ConvertFailureLogged = false;

        // 配置硬件加速
        if (useMediaCodec)
        {
            // MediaCodec 硬解。宿主注入 Surface → 表面直渲染（零拷贝）；
            // 未注入 → 缓冲模式（ByteBuffer 输出 NV12 软件帧，仍为硬解）
            try
            {
                InitializeMediaCodec(ctx);
            }
            catch (Exception ex)
            {
                // hwdevice 初始化失败不致命：MediaCodec 解码器无 hw_device_ctx 时自动走缓冲模式
                _logger.LogWarning(ex, "MediaCodec 硬件设备上下文初始化失败，使用缓冲模式（仍为硬解）");
            }
            IsHardwareAccelerated = true;
        }
            else if (hwEnabled && _gpuContext is not null && _gpuContext.ApiType == GPUApiType.D3D11)
            {
                // D3D11VA 硬件解码——使用渲染器共享的 D3D11 设备
                // 零拷贝链路：硬解输出 ID3D11Texture2D → D3D11HardwareFrameResource → D3D11Renderer
                try
                {
                    // 渲染器共享设备场景用渲染器共享 D3D11 设备（InitializeD3D11VA 统一读 _vaDeviceHandle/_vaContextHandle）。
                    _vaDeviceHandle = _gpuContext.DeviceHandle;
                    _vaContextHandle = _gpuContext.ContextHandle;
                // 配套：硬解帧现在由 D3D11HardwareFrameResource 持引用保活切片（详见该类注释），
                //    管线在途帧数 = VideoPipeline 有界队列(5) + 渲染中(1) + 呈现完成待回收(1) ≈ 7。
                //    若不给 hw frames pool 留余量，解码器会因取不到空闲切片而 av_hwframe_get_buffer 失败
                //    → 解码停摆（表现为 present 计数卡死、画面冻结而音频照常前进）。
                //    extra_hw_frames 语义即「在解码器自身 DPB 需求之外，额外分配供调用方长期持有的帧数」。
                ctx->extra_hw_frames = D3D11VAExtraHwFrames;

                InitializeD3D11VA(ctx);
                IsHardwareAccelerated = true;
            }
            catch (Exception ex)
            {
                // 句柄为零 = GPU 设备上下文未就绪（调用方未先 InitializeAsync），不是初始化本身失败；
                // 两者都回退软件解码，但可据此判断是「没准备好」还是「真失败」。
                if (_vaDeviceHandle == IntPtr.Zero || _vaContextHandle == IntPtr.Zero)
                    _logger.LogWarning("D3D11VA 未能启用：GPU 设备上下文未就绪（设备或上下文句柄为零），回退到软件解码");
                else
                    _logger.LogWarning(ex, "D3D11VA 硬件解码初始化失败，回退到软件解码");

                IsHardwareAccelerated = false;
            }
        }
        else if (hwEnabled && OperatingSystem.IsLinux() && _vaApiExport is not null)
        {
            // Linux VAAPI 硬件解码（真实零拷贝路径）：
            // 有匹配 GPU 生产者(_gpuProducer) → VA Surface 经 dma_buf 零拷贝导入 GL/Vulkan；
            // 无生产者（如 Phase 1 无头探针）→ 硬解 + CPU 回读（仍属硬解，仅非零拷贝）。
            try
            {
                InitializeVAAPI(ctx);
                IsHardwareAccelerated = true;
                if (_gpuProducer is not null) _gpuImportMode = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VAAPI 硬件解码初始化失败，回退软件解码");
                IsHardwareAccelerated = false;
            }
        }
        else if (hwEnabled && _gpuContext is not null && _gpuContext.ApiType != GPUApiType.D3D11 && _gpuProducer is not null)
        {
            // GPU 零拷贝硬解：FFmpeg 仍走 D3D11VA 产出 D3D11 纹理（Windows），
            // 由匹配的渲染器生产者（Vulkan/OpenGL）经互操作导入为 GPU 纹理上屏（零拷贝，不回读 CPU）。
            // 依赖倒置：解码器只经 IGpuFrameProducer 抽象把原生句柄交给渲染器侧生产者。
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // 同 D3D11 分支：纹理数组须留余量供管线长期持有切片（见 D3D11VA 注释）。
                    ctx->extra_hw_frames = D3D11VAExtraHwFrames;
                    // ffmpeg 自有设备场景：渲染器为 GL/Vulkan，其 IGpuDeviceContext.DeviceHandle 是 GL/Vk 设备（非 ID3D11Device），
                    // 不可喂给 ffmpeg D3D11VA。故 ffmpeg 须【自有独立 D3D11 设备】做 D3D11VA→NV12，
                    // 转换/导出共享句柄亦用此同一设备；该设备由本解码器创建并持有（_vaOwnedDevice），Dispose 释放。
                    // 必须带 VideoSupport：NV12→RGBA 的 VideoProcessor 输出视图要求设备具视频能力，
                    //    缺该标志 CreateVideoProcessorOutputView 报 E_INVALIDARG。
                    // 独显优先（不绑定厂商/型号）：枚举 DXGI 选首选适配器
                    //    （跳过软件适配器，DedicatedVideoMemory 最大者；无独显退化为集显），
                    //    再在其上创建 D3D11 设备；未找到回退默认适配器（Hardware）。
                    IntPtr preferredAdapter = LingFan.Media.GPUShare.D3D11.D3D11Interop.FindPreferredAdapter();
                    if (preferredAdapter != IntPtr.Zero)
                    {
                        try
                        {
                            // GPUShare 手写 D3D11CreateDevice 在首选适配器上创建设备
                            //（DriverType=UNKNOWN + BgraSupport|VideoSupport，pAdapter 非空时 UNKNOWN 必选）。
                            LingFan.Media.GPUShare.D3D11.D3D11Interop.D3D11CreateDeviceOnAdapter(
                                preferredAdapter, out _vaDeviceHandle, out _vaContextHandle);
                            // Vortice 包装 GPUShare 创建的设备/上下文（创建引用=1，Dispose 时 Release 配对；
                            // 自有指针，Vortice Dispose 语义正确，无需 AddRef）。
                            _vaOwnedDevice = new Vortice.Direct3D11.ID3D11Device(_vaDeviceHandle);
                            _vaOwnedContext = new Vortice.Direct3D11.ID3D11DeviceContext(_vaContextHandle);
                            _logger.LogInformation("ffmpeg 自有 D3D11 设备创建: 首选适配器(独显优先)");
                        }
                        finally
                        {
                            System.Runtime.InteropServices.Marshal.Release(preferredAdapter);
                        }
                    }
                    else
                    {
                        _vaOwnedDevice = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                            Vortice.Direct3D.DriverType.Hardware,
                            Vortice.Direct3D11.DeviceCreationFlags.BgraSupport | Vortice.Direct3D11.DeviceCreationFlags.VideoSupport);
                        _vaOwnedContext = _vaOwnedDevice.ImmediateContext;
                        _vaDeviceHandle = _vaOwnedDevice.NativePointer;
                        _vaContextHandle = _vaOwnedContext.NativePointer;
                        _logger.LogInformation("ffmpeg 自有 D3D11 设备创建: 无首选适配器，回退默认适配器");
                    }
                    _vaDeviceHandle = _vaOwnedDevice.NativePointer;
                    _vaContextHandle = _vaOwnedContext.NativePointer;
                    // 打印自有设备 FeatureLevel（对照 D3D11RendererFactory 共享设备，格式支持集可能随 FeatureLevel 变化）。
                    _logger.LogInformation(
                        "ffmpeg 自有 D3D11 设备创建: FeatureLevel={FL}（对照 D3D11RendererFactory 共享设备）",
                        _vaOwnedDevice.FeatureLevel);
                    InitializeD3D11VA(ctx);
                    // 解码侧零拷贝统一路径：ffmpeg NV12 硬解帧经 VideoProcessorBlt 转 RGBA32，
                    // 导出共享 BGRA 句柄由 Vulkan/GL/D3D11 生产者导入零拷贝（全程 GPU 内、无 CPU 回读）。
                    // 用 ffmpeg 自有 D3D11 设备（非 _gpuContext），与 InitializeD3D11VA 同源。
                    // 构造失败会抛到下方 catch，回落软件解码（_gpuImportMode 保持 false）。
                    // 注：部分驱动/硬件拒绝「NV12 + 共享标志」纹理（NV12 共享导出不可移植），
                    // 故统一走 blt→RGBA 的共享 BGRA 路径（该组合已验证可用）。
                    _nv12ToRgbaConverter = new LingFan.Media.GPUShare.D3D11.D3D11Nv12ToRgbaConverter(
                        _vaDeviceHandle, _vaContextHandle);
                    _gpuImportMode = true;
                    IsHardwareAccelerated = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GPU 零拷贝（D3D11VA→GPU）初始化失败，回退软件解码");
                    IsHardwareAccelerated = false;
                }
            }
            else
            {
                // Linux/macOS：GPU 零拷贝需 VAAPI→GPU（VaApiInterop 为 Phase 2 桩，未实现）→ 软解。
                // 不进入 _gpuImportMode，避免运行时每帧都尝试不可用的导入。
                IsHardwareAccelerated = false;
                _logger.LogWarning(
                    "已请求 GPU 零拷贝，但当前平台不支持硬解→GPU 互操作（VAAPI 为 Phase 2），" +
                    "本次{Codec}将全程软件解码（CPU 拷贝）。", codec);
            }
        }
        else if (hwEnabled)
        {
            // 硬解已请求但拿不到可用 GPU 设备上下文 → 只能软解。
            // 此处**必须出声**：静默回落会让宿主以为「硬解优先」已生效，实则全程 CPU 解码 + CPU 拷贝，
            // 硬件彻底成摆设，且日志上毫无痕迹（与「带默认实现的接口成员漏转发=静默失效」同类反模式）。
            // 典型成因：只注册了 AddFFmpeg() + AddHeadlessRenderer()，而 IGpuDeviceContext 由
            // AddD3D11Renderer() 或 AddMediaFoundation()（自备窗口无关设备）注册——两者都没注册即为 null。
            IsHardwareAccelerated = false;
            if (_gpuContext is null)
                _logger.LogWarning(
                    "已请求硬件解码，但容器中没有 IGpuDeviceContext → D3D11VA 无法启用，本次{Codec}将全程软件解码（CPU 拷贝）。" +
                    "修法：注册 AddD3D11Renderer()（有头，与渲染器共享设备=零拷贝）或 AddMediaFoundation()（无头自备 D3D11 设备）。",
                    codec);
            else
                _logger.LogWarning(
                    "已请求硬件解码，但 IGpuDeviceContext.ApiType={Api} 非 D3D11 → D3D11VA 无法启用，本次{Codec}将全程软件解码（CPU 拷贝）。",
                    _gpuContext.ApiType, codec);
        }

        // 打开解码器
        int ret = FF.avcodec_open2(ctx, avCodec, null);
        if (ret < 0 && useMediaCodec)
        {
            // MediaCodec 打开失败（如宿主未调 MediaCodecInterop.SetJavaVM）→ 回退软件解码
            _logger.LogWarning("MediaCodec 解码器打开失败 ({Error})，回退到软件解码。" +
                "提示：宿主须先调用 MediaCodecInterop.SetJavaVM 注入 JavaVM", GetErrorString(ret));
            _hwDeviceCtx?.Dispose();
            _hwDeviceCtx = null;
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            IsHardwareAccelerated = false;

            avCodec = FF.avcodec_find_decoder(codecId);
            if (avCodec == null)
                throw new NotSupportedException($"FFmpeg 未找到视频解码器: {codec} (codec_id={codecId})");
            ctx = FF.avcodec_alloc_context3(avCodec);
            if (ctx == null)
                throw new InvalidOperationException("avcodec_alloc_context3 失败（软件回退）");
            _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);
            ret = FF.avcodec_open2(ctx, avCodec, null);
        }
        if (ret < 0 && IsHardwareAccelerated)
        {
            // D3D11VA 解码器打开失败（如驱动不支持该 profile 的硬件解码会话、
            // 共享设备在该分辨率下无法建立 DXVA 会话）——但软件解码器仍可用，干净回退，
            // 绝不抛异常阻断播放（与上方 MediaCodec 分支对称，落实「软解正确的逻辑」）。
            _logger.LogWarning("D3D11VA 解码器打开失败 ({Error})，回退到软件解码。", GetErrorString(ret));
            _hwDeviceCtx?.Dispose();
            _hwDeviceCtx = null;
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            IsHardwareAccelerated = false;

            avCodec = FF.avcodec_find_decoder(codecId);
            if (avCodec == null)
                throw new NotSupportedException($"FFmpeg 未找到视频解码器: {codec} (codec_id={codecId})");
            ctx = FF.avcodec_alloc_context3(avCodec);
            if (ctx == null)
                throw new InvalidOperationException("avcodec_alloc_context3 失败（软件回退）");
            _codecContextHandle = new SafeAVCodecContextHandle((IntPtr)ctx);
            ret = FF.avcodec_open2(ctx, avCodec, null);
        }
        if (ret < 0)
        {
            _codecContextHandle.Dispose();
            _codecContextHandle = null;
            throw new InvalidOperationException($"avcodec_open2 失败: {GetErrorString(ret)} (code={ret})");
        }

        _initialized = true;
        // 首启也打印前 4 帧 pts（此前仅 Reset() 后触发）——CPU 回落链路的时间戳健康度直接可见。
        _decodeDiagCount = 4;
        _logger.LogInformation("视频解码器初始化: {Codec}, 硬件加速={HwAccel}", codec, IsHardwareAccelerated);
    }

    /// <summary>
    /// 应用编解码器私有配置到解码上下文：设置 <c>extradata</c>，并对 MP4 中的 HEVC/H264 安装
    /// <c>mp4toannexb</c> 比特流过滤器（将 length-prefixed 包转为 Annex-B 起始码格式）。
    /// </summary>
    /// <remarks>
    /// <para>MP4 容器中的 HEVC/H264 样本为长度前缀格式，ffmpeg 解码器需 Annex-B（起始码）才能解析 NAL 单元；
    /// 缺少 extradata 与转换会报「No start code is found. Error splitting the input into NAL units.」。</para>
    /// <para>par_in->extradata 用 ffmpeg 分配器（<c>av_malloc</c>）分配，因 <c>av_bsf_free</c> 经
    /// <c>avcodec_parameters_free→av_freep</c> 释放，须与分配器匹配，避免 <c>Marshal.AllocHGlobal</c> 堆损坏。</para>
    /// </remarks>
    private unsafe void ApplyCodecConfiguration(AVCodecContext* ctx, VideoCodec codec, AVCodecID codecId, ReadOnlyMemory<byte> cfg)
    {
        // 记录参数供 Reset() 重播/seek 重建 BSF（必须从原始 HVCC/avcC extradata 重建，不能沿用 Annex-B 版）
        _bsfCodec = codec;
        _bsfCodecId = codecId;
        _bsfCfg = cfg;

        if (cfg.IsEmpty)
            return;

        bool needBsf = codec == VideoCodec.H264 || codec == VideoCodec.H265;
        if (!needBsf)
        {
            SetExtradata(ctx, cfg);
            return;
        }

        string filterName = codec == VideoCodec.H265 ? "hevc_mp4toannexb" : "h264_mp4toannexb";
        AVBitStreamFilter* filter = FF.av_bsf_get_by_name(filterName);
        if (filter == null)
        {
            _logger.LogWarning("未找到比特流过滤器 {Filter}，回退仅设置 extradata（HEVC/H264 可能仍无法解码）", filterName);
            SetExtradata(ctx, cfg);
            return;
        }

        AVBSFContext* bsfCtx = null;
        int ret = FF.av_bsf_alloc(filter, &bsfCtx);
        if (ret < 0 || bsfCtx == null)
        {
            _logger.LogWarning("av_bsf_alloc 失败 ({Error})，回退仅设置 extradata", GetErrorString(ret));
            SetExtradata(ctx, cfg);
            return;
        }

        ((AVCodecParameters*)bsfCtx->par_in)->codec_id = codecId;
        ((AVCodecParameters*)bsfCtx->par_in)->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;

        // 用 ffmpeg 分配器拷贝 extradata（av_bsf_free 释放时须同分配器）。
        int size = cfg.Length;
        int padded = size + 64;
        byte* edata = (byte*)FF.av_malloc((UIntPtr)padded);
        if (edata == null)
        {
            FF.av_bsf_free(&bsfCtx);
            SetExtradata(ctx, cfg);
            return;
        }
        cfg.Span.CopyTo(new Span<byte>(edata, size));
        new Span<byte>(edata + size, 64).Clear();
        ((AVCodecParameters*)bsfCtx->par_in)->extradata = edata;
        ((AVCodecParameters*)bsfCtx->par_in)->extradata_size = size;

        ret = FF.av_bsf_init(bsfCtx);
        if (ret < 0)
        {
            _logger.LogWarning("av_bsf_init 失败 ({Error})，回退仅设置 extradata", GetErrorString(ret));
            FF.av_bsf_free(&bsfCtx);
            SetExtradata(ctx, cfg);
            return;
        }

        // 解码器 extradata 必须与「经 BSF 转换后的包格式」配套，绝不能沿用原始 hvcC/avcC：
        //   ffmpeg 的 hevc/h264 解码器按 extradata 首字节判定码流格式 —— hvcC/avcC（首字节 0x01）
        //   会令 is_nalff / is_avc = 1，解码器随后调用 ff_h2645_packet_split 按「长度前缀」拆 NAL；
        //   而包经 mp4toannexb 之后已是 Annex-B 起始码格式 ⇒ 两者矛盾，解码依旧失败
        //   （表现仍是 "No start code is found. / Error splitting the input into NAL units."）。
        //   av_bsf_init 之后 par_out->extradata 即为 BSF 产出的 Annex-B 参数集（VPS/SPS/PPS），
        //   用它设置解码器才自洽。par_out 为空属理论异常，回退原始 cfg 并告警。
        AVCodecParameters* parOut = (AVCodecParameters*)bsfCtx->par_out;
        if (parOut != null && parOut->extradata != null && parOut->extradata_size > 0)
        {
            SetExtradata(ctx, new ReadOnlySpan<byte>(parOut->extradata, parOut->extradata_size).ToArray());
        }
        else
        {
            _logger.LogWarning("{Filter} 的 par_out->extradata 为空，回退使用原始 hvcC/avcC extradata（解码可能失败）", filterName);
            SetExtradata(ctx, cfg);
        }

        _bsfContext = bsfCtx;
        _logger.LogDebug("已安装 {Filter} 比特流过滤器（MP4→Annex-B，解码器 extradata={Size}B）",
            filterName, ctx->extradata_size);
    }

    /// <summary>
    /// 将编解码器私有配置写入 <c>ctx->extradata</c>（含 64 字节零填充，符合 ffmpeg 要求）。
    /// </summary>
    /// <remarks>
    /// <para>缓冲必须用 <c>av_malloc</c> 分配：<c>avcodec_free_context</c> 经
    /// <c>av_freep(&amp;avctx->extradata)</c> 释放，须与分配器匹配。此前用
    /// <see cref="Marshal.AllocHGlobal"/> + <see cref="Marshal.FreeHGlobal"/> 的组合在退出时
    /// 双重释放（先 HGlobal 释放、av_freep 再释放）且分配器错配 → 原生访问违例。</para>
    /// <para>重播重建 BSF 时先经 <c>av_free</c> 释放旧缓冲（同分配器）。</para>
    /// </remarks>
    private unsafe void SetExtradata(AVCodecContext* ctx, ReadOnlyMemory<byte> cfg)
    {
        int size = cfg.Length;
        if (size <= 0)
            return;
        int padded = size + 64;
        byte* buf = (byte*)FF.av_malloc((UIntPtr)padded);
        if (buf == null)
            return;
        cfg.Span.CopyTo(new Span<byte>(buf, size));
        new Span<byte>(buf + size, 64).Clear();
        if (_extradataBuffer != IntPtr.Zero)
            FF.av_free((void*)_extradataBuffer);   // 重播重建 BSF 时先释放旧缓冲，避免泄漏
        _extradataBuffer = (IntPtr)buf;
        ctx->extradata = buf;
        ctx->extradata_size = size;
    }
}
