---
title: "MediaStateChangedEventArgs"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Events/MediaStateChangedEventArgs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaStateChangedEventArgs", "OldState", "NewState"]
lang: "en-US"
---
# MediaStateChangedEventArgs

Source: [`MediaStateChangedEventArgs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Events/MediaStateChangedEventArgs.cs)

Playback status change event parameters.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaStateChangedEventArgs : EventArgs
```

## Members

### OldState

```csharp
public MediaState OldState { get; }
```

The state before the change.

### NewState

```csharp
public MediaState NewState { get; }
```

The status after the change.

### Constructor

```csharp
public MediaStateChangedEventArgs(MediaState oldState, MediaState newState);
```

Initialize a new instance of `MediaStateChangedEventArgs`.

**Parameters**

- `oldState`: The state before the change.
- `newState`: The status after the change.
