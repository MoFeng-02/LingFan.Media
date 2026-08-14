namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// Android 后端（MediaExtractor + MediaCodec）配置。
/// </summary>
/// <remarks>
/// <para>纯 POCO，Singleton 注册。解析它不触碰任何原生（开箱即用原则）：
/// NDK 原生初始化延迟到 demuxer/decoder 实际 Open/Initialize 时执行。</para>
/// <para>构造与注册阶段只持有配置，绝不在注册期要求 libmediandk/libandroid 在场。</para>
/// </remarks>
public sealed class AndroidOptions
{
    /// <summary>
    /// 是否尝试 AHardwareBuffer 零拷贝上屏。
    /// </summary>
    /// <remarks>
    /// <para>零拷贝需要两件事同时成立：① 当前激活渲染器注册了支持
    /// <see cref="GpuFrameImportKind.AndroidHardwareBuffer"/> 的 <see cref="IGpuFrameProducer"/>
    /// （即 Android Vulkan/GLES 渲染器 + C 线 AHB interop，对应路线图 A3/A5 + C 线）；
    /// ② 解码器经 Surface 喂 AHardwareBuffer 的 JNI 接线。</para>
    /// <para>上述渲染器/互操作尚未落地前，本开关恒被忽略、解码器自动回落软件 ByteBuffer 路径
    /// （绝不留“已就绪”假绿，符合 S_OK≠被接受 纪律）。留此开关仅为“能力自报”与未来启用点。</para>
    /// </remarks>
    public bool EnableHardwareBufferZeroCopy { get; set; }
}
