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

Media clock interface. Core component of audio and video synchronization.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaClock
```

## Remarks

Pure memory operations with no I/O waiting and no need for CancellationToken.

Clock cannot register Singleton—multiple players compete for the same clock.

Thread safety: All properties and methods must be thread-safe (lock or interlocked),

Because both video and audio pipelines access the clock.

## Members

### Position

```csharp
TimeSpan Position { get; }
```

Current clock position.

### Speed

```csharp
float Speed { get; set; }
```

Clock speed (1.0 = normal).

### IsRunning

```csharp
bool IsRunning { get; }
```

Whether to run.

### SyncSource

```csharp
ClockSyncSource SyncSource { get; set; }
```

Synchronize the source.

### SyncThreshold

```csharp
TimeSpan SyncThreshold { get; set; }
```

Synchronization threshold (default 50ms).

### DropThreshold

```csharp
TimeSpan DropThreshold { get; set; }
```

Frame loss threshold (default 200ms).

### Start

```csharp
void Start();
```

Start the clock and record the start timestamp.

### Pause

```csharp
void Pause();
```

Pause the clock and freeze the current location.

### Reset

```csharp
void Reset();
```

Reset to Zero and stop running.

### SeekTo

```csharp
void SeekTo(TimeSpan position);
```

Jump to the specified location.

**Parameters**

- `position`: Target clock position.

### SyncTo

```csharp
void SyncTo(TimeSpan masterPosition);
```

Synchronize to the master clock position.

**Parameters**

- `masterPosition`: Current position of the master clock.
