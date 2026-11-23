---
title: "IMediaClock"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IMediaClock.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaClock", "Position", "Speed", "IsRunning", "SyncSource", "SyncThreshold", "DropThreshold", "Start", "Pause", "Reset", "SeekTo", "SyncTo"]
lang: "en-US"
---
# IMediaClock

Source: [`IMediaClock.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Core/IMediaClock.cs)

媒体时钟接口。音视频同步的核心组件。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaClock
```

## Remarks

纯内存操作，无 I/O 等待，不需要 CancellationToken。

Clock 不能注册 Singleton——多播放器会抢同一时钟。

线程安全：所有属性和方法需线程安全（lock 或 Interlocked），

因为视频管线和音频管线都会访问时钟。

## Members

### Position

```csharp
TimeSpan Position { get; }
```

当前时钟位置。

### Speed

```csharp
float Speed { get; set; }
```

时钟速度（1.0=正常）。

### IsRunning

```csharp
bool IsRunning { get; }
```

是否运行。

### SyncSource

```csharp
ClockSyncSource SyncSource { get; set; }
```

同步源。

### SyncThreshold

```csharp
TimeSpan SyncThreshold { get; set; }
```

同步阈值（默认 50ms）。

### DropThreshold

```csharp
TimeSpan DropThreshold { get; set; }
```

丢帧阈值（默认 200ms）。

### Start

```csharp
void Start();
```

启动时钟，记录起始时间戳。

### Pause

```csharp
void Pause();
```

暂停时钟，冻结当前位置。

### Reset

```csharp
void Reset();
```

重置到 Zero，停止运行。

### SeekTo

```csharp
void SeekTo(TimeSpan position);
```

跳转到指定位置。

### SyncTo

```csharp
void SyncTo(TimeSpan masterPosition);
```

同步到主时钟位置。
