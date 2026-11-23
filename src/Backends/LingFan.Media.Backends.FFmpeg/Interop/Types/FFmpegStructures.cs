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
