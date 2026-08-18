#pragma warning disable CS0649 // interop 结构体字段由原生代码写入，C# 从不赋值

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// FFmpeg 不透明/指针类型。本后端仅持有指针并传递给原生 API；
/// 除 <see cref="AVInputFormat"/>（读 name）、<see cref="AVIOContext"/>（写 buffer/buffer_size）、
/// <see cref="AVHWDeviceContext"/>（读 hwctx）外，均从不读取字段。
/// 强类型指针替代 AutoGen 的 IntPtr 混用，提升调用点可读性与类型安全。
/// </summary>
internal unsafe struct AVDictionary { }

/// <summary>输入格式（严格对齐 libavformat/avformat.h；仅读取首字段 name）。</summary>
internal unsafe struct AVInputFormat
{
    public byte* name; // const char*，首字段（offset 0）
}

/// <summary>AVIO 上下文（严格对齐 libavformat/avio.h）。
/// 仅读取/写入 buffer 与 buffer_size（位于 av_class 之后），其余字段绝不触碰。</summary>
internal unsafe struct AVIOContext
{
    public IntPtr av_class;  // const AVClass*（offset 0）
    public byte* buffer;     // unsigned char*（offset 8）
    public int buffer_size;  //（offset 16）
}

/// <summary>SWScale 上下文（不透明）。</summary>
internal unsafe struct SwsContext { }

/// <summary>SWResample 上下文（不透明）。</summary>
internal unsafe struct SwrContext { }

/// <summary>比特流过滤器（不透明）。</summary>
internal unsafe struct AVBitStreamFilter { }

/// <summary>硬件设备上下文（严格对齐 libavutil/hwcontext.h；仅读取首字段之后的 hwctx）。</summary>
internal unsafe struct AVHWDeviceContext
{
    public IntPtr av_class; // const AVClass*（offset 0）
    public int type;        // AVHWDeviceType（offset 8）
    public void* hwctx;     // 各后端私有上下文指针（offset 16）
}
