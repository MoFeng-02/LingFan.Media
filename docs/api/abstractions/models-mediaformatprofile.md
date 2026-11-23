---
title: "MediaFormatProfile"
type: "struct"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/MediaFormatProfile.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaFormatProfile", "Container", "Video"]
lang: "en-US"
---
# MediaFormatProfile

Source: [`MediaFormatProfile.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/MediaFormatProfile.cs)

轻量探测到的「容器格式 + 主视频编码」组合。
描述纯数据，不含任何检测算法。
用于回退调度器的「格式级记忆」：同 (容器, 编码) 组合只经历一次异常驱动回退，
后续同类文件直接命中记忆后端，免去重复回退开销。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public readonly struct MediaFormatProfile
```

## Remarks

该结构置于契约层（Abstractions），使高层中间件（如 LingFan.Media.Playback 回退调度器）
只依赖本数据契约与 `IFormatDetector` 接口，而不引用具体探测实现（LingFan.Media.Formats），
严守依赖倒置（DIP）。

## Members

### Container

```csharp
public readonly ContainerFormat Container;
```

容器格式（如 MP4 / WebM）。

### Video

```csharp
public readonly VideoCodec Video;
```

主视频编码（探测不到为 `Unknown`）。

### Constructor

```csharp
public MediaFormatProfile(ContainerFormat container, VideoCodec video);
```

初始化格式组合画像。

**Parameters**

- `container`: 容器格式。
- `video`: 主视频编码（探测不到时为 &lt;see cref="VideoCodec.Unknown"/&gt;）。
