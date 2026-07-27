using LingFan.Media.Abstractions;

namespace LingFan.Media.Extensions;

/// <summary>
/// 编解码器注册表实现（静态映射表，编译期确定，AOT 友好）。
/// </summary>
/// <remarks>
/// <para>提供容器格式 → 编解码器的静态映射查询，供后端自动选择
/// （<c>MediaOptions.EnableAutoBackendSelection</c>）与能力探测使用。</para>
/// <para>纯内存静态表，全部同步（sync 分类）：无 I/O、无 await，方法返回普通
/// <see langword="bool"/> / 可空枚举，绝不包装为 <see cref="Task"/>（否则即伪异步）。</para>
/// <para>仅引用跨层枚举 <see cref="ContainerFormat"/> / <see cref="VideoCodec"/> / <see cref="AudioCodec"/>
/// （均为 Abstractions 已有类型），零外部引用，依赖倒置严守。</para>
/// </remarks>
internal sealed class CodecRegistry : ICodecRegistry
{
    private static readonly Dictionary<ContainerFormat, VideoCodec[]> _videoCodecs = new()
    {
        [ContainerFormat.MP4] = [VideoCodec.H264, VideoCodec.H265, VideoCodec.AV1],
        [ContainerFormat.MKV] = [VideoCodec.H264, VideoCodec.H265, VideoCodec.VP9, VideoCodec.AV1],
        [ContainerFormat.AVI] = [VideoCodec.H264, VideoCodec.MPEG4, VideoCodec.MPEG2],
        [ContainerFormat.TS] = [VideoCodec.H264, VideoCodec.H265, VideoCodec.MPEG2],
        [ContainerFormat.WebM] = [VideoCodec.VP9, VideoCodec.AV1],
        [ContainerFormat.FLV] = [VideoCodec.H264, VideoCodec.VP9],
    };

    private static readonly Dictionary<ContainerFormat, AudioCodec[]> _audioCodecs = new()
    {
        [ContainerFormat.MP4] = [AudioCodec.AAC, AudioCodec.MP3, AudioCodec.AC3],
        [ContainerFormat.MKV] = [AudioCodec.AAC, AudioCodec.MP3, AudioCodec.FLAC, AudioCodec.Opus, AudioCodec.AC3],
        [ContainerFormat.AVI] = [AudioCodec.MP3, AudioCodec.PCM, AudioCodec.AC3],
        [ContainerFormat.TS] = [AudioCodec.AAC, AudioCodec.MP3, AudioCodec.AC3],
        [ContainerFormat.WebM] = [AudioCodec.Opus, AudioCodec.Vorbis],
        [ContainerFormat.FLV] = [AudioCodec.AAC, AudioCodec.MP3],
    };

    /// <inheritdoc/>
    public bool IsCodecSupported(ContainerFormat container, VideoCodec videoCodec)
        => _videoCodecs.TryGetValue(container, out var list) && Array.IndexOf(list, videoCodec) >= 0;

    /// <inheritdoc/>
    public bool IsCodecSupported(ContainerFormat container, AudioCodec audioCodec)
        => _audioCodecs.TryGetValue(container, out var list) && Array.IndexOf(list, audioCodec) >= 0;

    /// <inheritdoc/>
    public VideoCodec? GetDefaultVideoCodec(ContainerFormat container)
        => _videoCodecs.TryGetValue(container, out var list) && list.Length > 0 ? list[0] : null;

    /// <inheritdoc/>
    public AudioCodec? GetDefaultAudioCodec(ContainerFormat container)
        => _audioCodecs.TryGetValue(container, out var list) && list.Length > 0 ? list[0] : null;
}
