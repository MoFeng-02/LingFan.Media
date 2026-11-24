---
title: "MediaBuilderPipelineExtensions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/MediaBuilderPipelineExtensions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaBuilderPipelineExtensions", "WithAudioPipeline", "WithAudioTransforms", "WithVideoPipeline", "WithVideoTransforms"]
lang: "en-US"
---
# MediaBuilderPipelineExtensions

Source: [`MediaBuilderPipelineExtensions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/MediaBuilderPipelineExtensions.cs)

Pipeline post-processing configuration extension (combined root side) for `MediaBuilder`.

## Declaration

```csharp
namespace LingFan.Media.Extensions;

public static class MediaBuilderPipelineExtensions
```

## Remarks

These methods rely on the `AudioPipelineConfig` / `VideoPipelineConfig` of the Audio/Video module,
Therefore, it stays at the Extensions (the only omniscient position) and does not sink into the Composition leaf—
Plugins (backend/renderer/output) only need MediaBuilder's Services/Options registration face and do not perceive pipeline configuration types.

The syntax of calling is consistent with the instance method: `builder.WithAudioPipeline(config)`; Each method is configured synchronously, with no I/O.

## Members

### WithAudioPipeline

```csharp
public static MediaBuilder WithAudioPipeline(this MediaBuilder builder, AudioPipelineConfig config);
```

Inject the audio pipeline configuration to pass the effect chain and reset hook to the Core audio pipeline.

The host can keep the `config` reference and adjust the start and stop of individual effects at runtime via `IsEnabled`;
`ToTransforms()` / `ResetEffects` The effect instance for closure capture is the same as this reference, and the runtime switch takes effect immediately.
If this method is not called, the audio post-processing and reset hook will be null.
If you need to access volume control and mixing simultaneously, use `WithAudioTransforms` to combine it yourself (or call it separately after this method to override).

**Parameters**

- `builder`: Media builder.
- `config`: Audio pipeline configuration (including effect chains and mixing settings).

**Returns**: The same builder facilitates chained calls.

### WithAudioTransforms

```csharp
public static MediaBuilder WithAudioTransforms(
    this MediaBuilder builder,
    IReadOnlyList<Func<AudioFrame, AudioFrame>> transforms,
    Action? reset = null);
```

Directly inject the assembled audio post-processing transformation chain and reset hook.
Suitable for those who need to control volume (`FromVolume`) and mix (`FromMixer`)
Scenes combined with effect chains (`ToTransforms`) and then injected.

**Parameters**

- `builder`: Media builder.
- `transforms`: Combined audio transformation chain (non-null).
- `reset`: Reset the combined state hook (can be null).

**Returns**: The same builder facilitates chained calls.

### WithVideoPipeline

```csharp
public static MediaBuilder WithVideoPipeline(this MediaBuilder builder, VideoPipelineConfig config);
```

Inject video pipeline configuration to pass the post-processing chain and reset hook to the Core video pipeline.

If this method is not called, the video post-processing and reset hook will be null.
If you need to combine post-processing chains and reset hooks yourself, please use `WithVideoTransforms` instead.

**Parameters**

- `builder`: Media builder.
- `config`: Video pipeline configuration (including post-processing chain).

**Returns**: The same builder facilitates chained calls.

### WithVideoTransforms

```csharp
public static MediaBuilder WithVideoTransforms(
    this MediaBuilder builder,
    IReadOnlyList<Func<VideoFrame, VideoFrame?>> transforms,
    Action? reset = null);
```

Directly inject the assembled video post-processing transformation chain and reset hook.

**Parameters**

- `builder`: Media builder.
- `transforms`: Combined video transformation chain (non-null).
- `reset`: Reset the combined state hook (can be null).

**Returns**: The same builder facilitates chained calls.
