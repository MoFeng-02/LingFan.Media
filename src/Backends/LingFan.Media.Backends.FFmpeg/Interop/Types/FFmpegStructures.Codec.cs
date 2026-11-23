using System.Runtime.InteropServices;

#pragma warning disable CS0649 // interop 结构体字段由原生代码写入，C# 从不赋值

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// 编解码参数（严格对齐 libavcodec/codec_par.h，FFmpeg 5.0+ 用 ch_layout）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVCodecParameters
{
    public AVMediaType codec_type;
    public AVCodecID codec_id;
    public uint codec_tag;
    public byte* extradata;            // uint8_t*（offset 16）
    public int extradata_size;
    public IntPtr coded_side_data;     // AVPacketSideData*
    public int nb_coded_side_data;
    public int format;                 // AVPixelFormat | AVSampleFormat
    public long bit_rate;
    public int bits_per_coded_sample;
    public int bits_per_raw_sample;
    public int profile;
    public int level;
    public int width;
    public int height;
    public AVRational sample_aspect_ratio;
    public AVRational framerate;
    public int field_order;
    public int color_range;
    public int color_primaries;
    public int color_trc;
    public int colorspace;
    public int chroma_location;
    public int video_delay;
    public AVChannelLayout ch_layout;
    public int sample_rate;
    public int block_align;
    public int frame_size;
    public int initial_padding;
    public int trailing_padding;
    public int seek_preroll;
    public int alpha_mode;
}

/// <summary>
/// 比特流过滤器上下文（严格对齐 libavcodec/avcodec.h；
/// av_class / internal / filter 之后即为 par_in / par_out）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVBSFContext
{
    public IntPtr av_class;
    public IntPtr @internal;           // AVBSFInternal*
    public IntPtr filter;              // const AVBitStreamFilter*
    public IntPtr par_in;              // AVCodecParameters*
    public IntPtr par_out;             // AVCodecParameters*
}

/// <summary>
/// 编解码器（不透明占位；本后端仅持有指针并传给 alloc/open，从不读取字段）。
/// </summary>
internal struct AVCodec
{
}

/// <summary>
/// 字幕（严格对齐 libavcodec/avcodec.h）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVSubtitle
{
    public ushort format;
    public uint start_display_time;
    public uint end_display_time;
    public uint num_rects;
    public IntPtr rects;               // AVSubtitleRect**
    public long pts;
}

/// <summary>
/// 字幕矩形（严格对齐 libavcodec/avcodec.h）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVSubtitleRect
{
    public int x;
    public int y;
    public int w;
    public int h;
    public int nb_colors;
    public PtrArray4 data;             // uint8_t* [4]
    public fixed int linesize[4];      // int [4]
    public int flags;
    public int type;                   // enum AVSubtitleType
    public IntPtr text;                // char*
    public IntPtr ass;                 // char*
}

/// <summary>
/// 编解码上下文（严格对齐 libavcodec/avcodec.h，FFmpeg 5.0+ 布局）。
/// 仅镜像至 ch_layout（读取止于此），其后字段（frame_size 等）不读，截断安全。
/// intra_dc_precision 等 FF_API 字段在 5.0+ 已移除，按当前 ABI 省略。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVCodecContext
{
    public IntPtr av_class;
    public int log_level_offset;
    public AVMediaType codec_type;
    public IntPtr codec;               // const AVCodec*
    public AVCodecID codec_id;
    public uint codec_tag;
    public IntPtr priv_data;
    public IntPtr p_internal;         // AVCodecInternal*
    public IntPtr opaque;
    public long bit_rate;
    public int flags;
    public int flags2;
    public byte* extradata;            // uint8_t*（offset 72）
    public int extradata_size;
    public AVRational time_base;
    public AVRational pkt_timebase;
    public AVRational framerate;
    public int delay;
    public int width;
    public int height;
    public int coded_width;
    public int coded_height;
    public AVRational sample_aspect_ratio;
    public AVPixelFormat pix_fmt;
    public AVPixelFormat sw_pix_fmt;
    public int color_primaries;
    public int color_trc;
    public int colorspace;
    public int color_range;
    public int chroma_sample_location;
    public int field_order;
    public int refs;
    public int has_b_frames;
    public int slice_flags;
    public IntPtr draw_horiz_band;     // 函数指针
    public IntPtr get_format;          // 函数指针
    public int max_b_frames;
    public float b_quant_factor;
    public float b_quant_offset;
    public float i_quant_factor;
    public float i_quant_offset;
    public float lumi_masking;
    public float temporal_cplx_masking;
    public float spatial_cplx_masking;
    public float p_masking;
    public float dark_masking;
    public int nsse_weight;
    public int me_cmp;
    public int me_sub_cmp;
    public int mb_cmp;
    public int ildct_cmp;
    public int dia_size;
    public int last_predictor_count;
    public int me_pre_cmp;
    public int pre_dia_size;
    public int me_subpel_quality;
    public int me_range;
    public int mb_decision;
    public IntPtr intra_matrix;        // uint16_t*
    public IntPtr inter_matrix;        // uint16_t*
    public IntPtr chroma_intra_matrix; // uint16_t*
    public int mb_lmin;
    public int mb_lmax;
    public int bidir_refine;
    public int keyint_min;
    public int gop_size;
    public int mv0_threshold;
    public int slices;
    public int sample_rate;
    public AVSampleFormat sample_fmt;
    public AVChannelLayout ch_layout;  // 音频布局（5.0+ 取代 channel_layout，offset 352）
    // 以下字段严格对齐 AutoGen 8.1.0 权威偏移（avcodec.h），顺序补全至 extra_hw_frames@572：
    public int frame_size;             // @376
    public int block_align;            // @380
    public int cutoff;                 // @384
    public int audio_service_type;     // @388 (AVAudioServiceType)
    public int request_sample_fmt;     // @392 (AVSampleFormat)
    public int initial_padding;        // @396
    public int trailing_padding;       // @400
    public int seek_preroll;           // @404
    public IntPtr get_buffer2;         // @408 (AVCodecContext_get_buffer2_func)
    public int bit_rate_tolerance;     // @416
    public int global_quality;         // @420
    public int compression_level;      // @424
    public float qcompress;            // @428
    public float qblur;                // @432
    public int qmin;                   // @436
    public int qmax;                   // @440
    public int max_qdiff;              // @444
    public int rc_buffer_size;         // @448
    public int rc_override_count;      // @452
    public IntPtr rc_override;         // @456 (RcOverride*)
    public long rc_max_rate;           // @464
    public long rc_min_rate;           // @472
    public float rc_max_available_vbv_use; // @480
    public float rc_min_vbv_overflow_use;  // @484
    public int rc_initial_buffer_occupancy; // @488
    public int trellis;                // @492
    public IntPtr stats_out;           // @496 (uint8_t*)
    public IntPtr stats_in;            // @504 (uint8_t*)
    public int workaround_bugs;        // @512
    public int strict_std_compliance;  // @516
    public int error_concealment;      // @520
    public int debug;                  // @524
    public int err_recognition;        // @528
    public IntPtr hwaccel;             // @536 (AVHWAccel*)
    public IntPtr hwaccel_context;     // @544 (void*)
    public AVBufferRef* hw_frames_ctx;       // @552 (AVBufferRef*)
    public AVBufferRef* hw_device_ctx;       // @560 (AVBufferRef*)
    public int hwaccel_flags;         // @568
    public int extra_hw_frames;        // @572
}
