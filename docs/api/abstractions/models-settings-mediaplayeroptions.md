---
title: "MediaPlayerOptions"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/MediaPlayerOptions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaPlayerOptions", "DefaultVolume", "DefaultMuted", "DefaultPlaybackRate", "EnableHardwareAcceleration", "SoftwareDecodeComplexityThreshold", "VideoFrameQueueCapacity", "AudioSampleQueueCapacity", "LocalBufferTarget", "NetworkBufferTarget", "AudioOutputSampleRate", "AudioOutputChannels", "AudioOutputSampleFormat"]
lang: "en-US"
---
# MediaPlayerOptions

Source: [`MediaPlayerOptions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/MediaPlayerOptions.cs)

Player default configuration options (contract layer shared configuration model).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaPlayerOptions
```

## Remarks

Read by the Core's MediaPlayerFactory, used to set the default value for newly created players;
Bound by host DI via `IOptions<MediaPlayerOptions>` (registered in Extensions layer `AddLingFanMedia`).

Placed at the Abstractions contract layer: This configuration is used by both Extensions (configuration side) and Core (consumption side), forming a multi-layer shared neutral data model,
Complies with the contract layer "pure data model, zero external references" criterion (co-listed with `AudioSettings` / `VideoSettings`).

## Members

### DefaultVolume

```csharp
public float DefaultVolume { get; set; } = 1.0f;
```

Default volume (0.0~1.0).

### DefaultMuted

```csharp
public bool DefaultMuted { get; set; }
```

Default is mute.

### DefaultPlaybackRate

```csharp
public float DefaultPlaybackRate { get; set; } = 1.0f;
```

Default playback rate.

### EnableHardwareAcceleration

```csharp
public bool EnableHardwareAcceleration { get; set; } = true;
```

Whether hardware accelerated decoding is enabled.

When the user explicitly sets it to false, the original intent must be genuinely effective (even if the content exceeds the software decoding threshold, hard decryption is not forced);
The policy only decides whether to "attempt" hard decryption and provides alerts based on this. See `DecodePreferencePolicy` for details.

### SoftwareDecodeComplexityThreshold

```csharp
public long? SoftwareDecodeComplexityThreshold { get; set; }
```

Software decoding capability threshold (decoding cost score). Content exceeding this value will trigger an alarm when "hardware acceleration is prioritized" and hardware decoding is not actually performed.
null = uses `DefaultSoftwareDecodeThreshold`(≈2K60 H265).

Adjustable up or down as needed for headless/confined environments. Lowering = earlier tendency to hard solve; Increase = more tolerant of software decoding.

### VideoFrameQueueCapacity

```csharp
public int VideoFrameQueueCapacity { get; set; } = 30;
```

Video frame queue capacity.

### AudioSampleQueueCapacity

```csharp
public int AudioSampleQueueCapacity { get; set; } = 60;
```

Audio frame queue capacity.

### FromSeconds

```csharp
public TimeSpan LocalBufferTarget { get; set; } = TimeSpan.FromSeconds(5);
```

Buffer target duration (local file).

### FromSeconds

```csharp
public TimeSpan NetworkBufferTarget { get; set; } = TimeSpan.FromSeconds(30);
```

Buffer target duration (network flow).

### AudioOutputSampleRate

```csharp
public int? AudioOutputSampleRate { get; set; }
```

Audio output target sample rate (optional override). null = sampling rate using source media.

Resampled via `AudioSettings` to FFmpegAudioDecoder. If one were to set it up,
Decoder output and WASAPI devices operate at this rate to avoid rhythm/pitch confusion.

### AudioOutputChannels

```csharp
public int? AudioOutputChannels { get; set; }
```

Number of target channels for audio output (optional coverage). null = Number of channels using source media.

### AudioOutputSampleFormat

```csharp
public SampleFormat? AudioOutputSampleFormat { get; set; }
```

Audio output target sampling format (optional override). null = Use source media sampling format.
