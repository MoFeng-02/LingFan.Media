---
title: "BufferProgressEventArgs"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Events/BufferProgressEventArgs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["BufferProgressEventArgs", "State", "BufferedDuration", "BufferedBytes", "Progress"]
lang: "en-US"
---
# BufferProgressEventArgs

Source: [`BufferProgressEventArgs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Events/BufferProgressEventArgs.cs)

Buffer progress event parameters.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class BufferProgressEventArgs : EventArgs
```

## Members

### State

```csharp
public BufferState State { get; }
```

Current buffer status.

### BufferedDuration

```csharp
public TimeSpan BufferedDuration { get; }
```

Buffered duration.

### BufferedBytes

```csharp
public long BufferedBytes { get; }
```

Number of bytes buffered.

### Progress

```csharp
public float Progress { get; }
```

Buffer progress (0.0~1.0, only network streams are meaningful).

### Constructor

```csharp
public BufferProgressEventArgs(BufferState state, TimeSpan bufferedDuration, long bufferedBytes, float progress);
```

Initialize a new instance of `BufferProgressEventArgs`.

**Parameters**

- `state`: Current buffer status.
- `bufferedDuration`: Buffered duration.
- `bufferedBytes`: Number of bytes buffered.
- `progress`: Buffer progress (0.0~1.0, only network streams are meaningful).
