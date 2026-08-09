namespace LingFan.Media.Backends.MediaFoundation;

/// <summary>
/// MediaFoundation 后端配置选项。
/// </summary>
/// <remarks>
/// 从 BackendStubs.cs 迁移至 MediaFoundation 后端项目。
/// MediaFoundation 后端仅 Windows 可用，其他平台运行时检测后抛 PlatformNotSupportedException。
/// </remarks>
public sealed class MediaFoundationOptions
{
    /// <summary>是否启用硬件解码（默认 true）。</summary>
    public bool EnableHardwareDecoding { get; set; } = true;

    /// <summary>是否启用 DXVA 硬件加速视频解码（默认 true）。</summary>
    public bool EnableDxva { get; set; } = true;

    /// <summary>
    /// 是否允许 SourceReader 进入「解封装 + 解码一体」模式（默认 <see langword="true"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为 <see langword="true"/> 时，MFDemuxer 会把视频流输出类型协商为 NV12，令 SourceReader 内部自行完成解码，
    /// <c>MFVideoDecoder</c> 退化为直通——少一次 MFT 往返，是默认的高效路径。
    /// </para>
    /// <para>
    /// 🔴 <b>置为 <see langword="false"/> 的唯一用途是零拷贝根因定界</b>：跳过上述协商，让视频流继续输出压缩裸流，
    /// 强制走「<c>MFVideoDecoder</c> 自管 MFT + 自行 SET_D3D_MANAGER」老路径。两条路径的帧落点对照即可定性：
    /// </para>
    /// <list type="bullet">
    /// <item>自管 MFT 能出 <c>IMFDXGIBuffer</c> ⇒ 读回发生在 <b>SourceReader 封装层</b>，MF 后端应改走自管 MFT。</item>
    /// <item>自管 MFT 同样出系统内存 ⇒ 读回发生在 <b>MFT/驱动层</b>，MF 对该 codec 无零拷贝解，应让位 FFmpeg D3D11VA。</item>
    /// </list>
    /// <para>诊断开关，非性能开关；日常播放请保持默认 <see langword="true"/>。</para>
    /// </remarks>
    public bool EnableReaderDecodeFusion { get; set; } = true;
}
