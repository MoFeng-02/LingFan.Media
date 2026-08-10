namespace LingFan.Media.Backends.VLCNative;

/// <summary>
/// VLC Native 后端配置选项（自写 Apache-2.0 P/Invoke，零 LibVLCSharp；替代已退役的 LibVLCSharp 旧后端）。
/// </summary>
public sealed class VLCOptions
{
    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool EnableHardwareDecoding { get; set; } = true;

    /// <summary>
    /// 是否以无头（headless）模式运行——禁止 VLC 打开任何原生视频窗口（默认 true）。
    /// </summary>
    /// <remarks>
    /// <para>本后端通过 <c>SetVideoCallbacks</c> 内存捕获解码帧，本身不依赖 VLC 原生窗口；
    /// 但在音频为主或未注册视频回调时，VLC 仍可能尝试打开窗口。
    /// 置 true 时向 LibVLC 注入 <c>--vout=dummy</c> 作为无头兜底（不影响回调式视频捕获）。</para>
    /// <para>注意：禁止使用 <c>--no-video</c>——那会停止视频解码，破坏 SetVideoCallbacks 捕获路径。</para>
    /// </remarks>
    public bool Headless { get; set; } = true;

    /// <summary>VLC 额外命令行参数（可为 null）。</summary>
    /// <remarks>
    /// 传递给 LibVLC 实例的额外参数，如 "--no-video-title-show"、"--network-caching=1000" 等。
    /// </remarks>
    public string[]? AdditionalOptions { get; set; }
}
