# Core（`LingFan.Media.Core`）

编排模块。它拥有**会话对象图** —— 时钟、解复用器、解码器、缓冲区、同步器以及两条管线 —— 并且是唯一知道它们如何组合在一起的模块。

> **DI 规范。** `MediaPlayerFactory` 与 `MediaClock` **不**作为共享 Singleton 注册。该工厂是一个无状态的 Singleton *构造器*，在每次 `Create()` 时 `new` 出一个全新的 `MediaPlayer`（及其整个会话）；时钟是每会话独立的，因此并发的播放器永不共享同一个时间基。实际的注册方式见 [Extensions](/zh/api/infrastructure/extensions)。

## MediaPlayer

Namespace: `LingFan.Media.Core`

具体的 `IMediaPlayer`。**线程安全**：公共方法可从任意线程调用。这是你从 DI 解析出的类型（作为 `IMediaPlayer`），也是每个工厂返回的类型。

```csharp
public sealed class MediaPlayer : IMediaPlayer
```

### 构造函数

通常你绝不会直接调用它 —— `MediaPlayerFactory.Create()` 负责构建。此处列出仅为完整性 / 高级手动组合之用。

```csharp
public MediaPlayer(
    IMediaStreamFactory streamFactory,
    IMediaDemuxerFactory demuxerFactory,
    IVideoDecoderFactory videoDecoderFactory,
    IAudioDecoderFactory audioDecoderFactory,
    ISubtitleDecoderFactory? subtitleDecoderFactory,
    IVideoRendererFactory videoRendererFactory,
    IAudioOutputFactory audioOutputFactory,
    ILoggerFactory loggerFactory,
    ILogger<MediaPlayer> logger,
    IReadOnlyList<Func<VideoFrame, VideoFrame?>>? videoTransforms = null,
    IReadOnlyList<Func<AudioFrame, AudioFrame>>? audioTransforms = null,
    Action? videoTransformsReset = null,
    Action? audioTransformsReset = null,
    MediaPlayerOptions? options = null)
```

### 属性

| 属性 | 类型 | 说明 |
|----------|------|-------|
| `State` | `MediaState` | 由内部 `PlaybackController` 状态机驱动；非法转换会被忽略并记录日志。 |
| `Position` | `TimeSpan` | 从 `MediaClock` 读取。`OpenAsync` 之前 / `StopAsync` 之后为 `0`。 |
| `Duration` | `TimeSpan` | 来自解复用器元数据。 |
| `Volume` | `float` | 限制在 `0.0`–`1.0`。立即应用于音频输出（静音 ⇒ `0`）。 |
| `IsMuted` | `bool` | 为 `true` 时，输出音量被强制为 `0`，且不改变 `Volume`。 |
| `PlaybackRate` | `float` | 转发至 `MediaClock.Speed`（影响时间基缩放）。 |
| `Mode` | `ProcessingMode` | `RealTime`（默认，音视频同步 + 实时节奏）对比 `Fastest`（禁用同步 —— 用于转码 / 离线 ML）。立即生效；在 `Fastest` 下同步器放行所有帧，无头音频输出停止实时节流。 |
| `Session` | `IMediaSession?` | 在 `OpenAsync` 内部创建的会话；打开前为 `null`。 |
| `VideoDroppedFrames` | `long` | 累计丢弃的视频帧数（诊断用）。 |

### 事件

| 事件 | 签名 | 说明 |
|-------|-----------|-------|
| `StateChanged` | `EventHandler<MediaStateChangedEventArgs>?` | 状态转换。 |
| `ErrorOccurred` | `EventHandler<MediaErrorEventArgs>?` | 致命/非致命错误。 |
| `PositionChanged` | `EventHandler<TimeSpan>?` | 由读取时钟的 33 ms 定时器触发。 |
| `SubtitleReceived` | `EventHandler<SubtitleFrame?>?` | `null` 清空显示。 |
| `AudioDataAvailable` | `Action<AudioFrame>?` | **只读借用** —— 在回调内同步复制；绝不 `Dispose`/持有。 |
| `VideoFrameAvailable` | `Action<VideoFrame>?` | **只读借用** —— 规则相同。订阅会将你的委托包装进 `DelegateFrameSink` 并订阅内部 `FrameChannel`。 |

### 方法

