using System.Runtime.InteropServices;

#pragma warning disable CS0649 // interop 结构体字段由原生代码写入，C# 从不赋值

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// 内联指针数组（8 元素）：映射 C 侧 <c>uint8_t* data[8]</c> / <c>AVBufferRef* buf[8]</c> 等。
/// C# fixed 缓冲区不允许 IntPtr 元素，故用 InlineArray（连续布局、索引器返回元素引用，AOT 友好）。
/// </summary>
[System.Runtime.CompilerServices.InlineArray(8)]
internal struct PtrArray8
{
    private IntPtr _e0;
}

/// <summary>
/// 内联指针数组（4 元素）：映射 C 侧 <c>uint8_t* data[4]</c>（AVSubtitleRect）。
/// </summary>
[System.Runtime.CompilerServices.InlineArray(4)]
internal struct PtrArray4
{
    private IntPtr _e0;
}

/// <summary>
/// 有理数时间基（严格对齐 libavutil/rational.h）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    public int num;
    public int den;
}

/// <summary>
/// 引用计数缓冲句柄（严格对齐 libavutil/buffer.h）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVBufferRef
{
    public IntPtr buffer;   // AVBuffer*（offset 0）
    public byte* data;      // uint8_t*（offset 8）
    public ulong size;      //（offset 16）
}

/// <summary>
/// 通道布局（严格对齐 libavutil/channel_layout.h）。
/// union { uint64_t mask; AVChannelCustom* map; } 以单一 8 字节字段承载，
/// 本后端仅读取 nb_channels，无需区分 mask/map。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVChannelLayout
{
    public AVChannelOrder order;
    public int nb_channels;
    public ulong u; // 联合体：mask (uint64) 或 map (AVChannelCustom*)
    public IntPtr opaque;
}

/// <summary>
/// 字典条目（严格对齐 libavutil/dict.h）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AVDictionaryEntry
{
    public IntPtr key;
    public IntPtr value;
}

/// <summary>
/// 压缩包（严格对齐 libavcodec/packet.h，FFmpeg 5.0+ 无 segment_info）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVPacket
{
    public IntPtr buf;          // AVBufferRef*
    public long pts;
    public long dts;
    public byte* data;          // uint8_t*（offset 24）
    public int size;            //（offset 32，权威布局为 int 4 字节；误用 ulong 会使 stream_index/flags 整体后移 4 字节→包路由错乱）
    public int stream_index;
    public int flags;
    public IntPtr side_data;    // AVPacketSideData*
    public int side_data_elems;
    public long duration;
    public long pos;
    public IntPtr opaque;
    public IntPtr opaque_ref;   // AVBufferRef*
    public AVRational time_base;
}

/// <summary>
/// 解码帧（严格对齐 libavutil/frame.h，FFmpeg 5.0+ 布局；
/// 含 ch_layout 取代 channel_layout，key_frame 合入 flags，crop 拆为四个 size_t）。
/// 只读字段止于 ch_layout 之后，故结构体在 ch_layout/duration/alpha_mode 处截断安全
/// （上下文始终经指针访问，不按值传递、不按 sizeof 分配）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVFrame
{
    public PtrArray8 data;             // uint8_t* [AV_NUM_DATA_POINTERS]
    public fixed int linesize[8];      // int [AV_NUM_DATA_POINTERS]
    public byte** extended_data;        // uint8_t**
    public int width;
    public int height;
    public int nb_samples;
    public int format;
    public int pict_type;              // enum AVPictureType
    public AVRational sample_aspect_ratio;
    public long pts;
    public long pkt_dts;
    public AVRational time_base;
    public int quality;
    public IntPtr opaque;
    public int repeat_pict;
    public int sample_rate;
    public PtrArray8 buf;              // AVBufferRef* [AV_NUM_DATA_POINTERS]
    public IntPtr extended_buf;        // AVBufferRef**
    public int nb_extended_buf;
    public IntPtr side_data;           // AVFrameSideData**
    public int nb_side_data;
    public int flags;
    public int color_range;
    public int color_primaries;
    public int color_trc;
    public int colorspace;
    public int chroma_location;
    public long best_effort_timestamp;
    public IntPtr metadata;            // AVDictionary*
    public int decode_error_flags;
    public IntPtr hw_frames_ctx;       // AVBufferRef*
    public IntPtr opaque_ref;          // AVBufferRef*
    public UIntPtr crop_top;
    public UIntPtr crop_bottom;
    public UIntPtr crop_left;
    public UIntPtr crop_right;
    public IntPtr private_ref;
    public AVChannelLayout ch_layout;
    public long duration;
    public int alpha_mode;             // enum AVAlphaMode
}

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
/// 流（严格对齐 libavformat/avformat.h；codec 已由 codecpar 取代）。
/// 只读字段止于 avg_frame_rate，其后再无读取，截断安全。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVStream
{
    public IntPtr av_class;
    public int index;
    public int id;
    public IntPtr codecpar;            // AVCodecParameters*
    public IntPtr priv_data;
    public AVRational time_base;
    public long start_time;
    public long duration;
    public long nb_frames;
    public int disposition;
    public int discard;                // enum AVDiscard
    public AVRational sample_aspect_ratio;
    public AVDictionary* metadata;     // AVDictionary*
    public AVRational avg_frame_rate;
    public AVPacket attached_pic;      //（offset 96, 104 字节；AVPacket 布局镜像）
    public int event_flags;            //（offset 200）
    public AVRational r_frame_rate;     //（offset 204）
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

/// <summary>
/// 格式上下文（严格对齐 libavformat/avformat.h）。
/// 仅镜像至 metadata（读取止于此），其后字段不读，截断安全。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AVFormatContext
{
    public IntPtr av_class;
    public AVInputFormat* iformat;     // const AVInputFormat*
    public IntPtr oformat;             // const AVOutputFormat*
    public IntPtr priv_data;
    public AVIOContext* pb;            // AVIOContext*
    public int ctx_flags;
    public uint nb_streams;
    public AVStream** streams;          // AVStream**（offset 48）
    public uint nb_stream_groups;
    public IntPtr stream_groups;       // AVStreamGroup**
    public uint nb_chapters;
    public IntPtr chapters;            // AVChapter**
    public IntPtr url;                 // char*
    public long start_time;
    public long duration;
    public long bit_rate;
    public uint packet_size;
    public int max_delay;
    public int flags;
    public long probesize;
    public long max_analyze_duration;
    public IntPtr key;                 // const uint8_t*
    public int keylen;
    public uint nb_programs;
    public IntPtr programs;            // AVProgram**
    public AVCodecID video_codec_id;
    public AVCodecID audio_codec_id;
    public AVCodecID subtitle_codec_id;
    public AVCodecID data_codec_id;
    public AVDictionary* metadata;     // AVDictionary*
}
