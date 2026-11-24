---
title: "IDisposableFrame"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IDisposableFrame.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IDisposableFrame", "IsDisposed"]
lang: "en-US"
---
# IDisposableFrame

Source: [`IDisposableFrame.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IDisposableFrame.cs)

Frame release interface. VideoFrame / AudioFrame implements this interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IDisposableFrame : IDisposable
```

## Remarks

Frame ownership transfer semantics: Decoder → FrameQueue → Renderer, at any given time, only one component holds ownership.
Frame loss must be Disposed, otherwise GPU resources will leak.
Must inherit `IDisposable`: FramePool full branch after `is IDisposable` determines Dispose frame
(When not inherited, it is always false and frame silent leakage—GPU/native memory continues to accumulate with playback until exhausted).

## Members

### IsDisposed

```csharp
bool IsDisposed { get; }
```

Whether it has been released.
