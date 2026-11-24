---
title: "IHeadlessAudioConsumer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Consumers/IHeadlessAudioConsumer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IHeadlessAudioConsumer", "Consume"]
lang: "en-US"
---
# IHeadlessAudioConsumer

Source: [`IHeadlessAudioConsumer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Consumers/IHeadlessAudioConsumer.cs)

Headless audio consumption contract (backend data processing). `AudioFrame` After this, the data is handed over to downstream for calculation and not played to the device.
Simple scenarios can be directly used with `Action{T}` to subscribe to `AudioDataAvailable`, no need for this interface;
This interface is used for scenarios requiring structured lifecycle/batch aggregation (in conjunction with `LingFan.Media.Consumers.ProcessingAudioSink`).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IHeadlessAudioConsumer : IDisposable, IAsyncDisposable
```

## Remarks

Zero external references: relies only on the contract layer neutral type (`AudioFrame`).

Read-only borrowing model: The implementer can only read and synchronously copy the required data in `Consume`,
**No Dispose, no cross-thread holding of incoming `AudioFrame`** — frame ownership belongs to the pipeline and is released upon the callback.

Symmetric lifecycle: implement `IDisposable` + `IAsyncDisposable`.

AOT compatibility: pure interface contract, no reflection, no P/Invoke.

## Members

### Consume

```csharp
void Consume(AudioFrame frame);
```

Consume one audio frame. The audio pipeline releases the frame after the call (read-only borrowing).
If data is held across calls, synchronous copies must be made within the method (PCM bytes → own buffer).

**Parameters**

- `frame`: Current audio frame (read-only borrowed, invalid upon callback return).
