using Microsoft.Extensions.Logging;
using LingFan.Media.Core.Platform;

namespace LingFan.Media.Core;

// MediaPlayer 的 Open 职责段（部分类，由 MediaPlayer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/PlayAsync/PauseAsync/StopAsync/SeekAsync/DisposeAsync/Dispose）在主文件；
// 本段为 OpenAsync 会话装配（流/Demuxer/解码器/管线/缓冲创建），真异步 I/O 边界。
public sealed partial class MediaPlayer
{
    /// <inheritdoc />
    public async Task OpenAsync(IMediaSource source, CancellationToken ct = default)
    {
        // 重置状态机（从 Error/Stopped 恢复到 Idle）后再进入 Opening
        _controller.Reset();
        TransitionState(MediaState.Opening);

        try
        {
            // 1. 创建流（异步优先：CreateAsync 为双版本接口首选，内部做 DNS 解析 + SSRF 校验，真实 I/O 必须 await）
            _stream = await _streamFactory.CreateAsync(source, ct);

            // 2. 创建并打开 Demuxer
            _demuxer = _demuxerFactory.Create(_stream);
            await _demuxer.OpenAsync(_stream, ct);

            // 3. 创建会话
            var duration = _demuxer.Metadata.Duration;
            // 直播语义取自源元数据（VOD 网络源不该被误标为直播）
            var isLive = source is IMediaSourceMetadata { IsLive: true };
            _session = new MediaSession(source, _demuxer.Tracks, _demuxer.Metadata, duration, isLive);

            // 4. 创建管线内部对象
            _clock = new MediaClock();
            _frameQueue = new FrameQueue();
            _sampleQueue = new SampleQueue();
            _synchronizer = new Synchronizer(_clock);
            // 解决方案（LINGFAN_CLOCK_AUDIO_POS=1）：用音频设备真实播放游标驱动视频主时钟，
            // 取代批提交内逐帧 SyncTo 的突发锯齿。闭包延后读取 _audioOutput（此时尚未 Create，调用时已就绪）。
            if (ClockTuning.UseAudioPlaybackClock)
                _synchronizer.SetMasterClockProvider(() => _audioOutput?.GetPlaybackPositionDirect() ?? TimeSpan.Zero);
            _bufferManager = new BufferManager(_demuxer, _loggerFactory.CreateLogger<BufferManager>());

            // 读取线程故障上报：缓冲读取异常（网络中断/读超时/容器损坏）→ 错误事件 + Error 状态。
            // 队列已由 BufferManager 在故障时 Complete，管线自然收场，不在此重复干预。
            // 处理器整体防抛：此处处于读循环异常路径，事件订阅者的异常若逃逸，
            // 会令 ReaderTask 进入 faulted 且 Error 状态未置——故吞入日志，绝不抛回读线程。
            var openedSourceType = source.Type;
            _bufferManager.ReaderFaulted += (_, readEx) =>
            {
                try
                {
                    var errorArgs = new MediaErrorEventArgs(
                        openedSourceType == MediaSourceType.Network ? MediaErrorCode.NetworkError : MediaErrorCode.Unknown,
                        "数据读取中断", readEx, isFatal: true);
                    _controller.OnError(errorArgs);
                    ErrorOccurred?.Invoke(this, errorArgs);
                    TransitionState(MediaState.Error);
                }
                catch (Exception handlerEx)
                {
                    _logger.LogError(handlerEx, "读取故障上报处理异常（已吞除，不影响读线程收场）");
                }
            };

            // 5. 创建解码器（延迟创建，需要 codec 信息）
            // 走异步入口：后端可在创建解码器前让硬解所需的 GPU 设备/上下文就绪。句柄 getter 不隐式创建设备，
            // 未就绪时句柄为零，解码器会静默回退软件解码（不报错）；同步入口没有这个就绪时机。
            var videoTrack = _session.SelectedVideoTrack;
            var audioTrack = _session.SelectedAudioTrack;
            var subtitleTrack = _session.SelectedSubtitleTrack;

            if (videoTrack != null && videoTrack.VideoCodec.HasValue)
            {
                // 透传解封装器提取的编解码器私有配置（H264/H265 的 SPS+PPS）→ 解码器输入类型
                // 同时透传公开开关 EnableHardwareAcceleration：用户显式关闭硬解时须真实生效，
                // 否则两级与（会话级 VideoSettings × 后端级 FFmpegOptions）的会话级恒为 true 而失效。
                var videoInfo = videoTrack.VideoInfo;
                _videoDecoder = await _videoDecoderFactory.CreateAsync(videoTrack.VideoCodec.Value, new VideoSettings
                {
                    CodecConfiguration = videoInfo?.CodecConfiguration ?? default,
                    TimeBase = videoInfo?.TimeBase ?? default,
                    // 透传源宽高：Android MediaCodec 的 configure 在部分设备/解码器要求显式 width/height
                    //（仅 csd-0 推导不足会 EINVAL），D1 视频解码器据此写入输入格式。
                    Width = videoInfo?.Width is > 0 ? videoInfo.Width : null,
                    Height = videoInfo?.Height is > 0 ? videoInfo.Height : null,
                    EnableHardwareAcceleration = _options.EnableHardwareAcceleration
                }, ct);

                // 解码偏好策略：内容复杂度超过软件解码阈值时，应优先硬件加速。
                // 若最终未走硬解（无可用 GPU / 用户显式关闭），明确告警"可能无法实时"，
                // 把静默丢帧转为可行动的提示——但绝不覆盖用户显式关闭硬解的意图。
                if (videoInfo != null
                    && DecodePreferencePolicy.ExceedsSoftwareDecodeCapability(videoInfo, videoTrack.VideoCodec, _options.SoftwareDecodeComplexityThreshold)
                    && !_videoDecoder.IsHardwareAccelerated)
                {
                    long score = DecodePreferencePolicy.ComputeScore(videoInfo, videoTrack.VideoCodec);
                    long threshold = _options.SoftwareDecodeComplexityThreshold ?? DecodePreferencePolicy.DefaultSoftwareDecodeThreshold;
                    _logger.LogWarning(
                        "视频内容超出软件解码实时能力阈值（{Width}x{Height}@{Fps}fps {Codec} {BitDepth}bit，成本分 {Score} ≥ 阈值 {Threshold}）。" +
                        "当前为软件解码，可能无法实时（丢帧）。建议启用硬件加速：注册 AddD3D11Renderer()/AddMediaFoundation() 并提供可用 GPU。",
                        videoInfo.Width, videoInfo.Height, videoInfo.FrameRate, videoTrack.VideoCodec,
                        DecodePreferencePolicy.BitDepthOf(videoInfo.PixelFormat), score, threshold);
                }
            }

            if (audioTrack != null && audioTrack.AudioCodec.HasValue)
            {
                // 从 MediaPlayerOptions 透传音频目标配置：任一字段为 null 时
                // FFmpegAudioDecoder 回退到源媒体参数，B11 重采样仅在显式配置目标时触发。
                var audioSettings = new AudioSettings
                {
                    OutputSampleRate = _options.AudioOutputSampleRate,
                    OutputChannels = _options.AudioOutputChannels,
                    OutputSampleFormat = _options.AudioOutputSampleFormat,
                    // 透传源采样率/声道数：Android MediaCodec 的 configure 在部分解码器要求显式
                    // sample-rate/channel-count（仅 csd-0 推导不足会 EINVAL），D1 音频解码器据此写入输入格式。
                    SourceSampleRate = audioTrack.AudioInfo is { SampleRate: > 0 } ai ? ai.SampleRate : null,
                    SourceChannels = audioTrack.AudioInfo is { Channels: > 0 } aic ? aic.Channels : null,
                    // 透传轨道 extradata（AAC 的 AudioSpecificConfig）。AAC 在 MP4 中为裸流，
                    // 解码器必须据此设置 ctx->extradata 才能解码，否则 avcodec_send_packet 返回 Invalid data。
                    CodecConfiguration = audioTrack.AudioInfo?.CodecConfiguration ?? default,
                    TimeBase = audioTrack.AudioInfo?.TimeBase ?? default,
                };
                _audioDecoder = await _audioDecoderFactory.CreateAsync(audioTrack.AudioCodec.Value, audioSettings, ct);

                // 直通型解码器（MediaFoundation：真正的解码由 SourceReader 内部完成，解码器只包装 PCM 字节）
                // 无从自知输出格式，须由此注入解封装层实测的 PCM 参数。
                // 关键性：下方 _audioOutput.Initialize 以 OutputSampleRate/OutputChannels 打开 WASAPI 设备，
                // 参数错误 → 音高/节奏错乱（早期 MF 解码器硬编码 44100/2，遇 48kHz 媒体即失真）。
                // FFmpeg 解码器不实现此接口（自带 codec context，参数自知），pattern matching 自动跳过。
                if (_audioDecoder is IAudioSourceFormatAware audioFormatAware
                    && audioTrack.AudioInfo is { SampleRate: > 0, Channels: > 0 } audioInfo)
                {
                    audioFormatAware.SetSourceFormat(
                        audioInfo.SampleRate,
                        audioInfo.Channels,
                        audioInfo.BitsPerSample switch
                        {
                            32 => SampleFormat.S32,
                            _ => SampleFormat.S16
                        });
                }
            }

            // 创建帧对象池并注入解码器（Session 级）
            _videoFramePool = new FramePool<VideoFrame>(
                factory: static () => new VideoFrame(),
                reset: static frame => frame.Reset(0, 0, default, null, default, default, false),
                maxSize: 16);
            _audioFramePool = new FramePool<AudioFrame>(
                factory: static () => new AudioFrame(),
                // 零拷贝 AudioFrame 持有原生引用计数所有者，Return 时必须经
                // Reset 释放旧所有者（原生引用计数减一），否则池内滞留帧会泄漏原生内存。
                reset: static frame => frame.Reset(default, 0, 0, SampleFormat.S16, default, default, 0),
                maxSize: 16);

            if (_videoDecoder is IFramePoolAware<VideoFrame> videoPoolAware)
                videoPoolAware.SetFramePool(_videoFramePool);
            if (_audioDecoder is IFramePoolAware<AudioFrame> audioPoolAware)
                audioPoolAware.SetFramePool(_audioFramePool);

            // 6. 创建渲染器和输出
            _videoRenderer = _videoRendererFactory.Create();
            _audioOutput = _audioOutputFactory.Create();

            // 修复：音频输出设备必须显式初始化。WASAPI 以固定采样率打开设备，
            // Submit 不校验采样率——设备率必须与解码器实际输出率一致，否则节奏/音高错乱。
            // Initialize 为同步 COM 原生边界，保持 sync void（非伪异步），不引入 await。
            if (_audioDecoder != null)
            {
                // 契约对称（InitializeAsync 必须先于 Initialize，WASAPI 强制——设备枚举/COM 准备前置）。
                // 无 I/O 的实现返回 Task.CompletedTask（非伪异步）。
                await _audioOutput.InitializeAsync(ct);
                // 采样率可能在首帧解码后才知道（AAC 经 FFmpeg 时 OutputSampleRate 初始化为 0）。
                // 仅当 OpenAsync 时刻已知合法采样率才在此显式初始化设备；否则延迟到首个音频帧
                // Submit 时由输出（如 WasapiRenderLoop）按真实采样率惰性打开，避免 0Hz 触发
                // ArgumentOutOfRangeException（WASAPI 设备必须以固定率打开）。
                if (_audioDecoder.OutputSampleRate > 0 && _audioDecoder.OutputChannels > 0)
                    _audioOutput.Initialize(_audioDecoder.OutputSampleRate, _audioDecoder.OutputChannels);
            }

            // 7. 设置轨道索引
            _bufferManager.SetTrackIndices(videoTrack?.Index ?? -1, audioTrack?.Index ?? -1);

            // 8. 创建管线
            if (_videoDecoder != null && _videoRenderer != null && videoTrack != null)
            {
                _videoPipeline = new VideoPipeline(
                    _bufferManager.VideoPacketQueue, _videoDecoder, _videoRenderer,
                    _frameQueue, _synchronizer, _clock,
                    _loggerFactory.CreateLogger<VideoPipeline>(),
                _videoFramePool,
                _videoTransforms,
                _videoTransformsReset,
                // 统一帧投递：所有帧经 FrameChannel 扇出至订阅的 Sink（无头计算 / Skia 软渲染 / D3D11 零拷贝 GPU 呈现）。
                // 有头与无头同饮此通道；D3D11 经 D3D11GpuPresenter 订阅同一通道 Present，零拷贝不变。
                videoFrameSink: frame => _frameChannel.Emit(frame));
            }

            if (_audioDecoder != null && _audioOutput != null && audioTrack != null)
            {
                _audioPipeline = new AudioPipeline(
                    _bufferManager.AudioPacketQueue, _audioDecoder, _audioOutput,
                    _sampleQueue, _synchronizer, _clock,
                    _loggerFactory.CreateLogger<AudioPipeline>(),
                    _audioFramePool,
                    _audioTransforms,
                    _audioTransformsReset,
                    audioDataSink: frame => _audioDataSink?.Invoke(frame));
            }

            // 9. 字幕轨道（条件创建，仅有字幕轨道时）
            if (subtitleTrack != null && _subtitleDecoderFactory != null)
            {
                _subtitleDecoder = await _subtitleDecoderFactory.CreateAsync(subtitleTrack, ct);
                _subtitleProcessor = new SubtitleProcessor(
                    _subtitleDecoder, _bufferManager.SubtitlePacketQueue, _clock,
                    _loggerFactory.CreateLogger<SubtitleProcessor>());
                _subtitleProcessor.SubtitleReceived += OnSubtitleReceived;
            }

            // 10. 管线宿主
            _pipelineHost = new MediaPipelineHost();
            _pipelineHost.Attach(_videoPipeline, _audioPipeline, _subtitleProcessor);
            _pipelineHost.PlaybackCompleted += OnPlaybackCompleted;

            // 11. 配置网络流缓冲
            if (isLive)
                _bufferManager.ConfigureForNetworkStream();

            // 12. 启动缓冲
            TransitionState(MediaState.Buffering);
            await _bufferManager.StartAsync(ct);

            // 13. 就绪
            TransitionState(MediaState.Idle);

            // 13.5 应用处理模式（同步器 / 音频输出已就绪；确保 Open 前设置的 Mode 生效）
            ApplyMode();

            // 14. 启动位置定时器（33ms = ~30fps）
            _positionTimer = new Timer(OnPositionTimer, null,
                TimeSpan.FromMilliseconds(33), TimeSpan.FromMilliseconds(33));
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync();
            TransitionState(MediaState.Idle);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开媒体源失败");
            await CleanupAsync();
            var errorArgs = new MediaErrorEventArgs(
                MediaErrorCode.SourceOpenFailed, "打开媒体源失败", ex, isFatal: true);
            _controller.OnError(errorArgs);
            TransitionState(MediaState.Error);
            ErrorOccurred?.Invoke(this, errorArgs);
            throw;
        }
    }
}
