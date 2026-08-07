namespace LingFan.Media.Abstractions;

/// <summary>
/// 一个已注册后端的只读描述（工厂接口集合，非实例）。
/// 由 <see cref="IBackendRegistry"/> 按 DI 注册顺序聚合，供中间件 / 高级消费方查看与选定后端。
/// </summary>
/// <remarks>
/// <para>持有的是 <b>工厂接口</b>（DI 解析的 Singleton 无状态服务），<b>不是</b> player / 后端实例。</para>
/// <para>命中某个后端组后，应把这些工厂接口交给
/// <see cref="IMediaPlayerFactory.Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)"/>
/// 去创建 Session 级对象。<b>lookup（接口）与 instance（实例）不可混淆。</b></para>
/// </remarks>
/// <param name="Name">后端友好名（如 "FFmpeg" / "MediaFoundation" / "VLC"）。</param>
/// <param name="Demuxer">解封装器工厂接口。</param>
/// <param name="VideoDecoder">视频解码器工厂接口。</param>
/// <param name="AudioDecoder">音频解码器工厂接口。</param>
/// <param name="SubtitleDecoder">字幕解码器工厂接口（可选；仅部分后端注册）。</param>
public sealed record BackendDescriptor(
    string Name,
    IMediaDemuxerFactory Demuxer,
    IVideoDecoderFactory VideoDecoder,
    IAudioDecoderFactory AudioDecoder,
    ISubtitleDecoderFactory? SubtitleDecoder);
