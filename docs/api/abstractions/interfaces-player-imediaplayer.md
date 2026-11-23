---
title: "IMediaPlayer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaPlayer", "OpenAsync", "PlayAsync", "PauseAsync", "StopAsync", "SeekAsync", "State", "Position", "Duration", "Volume", "IsMuted", "PlaybackRate", "Mode", "Session", "StateChanged", "ErrorOccurred", "PositionChanged", "SubtitleReceived", "AudioDataAvailable", "VideoFrameAvailable", "VideoDroppedFrames"]
lang: "en-US"
---
# IMediaPlayer

Source: [`IMediaPlayer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs)

最高层媒体播放器接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaPlayer : IDisposable, IAsyncDisposable
```

## Remarks

线程安全：公共方法线程安全，可在任意线程调用。

CancellationToken 传播策略：

- 需要 CT 的方法：OpenAsync / StopAsync / SeekAsync
- 不需要 CT 的方法：PlayAsync / PauseAsync——不接 CT 是有意为之：这两个方法签名上不提供取消，
实现内部可能仍含等待（seek 复位、管线启停），失败经 `ErrorOccurred` 上报而非靠取消令牌

## Members

### OpenAsync

```csharp
Task OpenAsync(IMediaSource source, CancellationToken ct = default);
```

打开媒体源。支持取消。

### PlayAsync

```csharp
Task PlayAsync();
```

开始播放（纯内存状态转换，无 CT）。

**前置条件**：媒体源须已成功打开，且状态处于就绪（Idle）/Paused/Stopped/Ended；
未打开、Opening/Buffering/Error 态调用抛 `InvalidOperationException`（不启动管线、不改状态）。

**幂等**：Playing 态重复调用直接返回，不重启管线。

**失败语义**：守卫拒绝抛异常（不发错误事件）；启动过程中的失败经
`ErrorOccurred` 上报、不抛出（与既有行为一致）。

### PauseAsync

```csharp
Task PauseAsync();
```

暂停播放（纯内存状态转换，无 CT）。

### StopAsync

```csharp
Task StopAsync(CancellationToken ct = default);
```

停止播放。不阻塞、不等待管线线程退出；`ct` 当前不受理（保留位）。

**实现现状**：当前实现只下发停止请求并返回已完成的 Task，**完全忽略 `ct`**，
也不等待解码/渲染线程真正退出——停止是异步完成的。保留该参数是为了将来支持「等待式停止」时不破坏签名。

因此调用方不得把本方法返回视为「管线线程已停」的凭据；需要该保证时应自行等待或重新
`OpenAsync`。

### SeekAsync

```csharp
Task SeekAsync(TimeSpan position, CancellationToken ct = default);
```

定位到指定位置。支持取消。

**失败语义**：定位失败必须**抛异常**而不是静默返回——当前实现先触发
`ErrorOccurred`（`SeekFailed`）再抛出原异常，调用方据此可只处理异常、
也可只订阅事件。

**前置条件**：媒体源须已成功打开且状态非 Opening/Error——此时没有可定位的解封装器，
定位无从执行。违反前置条件抛 `InvalidOperationException`（守卫拒绝，不发错误事件、
不动时钟与缓冲）。

**精度**：上层只承诺「已下发定位请求」，落点精度由解封装器决定
（见 `IMediaDemuxer.SeekAsync` 的返回值语义）。

### State

```csharp
MediaState State { get; }
```

当前播放状态。

### Position

```csharp
TimeSpan Position { get; }
```

当前播放位置（从 Clock 读取）。

### Duration

```csharp
TimeSpan Duration { get; }
```

媒体总时长。

### Volume

```csharp
float Volume { get; set; }
```

音量 (0.0~1.0)。

### IsMuted

```csharp
bool IsMuted { get; set; }
```

是否静音。

### PlaybackRate

```csharp
float PlaybackRate { get; set; }
```

播放速率 (1.0=正常)。

### Mode

```csharp
ProcessingMode Mode { get; set; }
```

无头 / 服务端处理模式（默认 `RealTime`，可切 `Fastest`）。
设 `Fastest` 时关掉音视频同步、无头音频输出不实时节流，
所有视频帧直接放行，适用于转码 / 离线 ML 等越快越好的批量处理场景。
可在 `OpenAsync` 前后设置；Open 后设置即时生效（同步内存操作，无 I/O）。

### Session

```csharp
IMediaSession? Session { get; }
```

当前播放会话（未打开时为 null）。

### StateChanged

```csharp
event EventHandler<MediaStateChangedEventArgs> StateChanged;
```

状态变更事件。

只通报状态迁移，不承载错误细节；非法/被忽略的迁移不会触发本事件。

### ErrorOccurred

```csharp
event EventHandler<MediaErrorEventArgs> ErrorOccurred;
```

错误事件。

只通报错误，**不隐式改变状态**：订阅方收到后不得假定 `State` 已变为
`Error`。

致命错误另有通道——`OpenAsync` 会抛出异常并由实现把状态置为 `Error`，
此时本事件与 `StateChanged` 都会出现，但**两者先后顺序不作保证**，
订阅方须各自幂等处理，不得依赖次序推断因果。

### PositionChanged

```csharp
event EventHandler<TimeSpan> PositionChanged;
```

位置变更事件。

### SubtitleReceived

```csharp
event EventHandler<SubtitleFrame?> SubtitleReceived;
```

字幕帧到达事件（null = 无活动字幕，UI 清除显示）。

### AudioDataAvailable

```csharp
event Action<AudioFrame>? AudioDataAvailable;
```

音频采样数据到达事件（供 AudioVisualizer 等可视化器消费）。
由音频管线在每帧提交给输出前**同步触发**（音频管线线程），
订阅方**仅可只读借用**传入的 `AudioFrame`：须在其回调内同步拷贝所需数据
（PCM 字节 / 采样格式 / 采样率 / 声道数），**不得 Dispose、不得跨线程持有该帧引用**。
未订阅时不触发（零额外开销）。

事件参数为契约层类型 `AudioFrame`，零外部引用合规。

### VideoFrameAvailable

```csharp
event Action<VideoFrame>? VideoFrameAvailable;
```

视频帧到达事件（供 Skia 软渲染 Presenter 等消费）。
由视频管线在 `Present` 分支、且 UI 已订阅（Skia 模式）时**同步触发**
（视频管线线程），订阅方**仅可只读借用**传入的 `VideoFrame`：须在其回调内同步拷贝所需数据
（像素缓冲 / 宽高 / 像素格式），**不得 Dispose、不得跨线程持有该帧引用**。
未订阅（D3D11 原生 GPU 模式）时管线直接 Present 到已 Attach 的共享 SwapChain，不触发此事件，保持零额外开销。

事件参数为契约层类型 `VideoFrame`，零外部引用合规。

### VideoDroppedFrames

```csharp
long VideoDroppedFrames { get; }
```

累计视频丢帧数（诊断/可观测性，只读）。正常播放到结尾时若此值 ≈ 文件视频帧数 − 实际呈现帧数，说明末段尾帧被同步器判 Drop 而非呈现。
