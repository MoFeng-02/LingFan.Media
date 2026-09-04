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
    /// 启用「桥接零拷贝」视频出帧路径：解码器渲入 GLES 桥接 SurfaceTexture，GPU 内 YUV→RGBA
    /// 落 AHardwareBuffer，帧以 <c>AndroidHardwareBufferFrameResource</c> 交付（无 ByteBuffer CPU 提取），
    /// 显示侧经 Vulkan AHB 导入 / Skia 直绘承接。桥接不可用时解码器自动回落 CPU 帧档，不影响播放。
    /// </summary>
    /// <remarks>
    /// 默认关闭（CPU 帧档为能播默认）。也可不经此选项，用环境变量
    /// <c>LFM_ANDROID_ZERO_COPY=1</c> 运行时开启（Android 调试构建可经
    /// <c>adb shell setprop debug.mono.env "LFM_ANDROID_ZERO_COPY=1"</c> 注入）。
    /// </remarks>
    public bool EnableHardwareZeroCopy { get; set; }
}