| 方法 | 返回 | 说明 |
|--------|---------|-------|
| `OpenAsync(IMediaSource, CancellationToken ct = default)` | `Task` | 14 步打开：stream → demuxer → `MediaSession` → clock/synchronizer/buffer manager → decoders → frame pools → renderer/audio output → track indices → video/audio pipelines → subtitle processor → pipeline host → network buffering → `Buffering` → `Idle` → `ApplyMode` → position timer。真正的异步（网络连接 + 缓冲区启动 + 流读取）。可取消。 |
| `PlayAsync()` | `Task` | 开始播放。**重放路径**：当 `State == Ended` 时，它 seek 到 `0`，重置播放时钟，重启管线（`MediaPipelineHost.StartAsync` —— 视频预滚 → 音频启动 → 呈现释放），然后启动时钟。返回一个真正的 `Task`（启动被 awaited）。 |
| `PauseAsync()` | `Task` | 纯内存操作（`Clock.Pause` + 主机 `Pause`）。返回 `Task.CompletedTask`。 |
| `StopAsync(CancellationToken ct = default)` | `Task` | 纯内存操作。返回 `Task.CompletedTask`。 |
| `SeekAsync(TimeSpan position, CancellationToken ct = default)` | `Task` | 停止缓冲区读取器，清空队列，seek 解复用器，冲刷管线，然后重启缓冲并**将管线重新指向重建后的数据包队列**（这是必需的，以便 EOF 之后的重放不会读取已完成的通道）。真正的异步。 |
| `Dispose()` | `void` | 同步回退清理 —— 它**自己的**同步路径（绝不 `DisposeAsync().GetAwaiter().GetResult()`；那将是伪异步）。 |
| `DisposeAsync()` | `ValueTask` | 有序的、逐步 `try/catch` 保护的释放。共享的 singleton 渲染器被刻意**不**在此释放（其生命周期属于渲染器工厂）。 |

> **帧路由（唯一规则）。** 每一个视频帧都经由单一的 `FrameChannel.Emit(frame)` 输出。公共的 `VideoFrameAvailable` 事件是该通道之上的一个门面。UI 呈现接收器、无头计算接收器与 GPU 零拷贝呈现器都从同一个通道取用，仅在终止动作上不同。不存在"有头 vs. 无头"的分叉。

## MediaPlayerFactory

Namespace: `LingFan.Media.Core`

无状态 Singleton 构造器。解析 `IEnumerable<...Factory>` 集合（每个后端通过 `TryAddEnumerable` 注册）并据此构建 `MediaPlayer`。

```csharp
public sealed class MediaPlayerFactory : IMediaPlayerFactory
```

| 成员 | 说明 |
|--------|-------|
| `Create()` | 取每个工厂集合的**第一个**元素作为默认后端组。若未注册任何后端，抛出 `InvalidOperationException`。 |
| `Create(IMediaDemuxerFactory, IVideoDecoderFactory, IAudioDecoderFactory, ISubtitleDecoderFactory?)` | 以显式强制的后端组构建播放器。 |
| ctor | `(IMediaStreamFactory, IEnumerable<IMediaDemuxerFactory>, IEnumerable<IVideoDecoderFactory>, IEnumerable<IAudioDecoderFactory>, IEnumerable<ISubtitleDecoderFactory>?, IVideoRendererFactory, IAudioOutputFactory, ILoggerFactory, IOptions<MediaPlayerOptions>?, videoTransforms?, audioTransforms?, videoTransformsReset?, audioTransformsReset?)` |

该构造函数由在 `AddLingFanMedia` 中注册的键控 `"composer"` `IMediaPlayerFactory` 惰性调用，因此在它读取构建器的变换链之前，所有链式 `AddXxx()` 注册都已完备。

> 在开放盒式流程中，你**不**直接使用此工厂。`BackendFallbackMediaPlayerFactory` 在选定后端组后调用键控的 `"composer"` 实例。

## MediaClock

Namespace: `LingFan.Media.Core`

