using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LingFan.Media.Core;

/// <summary>
/// 播放器工厂实现。创建完整的 Session 对象图。
/// </summary>
/// <remarks>
/// <para>Factory 自身为 Singleton（无状态），由 DI 注入系统级依赖。</para>
/// <para>Session 内部对象由 Create() 手动 new（在 MediaPlayer.OpenAsync 中延迟创建）。</para>
/// <para>区分 Infrastructure（Singleton 工厂）和 Session（Transient 播放器）两类依赖。</para>
/// <para>GPU 设备共享：IGpuDeviceContext 是 Singleton，多个播放器共享同一 GPU Device。
/// 但 SwapChain/CommandQueue 是 Session 级。</para>
/// </remarks>
public sealed class MediaPlayerFactory : IMediaPlayerFactory
{
    private readonly IMediaStreamFactory _streamFactory;
    private readonly IReadOnlyList<IMediaDemuxerFactory> _demuxerFactories;
    private readonly IReadOnlyList<IVideoDecoderFactory> _videoDecoderFactories;
    private readonly IReadOnlyList<IAudioDecoderFactory> _audioDecoderFactories;
    private readonly IReadOnlyList<ISubtitleDecoderFactory>? _subtitleDecoderFactories;
    private readonly IVideoRendererFactory _videoRendererFactory;
    private readonly IAudioOutputFactory _audioOutputFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MediaPlayerOptions _options;

    // 后处理变换链（中立 BCL 委托）。
    // 由 Extensions/DI 从 Video/Audio 模块的具体处理器/音量/混音转换而来；
    // Core 不直接依赖 Video/Audio 模块，保持分层倒置避免。
    private readonly IReadOnlyList<Func<VideoFrame, VideoFrame?>>? _videoTransforms;
    private readonly IReadOnlyList<Func<AudioFrame, AudioFrame>>? _audioTransforms;
    private readonly Action? _videoTransformsReset;
    private readonly Action? _audioTransformsReset;

    /// <summary>
    /// 初始化 <see cref="MediaPlayerFactory"/> 的新实例。
    /// </summary>
    /// <param name="streamFactory">媒体流工厂（Singleton）。</param>
    /// <param name="demuxerFactories">解封装器工厂集合（各后端经 TryAddEnumerable 注册，按序=回退优先级）。</param>
    /// <param name="videoDecoderFactories">视频解码器工厂集合。</param>
    /// <param name="audioDecoderFactories">音频解码器工厂集合。</param>
    /// <param name="subtitleDecoderFactories">字幕解码器工厂集合（可选；仅部分后端注册）。</param>
    /// <param name="videoRendererFactory">视频渲染器工厂（Singleton）。</param>
    /// <param name="audioOutputFactory">音频输出工厂（Singleton）。</param>
    /// <param name="loggerFactory">日志工厂（Singleton）。</param>
    /// <param name="options">播放器配置选项。</param>
    /// <param name="videoTransforms">视频后处理变换链（中立委托，可为 null）。</param>
    /// <param name="audioTransforms">音频后处理变换链（中立委托，可为 null）。</param>
    /// <param name="videoTransformsReset">视频后处理状态重置委托（中立委托，可为 null）。</param>
    /// <param name="audioTransformsReset">音频效果状态重置委托（中立委托，可为 null）。由 Audio 模块把各 <c>IAudioEffect.Reset</c> 合并而来，Core 不依赖 Audio 模块。</param>
    /// <remarks>
    /// 解码/解封装工厂一律取集合首元素作为 <see cref="Create()"/> 的默认后端组；显式指定后端组走
    /// <see cref="Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)"/> 重载。
    /// 不再依赖任何单数注册，避免与「多后端 TryAddEnumerable 集合注册」冲突。
    /// </remarks>
    public MediaPlayerFactory(
        IMediaStreamFactory streamFactory,
        IEnumerable<IMediaDemuxerFactory> demuxerFactories,
        IEnumerable<IVideoDecoderFactory> videoDecoderFactories,
        IEnumerable<IAudioDecoderFactory> audioDecoderFactories,
        IEnumerable<ISubtitleDecoderFactory>? subtitleDecoderFactories,
        IVideoRendererFactory videoRendererFactory,
        IAudioOutputFactory audioOutputFactory,
        ILoggerFactory loggerFactory,
        IOptions<MediaPlayerOptions>? options = null,
        IReadOnlyList<Func<VideoFrame, VideoFrame?>>? videoTransforms = null,
        IReadOnlyList<Func<AudioFrame, AudioFrame>>? audioTransforms = null,
        Action? videoTransformsReset = null,
        Action? audioTransformsReset = null)
    {
        _streamFactory = streamFactory;
        _demuxerFactories = demuxerFactories?.ToList() ?? [];
        _videoDecoderFactories = videoDecoderFactories?.ToList() ?? [];
        _audioDecoderFactories = audioDecoderFactories?.ToList() ?? [];
        _subtitleDecoderFactories = subtitleDecoderFactories?.ToList();
        _videoRendererFactory = videoRendererFactory;
        _audioOutputFactory = audioOutputFactory;
        _loggerFactory = loggerFactory;
        _options = options?.Value ?? new MediaPlayerOptions();
        _videoTransforms = videoTransforms;
        _audioTransforms = audioTransforms;
        _videoTransformsReset = videoTransformsReset;
        _audioTransformsReset = audioTransformsReset;
    }

