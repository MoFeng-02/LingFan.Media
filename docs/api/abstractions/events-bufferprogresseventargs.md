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

缓冲进度事件参数。

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

当前缓冲状态。

### BufferedDuration

```csharp
public TimeSpan BufferedDuration { get; }
```

已缓冲时长。

### BufferedBytes

```csharp
public long BufferedBytes { get; }
```

已缓冲字节数。

### Progress

```csharp
public float Progress { get; }
```

缓冲进度（0.0~1.0，仅网络流有意义）。

### Constructor

```csharp
public BufferProgressEventArgs(BufferState state, TimeSpan bufferedDuration, long bufferedBytes, float progress);
```

初始化 `BufferProgressEventArgs` 的新实例。

**Parameters**

- `state`: 当前缓冲状态。
- `bufferedDuration`: 已缓冲时长。
- `bufferedBytes`: 已缓冲字节数。
- `progress`: 缓冲进度（0.0~1.0，仅网络流有意义）。
