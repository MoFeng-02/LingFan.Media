---
title: "IAudioOutput"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutput.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioOutput", "Initialize", "Submit", "Pause", "Resume", "BeginStreamingAsync", "Flush", "GetPlaybackPosition", "GetPlaybackPositionDirect", "ResetPlaybackClock", "Latency", "Volume"]
lang: "en-US"
---
# IAudioOutput

Source: [`IAudioOutput.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioOutput.cs)

音频输出接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioOutput : IMediaComponent
```

## Remarks

线程模型：Submit 在音频线程调用。

所有权语义：Submit 不接管帧所有权，不 Dispose 帧；调用方负责 Submit 后归还池或释放。

实现仅同步拷贝 PCM 数据到输出缓冲；调用方（AudioPipeline）负责 Submit 后将帧 Return 到 FramePool 或 Dispose。

IFrameResource 非线程安全，需在单线程内使用。

## Members

### Initialize

```csharp
void Initialize(int sampleRate, int channels);
```

参数化配置：设置采样率和声道数。

### Submit

```csharp
void Submit(AudioFrame frame);
```

提交音频帧。不接管帧所有权；仅同步拷贝 PCM 数据，
调用方负责 Submit 后释放帧（Return 到池或 Dispose）。
音频线程调用，缓冲满时阻塞（COM 背压，同步阻塞背压是正常机制，非伪异步）。

### Pause

```csharp
void Pause();
```

暂停播放。

### Resume

```csharp
void Resume();
```

恢复播放。

### BeginStreamingAsync

```csharp
ValueTask BeginStreamingAsync(CancellationToken ct);
```

开始流式输出，但在缓冲区预填足够真实 PCM 后再启动设备时钟（preroll 语义），
返回时音频已开始流动。默认实现直接恢复播放（无预填需求的后端，如无声/无设备输出）；
WASAPI 重写为解决起播静默窗（引擎抓取的是真实数据而非静音）。

### Flush

```csharp
void Flush();
```

清空输出缓冲。

### GetPlaybackPosition

```csharp
TimeSpan GetPlaybackPosition();
```

获取已播放位置（用于时钟同步）。

### GetPlaybackPositionDirect

```csharp
TimeSpan GetPlaybackPositionDirect() => GetPlaybackPosition();
```

高频、线程安全的播放位置读取（可选优化）。默认实现回落到 `GetPlaybackPosition`。

**用途**：视频主时钟需要以视频帧率（~30Hz）轮询播放位置。某些后端
（WASAPI）的 `GetPlaybackPosition` 走跨线程封送，高频调用有成本；
这类后端应重写本方法，直接读取设备时钟（`IAudioClock::GetPosition` 可由任意线程调用，无需跨线程），
既平滑又零封送开销。

### ResetPlaybackClock

```csharp
void ResetPlaybackClock() { }

/// <summary>当前输出延迟。</summary>
TimeSpan Latency { get; }

/// <summary>输出音量 (0.0~1.0)。</summary>
float Volume { get; set; }
```

重播（Ended→Playing）主时钟归零：音频尚未为本遍播放 Start 前，`GetPlaybackPositionDirect`
应返回 0，避免沿用上一遍播放的时钟锚点导致重播首帧被错判为过期而丢弃。
默认空实现（无主时钟后端无需处理）；WASAPI 重写为真正的去武装逻辑。
