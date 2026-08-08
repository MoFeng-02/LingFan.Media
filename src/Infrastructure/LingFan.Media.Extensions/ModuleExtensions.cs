namespace LingFan.Media.Extensions;

/// <summary>
/// 各子模块内部服务注册的辅助扩展方法。
/// </summary>
/// <remarks>
/// <para>V1：各模块的服务注册直接在各自的扩展方法中完成（如
/// <c>AddFFmpeg()</c> 在 <c>FFmpegExtensions.cs</c> 中注册解码器工厂）。</para>
/// <para>此保留类用于未来跨模块的内部服务注册，如编解码注册表、GPU 设备上下文等。</para>
/// <para>所有方法为同步配置（config 分类），无 I/O、无异步。</para>
/// </remarks>
public static class ModuleExtensions
{
    // V1 不需要在此添加方法。
    //
    // 各模块的注册位置：
    //   后端（FFmpeg/VLC/MF/GStreamer/WebRTC）→ 各 Backends 项目的 *Extensions.cs
    //   渲染器（D3D11/Vulkan/Metal/OpenGL）   → 各 Renderers 项目的 *Extensions.cs
    //   输出（WASAPI/ALSA/CoreAudio/...）      → 各 Outputs 项目的 *Extensions.cs
    //   UI（Avalonia）                          → LingFan.Media.Avalonia/Extensions/
    //
    // 未来扩展（仅考虑，可能丢弃）：
    //   services.AddCodecRegistry()    → ICodecRegistry
    //   services.AddGpuDeviceContext() → IGpuDeviceContext
}
