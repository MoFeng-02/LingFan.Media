# Renderers & Outputs

## IVideoRenderer

Namespace: `LingFan.Media.Abstractions`

Presents a video frame. **Thread model:** `Attach` / `Detach` on the UI thread; `Present` / `Clear` on the render thread.

```csharp
public interface IVideoRenderer : IMediaComponent
{
    void Attach(IRenderTarget target);
    void Detach();
    void Present(VideoFrame frame);     // synchronous consume
    void Clear();
    TimeSpan PresentationLatency { get; }
}
```

| Member | Notes |
|--------|-------|
| `Attach(target)` | Bind a render target (UI thread). |
| `Detach()` | Unbind (UI thread). |
| `Present(frame)` | Present a frame (render thread). **Synchronous** — the renderer finishes the GPU upload/copy before returning, so the caller may safely release the frame. `Present` is a pure GPU operation; never async-ify it. |
| `Clear()` | Clear the surface (render thread). |
| `PresentationLatency` | End-to-end `Present` → pixels-visible latency. The synchronizer uses this to decide how early to call `Present` so the frame appears exactly when audio reaches its PTS. GPU paths return ~1–2 refresh periods; headless / stub sinks return `TimeSpan.Zero`. |

## IRenderTarget

`Type` / `HandleType` / `NativeHandle` / `Width` / `Height` / `Scale` — describes where the renderer draws.

## IVideoRendererFactory / IRendererHealth

| Type | Role |
|------|------|
| `IVideoRendererFactory` | `Create()` a renderer. |
| `IRendererHealth` | `event Action? Unhealthy` — raised when the renderer loses its device / surface. |

## IAudioOutput

Namespace: `LingFan.Media.Abstractions`

The unified **audio output port**. `AudioPipeline` normalises every backend's decoded audio into `Submit`. Audio is submitted **directly to `IAudioOutput` and bypasses the synchronizer** — the A/V asymmetry is intentional.

```csharp
public interface IAudioOutput : IMediaComponent
{
    void Initialize(int sampleRate, int channels);
    void Submit(AudioFrame frame);          // does NOT take ownership
    void Pause();
    void Resume();
    ValueTask BeginStreamingAsync(CancellationToken ct);   // default: Resume()
    void Flush();
    TimeSpan GetPlaybackPosition();
    TimeSpan GetPlaybackPositionDirect();   // default: GetPlaybackPosition()
    void ResetPlaybackClock();              // default: empty
    TimeSpan Latency { get; }
    float Volume { get; set; }
}
```

| Member | Notes |
|--------|-------|
| `Initialize(rate, channels)` | Configure (pure in-memory). |
| `Submit(frame)` | Submit a frame. **Does not take ownership** — only copies PCM synchronously; the caller releases the frame. Called on the audio thread; blocks when the buffer is full (COM back-pressure — a normal mechanism, **not** pseudo-async). |
| `Pause()` / `Resume()` | Playback control. |
| `BeginStreamingAsync(ct)` | Pre-fill real PCM before starting the device clock (preroll), fixing start-of-playback silence. Default impl just `Resume()`. |
| `Flush()` | Drain the output buffer. |
| `GetPlaybackPosition()` | Playback position (for clock sync). |
| `GetPlaybackPositionDirect()` | High-frequency, thread-safe position read. Default falls back to `GetPlaybackPosition()`. WASAPI overrides it to read the device clock directly (zero marshalling). |
| `ResetPlaybackClock()` | Replay clock reset — on `Ended → Playing`, `GetPlaybackPositionDirect()` should return 0 until the device starts, so the first frame isn't misjudged stale and dropped. Default empty; WASAPI overrides. |
| `Latency` | Output latency. |
| `Volume` | Output volume 0.0–1.0. |

## IAudioEngine / IAudioOutputFactory / IBatchAudioSubmit / IRealtimePacedOutput

| Type | Role |
|------|------|
| `IAudioEngine` | `IsWarm` / `Warmup` / `WarmupAsync` — warms up the audio stack (reduces first-play latency). |
| `IAudioOutputFactory` | `Create()` an `IAudioOutput`. |
| `IBatchAudioSubmit` | `SubmitBatch(...)` — batched audio submission. |
| `IRealtimePacedOutput` | `bool PaceRealTime { set; }` — marker for a headless audio output's real-time pacing. |
