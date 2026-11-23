---
title: Getting Started
sources: ["src/Infrastructure/LingFan.Media.Extensions/ServiceCollectionExtensions.cs", "src/Infrastructure/LingFan.Media.Extensions/ModuleExtensions.cs", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Sources/Sources/FileMediaSource.cs"]
symbols: ["IMediaPlayer", "IMediaPlayerFactory", "IMediaSource", "FileMediaSource", "VideoFrame", "AudioFrame", "MediaState"]
lang: en
---

# Getting Started

This page walks through registering the media services and playing a file in
three shapes: a headed UI, a headless consumer, and a backend-fallback setup.

## 1. Register services

LingFan.Media is assembled through dependency injection. Add the core, then the
backend(s), renderer and audio output you want. Registration order matters for
fallback (see below).

```csharp
using LingFan.Media.Extensions;                 // AddLingFanMedia
using LingFan.Media.Backends.FFmpeg;            // AddFFmpeg
using LingFan.Media.Backends.MediaFoundation;   // AddMediaFoundation
using LingFan.Media.Renderers.D3D11;            // AddD3D11Renderer
using LingFan.Media.Outputs.Wasapi;             // AddWasapiOutput
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddLingFanMedia()        // core: clock, buffering, frame routing, session
        .AddFFmpeg()              // cross-platform decoding backend (primary)
        .AddMediaFoundation()     // Windows native backend (optional fallback)
        .AddD3D11Renderer()       // Windows GPU renderer
        .AddWasapiOutput();       // Windows audio output

var provider = services.BuildServiceProvider();
```

`AddLingFanMedia()` mounts the shared infrastructure (stream factory, format
detector, codec registry, named `HttpClient` with SSRF protection) and the
backend-fallback player factory. Each `AddXxx()` registers that component's
factories; demuxer factories are registered as an ordered collection so the
player can try them in sequence.

> Pick the renderer and audio output that match your target platform. See
> [Platform Support](/guide/platforms) for the full matrix. On Linux, for
> example, use a Vulkan or OpenGL renderer with an OpenAL output instead of
> D3D11 / WASAPI.

## 2. Headless playback

A headless consumer subscribes to decoded frames directly — no window, no UI
framework. This is the same pipeline a UI uses.

```csharp
using LingFan.Media.Abstractions;
using LingFan.Media.Sources;

var factory = provider.GetRequiredService<IMediaPlayerFactory>();
await using var player = factory.Create();

// Frames are borrowed read-only inside the callback. Copy what you need
// synchronously; do NOT dispose the frame or keep a reference after returning.
player.VideoFrameAvailable += (VideoFrame frame) =>
{
    // e.g. read frame.Width / frame.Height, copy pixels, run inference...
};

player.StateChanged += (_, e) => Console.WriteLine(e.State);

await player.OpenAsync(new FileMediaSource("path/to/video.mp4"));
await player.PlayAsync();

// ... let it play ...

await player.StopAsync();
```

Key lifetime rules:

- `OpenAsync` / `SeekAsync` / `StopAsync` perform real I/O and accept a
  `CancellationToken`. `PlayAsync` / `PauseAsync` are pure in-memory state
  transitions and complete synchronously.
- The frame passed to `VideoFrameAvailable` (and `AudioDataAvailable`) is a
  **read-only borrow**. Copy any data you need before the callback returns; never
  dispose it or hold it across threads.
- Prefer `await player.DisposeAsync()`; the synchronous `Dispose()` is a fallback
  that releases resources without waiting for the pipeline threads.

## 3. Headed playback (Avalonia)

When you use the Avalonia UI layer, bind a player to a `VideoView`. Open the
player **before** assigning it, because the property-changed callback is a
synchronous `void` and cannot await.

```csharp
await player.OpenAsync(new FileMediaSource("path/to/video.mp4"));
myVideoView.Player = player;   // VideoView subscribes to the frame channel and presents
await player.PlayAsync();
```

The `VideoView` subscribes to the same frame channel as a headless consumer and
presents frames through the platform compositor — there is no second, divergent
delivery path.

## 4. Backend fallback

When more than one backend is registered, the default factory tries them in
registration order and remembers which backend succeeded for a given
`(container, codec)` pair, so subsequent opens skip straight to the working one.

```csharp
services.AddLingFanMedia()
        .AddFFmpeg()            // tried first
        .AddMediaFoundation();  // tried if FFmpeg cannot open the source
```

To force a specific backend regardless of fallback, use the explicit
`IMediaPlayerFactory.Create(...)` overload that takes a backend group.

## Key types

| Type | Role |
|---|---|
| `IMediaPlayer` | The playback session: open / play / pause / seek / stop / dispose, state and position, frame events. |
| `IMediaPlayerFactory` | Creates player sessions; resolves the backend group. |
| `IMediaSource` | Describes where media comes from (file, network, stream). |
| `VideoFrame` / `AudioFrame` | Decoded frames delivered through the frame channel / audio output. |
| `MediaState` | The session state machine (Idle, Opening, Buffering, Playing, Paused, Stopped, Ended, Error). |

Every public type above has a reference page under [API Reference](/api/)
with its exact signature and remarks.
