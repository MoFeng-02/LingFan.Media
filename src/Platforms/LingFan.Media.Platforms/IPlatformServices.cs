namespace LingFan.Media.Platforms;

/// <summary>
/// 平台服务接口。为后端和渲染器提供平台底层支持。
/// </summary>
/// <remarks>
/// <para>平台服务职责：</para>
/// <list type="bullet">
/// <item>硬件解码桥接——将平台硬解输出（D3D11VA / VAAPI / VideoToolbox / MediaCodec）转换为 <see cref="IFrameResource"/>。</item>
/// <item>GPU 资源跨 API 共享——如 D3D11 → Vulkan → OpenGL。</item>
/// <item>系统媒体 API 对接——如 Windows Media Foundation。</item>
/// <item>设备枚举——音频设备、显示设备。</item>
/// <item>电源管理——播放时阻止休眠。</item>
/// </list>
/// <para>V1 范围：<see cref="CreateHardwareDecoder"/> 和 <see cref="GetGPUContext"/> 均为桩（抛出 <see cref="NotSupportedException"/>）。
/// V1 实际可用的硬解 / GPU 互操作仅 <b>Windows（D3D11VA）</b>，其余平台属 Phase 2-3 目标。</para>
/// <para>AOT 兼容：接口定义无反射，实现为 sealed 类。</para>
/// </remarks>
public interface IPlatformServices
{
    /// <summary>当前平台。</summary>
    OSPlatform Platform { get; }

    /// <summary>是否支持硬件解码。</summary>
    bool SupportsHardwareDecode { get; }

    /// <summary>是否支持 GPU 互操作（跨 API 资源共享）。</summary>
    bool SupportsGPUInterop { get; }

    /// <summary>
    /// 创建平台硬件解码器。
    /// </summary>
    /// <param name="codec">视频编解码器类型。</param>
    /// <returns>硬件解码器实例，平台不支持时返回 null。</returns>
    /// <remarks>
    /// V1 桩——抛出 <see cref="NotSupportedException"/>。
    /// 未来实现：Windows → D3D11VA / DXVA2，Linux → VAAPI，macOS/iOS → VideoToolbox，Android → MediaCodec。
    /// </remarks>
    IVideoDecoder? CreateHardwareDecoder(VideoCodec codec);

    /// <summary>
    /// 获取 GPU 上下文。
    /// </summary>
    /// <param name="type">GPU API 类型（D3D11 / Vulkan / Metal / OpenGL）。</param>
    /// <returns>GPU 上下文对象（如 ID3D11Device / VkDevice / id<MTLDevice>），运行时显式 cast，不用反射。</returns>
    /// <remarks>
    /// V1 桩——抛出 <see cref="NotSupportedException"/>。
    /// GPU 设备为 Singleton 共享，但 SwapChain / CommandQueue 是 Session 级。
    /// </remarks>
    object? GetGPUContext(GPUApiType type);
}
