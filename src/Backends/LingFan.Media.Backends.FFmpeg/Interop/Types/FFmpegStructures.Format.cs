using System.Runtime.InteropServices;

#pragma warning disable CS0649 // interop 结构体字段由原生代码写入，C# 从不赋值

namespace LingFan.Media.Backends.FFmpeg.Interop;

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
