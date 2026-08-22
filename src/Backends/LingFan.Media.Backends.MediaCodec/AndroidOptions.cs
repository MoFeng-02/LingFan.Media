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
    /// 是否尝试 AHardwareBuffer 零拷贝上屏（默认 false）。
    /// </summary>
    /// <remarks>
    /// <para>开启后解码器输出到 <c>AImageReader</c>（YUV_420_888 + GPU_SAMPLED_IMAGE 用途，API 26+）的
    /// Surface，帧经 <c>AImage_getHardwareBuffer</c> 取 AHardwareBuffer 交当前激活渲染器注册的
    /// <see cref="IGpuFrameProducer"/> 导入（如 Vulkan 的 AHB 外部内存 + YCbCr 采样转换），全程无 CPU 拷贝。</para>
    /// <para>零拷贝成立须同时满足：① 开启本开关；② 已注册匹配当前渲染器
    /// <see cref="IGpuDeviceContext.ApiType"/> 且支持 <see cref="GpuFrameImportKind.AndroidHardwareBuffer"/>
    /// 的生产者（Vulkan 渲染器已接线）；③ 设备 API 26+ 且 gralloc 接受用途组合。任一不满足即自动回落
    /// 软件 ByteBuffer 路径；运行中单帧导入失败按帧回落 CPU 平面提取（帧不丢，绝不留“已就绪”假绿，
    /// 符合 S_OK≠被接受 纪律）。</para>
    /// </remarks>
    public bool EnableHardwareBufferZeroCopy { get; set; }
}
