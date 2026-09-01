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

    /// <summary>
    /// 硬解优先选 Codec2 栈的厂商实现（如 <c>c2.qti.avc.decoder</c>），而非系统按类型默认给出的
    /// 旧 OMX 实现（如 <c>OMX.qcom.video.decoder.avc</c>）。
    /// </summary>
    /// <remarks>
    /// 默认开启。枚举失败或目标解码器不可创建时自动回退到 <c>CreateDecoderByType</c>，不会比现状更差。
    /// 需要对比验证时可经环境变量 <c>LFM_ANDROID_PREFER_C2=0</c> 关闭。
    /// </remarks>
    public static volatile bool PreferCodec2HardwareDecoder = ReadPreferC2Switch();

    private static bool ReadPreferC2Switch()
    {
        string? v = Environment.GetEnvironmentVariable("LFM_ANDROID_PREFER_C2");
        return !string.Equals(v, "0", StringComparison.Ordinal)
            && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 强制使用 AOSP 软件解码器（+ ByteBuffer CPU 帧 + Skia 软渲染），其余链路完全不变。
    /// </summary>
    /// <remarks>
    /// <b>用途：单变量对照实验。</b>当怀疑硬件解码器输出坏帧时，只把解码器换成软解，
    /// 帧提取、重排、同步、上屏全部保持原样 —— 若软解下画面干净，即可定位坏帧来自硬解侧；
    /// 若软解同样坏，则是我们 ByteBuffer→YUV 提取→上屏这一段的问题。
    /// 软解高分辨率帧率很低，仅用于定案，不用于性能评估。
    /// 经环境变量 <c>LFM_ANDROID_SW_DECODER=1</c> 启用：
    /// <c>adb shell setprop debug.mono.env "LFM_ANDROID_SW_DECODER=1"</c>。
    /// </remarks>
    public static volatile bool ForceSoftwareDecoder = ReadForceSwSwitch();

    private static bool ReadForceSwSwitch()
    {
        string? v = Environment.GetEnvironmentVariable("LFM_ANDROID_SW_DECODER");
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadEnvironmentSwitch()
    {
        string? v = Environment.GetEnvironmentVariable("LFM_ANDROID_ZERO_COPY");
        return string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }
}
