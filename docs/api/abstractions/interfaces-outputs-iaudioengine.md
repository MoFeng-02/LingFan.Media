---
title: "IAudioEngine"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioEngine.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IAudioEngine", "IsWarm", "Warmup", "WarmupAsync"]
lang: "en-US"
---
# IAudioEngine

Source: [`IAudioEngine.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Outputs/IAudioEngine.cs)

Audio engine (Infrastructure-level long-term native resource). Represents the "unique in-process, shared across playback sessions" underlying audio subsystem connection,
Strictly layered with `IAudioOutput` (Session level, each `OpenAsync` new creation).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IAudioEngine : IDisposable, IAsyncDisposable
```

## Remarks

**Why is this layer needed?**: Some platforms' audio subsystems incur a one-time cold start overhead when the "first audio stream in the process" is established
(Typical example is Windows WASAPI: the first `IAudioClient.Initialize` triggers audiodg.exe pull, taking about a few seconds.)
The scope of this overhead is the **operating system audio engine** rather than a playback session, so it must be borne once by a long-lifecycle object,
Instead of making each session pay repeatedly.

**DI Lifecycle**:* *Singleton**. Consistent with the layering paradigm of GPU devices—
`ID3D11Device`/`VkDevice` are Singleton shares, while `SwapChain`/`RenderTarget` are Session-level;
Corresponding to audio: **Engine/device endpoint handle is Singleton, `IAudioOutput` is Transient**.
Never raise the Session state object (holding the playback thread, buffer, volume) to Singleton.

**Coupling with Session**: Zero. `IAudioOutput` Does not reference this interface, nor does it depend on its internal native objects;
The two only collaborated indirectly through the **process-level side effect** of "operating system audio engine is hot." This way, multitrack playback (N concurrent sessions)
Naturally DIs each create new or dispose without contamination and require no manual reference counting.

**Call timing is determined by the host**: The library only exposes capabilities and does not manage "when to warm up". The host should call during the startup screen/loading screen period
`WarmupAsync` Absorbs cold start costs; If not called, the original behavior is maintained (the cost falls on the first playback).

**Failure Meaning**: Preheating failure is always considered "not prewarmed"; host startup must never be interrupted, and subsequent playback will not be affected
(The official playback path will still fully initialize the audio).

## Members

### IsWarm

```csharp
bool IsWarm { get; }
```

Whether the audio engine is in a hot state (preheating successful and long-term resources still active).

### Warmup

```csharp
void Warmup();
```

Synchronize preheat of the audio engine. Idempotent: Repeated calls return directly.

Blocks the calling thread until the cold start completes (possibly a few seconds), and do not call it in the UI thread;
UI host, please use `WarmupAsync`.

### WarmupAsync

```csharp
Task WarmupAsync(CancellationToken ct = default);
```

Asynchronous preheating of the audio engine. Idempotent: Repeated calls return directly (if prewarmed, the same ongoing operation is reused).

**Parameters**

- `ct`: Cancel token. Canceling only &lt;b&gt;aborts the wait&lt;/b&gt;; delivered native initializations will not be rolled back.

**Returns**: Tasks completed when warm-up is completed (or completed directly in the hot state); For failure semantics, see remarks (considered unwarmed, does not interrupt host startup).
