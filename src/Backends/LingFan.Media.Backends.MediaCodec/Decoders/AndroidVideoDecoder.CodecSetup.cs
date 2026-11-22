using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Java.Nio;
using LingFan.Media.GPUShare.Android; // AndroidHardwareBufferFrameResource（跨 GPU API 中立帧 DTO，消费方向：Backends→GPUShare.Android）
using LingFan.Media.GPUShare.Android.Bridge; // AndroidAhbRgbaBridge（解码侧 GLES/EGL→AHB 桥接，GPUShare.Android android 资产）
using AndroidMediaCodec = Android.Media.MediaCodec;
using PixelFormat = LingFan.Media.Abstractions.PixelFormat;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

// AndroidVideoDecoder 的 CodecSetup 职责段（部分类，由 AndroidVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeAsync/FlushAsync/FeedInput/ReadOutput/DrainOutput/Dispose）在主文件；
// MediaCodec 双路径（Surface/AHB 零拷贝 + ByteBuffer I420）解码。
internal sealed partial class AndroidVideoDecoder
{
    /// <summary>以「灵活 YUV420 + 显式尺寸」配置 ByteBuffer 输出；被拒时降级到软件解码器重试一次。</summary>
    /// <remarks>显式 width/height 用容器声明值：硬件解码器普遍拒绝 csd-only 配置，而真实可见尺寸
    /// 由输出格式的 crop 矩形给出（见 <see cref="ReadOutputFormat"/>），故此处无需也不应依赖
    /// 手写 SPS 解析（该解析器已证伪，会把 1080x1920 解成 16x32）。</remarks>
    private void ConfigureFlexibleYuv(ref AndroidMediaCodec codecObj, string mime, VideoCodec codec,
        ReadOnlyMemory<byte> csd, int frameW, int frameH, Surface? outputSurface, bool useAhbFrames)
    {
        // csd 拆分诊断（在设备上核对 SPS/PPS 切分是否正确；拆不出 PPS 会回退单 csd-0 旧形态）。
        byte[] spsB = Array.Empty<byte>(), ppsB = Array.Empty<byte>();
        bool splitOk = mime == "video/avc" && TrySplitAvcCsd(csd, out spsB, out ppsB) && ppsB.Length > 0;
        _logger.LogInformation("[ANDROID-VID] csd 拆分: 成功={Ok} SPS={SpsLen}B PPS={PpsLen}B（总 {Total}B）",
            splitOk, splitOk ? spsB.Length : csd.Length, splitOk ? ppsB.Length : 0, csd.Length);

        try
        {
            codecObj.Configure(BuildFormat(mime, csd, frameW, frameH, useAhbFrames),
                useAhbFrames ? outputSurface : null, null, 0);
            return;
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
        {
            _logger.LogWarning("[ANDROID-VID] 解码器 {Name} 配置被拒（{Reason}），降级软件解码器重试",
                _codecName, ex.Message);
        }

        // 硬件解码器拒绝该配置：换软件解码器（c2/OMX 阶梯）重试。软件路径不支持 AHB 零拷贝，
        // 释放桥接 Surface 并回退 ByteBuffer CPU 路径（useAhbFrames=false）。
        codecObj.Release();
        _bridge?.Dispose();
        _bridge = null;
        _outputSurface = null;
        _useAhbFrames = false;
        codecObj = CreateVideoCodec(mime, codec, preferSoftwareDecoder: true);
        codecObj.Configure(BuildFormat(mime, csd, frameW, frameH, false), null, null, 0);
    }

    /// <summary>构造解码器输入格式（每次 configure 需新实例：MediaFormat 被 configure 消费后不应复用）。
    /// 输出到 Surface（AHB 零拷贝路径）时颜色格式由 Surface 决定，不强制灵活 YUV420；回退 ByteBuffer 路径时才设。</summary>
    private static MediaFormat BuildFormat(string mime, ReadOnlyMemory<byte> csd, int frameW, int frameH, bool useAhbFrames)
    {
        var fmt = new MediaFormat();
        fmt.SetString(MediaFormat.KeyMime, mime);
        if (frameW > 0) fmt.SetInteger(MediaFormat.KeyWidth, frameW);
        if (frameH > 0) fmt.SetInteger(MediaFormat.KeyHeight, frameH);
        // 仅 ByteBuffer 回退路径强制灵活 YUV420（令 getOutputImage 返回标准化三平面，屏蔽厂商私有布局差异）；
        // Surface 输出路径（AHB 零拷贝）颜色格式由 Surface 决定，无需也不能设 KeyColorFormat。
        if (!useAhbFrames)
            fmt.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatyuv420flexible);
        // H.264/5 参数集必须按 NAL 类型拆分为 csd-0(SPS)/csd-1(PPS)（AOSP MediaCodec 契约）。
        // 全部塞进单个 csd-0 时 Surface 输出模式的解码器会吞输入不吐输出（画面冻结；
        // ByteBuffer 模式部分解码器自行重解析故未暴露）。解析失败时保守回退单 csd-0。
        if (csd.Length > 0)
        {
            if (mime == "video/avc" && TrySplitAvcCsd(csd, out var sps, out var pps) && pps.Length > 0)
            {
                fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(sps));
                fmt.SetByteBuffer("csd-1", ByteBuffer.Wrap(pps));
            }
            else
            {
                fmt.SetByteBuffer("csd-0", ByteBuffer.Wrap(csd.ToArray()));
            }
        }
        // 显式输入缓冲上限：解码器按 SPS 推出的 max-input-size 可能过小，大关键帧会被截断喂入 → 码流损坏。
        fmt.SetInteger(MediaFormat.KeyMaxInputSize, 2 << 20);
        return fmt;
    }

    /// <summary>把 Annex-B 起始码格式的 AVC 参数集拆为 SPS（csd-0）与 PPS（csd-1）两部分。</summary>
    /// <remarks>按 0x000001/0x0000000001 起始码切出 NAL，SPS(nalType 7) 与前置 SEI(6) 归 csd-0，
    /// PPS(nalType 8) 归 csd-1；各段保留自身起始码（解码器需要）。无法切出 PPS 时返回 false。</remarks>
    private static bool TrySplitAvcCsd(ReadOnlyMemory<byte> csd, out byte[] sps, out byte[] pps)
    {
        sps = Array.Empty<byte>();
        pps = Array.Empty<byte>();
        var data = csd.Span;
        var spsRanges = new List<(int Start, int Length)>();
        var ppsRanges = new List<(int Start, int Length)>();
        int i = 0;
        while (i + 4 <= data.Length)
        {
            // 找起始码（3 或 4 字节）
            int sc;
            if (i + 3 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) sc = 3;
            else if (i + 4 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) sc = 4;
            else { i++; continue; }

            int nalStart = i + sc;
            if (nalStart >= data.Length) break;
            byte nalType = (byte)(data[nalStart] & 0x1F);

            // 找下一个起始码（或结尾）
            int j = nalStart + 1;
            int nalEnd = data.Length;
            while (j + 3 <= data.Length)
            {
                if (data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 1) { nalEnd = j; break; }
                if (j + 4 <= data.Length && data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 0 && data[j + 3] == 1) { nalEnd = j; break; }
                j++;
            }

            if (nalType == 7) spsRanges.Add((i, nalEnd - i));
            else if (nalType == 8) ppsRanges.Add((i, nalEnd - i));

            i = nalEnd;
        }

        if (spsRanges.Count == 0 || ppsRanges.Count == 0)
            return false;

        sps = JoinRanges(data, spsRanges);
        pps = JoinRanges(data, ppsRanges);
        return true;
    }

    /// <summary>把 (偏移,长度) 列表对应的段拼接为一个数组（各段含起始码）。</summary>
    private static byte[] JoinRanges(ReadOnlySpan<byte> data, List<(int Start, int Length)> ranges)
    {
        int total = 0;
        foreach (var (_, len) in ranges) total += len;
        var result = new byte[total];
        int pos = 0;
        foreach (var (start, len) in ranges)
        {
            data.Slice(start, len).CopyTo(result.AsSpan(pos));
            pos += len;
        }
        return result;
    }

    /// <summary>尝试建立 GLES/EGL 桥接的 AHB 输出 Surface（GPU 零拷贝前置）：构造
    /// <see cref="AndroidAhbRgbaBridge"/> 并初始化 EGL/GLES 上下文、SurfaceTexture 与输出 Surface。
    /// 失败（API&lt;29 或 EGL/GLES 不可用）则回退 ByteBuffer CPU 路径，返回 false。</summary>
    /// <remarks>DIP：桥接仅依赖 Abstractions + GPUShare.Android，本解码器不引用任何 Renderer 或图形 API 绑定。
    /// 桥接初始化抛 <see cref="NotSupportedException"/> 视为「环境不支持」，catch 回退，不影响 ByteBuffer 主路径。</remarks>
    private bool TryCreateAhbOutputSurface(int frameW, int frameH)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            _logger.LogTrace("[ANDROID-AHB-DEC] API<29，GLES 桥接不可用，回退 ByteBuffer CPU 路径。");
            return false;
        }
        try
        {
            _bridge = new AndroidAhbRgbaBridge(frameW, frameH, _logger);
            _bridge.Initialize(); // 失败抛 NotSupportedException（EGL/GLES 配置缺失）
            _outputSurface = _bridge.OutputSurface;
            _logger.LogInformation(
                "[ANDROID-AHB-DEC] GLES/EGL 桥接 Surface 就绪（{W}x{H}），零拷贝路径启用。",
                _bridge.FrameWidth, _bridge.FrameHeight);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ANDROID-AHB-DEC] GLES 桥接初始化失败，回退 ByteBuffer CPU 路径。");
            _bridge?.Dispose();
            _bridge = null;
            _outputSurface = null;
            return false;
        }
    }

    /// <summary>创建视频解码器。默认按 MIME 取系统首选（通常为硬件解码器，实时性最佳）；
    /// <paramref name="preferSoftwareDecoder"/> 为真时走 AOSP 软件解码器阶梯
    /// （<see cref="SoftwareCodecCandidates"/>，c2 新栈优先、OMX.google 旧栈兜底）→ 按类型任选。</summary>
    /// <remarks>ByteBuffer 输出对硬件解码器同样安全：灵活 YUV420 由平台契约保证全解码器支持，
    /// 且等价于灵活格式的厂商私有格式仍可经 getOutputImage 取到标准化平面。</remarks>
    private AndroidMediaCodec CreateVideoCodec(string mime, VideoCodec codec, bool preferSoftwareDecoder)
    {
        // 开关状态必须可见：环境变量注入（adb shell setprop debug.mono.env）可能因构建配置失效，
        // 若不打印就无法区分「开关没生效」与「软解也坏帧」——两者结论完全相反。
        _logger.LogInformation(
            "[ANDROID-VID] 解码策略: 强制软解={Sw} 优先Codec2={C2} 桥接零拷贝={Zc}（本次请求={ReqSoft}）",
            AndroidVideoDecodePolicy.ForceSoftwareDecoder,
            AndroidVideoDecodePolicy.PreferCodec2HardwareDecoder,
            AndroidVideoDecodePolicy.EnableHardwareZeroCopy,
            preferSoftwareDecoder);

        if (preferSoftwareDecoder)
        {
            // 软解候选顺序按输出模式区分（实测两模式下症状相反）：
            // · Surface 输出（AHB 零拷贝桥接）：c2.android 优先。OMX.google 旧组件在
            //   SurfaceTexture/GL 消费者模式下吞输入不吐输出（产帧数次后永久停摆，
            //   画面冻结在首帧附近、音频正常播完）。
            // · ByteBuffer 输出（CPU 路径）：OMX.google 优先。c2 软解 + ByteBuffer 命中
            //   numClientBuffers 僵死（解码器不归还输出缓冲）。
            var candidates = _useAhbFrames
                ? SoftwareCodecCandidatesSurfaceFirst(codec)
                : SoftwareCodecCandidates(codec);
            foreach (string name in candidates)
            {
                try
                {
                    var sw = AndroidMediaCodec.CreateByCodecName(name);
                    _codecName = name;
                    _hardwareDecoder = false;
                    return sw;
                }
                catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException)
                {
                    // 该软解在本机不存在（旧/裁剪机型）：试下一个，最后按类型任选。
                }
            }
        }

        // 优先挑 Codec2 栈的厂商硬解（如 c2.qti.avc.decoder），而不是系统按类型默认给出的
        // 旧 OMX 实现（OMX.qcom.video.decoder.avc）。后者在 ByteBuffer + 灵活 YUV 输出下存在开播期
        // 输出坏帧现象（宏块 garbage、参考帧损坏），且不报任何错误。Codec2 实现是同一硬件的
        // 另一套框架封装，可作为低风险对照。任何一步失败都回落到原来的 CreateDecoderByType。
        var hw = TryCreatePreferredHardwareCodec(mime);
        if (hw is not null)
            return hw;

        var obj = AndroidMediaCodec.CreateDecoderByType(mime);
        _codecName = obj.Name ?? "unknown";
        // 名称约定判定软/硬（CodecInfo.IsSoftwareOnly 为 API 29+；名称前缀是全版本可用的稳定判据）。
        _hardwareDecoder = !(_codecName.StartsWith("c2.android.", StringComparison.Ordinal)
            || _codecName.StartsWith("OMX.google.", StringComparison.Ordinal));
        return obj;
    }

    /// <summary>
    /// 枚举本机支持该 mime 的解码器，按「Codec2 厂商硬解 → OMX 厂商硬解」顺序创建（跳过软件实现）。
    /// 同时打印全部候选名，便于在设备上对照。失败返回 <c>null</c>，调用方回落到按类型创建。
    /// </summary>
    private AndroidMediaCodec? TryCreatePreferredHardwareCodec(string mime)
    {
        if (!AndroidVideoDecodePolicy.PreferCodec2HardwareDecoder)
            return null;
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return null; // MediaCodecList(MediaCodecListKind) 为 API 26+

        List<string> c2 = new(), omx = new();
        try
        {
            var list = new Android.Media.MediaCodecList(Android.Media.MediaCodecListKind.AllCodecs);
            var infos = list.GetCodecInfos();
            if (infos is null)
                return null;
            foreach (var info in infos)
            {
                if (info is null || info.IsEncoder)
                    continue;
                string? name = info.Name;
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!info.GetSupportedTypes().Contains(mime))
                    continue;
                // 软解实现（AOSP 自带）不作为硬解候选。
                if (name.StartsWith("c2.android.", StringComparison.Ordinal)
                    || name.StartsWith("OMX.google.", StringComparison.Ordinal))
                    continue;
                if (name.StartsWith("c2.", StringComparison.Ordinal))
                    c2.Add(name);
                else if (name.StartsWith("OMX.", StringComparison.Ordinal))
                    omx.Add(name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ANDROID-VID] 解码器清单枚举失败，回落按类型创建。");
            return null;
        }

        _logger.LogInformation("[ANDROID-VID] 解码器候选: c2硬解=[{C2}] omx硬解=[{Omx}]",
            c2.Count == 0 ? "无" : string.Join(",", c2),
            omx.Count == 0 ? "无" : string.Join(",", omx));

        foreach (string name in c2.Concat(omx))
        {
            try
            {
                var c = AndroidMediaCodec.CreateByCodecName(name);
                _codecName = name;
                _hardwareDecoder = true;
                return c;
            }
            catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException
                                          or Java.Lang.IllegalStateException
                                          or Java.IO.IOException)
            {
                _logger.LogWarning("[ANDROID-VID] 解码器 {Name} 创建失败，试下一个：{Msg}", name, ex.Message);
            }
        }
        return null;
    }

    /// <summary>AOSP 软件解码器候选。
    /// <b>OMX.google 排在 c2.android 之前</b>：实测 c2 软解 + ByteBuffer 会命中
    /// numClientBuffers 僵死（解码器不再归还输出缓冲），而 OMX.google 软解 + ByteBuffer 正常。
    /// 两者输出都是标准灵活 YUV 到 ByteBuffer，语义等价。</summary>
    private static string[] SoftwareCodecCandidates(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => new[] { "OMX.google.h264.decoder", "c2.android.avc.decoder" },
        VideoCodec.H265 => new[] { "OMX.google.hevc.decoder", "c2.android.hevc.decoder" },
        VideoCodec.AV1 => new[] { "c2.android.av1.decoder" },
        VideoCodec.VP9 => new[] { "c2.android.vp9.decoder", "OMX.google.vp9.decoder" },
        VideoCodec.MPEG2 => new[] { "c2.android.mpeg2.decoder", "OMX.google.mpeg2.decoder" },
        _ => new[] { "OMX.google.h264.decoder", "c2.android.avc.decoder" }
    };

    /// <summary>Surface 输出（AHB 零拷贝桥接）的 AOSP 软件解码器候选：<b>c2 新栈优先</b>。
    /// OMX.google 旧组件在 SurfaceTexture/GL 消费者模式下会吞输入不吐输出（产帧数次后永久停摆）；
    /// c2.android 是 ExoPlayer 等主流播放器 Surface 模式的标准软解路径。</summary>
    private static string[] SoftwareCodecCandidatesSurfaceFirst(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => new[] { "c2.android.avc.decoder", "OMX.google.h264.decoder" },
        VideoCodec.H265 => new[] { "c2.android.hevc.decoder", "OMX.google.hevc.decoder" },
        VideoCodec.AV1 => new[] { "c2.android.av1.decoder" },
        VideoCodec.VP9 => new[] { "c2.android.vp9.decoder", "OMX.google.vp9.decoder" },
        VideoCodec.MPEG2 => new[] { "c2.android.mpeg2.decoder", "OMX.google.mpeg2.decoder" },
        _ => new[] { "c2.android.avc.decoder", "OMX.google.h264.decoder" }
    };

    private void ReadOutputFormat(MediaFormat fmt)
    {
        // 帧由 ExtractI420FromImage 按 plane 的 pixelStride/rowStride 统一产出标准紧凑 I420，
        // 与底层是 NV12/semiplanar 还是 I420/planar 无关（灵活 YUV420 二者皆可能）。

        // 可见区（crop 矩形）
        // 平台契约：输出格式的 width/height 是「视频帧」尺寸（常按 16 对齐做了填充），真正的可见图像
        // 只占其中一部分，由 crop 矩形界定，且右/下坐标是「减 1」语义：
        //   width  = crop-right  + 1 - crop-left
        //   height = crop-bottom + 1 - crop-top
        // 键不存在时视频占满整帧。常量 KEY_CROP_* 直到 API 33 才公开，但字符串键自 Lollipop 起即有效，
        // 故统一用字符串键读取以覆盖全版本。
        int fw = fmt.ContainsKey(MediaFormat.KeyWidth) ? fmt.GetInteger(MediaFormat.KeyWidth) : 0;
        int fh = fmt.ContainsKey(MediaFormat.KeyHeight) ? fmt.GetInteger(MediaFormat.KeyHeight) : 0;
        int cl = 0, ctop = 0, vw = fw, vh = fh;
        if (fmt.ContainsKey("crop-left") && fmt.ContainsKey("crop-right"))
        {
            cl = fmt.GetInteger("crop-left");
            vw = fmt.GetInteger("crop-right") + 1 - cl;
        }
        if (fmt.ContainsKey("crop-top") && fmt.ContainsKey("crop-bottom"))
        {
            ctop = fmt.GetInteger("crop-top");
            vh = fmt.GetInteger("crop-bottom") + 1 - ctop;
        }
        if (vw > 0 && vh > 0)
        {
            _cropLeft = cl;
            _cropTop = ctop;
            _visibleWidth = vw;
            _visibleHeight = vh;
        }

        // 显示旋转（KEY_ROTATION = "rotation-degrees"，由 MediaExtractor 从容器 tkhd 旋转矩阵填入输出格式）。
        // 平台契约：MediaCodec 输出 buffer 的 width/height 永远是「旋转前（编码）」尺寸，真实显示尺寸
        // 在 90/270° 时需交换宽高。当前 VideoFrame 不携带 rotation，渲染端按编码尺寸呈现——
        // 若视频带 90/270° 旋转（竖屏拍摄的横屏内容），将出现方向错乱/溢出观感。此处先读取诊断，
        // 供设备上确认旋转角度，再决定是否在解码端交换显示宽高 + 透传 rotation 给渲染端旋转。
        // 注：KEY_ROTATION 常量 MS Learn 标注仅 API 23+ 受支持（CA1416），故统一用字符串键读取以覆盖全版本。
        int rotationDeg = fmt.ContainsKey("rotation-degrees") ? fmt.GetInteger("rotation-degrees") : 0;
        _rotationDegrees = rotationDeg;

        // 色彩空间（可选键，API 24+）：渲染端据以选择 YUV→RGB 矩阵。低版本/缺失时回退 Unspecified。
        int cs = -1, cr = -1, ctr = -1;
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
        {
            if (fmt.ContainsKey(MediaFormat.KeyColorStandard)) cs = fmt.GetInteger(MediaFormat.KeyColorStandard);
            if (fmt.ContainsKey(MediaFormat.KeyColorRange)) cr = fmt.GetInteger(MediaFormat.KeyColorRange);
            if (fmt.ContainsKey(MediaFormat.KeyColorTransfer)) ctr = fmt.GetInteger(MediaFormat.KeyColorTransfer);
        }
        _colorInfo = new VideoColorInfo(
            AndroidCodecMaps.ColorStandardFromNdk(cs),
            AndroidCodecMaps.ColorRangeFromNdk(cr),
            AndroidCodecMaps.ColorTransferFromNdk(ctr));

        _logger.LogInformation(
            "[ANDROID-VID] 输出格式: 帧={FW}x{FH} 可见={VW}x{VH} crop=({CL},{CT}) 旋转={Rot}° 色彩={Color}",
            fw, fh, _visibleWidth, _visibleHeight, _cropLeft, _cropTop, _rotationDegrees, _colorInfo);
    }
}
