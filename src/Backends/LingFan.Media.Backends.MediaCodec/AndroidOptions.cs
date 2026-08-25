namespace LingFan.Media.Backends.MediaCodec;

/// <summary>
/// Android 后端（MediaExtractor + MediaCodec）配置。
/// </summary>
/// <remarks>
/// <para>纯 POCO，Singleton 注册。解析它不触碰任何原生（开箱即用原则）：
/// NDK 原生初始化延迟到 demuxer/decoder 实际 Open/Initialize 时执行。</para>
/// <para>构造与注册阶段只持有配置，绝不在注册期要求 libmediandk/libandroid 在场。</para>
/// <para>当前无可调选项：AHardwareBuffer GPU 零拷贝上屏路径已按 2026-08-22 架构裁定移除
/// （Android 走 net-* 托管绑定，不自写 P/Invoke；GPU 零拷贝属暂缓项，见设计文档 §5.2），
/// 解码输出统一为 CPU 侧 <c>Image.Plane</c> 提取的标准 I420 帧。此类保留作 Android 专属调参扩展点。</para>
/// </remarks>
public sealed class AndroidOptions
{
}
