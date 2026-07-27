namespace LingFan.Media.Backends.WebRTC;

/// <summary>
/// WebRTC 后端配置选项。
/// </summary>
/// <remarks>
/// 从 BackendStubs.cs 迁移至 WebRTC 后端项目（Task-V2-14 B4）。
/// WebRTC 后端需要原生 WebRTC 库（如 Google libwebrtc 的 C API 绑定），
/// 当前未集成原生库，所有运行时操作抛 <see cref="PlatformNotSupportedException"/>。
/// </remarks>
public sealed class WebRTCOptions
{
    /// <summary>ICE 服务器地址列表（STUN / TURN）。</summary>
    public string[] IceServers { get; set; } = [];

    /// <summary>是否启用数据通道（默认 false）。</summary>
    public bool EnableDataChannel { get; set; }

    /// <summary>是否启用硬件加速视频解码（默认 true）。</summary>
    public bool EnableHardwareDecoding { get; set; } = true;

    /// <summary>音频采样率（Hz，默认 48000，WebRTC Opus 标准采样率）。</summary>
    public int AudioSampleRate { get; set; } = 48000;

    /// <summary>音频声道数（默认 1，WebRTC 默认单声道）。</summary>
    public int AudioChannels { get; set; } = 1;
}
