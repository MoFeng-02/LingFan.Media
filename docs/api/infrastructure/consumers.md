# Consumers (`LingFan.Media.Consumers`)

The **headless / server-side** building blocks. They let `MediaPlayer` run with **no window, no GPU device, and no audio device** — frames and PCM flow out through the contract events for your own compute (transcode, ML inference, thumbnail, spectrum analysis).

The golden rule is the same as everywhere: frames delivered via `VideoFrameAvailable` / `AudioDataAvailable` are **read-only borrows**. Copy what you need synchronously inside the callback; never `Dispose` or retain the frame reference across threads.

## ConsumersExtensions

Namespace: `LingFan.Media.Consumers`

`static class` with two `MediaBuilder` extensions (synchronous config, no I/O):

```csharp
public static MediaBuilder AddHeadlessRenderer(this MediaBuilder builder);
public static MediaBuilder AddSilentAudioOutput(this MediaBuilder builder);
```

| Method | Registers | Use when |
|--------|-----------|----------|
| `AddHeadlessRenderer()` | `IVideoRendererFactory` → `NoOpVideoRendererFactory` | No `VideoView` / no window / no GPU. **Does not** register `IGpuDeviceContext` (dependency inversion: `MediaPlayer` only knows `IVideoRendererFactory`). |
| `AddSilentAudioOutput()` | `IAudioOutputFactory` → `NoOpAudioOutputFactory` | You do not want sound (CI, transcode, ML). The audio samples are simply dropped. **Not** the same as "no audio device" — if you *do* have a device and want real sound in a headless process, use `AddWasapiOutput()` instead (WASAPI needs no window). |

Pair them for a fully headless player:

```csharp
services.AddLingFanMedia()
        .AddFFmpeg()
        .AddHeadlessRenderer()
        .AddSilentAudioOutput();
```

Then consume frames via `ProcessingFrameSink` / audio via `ProcessingAudioSink`.

## NoOpVideoRenderer

Namespace: `LingFan.Media.Consumers`

`sealed class : IVideoRenderer`. All methods are no-ops; `PresentationLatency` is `TimeSpan.Zero`. `Present` is safe even if a frame reaches it (frames normally go to the sink instead). Closed lifecycle, no native resources.

| Member | Type | Notes |
|--------|------|-------|
| `InitializeAsync(ct)` | `Task` | `Task.CompletedTask`. |
| `Attach(IRenderTarget)` / `Detach()` / `Present(VideoFrame)` / `Clear()` | `void` | no-ops. |
| `PresentationLatency` | `TimeSpan` | `Zero`. |
| `Dispose()` / `DisposeAsync()` | | no-op. |

## NoOpAudioOutput

Namespace: `LingFan.Media.Consumers`

`sealed class : IAudioOutput, IRealtimePacedOutput`. Drops PCM; opens no device. Implements real-time **back-pressure** so the clock advances at the correct pace (otherwise audio would submit instantly, the synchronizer would yank the master clock to end-of-file, and video frames would all be judged "late → dropped").

| Member | Type | Notes |
|--------|------|-------|
| `Initialize(int sampleRate, int channels)` | `void` | |
| `Submit(AudioFrame frame)` | `void` | In `RealTime` mode, anchors the first frame and throttles by `submittedSamples / sampleRate` (`Thread.Sleep` back-pressure — normal, not pseudo-async). In `Fastest` mode (`PaceRealTime == false`) returns immediately. Falls back to the frame's own sample rate if the decoder-reported rate is `0`. |
| `Pause()` / `Resume()` / `Flush()` | `void` | `Flush` re-anchors. |
| `GetPlaybackPosition()` | `TimeSpan` | `Zero` (headless has no device cursor). |
| `Latency` | `TimeSpan` | `Zero`. |
| `Volume` (get/set) / `PaceRealTime` (set-only) | `float` / `bool` | settable. |
| `InitializeAsync(ct)` | `Task` | `Task.CompletedTask`. |
| `Dispose()` / `DisposeAsync()` | | no-op. |

## ProcessingFrameSink

Namespace: `LingFan.Media.Consumers`

`sealed class : IHeadlessFrameConsumer`. Subscribes to `IMediaPlayer.VideoFrameAvailable` and dispatches by resource type — **zero-copy for GPU textures, zero-allocation for CPU frames**.

```csharp
public ProcessingFrameSink(
    Action<VideoFrame>? onFrame = null,
    Action<IGpuTextureResource, VideoFrame>? onGpu = null,
    Action<SoftwareFrameResource, VideoFrame>? onCpu = null)
```

| Member | Notes |
|--------|-------|
| `Attach(IMediaPlayer player)` | Subscribes to `VideoFrameAvailable` (idempotent — detaches any previous). |
| `Detach()` | Unsubscribes. |
| `Consume(VideoFrame frame)` | If `onGpu` set and `frame.Resource is IGpuTextureResource` → invoke `onGpu` (handle valid only inside the callback). Else if `onCpu` set and `frame.Resource is SoftwareFrameResource` → invoke `onCpu` (read the `Span` directly). Then `onFrame` (always, if set). **Read-only borrow** — never `Dispose`/`retain`. |
| `Dispose()` / `DisposeAsync()` | Detach + clear. |

## ProcessingAudioSink

Namespace: `LingFan.Media.Consumers`

`sealed class : IHeadlessAudioConsumer`. Symmetric to `ProcessingFrameSink` for the audio side.

```csharp
public ProcessingAudioSink(Action<AudioFrame>? onAudio = null)
```

| Member | Notes |
|--------|-------|
| `Attach(IMediaPlayer player)` | Subscribes to `AudioDataAvailable` (idempotent). |
| `Detach()` | Unsubscribes. |
| `Consume(AudioFrame frame)` | Invokes `onAudio` with a **read-only borrow** — copy the PCM synchronously; never `Dispose`/retain. |
| `Dispose()` / `DisposeAsync()` | Detach + clear. |

> **Headless output, two shapes.** Video leaves via `ProcessingFrameSink` (GPU handle or CPU `Span`); audio leaves via `ProcessingAudioSink` (PCM bytes). Both reuse the existing frame/audio routing — the pipeline code is unchanged; only the terminal sink differs. This is the concrete payoff of the "single frame route" design.