`sealed class : IMediaClock`。纯内存，基于 `Stopwatch`，以 `lock` 保护并发的视频/音频管线访问。**不要注册为 Singleton** —— 它是每个会话独立的。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Position` | `TimeSpan` | 运行时为 `base + elapsed * speed`，否则为 `base`。 |
| `Speed` | `float` | 改变速度会重新设定 `Position` 基准并重启秒表。 |
| `IsRunning` | `bool` | |
| `SyncSource` | `ClockSyncSource` | 默认 `Audio`。 |
| `SyncThreshold` | `TimeSpan` | 默认 `50 ms`。 |
| `DropThreshold` | `TimeSpan` | 默认 `200 ms`。 |
| `Start()` / `Pause()` / `Reset()` | `void` | |
| `SeekTo(TimeSpan)` | `void` | 重新基准到一个位置；若正在运行则重启秒表。 |
| `SyncTo(TimeSpan masterPosition)` | `void` | 主时钟校正。默认硬对齐；设置 `LINGFAN_CLOCK_SMOOTH=1` 时应用一阶低通 + 斜率钳制（仅大的漂移硬对齐）。遵循 `LINGFAN_PACING_DIAG=1` 用于诊断。 |

## MediaSession

Namespace: `LingFan.Media.Core`

`sealed class : IMediaSession`。在 `OpenAsync` 内部创建；保存轨道、元数据与轨道选择。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Source` | `IMediaSource` | 已打开的源。 |
| `Metadata` | `MediaMetadata` | 容器元数据。 |
| `VideoTracks` / `AudioTracks` / `SubtitleTracks` | `IReadOnlyList<MediaTrack>` | 按类型过滤。 |
| `SelectedVideoTrack` / `SelectedAudioTrack` / `SelectedSubtitleTrack` | `MediaTrack?` | 受 `lock` 保护的 setter；默认 = `IsDefault` 轨道，否则为第一个。 |
| `Duration` | `TimeSpan` | |
| `IsLive` | `bool` | 网络源为 `true`。 |
| `CloseAsync(CancellationToken ct = default)` | `Task` | 仅释放会话级信息；管线资源由 `MediaPlayer.DisposeAsync` 释放。 |

## MediaPlayerOptions

Namespace: `LingFan.Media.Abstractions.Models.Settings`（共享配置模型，归 Core 的消费者所有）

| 属性 | 类型 | 默认值 |
|----------|------|---------|
| `DefaultVolume` | `float` | `1.0f` |
| `DefaultMuted` | `bool` | `false` |
| `DefaultPlaybackRate` | `float` | `1.0f` |
| `EnableHardwareAcceleration` | `bool` | `true` |
| `VideoFrameQueueCapacity` | `int` | `30` |
| `AudioSampleQueueCapacity` | `int` | `60` |
| `LocalBufferTarget` | `TimeSpan` | `5 s` |
| `NetworkBufferTarget` | `TimeSpan` | `30 s` |
| `AudioOutputSampleRate` | `int?` | `null` (= 源采样率) |
| `AudioOutputChannels` | `int?` | `null` |
| `AudioOutputSampleFormat` | `SampleFormat?` | `null` |

由 `AddLingFanMedia` 绑定到 `IOptions<MediaPlayerOptions>`，以便宿主配置（如 `DefaultVolume`）传播进 Core 的工厂。

## MediaPipelineHost

Namespace: `LingFan.Media.Core`

围绕视频/音频/字幕组件的轻量生命周期包装器。除启动编排外，方法大多为同步 `void`。

| 成员 | 说明 |
|--------|-------|
| `Attach(VideoPipeline?, AudioPipeline?, SubtitleProcessor?)` | 连接三个组件。 |
| `StartAsync()` | 编排顺序：启动视频 → `WaitForPrerollAsync` → `SignalAudioReady` → `WaitForFirstFramePresentedAsync` → 启动音频 → 启动字幕。 |
| `Pause()` / `Stop()` / `Flush()` / `FlushAsync()` / `Detach()` | |
| `PlaybackCompleted` | 当两条音视频管线都排空时触发的事件（驱动 `MediaPlayer` → `Ended`）。 |
| `VideoDroppedFrames` | `long`（转发）。 |

## VideoPipeline / AudioPipeline

Namespaces: `LingFan.Media.Core.Playback`

`sealed class : IAsyncDisposable, IDisposable`。每个都运行一个**解码循环**（`Task.Run`），与**实时管线循环**（长时间运行、最高优先级）解耦，后者窥看数据包队列、为第一帧设门、并在主时钟下呈现/提交（带有看门狗，在主时钟停滞时发出一帧）。

### VideoPipeline

| 成员 | 说明 |
|--------|-------|
| ctor | `(Channel<MediaPacket> packetQueue, IVideoDecoder, IVideoRenderer, FrameQueue, Synchronizer, IMediaClock, ILogger, IFramePool<VideoFrame>? framePool=null, IReadOnlyList<Func<VideoFrame,VideoFrame?>>? processors=null, Action? processorReset=null, Action<VideoFrame>? videoFrameSink=null)` |
| `IsRunning`, `FrameQueueSize`, `DroppedFrames` | 诊断 |
| `Start()` | |
| `WaitForPrerollAsync(ct)` | 首帧门。 |
| `SignalAudioReady()` | 释放音频启动门。 |
| `WaitForFirstFramePresentedAsync(timeout, ct)` | 音视频启动排序。 |
| `Pause()` / `Stop()` / `Flush()` / `FlushAsync()` / `Dispose()` / `DisposeAsync()` | |
| `SetPacketQueue(Channel<MediaPacket>)` | internal —— seek/EOF 之后重新指向。 |
| `Completed` | 事件。 |

