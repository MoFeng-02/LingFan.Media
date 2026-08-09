# 播放器与会话

## IMediaPlayer

命名空间：`LingFan.Media.Abstractions`

顶层播放门面。**线程安全**：公共方法可从任意线程调用。

```csharp
public interface IMediaPlayer : IDisposable, IAsyncDisposable
```

### 方法

| 方法 | 返回 | 说明 |
|--------|---------|-------|
| `OpenAsync(IMediaSource source, CancellationToken ct = default)` | `Task` | 打开一个媒体源。可取消。真实异步（解复用器打开 + 缓冲启动 + 流读取）。 |
| `PlayAsync()` | `Task` | 开始播放。纯内存状态切换——无 CT。返回 `Task.CompletedTask`。 |
| `PauseAsync()` | `Task` | 暂停。纯内存操作。返回 `Task.CompletedTask`。 |
| `StopAsync(CancellationToken ct = default)` | `Task` | 停止。接口契约；实现可返回 `Task.CompletedTask`。 |
| `SeekAsync(TimeSpan position, CancellationToken ct = default)` | `Task` | 跳转。真实异步（解复用器跳转依赖流的 seek/读取）。 |

### 属性

| 属性 | 类型 | 说明 |
|----------|------|-------|
| `State` | `MediaState` | 当前播放状态。 |
| `Position` | `TimeSpan` | 当前位置（从时钟读取）。 |
| `Duration` | `TimeSpan` | 媒体总时长。 |
| `Volume` | `float` | 音量，0.0–1.0。 |
| `IsMuted` | `bool` | 静音标志。 |
| `PlaybackRate` | `float` | 播放速率（1.0 = 正常）。 |
| `Mode` | `ProcessingMode` | 无界面 / 服务端处理模式。`Fastest` 禁用 A/V 同步与实时节奏控制——用于转码 / 离线 ML 批处理作业。可在 `OpenAsync` 之前或之后设置；立即生效（同步，无 I/O）。 |
| `Session` | `IMediaSession?` | 当前会话（打开前为 null）。 |
| `VideoDroppedFrames` | `long` | 累计丢弃的视频帧数（诊断用）。若结束时约等于文件帧数 − 已呈现帧数，则尾部帧被同步器丢弃。 |

### 事件

| 事件 | 签名 | 说明 |
|-------|-----------|-------|
| `StateChanged` | `EventHandler<MediaStateChangedEventArgs>` | 状态切换。 |
| `ErrorOccurred` | `EventHandler<MediaErrorEventArgs>` | 错误。 |
| `PositionChanged` | `EventHandler<TimeSpan>` | 位置更新。 |
| `SubtitleReceived` | `EventHandler<SubtitleFrame?>` | 字幕帧到达（`null` = 清除显示）。 |
| `AudioDataAvailable` | `Action<AudioFrame>?` | 音频 PCM 到达。**只读借用**——回调内同步复制所需数据；切勿跨线程 `Dispose` 或持有帧引用。 |
| `VideoFrameAvailable` | `Action<VideoFrame>?` | 视频帧到达。**只读借用**——规则同上。仅在 UI 已订阅时触发（Skia 模式）；在原生 GPU 模式下管线直接呈现。 |

> **帧所有权：** `AudioDataAvailable` / `VideoFrameAvailable` 传递的是只读借用。生产者拥有该帧；你必须同步复制所需数据，切勿 `Dispose` 它。

## IMediaSession

命名空间：`LingFan.Media.Abstractions`

每次播放的会话状态，在 `OpenAsync` 内创建。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Source` | `IMediaSource?` | 已打开的源。 |
| `Metadata` | `MediaMetadata?` | 容器元数据。 |
| `Tracks` | `IReadOnlyList<MediaTrack>` | 全部轨道。 |
| `VideoTracks` / `AudioTracks` / `SubtitleTracks` | `IReadOnlyList<…TrackInfo>` | 按类型过滤。 |
| `SelectedVideoTrack` / `SelectedAudioTrack` / `SelectedSubtitleTrack` | `MediaTrack?` | 当前选中的轨道。 |
| `Duration` | `TimeSpan` | 时长。 |
| `IsLive` | `bool` | 直播流标志。 |
| `CloseAsync()` | `Task` | 关闭会话。 |

## IMediaPlayerFactory

命名空间：`LingFan.Media.Abstractions`

创建 `IMediaPlayer` 实例。

```csharp
public interface IMediaPlayerFactory
{
    IMediaPlayer Create();
    IMediaPlayer Create(IMediaDemuxerFactory demuxerFactory,
                        IVideoDecoderFactory videoDecoderFactory,
                        IAudioDecoderFactory audioDecoderFactory,
                        ISubtitleDecoderFactory? subtitleDecoderFactory = null);
}
```

- `Create()` —— 使用 DI 注册的后端集创建播放器（受回退影响）。
- `Create(...)` —— 通过传入工厂接口强制指定某后端组。

## IBackendRegistry

命名空间：`LingFan.Media.Abstractions`

已注册后端的只读视图。

```csharp
public interface IBackendRegistry
{
    IReadOnlyList<BackendDescriptor> Backends { get; }
}
```

## BackendDescriptor

命名空间：`LingFan.Media.Abstractions`

一个**已注册后端的只读描述**——它持有**工厂接口**（DI 解析的 Singleton 服务），而非播放器 / 后端实例。

```csharp
public sealed record BackendDescriptor(
    string Name,
    IMediaDemuxerFactory Demuxer,
    IVideoDecoderFactory VideoDecoder,
    IAudioDecoderFactory AudioDecoder,
    ISubtitleDecoderFactory? SubtitleDecoder);
```

当选择一个后端组时，将这些工厂接口传给 `IMediaPlayerFactory.Create(...)` 来构建会话。**勿将查找（接口）与实例（播放器）混淆。**

## MediaBackendUnsupportedException

命名空间：`LingFan.Media.Abstractions`

`sealed class`。当**每一个**已注册后端都未能打开源时，由回退中间件抛出。
