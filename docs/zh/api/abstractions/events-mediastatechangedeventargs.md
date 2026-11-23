---
title: "MediaStateChangedEventArgs"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Events/MediaStateChangedEventArgs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaStateChangedEventArgs", "OldState", "NewState"]
lang: "zh-CN"
---
# MediaStateChangedEventArgs

源码: [`MediaStateChangedEventArgs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Events/MediaStateChangedEventArgs.cs)

播放状态变更事件参数。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaStateChangedEventArgs : EventArgs
```

## 成员

### OldState

```csharp
public MediaState OldState { get; }
```

变更前的状态。

### NewState

```csharp
public MediaState NewState { get; }
```

变更后的状态。

### 构造函数

```csharp
public MediaStateChangedEventArgs(MediaState oldState, MediaState newState);
```

初始化 `MediaStateChangedEventArgs` 的新实例。

**参数**

- `oldState`：变更前的状态。
- `newState`：变更后的状态。
