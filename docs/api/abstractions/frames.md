# Frames & Resources

## IFrameChannel & IFrameSink

These two interfaces are the **single video frame route** out of the pipeline. Every video endpoint — headless compute, Skia soft render, D3D11 zero-copy GPU present — implements `IFrameSink` and subscribes through `IFrameChannel`. There is no "headed vs. headless" fork; they drink from one channel and differ only in terminal action and capability.

```csharp
public interface IFrameSink
{
    void OnFrame(VideoFrame frame);   // read-only borrow, never Dispose
}

public interface IFrameChannel
{
    IDisposable Subscribe(IFrameSink sink);   // returns an unsubscribe handle
    void Unsubscribe(IFrameSink sink);
    void Emit(VideoFrame frame);              // fan out to all subscribers
}
```

> **Frame ownership:** `Emit` is called by the pipeline inside a `try`; the frame is released by the pipeline's `ReturnFrame` in a `finally`. The channel and all sinks are **read-only borrowers** and must never `Dispose`. Disposing a frame inside `OnFrame` corrupts later subscribers under multicast — a hard violation.

The public `IMediaPlayer.VideoFrameAvailable` event is an `Action<VideoFrame>` façade over this channel. Advanced consumers (recorder, thumbnail) may implement `IFrameSink` directly.

## VideoFrame

Namespace: `LingFan.Media.Abstractions`

`sealed class : IDisposableFrame` — a decoded video frame.

| Member | Type | Notes |
|--------|------|-------|
| `Width` / `Height` | `int` | Frame dimensions. |
| `Format` | `PixelFormat` | Pixel format. |
| `Resource` | `IFrameResource?` | The pixel buffer (CPU or GPU). |
| `Timestamp` | `TimeSpan` | Presentation timestamp. |
| `Duration` | `TimeSpan` | Frame duration. |
| `KeyFrame` | `bool` | Key-frame flag. |
| `Reset()` | `void` | Reuse the frame (pool). |
| `Dispose()` | `void` | Release the frame. |

## AudioFrame

Namespace: `LingFan.Media.Abstractions`

`sealed class : IDisposableFrame` — a decoded audio frame.

| Member | Type | Notes |
|--------|------|-------|
| `Data` | `ReadOnlyMemory<byte>` | PCM bytes. |
| `SampleRate` | `int` | Sample rate. |
| `Channels` | `int` | Channel count. |
| `SampleFormat` | `SampleFormat` | S16 / S32 / F32. |
| `Timestamp` / `Duration` | `TimeSpan` | Timing. |
| `FrameCount` | `int` | Sample frames. |
| `Reset()` / `Dispose()` | `void` | Reuse / release. |

## SubtitleFrame

Namespace: `LingFan.Media.Abstractions`

`Text` / `Start` / `End` / `Style` — a timed subtitle cue.

## MediaPacket

Namespace: `LingFan.Media.Abstractions`

`sealed class : IDisposable` — a demuxer packet (compressed or pre-decoded).

| Member | Type | Notes |
|--------|------|-------|
| `TrackIndex` | `int` | Owning track. |
| `Data` | `ReadOnlyMemory<byte>` | Packet bytes. |
| `Timestamp` / `Duration` | `TimeSpan` | Timing. |
| `KeyFrame` | `bool` | Key-frame flag. |
| `DecodedFrameResource` | `IFrameResource?` | Optional pre-decoded resource (zero-copy path). |
| `HasDecodedFrameResource` | `bool` | Whether a decoded resource is attached. |
| `TakeDecodedFrameResource()` | `IFrameResource?` | Take ownership of the decoded resource. |
| `Dispose()` | `void` | Release. |

## Frame resource interfaces

| Interface | Role |
|-----------|------|
| `IDisposableFrame` | `IsDisposed` + `Dispose()` — a disposable frame. |
| `IFrameResource` | `Width` / `Height` / `Format` + `IDisposable` — a CPU or GPU pixel buffer behind a frame. |
| `IFramePool<T>` | `Rent()` / `Return()` — frame pooling. |
| `IFramePoolAware<T>` | `SetFramePool()` — a frame that knows its pool. |
| `IGpuTextureResource` | `NativeTextureHandle` / `SubresourceIndex` / `ReadbackToCpu` — a GPU texture resource (zero-copy). |
| `IGpuDeviceContext` | `ApiType` / `DeviceHandle` / `ContextHandle` / `IsInitialized` / `InitializeAsync` / `GetCapabilities` — a neutral GPU device context (shared D3D11 device, etc.). |
| `ISharedGpuSurfaceSource` / `ISharedGpuSurfaceSourceFactory` | `HandleKind` / `ConsumerAcquireKey` / `ConsumerReleaseKey` / `TryWriteFrame` — cross-process / cross-API shared GPU surface. |
| `IRealtimePacedOutput` | `bool PaceRealTime { set; }` — marker for real-time pacing of a headless audio output. |
