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

帧释放接口。VideoFrame / AudioFrame 实现此接口。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IDisposableFrame : IDisposable
```

## Remarks

帧所有权转移语义：Decoder → FrameQueue → Renderer，任何时刻只有一个组件持有所有权。
丢帧必须 Dispose，否则 GPU 资源泄漏。
必须继承 `IDisposable`：FramePool 满池分支经 `is IDisposable` 判定后 Dispose 帧
（不继承时判定恒 false、帧静默泄漏——GPU/原生内存随播放持续累积直至耗尽）。

## Members

### IsDisposed

```csharp
bool IsDisposed { get; }
```

是否已释放。
