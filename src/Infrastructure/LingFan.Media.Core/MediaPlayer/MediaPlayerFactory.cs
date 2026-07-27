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
    private readonly IMediaDemuxerFactory _demuxerFactory;
    private readonly IVideoDecoderFactory _videoDecoderFactory;
    private readonly IAudioDecoderFactory _audioDecoderFactory;
    private readonly ISubtitleDecoderFactory? _subtitleDecoderFactory;
    private readonly IVideoRendererFactory _videoRendererFactory;
    private readonly IAudioOutputFactory _audioOutputFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly MediaPlayerOptions _options;

    // V2-06 C5/C6: 后处理变换链（中立 BCL 委托）。
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
    /// <param name="demuxerFactory">解封装器工厂（Singleton）。</param>
    /// <param name="videoDecoderFactory">视频解码器工厂（Singleton）。</param>
    /// <param name="audioDecoderFactory">音频解码器工厂（Singleton）。</param>
    /// <param name="subtitleDecoderFactory">字幕解码器工厂（Singleton，可为 null）。</param>
    /// <param name="videoRendererFactory">视频渲染器工厂（Singleton）。</param>
    /// <param name="audioOutputFactory">音频输出工厂（Singleton）。</param>
    /// <param name="loggerFactory">日志工厂（Singleton）。</param>
    /// <param name="options">播放器配置选项。</param>
    /// <param name="videoTransforms">视频后处理变换链（中立委托，可为 null）。</param>
    /// <param name="audioTransforms">音频后处理变换链（中立委托，可为 null）。</param>
    /// <param name="videoTransformsReset">视频后处理状态重置委托（中立委托，可为 null）。</param>
    /// <param name="audioTransformsReset">音频效果状态重置委托（V2-08.1，中立委托，可为 null）。由 Audio 模块把各 <c>IAudioEffect.Reset</c> 合并而来，Core 不依赖 Audio 模块。</param>
    public MediaPlayerFactory(
        IMediaStreamFactory streamFactory,
        IMediaDemuxerFactory demuxerFactory,
        IVideoDecoderFactory videoDecoderFactory,
        IAudioDecoderFactory audioDecoderFactory,
        ISubtitleDecoderFactory? subtitleDecoderFactory,
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
        _demuxerFactory = demuxerFactory;
        _videoDecoderFactory = videoDecoderFactory;
        _audioDecoderFactory = audioDecoderFactory;
        _subtitleDecoderFactory = subtitleDecoderFactory;
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
    public IMediaPlayer Create()
    {
        var logger = _loggerFactory.CreateLogger<MediaPlayer>();

        // 创建 MediaPlayer（Session 根），传入 Infrastructure 依赖
        var player = new MediaPlayer(
            _streamFactory,
            _demuxerFactory,
            _videoDecoderFactory,
            _audioDecoderFactory,
            _subtitleDecoderFactory,
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
