namespace LingFan.Media.Abstractions;

/// <summary>
/// 轻量探测到的「容器格式 + 主视频编码」组合。
/// 描述纯数据，不含任何检测算法。
/// 用于回退调度器的「格式级记忆」：同 (容器, 编码) 组合只经历一次异常驱动回退，
/// 后续同类文件直接命中记忆后端，免去重复回退开销。
/// </summary>
/// <remarks>
/// 该结构置于契约层（Abstractions），使高层中间件（如 LingFan.Media.Playback 回退调度器）
/// 只依赖本数据契约与 <see cref="IFormatDetector"/> 接口，而不引用具体探测实现（LingFan.Media.Formats），
/// 严守依赖倒置（DIP）。
/// </remarks>
public readonly struct MediaFormatProfile
{
    /// <summary>容器格式（如 MP4 / WebM）。</summary>
    public readonly ContainerFormat Container;

    /// <summary>主视频编码（探测不到为 <see cref="VideoCodec.Unknown"/>）。</summary>
    public readonly VideoCodec Video;

    public MediaFormatProfile(ContainerFormat container, VideoCodec video)
    {
        Container = container;
        Video = video;
    }
}
