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

Track switch event parameters.

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

Track type.

### OldTrack

```csharp
public MediaTrack? OldTrack { get; }
```

Old track (may be null).

### NewTrack

```csharp
public MediaTrack? NewTrack { get; }
```

New orbit (possibly null).

### Constructor

```csharp
public TrackChangedEventArgs(TrackType trackType, MediaTrack? oldTrack, MediaTrack? newTrack);
```

Initialize a new instance of `TrackChangedEventArgs`.

**Parameters**

- `trackType`: Track type.
- `oldTrack`: Old track (can be null).
- `newTrack`: New track (can be null).
