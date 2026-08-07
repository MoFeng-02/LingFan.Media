namespace LingFan.Media.Abstractions;

/// <summary>
/// 播放器工厂接口。
/// </summary>
/// <remarks>
/// 工厂自身为 Singleton（无状态）。每次 Create() 返回新实例（Session 级，Transient）。
/// <para>新增 <see cref="Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)"/> 重载以显式指定后端组，
/// 供中间件 / 高级消费方手动组合或运行时回退使用——该重载只依赖契约接口，不引用任何具体后端，严守依赖倒置。</para>
/// </remarks>
public interface IMediaPlayerFactory
{
    /// <summary>创建新的 IMediaPlayer 实例（解析 DI 中已注册的单体工厂）。</summary>
    IMediaPlayer Create();

    /// <summary>
    /// 创建新的 IMediaPlayer 实例，使用显式指定的后端组工厂（demuxer + 视频/音频/字幕解码器）。
    /// 用于手动组合后端（如 <see cref="IBackendRegistry"/> 选定的某个后端组），不依赖 DI 默认的单体工厂解析。
    /// </summary>
    /// <param name="demuxerFactory">解封装器工厂（已注册后端的 Singleton 无状态服务）。</param>
    /// <param name="videoDecoderFactory">视频解码器工厂。</param>
    /// <param name="audioDecoderFactory">音频解码器工厂。</param>
    /// <param name="subtitleDecoderFactory">字幕解码器工厂（可选；部分后端不提供字幕解码，传 null）。</param>
    IMediaPlayer Create(
        IMediaDemuxerFactory demuxerFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IAudioDecoderFactory audioDecoderFactory,
        ISubtitleDecoderFactory? subtitleDecoderFactory = null);
}
