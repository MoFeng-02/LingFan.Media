namespace LingFan.Media.Backends.MediaCodec.Decoders;

/// <summary>
/// Android 视频解码路径策略（两档分层）：
/// <list type="number">
/// <item><b>硬解 + CPU 帧</b>（能播档，默认）：OMX 硬件解码器 + ByteBuffer CPU 帧 + Skia 软渲染。
/// OMX 走旧 OMX 框架（非 Codec2），无 c2 的 numClientBuffers 僵死。</item>
/// <item><b>软解 + 桥接零拷贝</b>（增强档，<see cref="EnableHardwareZeroCopy"/>）：c2 软件解码器渲入
/// GLES 桥接 SurfaceTexture → GPU 内 YUV→RGBA 落 AHardwareBuffer，渲染侧零拷贝。产帧稳定，
/// 但 Vulkan AHB 采样在 Adreno 上驱动崩（已联网核实为 Adreno workaround 重灾区），GL 纹理路线重做中。</item>
/// </list>
/// 桥接不可用时回落 ①（绝不 c2 软解 + ByteBuffer——那是 numClientBuffers 僵死档）。
/// </summary>
/// <remarks>
/// 开关可由宿主在播放前置 <c>true</c>，或经环境变量 <c>LFM_ANDROID_ZERO_COPY=1</c> 启用
/// （Android 调试构建可用 <c>adb shell setprop debug.mono.env "LFM_ANDROID_ZERO_COPY=1"</c> 注入）。
/// </remarks>
public static class AndroidVideoDecodePolicy
{
    /// <summary>启用「软解 + 桥接零拷贝」路径（默认关闭，硬解 + CPU 帧为能播默认档）。</summary>
    public static volatile bool EnableHardwareZeroCopy = ReadEnvironmentSwitch();

    private static bool ReadEnvironmentSwitch()
    {
        string? v = Environment.GetEnvironmentVariable("LFM_ANDROID_ZERO_COPY");
        return string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