### AudioPipeline

| 成员 | 说明 |
|--------|-------|
| ctor | `(Channel<MediaPacket>, IAudioDecoder, IAudioOutput, SampleQueue, Synchronizer, IMediaClock, ILogger, IFramePool<AudioFrame>? framePool=null, IReadOnlyList<Func<AudioFrame,AudioFrame>>? transforms=null, Action? effectReset=null, Action<AudioFrame>? audioDataSink=null)` |
| `IsRunning`, `SampleQueueSize`, `OutputLatency` | 诊断 |
| `StartAsync()` | 通过 `output.BeginStreamingAsync` 预滚。 |
| `Pause()` / `Stop()` / `Flush()` / `FlushAsync()` / `Dispose()` / `DisposeAsync()` | |
| `SetPacketQueue(Channel<MediaPacket>)` | internal —— seek/EOF 之后重新指向。 |
| _(internal)_ `SubmitBatch` | 音频帧通过**内部**方法以小量子（`MaxSubmitChunkMs = 40`）冲刷到输出 —— **不**属于公共 API 接口面。记录它仅为解释节流行为。 |
| `Completed` | 事件。 |

## BufferManager

Namespace: `LingFan.Media.Core`

`sealed class : IBufferManager`。拥有每条轨道的数据包队列（有界 `Channel`，容量 256/512）与缓冲状态机。

| 成员 | 说明 |
|--------|-------|
| ctor | `(IMediaDemuxer demuxer, ILogger<BufferManager> logger)` |
| `BufferedDuration` / `BufferedBytes` / `IsReady` / `State` (`BufferState`) / `TargetDuration` | 诊断 |
| `VideoPacketQueue` / `AudioPacketQueue` / `SubtitlePacketQueue` | 交给管线的 `Channel<MediaPacket>` |
| `BufferProgressChanged` | `EventHandler<BufferProgressEventArgs>?` 事件 |
| `SetTrackIndices(int videoTrackIndex, int audioTrackIndex)` | |
| `ConfigureForNetworkStream()` | |
| `StartAsync(ct)` / `Stop()` / `Clear()` / `Complete()` / `ResetQueues()` | `ResetQueues` 在 EOF 之后重建通道，以便重放不会读取已完成的通道 |
| `ReaderTask` | internal 后台读取任务 |

## Synchronizer（public 具体类型）

Namespace: `LingFan.Media.Core` —— `public sealed class`（具体类型；实现**无**契约接口）。驱动音视频对齐。

- ctor `(IMediaClock clock, TimeSpan audioLatency = default)`
- `RealTimeSync` —— 当为 `false`（Fastest 模式）时放行所有视频帧。
- `PresentationLatency` —— 默认 `1000/60 ms`。
- `SetMasterClockProvider(Func<TimeSpan>?)` —— 音频播放位置提供器（默认主时钟；`LINGFAN_CLOCK_AUDIO_POS=1`）。
- `OnAudioFrameSubmitted(AudioFrame)` —— 推进主时钟。
- `CheckVideoFrame(VideoFrame) → SyncAction` —— 返回 `Present` / `Wait` / `Drop`（`public enum SyncAction`，位于 `LingFan.Media.Core.Clock`，独立文件 —— **不**嵌套在 `Synchronizer` 内）。
- `OnSeek(TimeSpan)` —— seek 时重新同步。

## `FramePool<T>`（public）与 `FrameChannel`（internal）

| 类型 | 说明 |
|------|-------|
| `FramePool<T> : IFramePool<T>, IDisposable where T : class`（`LingFan.Media.Core.Buffer`） | ctor `(Func<T> factory, Action<T>? reset = null, int maxSize = 16)`；`Rent()` / `Return(T)`（溢出时释放）/ `Dispose()`。AOT 友好的 `ConcurrentStack`。 |
| `FrameChannel : IFrameChannel`（`LingFan.Media.Core.Playback`，`internal`） | 线程安全的多播。`Subscribe(IFrameSink)` → `IDisposable`、`Emit(VideoFrame)`、`Unsubscribe`。接收器是**只读借用者** —— 绝不 `Dispose`。管线在 `Emit` 之后的 `finally` 中释放帧。 |
