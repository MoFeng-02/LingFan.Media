namespace LingFan.Media.Backends.Apple;

/// <summary>
/// Apple 后端（AVAssetReader passthrough + VideoToolbox 解码）配置。
/// </summary>
/// <remarks>
/// <para>纯 POCO，Singleton 注册。解析它不触碰任何原生（开箱即用原则）：
/// 原生初始化延迟到 demuxer.OpenAsync / decoder.Initialize 时执行。</para>
/// <para>构造与注册阶段只持有配置，绝不在注册期要求 AVFoundation/VideoToolbox 在场。</para>
/// </remarks>
public sealed class AppleOptions
{
    /// <summary>
    /// 是否尝试 VideoToolbox 零拷贝上屏（CVPixelBuffer → IOSurface → Metal 纹理）。
    /// </summary>
    /// <remarks>
    /// <para>默认关闭：先软解（CVPixelBuffer 拷贝进 <see cref="SoftwareFrameResource"/>），
    /// 与"先软解再硬解"策略一致；待 Metal 消费侧 IOSurface 零拷贝（C0 任务）验收后再开启。</para>
    /// <para>开启后，解码器在 <see cref="GpuFrameImportKind.IOSurface"/> 生产者可用时把 IOSurfaceRef 经
    /// <see cref="GpuFrameImportSource"/> 喂出，否则回落软件拷贝（绝不留"已就绪"假绿，符合 S_OK≠被接受 纪律）。</para>
    /// </remarks>
    public bool EnableVideoToolboxZeroCopy { get; set; }
}
