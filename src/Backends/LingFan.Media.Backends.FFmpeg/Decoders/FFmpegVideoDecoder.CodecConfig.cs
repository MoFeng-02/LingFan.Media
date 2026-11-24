using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg.Interop;
namespace LingFan.Media.Backends.FFmpeg.Decoders;

// FFmpegVideoDecoder 的编解码私有配置段（部分类，由 Init 段再拆，编译期等价）。
// extradata 设置与 mp4toannexb 比特流过滤器安装；同步原生调用。
internal sealed partial class FFmpegVideoDecoder
{
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
    /// <c>av_freep(&amp;avctx->extradata)</c> 释放，须与分配器匹配。勿改用
    /// <see cref="Marshal.AllocHGlobal(nint)"/> + <see cref="Marshal.FreeHGlobal"/>——与 av_freep 分配器错配，
    /// 且退出时双重释放（先 HGlobal 释放、av_freep 再释放）→ 原生访问违例。</para>
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
