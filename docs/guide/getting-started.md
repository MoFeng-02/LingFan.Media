# Getting Started

This guide wires up LingFan.Media with dependency injection and plays a file — headless and headed.

## 1. Register services

LingFan.Media is assembled entirely through `Microsoft.Extensions.DependencyInjection`. The single entry point is `AddLingFanMedia()`, which returns a fluent `MediaBuilder`. You then chain backend and renderer/output registrations.

```csharp
using LingFan.Media.Extensions;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services
    .AddLingFanMedia()          // core + fallback middleware (auto-mounted)
    .AddFFmpeg()                 // FFmpeg backend (cross-platform)
    .AddMediaFoundation()        // Windows MediaFoundation backend
    .AddD3D11Renderer()          // D3D11 GPU renderer (Windows)
    .AddWasapiOutput();          // WASAPI audio output (Windows)

// On Apple / Android, FFmpeg + VLC already work today; first-party native backends
// (AVFoundation, MediaCodec) will be added progressively. Linux is not a target platform.

var provider = services.BuildServiceProvider();
```

`AddLingFanMedia()` automatically mounts `BackendFallbackMediaPlayerFactory`, so the runtime picks a working backend for you and falls back when one throws.

## 2. Open and play (headless)

In headless mode you subscribe to `VideoFrameAvailable` / `AudioDataAvailable` and consume frames yourself — no UI required.

```csharp
using LingFan.Media.Abstractions;
using LingFan.Media.Sources;

// Resolve the factory (auto-mounted by AddLingFanMedia).
var factory = provider.GetRequiredService<IMediaPlayerFactory>();
var player = factory.Create();

// Build a source for a local file.
IMediaSource source = new FileMediaSource("sample.mp4");

player.VideoFrameAvailable += frame =>
{
    // frame is a read-only borrow — do NOT Dispose it.
    Console.WriteLine($"frame {frame.Width}x{frame.Height} @ {frame.Timestamp}");
};

await player.OpenAsync(source);
await player.PlayAsync();

// ... keep the process alive while playback runs ...
await player.StopAsync();
await player.DisposeAsync();
```

`IMediaPlayer` exposes the full contract: `State`, `Position`, `Duration`, `Volume`, `PlaybackRate`, `VideoDroppedFrames`, plus the `StateChanged`, `ErrorOccurred`, `PositionChanged`, and `SubtitleReceived` events.

## 3. Open and play (headed, Avalonia)

For an on-screen player, bind the `IMediaPlayer` to the Avalonia `VideoView`. The UI subscribes to the same `VideoFrameAvailable` channel and bridges it to `IVideoRenderer.Present` — no backend changes.

```csharp
// After AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()
// and AddAvaloniaControls() (UI layer):
var player = provider.GetRequiredService<IMediaPlayerFactory>().Create();
await player.OpenAsync(new FileMediaSource("sample.mp4"));
myVideoView.Player = player;   // VideoView subscribes to the frame channel
await player.PlayAsync();
```

## 4. Backend selection & fallback

You normally do not pick a backend. `BackendFallbackMediaPlayerFactory` tries backends in DI registration order and remembers the working one per file and per `(container, codec)` pair. To force a specific backend, use the `IMediaPlayerFactory.Create(...)` overload with explicit demuxer/decoder factories.

> **Not supported out of the box:** loop playback (no `Loop` property — subscribe to `StateChanged` and call `PlayAsync()` on `Ended`), playlists, and persistent backend-memory across processes.

## Key types

| Type | Namespace | Role |
|------|-----------|------|
| `IMediaPlayer` | `LingFan.Media.Abstractions` | The playback facade |
| `IMediaPlayerFactory` | `LingFan.Media.Abstractions` | Creates players; `BackendFallbackMediaPlayerFactory` is the default |
| `IMediaSource` / `FileMediaSource` | `LingFan.Media.Abstractions` / `LingFan.Media.Sources` | Describes what to play |
| `AddLingFanMedia()` | `LingFan.Media.Extensions` | DI entry point |
| `MediaBuilder` | `LingFan.Media.Extensions` | Fluent registration chain |
