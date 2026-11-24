---
title: "IHeadlessFrameConsumer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Consumers/IHeadlessFrameConsumer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IHeadlessFrameConsumer", "Consume"]
lang: "en-US"
---
# IHeadlessFrameConsumer

Source: [`IHeadlessFrameConsumer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Consumers/IHeadlessFrameConsumer.cs)

Headless consumption contract (backend data processing). `VideoFrame` This is handed over to downstream computation, no rendering or display.
Simple scenarios can be directly used with `Action{T}` to subscribe to `VideoFrameAvailable`, no need for this interface;
This interface is used for scenarios requiring structured lifecycle/batch aggregation (in conjunction with `LingFan.Media.Consumers.ProcessingFrameSink`).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IHeadlessFrameConsumer : IDisposable, IAsyncDisposable
```

## Remarks

Zero external references: relies only on the contract layer neutral type (`VideoFrame`).

Read-only borrowing model: The implementer can only read and synchronously copy the required data in `Consume`,
**No Dispose, no cross-thread holding of incoming `VideoFrame`** — frame ownership belongs to the pipeline and is released upon the callback.

Symmetric lifecycle: implement `IDisposable` + `IAsyncDisposable`.

AOT compatibility: pure interface contract, no reflection, no P/Invoke.

## Members

### Consume

```csharp
void Consume(VideoFrame frame);
```

Consume one frame. The video pipeline releases the frame after the call (read-only borrowing).
If data is held across calls, it must be copied synchronously within the method
(CPU: `Data`'s `ReadOnlySpan{T}` → own buffer;
GPU: `ReadbackToCpu` Takes managed snapshots and submits them downstream).

**Parameters**

- `frame`: Current frame (read-only borrowed, invalid upon return callback).
