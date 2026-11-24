---
title: "IBatchAudioSubmit"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IBatchAudioSubmit.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IBatchAudioSubmit", "SubmitBatch"]
lang: "en-US"
---
# IBatchAudioSubmit

Source: [`IBatchAudioSubmit.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IBatchAudioSubmit.cs)

Optional batch audio submission capability (performance optimization feature interface).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IBatchAudioSubmit
```

## Remarks

Used to continuously submit multiple audio frames within a single STA cross-thread round trip, eliminating the fixed overhead caused by frame-by-frame `RunOnSta` blocking round trips,
This ensures the audio supply rate is real-time (fixing stuttering and slowdown).

Optional enhancement to `IAudioOutput`: implementers implement this interface if they support batch commits,
`AudioPipeline` Detection via `is`; if unsupported, revert frame-by-frame `Submit` (behavior remains unchanged on other platforms).

This interface evolves according to the principle of "only add, not change." Zero external references, relying only on this namespace type.

## Members

### SubmitBatch

```csharp
void SubmitBatch(IEnumerable<AudioFrame> frames);
```

Batch submit audio frames. The implementation should continuously write all frames (including necessary backpressure waiting) within a native/STA context,
Instead of making one cross-thread round trip per frame.
Only when **backpressure timeout** (`TimeoutException`: buffer cannot be freed for a long time) is it downgraded to "discard this frame, continue with subsequent frames";
Other exceptions (such as parameter errors such as channel count/sample rate/format mismatch) must bubble and interrupt the batch as is—silent swallowing will cause configuration errors
resulting in continuous frame drops or noise, and the caller can never see the root cause.
**Cancellation does not fall under the above category**: only when there is an abort that can be canceled and triggered by `CancellationToken`
(`OperationCanceledException`, normal exit path for Stop/Dispose), the implementation should abandon the remaining frames of the current batch
and return as soon as possible; **must not** be mistaken for bubbles; When the caller receives a normal return, it means the batch has been abandoned and is treated as a normal exit.
Do not use this to determine failure or log fail.
It does not take over frame ownership; the caller is responsible for releasing the frame after submission.

**Parameters**

- `frames`: Audio frame collection to be submitted (may contain null, implementation must be skipped).

### SubmitBatch

```csharp
void SubmitBatch(IEnumerable<AudioFrame> frames, CancellationToken ct);
```

Batch submission of audio frames (perceptible cancel tokens). Same as `SubmitBatch(IEnumerable{AudioFrame})`,
But when `ct` triggers cancellation, the implementation should quickly abandon blocking wait (backpressure/render thread handshake) and return,
Allows the caller (audio pipeline) to exit immediately upon Stop/Dispose to avoid exit pending.

After canceling triggering, the implementation should **return normally** as soon as possible (internally swallowing `OperationCanceledException`),
No exceptions thrown to the caller: frames not written (including those queued but unconfirmed) are handled uniformly by the caller as "Abandoned in this batch",
Return normally **without guaranteeing that all entries in this batch have been written.' Parameter class error still pressed
`SubmitBatch(IEnumerable{AudioFrame})` rules bubble up as they are.

**Parameters**

- `frames`: Audio frame collection to be submitted (may contain null, implementation must be skipped).
- `ct`: Cancel token (triggered when off/stopped).