    /// <inheritdoc />
    /// <remarks>默认取各集合首元素组成后端组（= 第一个注册的已注册后端）。未注册任何后端时抛 <see cref="InvalidOperationException"/>。</remarks>
    public IMediaPlayer Create()
    {
        var demuxer = _demuxerFactories.Count > 0 ? _demuxerFactories[0]
            : throw new InvalidOperationException("未注册任何后端（IMediaDemuxerFactory）。请调用 AddFFmpeg()/AddMediaFoundation()/AddVLCNative() 等注册至少一个后端。");
        var video = _videoDecoderFactories.Count > 0 ? _videoDecoderFactories[0]
            : throw new InvalidOperationException("未注册任何视频解码器后端（IVideoDecoderFactory）。");
        var audio = _audioDecoderFactories.Count > 0 ? _audioDecoderFactories[0]
            : throw new InvalidOperationException("未注册任何音频解码器后端（IAudioDecoderFactory）。");
        var sub = _subtitleDecoderFactories is { Count: > 0 } ? _subtitleDecoderFactories[0] : null;
        return BuildPlayer(demuxer, video, audio, sub);
    }

    /// <inheritdoc />
    public IMediaPlayer Create(
        IMediaDemuxerFactory demuxerFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IAudioDecoderFactory audioDecoderFactory,
        ISubtitleDecoderFactory? subtitleDecoderFactory)
        => BuildPlayer(demuxerFactory, videoDecoderFactory, audioDecoderFactory, subtitleDecoderFactory);

    /// <summary>
    /// 用给定后端组工厂构建 <see cref="MediaPlayer"/>（Session 根）。
    /// 渲染器 / 输出 / 流工厂 / 后处理链 / 配置沿用本 Factory 的字段，仅解封装 + 解码器按入参替换（支持显式指定后端组）。
    /// </summary>
    private IMediaPlayer BuildPlayer(
        IMediaDemuxerFactory demuxerFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IAudioDecoderFactory audioDecoderFactory,
        ISubtitleDecoderFactory? subtitleDecoderFactory)
    {
        var logger = _loggerFactory.CreateLogger<MediaPlayer>();

        // 创建 MediaPlayer（Session 根），传入 Infrastructure 依赖
        var player = new MediaPlayer(
            _streamFactory,
            demuxerFactory,
            videoDecoderFactory,
            audioDecoderFactory,
            subtitleDecoderFactory,
            _videoRendererFactory,
            _audioOutputFactory,
            _loggerFactory,
            logger,
            _videoTransforms,
            _audioTransforms,
            _videoTransformsReset,
            _audioTransformsReset,
            _options);

        // 配置默认值（从 MediaPlayerOptions）
        player.Volume = _options.DefaultVolume;
        player.IsMuted = _options.DefaultMuted;
        player.PlaybackRate = _options.DefaultPlaybackRate;

        return player;
    }
}
