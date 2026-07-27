namespace LingFan.Media.Backends.MediaFoundation;

/// <summary>
/// MediaFoundation 后端配置选项。
/// </summary>
/// <remarks>
/// 从 BackendStubs.cs 迁移至 MediaFoundation 后端项目（Task-V2-14 B2）。
/// MediaFoundation 后端仅 Windows 可用，其他平台运行时检测后抛 PlatformNotSupportedException。
/// </remarks>
public sealed class MediaFoundationOptions
{
    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool EnableHardwareDecoding { get; set; } = true;

    /// <summary>是否启用 DXVA 硬件加速视频解码（默认 true）。</summary>
    public bool EnableDxva { get; set; } = true;
}
