---
title: "TrackChangedEventArgs"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Events/TrackChangedEventArgs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["TrackChangedEventArgs", "TrackType", "OldTrack", "NewTrack"]
lang: "en-US"
---
# TrackChangedEventArgs

Source: [`TrackChangedEventArgs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Events/TrackChangedEventArgs.cs)

轨道切换事件参数。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class TrackChangedEventArgs : EventArgs
```

## Members

### TrackType

```csharp
public TrackType TrackType { get; }
```

轨道类型。

### OldTrack

```csharp
public MediaTrack? OldTrack { get; }
```

旧轨道（可能为 null）。

### NewTrack

```csharp
public MediaTrack? NewTrack { get; }
```

新轨道（可能为 null）。

### Constructor

```csharp
public TrackChangedEventArgs(TrackType trackType, MediaTrack? oldTrack, MediaTrack? newTrack);
```

初始化 `TrackChangedEventArgs` 的新实例。
