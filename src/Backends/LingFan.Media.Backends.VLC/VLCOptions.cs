namespace LingFan.Media.Backends.VLC;

/// <summary>
/// VLC 后端配置选项。
/// </summary>
/// <remarks>
/// 从 BackendStubs.cs 迁移至 VLC 后端项目（Task-V2-14 B1）。
/// </remarks>
public sealed class VLCOptions
{
    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool EnableHardwareDecoding { get; set; } = true;

    /// <summary>VLC 额外命令行参数（可为 null）。</summary>
    /// <remarks>
    /// 传递给 LibVLC 实例的额外参数，如 "--no-video-title-show"、"--network-caching=1000" 等。
    /// </remarks>
    public string[]? AdditionalOptions { get; set; }
}
