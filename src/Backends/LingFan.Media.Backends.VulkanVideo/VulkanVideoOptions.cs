namespace LingFan.Media.Backends.VulkanVideo;

/// <summary>
/// Vulkan 硬解后端配置。
/// </summary>
/// <remarks>
/// <para>仅承载用户可调选项，无原生状态。</para>
/// <para>当前仅实现 H.264 硬解；H.265 尚未支持（<see cref="EnableH265"/> 为预留开关，默认关闭）。</para>
/// </remarks>
public sealed class VulkanVideoOptions
{
    /// <summary>是否启用 H.264 Vulkan 硬解（默认 true）。</summary>
    public bool EnableH264 { get; set; } = true;

    /// <summary>是否启用 H.265 Vulkan 硬解（默认 false；H.265 尚未实现，置 true 也不会被选择）。</summary>
    public bool EnableH265 { get; set; }
}
