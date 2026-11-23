namespace LingFan.Media.Extensions;

/// <summary>
/// 各子模块内部服务注册的辅助扩展方法。
/// </summary>
/// <remarks>
/// <para>各模块的服务注册直接在各自的扩展方法中完成（如
/// <c>AddFFmpeg()</c> 在 <c>FFmpegExtensions.cs</c> 中注册解码器工厂）。</para>
/// <para>本类当前为空占位：跨模块内部服务已在各自实际位置注册——
/// 编解码注册表（ICodecRegistry）在 <c>AddLingFanMedia()</c> 中注册，
/// GPU 设备上下文（IGpuDeviceContext）由各渲染器扩展（如 <c>AddD3D11Renderer()</c>）注册。</para>
/// <para>所有方法为同步配置，无 I/O、无异步。</para>
/// </remarks>
public static class ModuleExtensions
{
    // 不需要在此添加方法。
    //
    // 各模块的注册位置：
    //   后端（FFmpeg/VLC/MF）→ 各 Backends 项目的 *Extensions.cs
    //   不支持后端桩（GStreamer/WebRTC）→ 本层 BackendStubs.cs（注册期 fail-fast）
    //   渲染器（D3D11/Vulkan/Metal/OpenGL）   → 各 Renderers 项目的 *Extensions.cs
    //   输出（WASAPI/ALSA/CoreAudio/...）      → 各 Outputs 项目的 *Extensions.cs
    //   UI（Avalonia）                          → LingFan.Media.Avalonia/Extensions/
}
