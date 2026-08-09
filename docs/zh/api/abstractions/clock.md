# 时钟与缓冲

## IMediaClock

命名空间：`LingFan.Media.Abstractions`

媒体时钟——A/V 同步的核心。**纯内存**，无 I/O，无 `CancellationToken`。**线程安全**（lock / Interlocked）：视频与音频管线都会读取它。**不可注册为 Singleton**——多个播放器会争用同一个时钟。

```csharp
public interface IMediaClock
{
    TimeSpan Position { get; }
    float Speed { get; set; }
    bool IsRunning { get; }
    ClockSyncSource SyncSource { get; set; }
    TimeSpan SyncThreshold { get; set; }    // default 50 ms
    TimeSpan DropThreshold { get; set; }    // default 200 ms
    void Start();
    void Pause();
    void Reset();
    void SeekTo(TimeSpan position);
    void SyncTo(TimeSpan masterPosition);
}
```

| 成员 | 说明 |
|--------|-------|
| `Position` | 当前时钟位置。 |
| `Speed` | 时钟速度（1.0 = 正常）。 |
| `IsRunning` | 时钟是否在走。 |
| `SyncSource` | 主同步源：`Audio` / `Video` / `System`。 |
| `SyncThreshold` | 同步器介入前的最大漂移（默认 50 ms）。 |
| `DropThreshold` | 超过此值的漂移将导致丢帧（默认 200 ms）。 |
| `Start()` / `Pause()` / `Reset()` | 生命周期（纯内存）。 |
| `SeekTo(pos)` | 跳转到某个位置。 |
| `SyncTo(masterPosition)` | 对齐到主时钟。 |

> 音频通常是主时钟（`LINGFAN_CLOCK_AUDIO_POS = 1`）。视频帧通过将自身的 PTS 与该时钟比较来决定呈现或丢弃。

## IBufferManager

命名空间：`LingFan.Media.Abstractions`

管理解码缓冲区。

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `BufferedDuration` / `BufferedBytes` | `TimeSpan` / `long` | 已缓冲的量。 |
| `IsReady` | `bool` | 可播放。 |
| `State` | `BufferState` | `Empty` / `Buffering` / `Ready` / `Starved`。 |
| `TargetDuration` | `TimeSpan` | 目标缓冲时长。 |
| `BufferProgressChanged` | `event` | 进度通知。 |
| `StartAsync(ct)` / `Stop()` / `Clear()` | 方法 | 缓冲控制。 |

## IMediaComponent

命名空间：`LingFan.Media.Abstractions`

每个管线组件（解码器、解复用器、渲染器、音频输出）的公共基接口。

```csharp
public interface IMediaComponent : IDisposable, IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
}
```

`IVideoDecoder` / `IAudioDecoder` / `ISubtitleDecoder` / `IVideoRenderer` / `IAudioOutput` / `IMediaDemuxer` 全部派生自它。
