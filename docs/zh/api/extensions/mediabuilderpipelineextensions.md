---
title: "MediaBuilderPipelineExtensions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/MediaBuilderPipelineExtensions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaBuilderPipelineExtensions", "WithAudioPipeline", "WithAudioTransforms", "WithVideoPipeline", "WithVideoTransforms"]
lang: "zh-CN"
---
# MediaBuilderPipelineExtensions

源码: [`MediaBuilderPipelineExtensions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/MediaBuilderPipelineExtensions.cs)

`MediaBuilder` 的管线后处理配置扩展（组合根侧）。

## 声明

```csharp
namespace LingFan.Media.Extensions;

public static class MediaBuilderPipelineExtensions
```

## 备注

这些方法依赖 Audio/Video 模块的 `AudioPipelineConfig` / `VideoPipelineConfig`，
故留在组合根 Extensions（唯一全知位置），不下沉到 Composition 叶子——
插件（后端/渲染器/输出）只需 MediaBuilder 的 Services/Options 注册面，不感知管线配置类型。

调用语法与实例方法一致：`builder.WithAudioPipeline(config)`；各方法为同步配置，无 I/O。

## 成员

### WithAudioPipeline

```csharp
public static MediaBuilder WithAudioPipeline(this MediaBuilder builder, AudioPipelineConfig config);
```

注入音频管线配置，将效果链与重置钩子透传至 Core 音频管线。

宿主可保留 `config` 引用，在运行时通过 `IsEnabled` 调整单个效果启停；
`ToTransforms()` / `ResetEffects` 闭包捕获的效果实例与此引用相同，运行时切换即时生效。
不调用本方法则音频后处理与重置钩子为 null。
如需同时接入音量控制 / 混音，请改用 `WithAudioTransforms` 自行组合（或在本方法之后另行调用以覆盖）。

**参数**

- `builder`：媒体构建器。
- `config`：音频管线配置（含效果链及混音设置）。

**返回**：同一构建器，便于链式调用。

### WithAudioTransforms

```csharp
public static MediaBuilder WithAudioTransforms(
    this MediaBuilder builder,
    IReadOnlyList<Func<AudioFrame, AudioFrame>> transforms,
    Action? reset = null);
```

直接注入已组合好的音频后处理变换链与重置钩子。
适用于需将音量控制（`FromVolume`）、混音（`FromMixer`）
与效果链（`ToTransforms`）组合后再注入的场景。

**参数**

- `builder`：媒体构建器。
- `transforms`：组合后的音频变换链（非 null）。
- `reset`：组合后的状态重置钩子（可为 null）。

**返回**：同一构建器，便于链式调用。

### WithVideoPipeline

```csharp
public static MediaBuilder WithVideoPipeline(this MediaBuilder builder, VideoPipelineConfig config);
```

注入视频管线配置，将后处理链与重置钩子透传至 Core 视频管线。

不调用本方法则视频后处理与重置钩子为 null。
如需自行组合后处理链与重置钩子，请改用 `WithVideoTransforms`。

**参数**

- `builder`：媒体构建器。
- `config`：视频管线配置（含后处理链）。

**返回**：同一构建器，便于链式调用。

### WithVideoTransforms

```csharp
public static MediaBuilder WithVideoTransforms(
    this MediaBuilder builder,
    IReadOnlyList<Func<VideoFrame, VideoFrame?>> transforms,
    Action? reset = null);
```

直接注入已组合好的视频后处理变换链与重置钩子。

**参数**

- `builder`：媒体构建器。
- `transforms`：组合后的视频变换链（非 null）。
- `reset`：组合后的状态重置钩子（可为 null）。

**返回**：同一构建器，便于链式调用。
