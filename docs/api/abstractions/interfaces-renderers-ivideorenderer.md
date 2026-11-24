---
title: "IVideoRenderer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRenderer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IVideoRenderer", "Attach", "Detach", "Present", "Clear", "PresentationLatency"]
lang: "en-US"
---
# IVideoRenderer

Source: [`IVideoRenderer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Renderers/IVideoRenderer.cs)

Video renderer interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IVideoRenderer : IMediaComponent
```

## Remarks

Thread model:

- Attach / Detach is called in the UI thread
- Present / Clear is called in the rendering thread

IFrameResource is non-thread-safe and must be used within a single thread.

## Members

### Attach

```csharp
void Attach(IRenderTarget target);
```

Bind render target. UI thread call.

**Parameters**

- `target`: Render the target.

### Detach

```csharp
void Detach();
```

Unbound render target. UI thread call.

### Present

```csharp
void Present(VideoFrame frame);
```

Present a frame. Render thread call.
Present means synchronous consumption—the renderer completes the upload/copy of GPU resources before returning,
The caller can safely release the frame; If a Renderer needs to retain frames asynchronously, that Renderer should take ownership of the frames themselves.

**Parameters**

- `frame`: Video frames to be presented; After returning, the caller can release it (unless the Renderer documentation claims ownership).

### Clear

```csharp
void Clear();
```

Clear the current screen. Render thread call.

### PresentationLatency

```csharp
TimeSpan PresentationLatency { get; }
```

End-to-end delay in "calling `Present` → the actual visible screen (i.e., the actual time taken to get to the pixel on the screen).

Video synchronization determines how early to call Present** so that the image is visible just when the audio arrives at the frame PTS —
This is the true physical basis for audio-video alignment, not arbitrary synchronization tolerance.

Headed GPU paths (D3D11/Vulkan, etc., vsync rendering) should return to the monitor **refresh cycle**
(Includes DWM/compositor buffer, approximately 1~2 refresh cycles); Headless / Test Stakes / Pure Computation Sink No real upload delay, return
`Zero`。
