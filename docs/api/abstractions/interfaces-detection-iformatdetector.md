---
title: "IFormatDetector"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Detection/IFormatDetector.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFormatDetector", "DetectProfile", "DetectProfileAsync"]
lang: "en-US"
---
# IFormatDetector

Source: [`IFormatDetector.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Detection/IFormatDetector.cs)

媒体格式探测器契约（依赖倒置点）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFormatDetector
```

## Remarks

在 Open 前对可定位流做轻量头部探测，返回 `MediaFormatProfile`(容器, 主视频编码)，
供回退调度器提前命中「格式级记忆」、跳过已知坏后端，避免每次同格式重新走异常驱动回退。

具体实现位于基础设施层（LingFan.Media.Formats 的 `FormatDetector`）。
高层中间件（LingFan.Media.Playback）仅依赖本契约，**不**引用具体实现，严守依赖倒置（DIP）。

仅读流头部魔数/编码标识，不建立完整 Session；不可定位流返回 Unknown/Unknown。

## Members

### DetectProfile

```csharp
MediaFormatProfile DetectProfile(IMediaStream stream);
```

同步轻量探测 (容器, 视频编码)。不可定位流返回 Unknown/Unknown。

**Parameters**

- `stream`: 媒体数据流（应可定位）。

**Returns**: 探测到的 `MediaFormatProfile`。

### DetectProfileAsync

```csharp
Task<MediaFormatProfile> DetectProfileAsync(IMediaStream stream, CancellationToken ct = default);
```

异步轻量探测 (容器, 视频编码)。不可定位流返回 Unknown/Unknown。

**Parameters**

- `stream`: 媒体数据流（应可定位）。
- `ct`: 取消令牌。

**Returns**: 探测到的 `MediaFormatProfile`。
