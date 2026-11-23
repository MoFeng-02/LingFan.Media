---
title: "MediaSourceType"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaSourceType.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaSourceType", "File", "Network", "Stream"]
lang: "zh-CN"
---
# MediaSourceType

源码: [`MediaSourceType.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/MediaSourceType.cs)

媒体来源类型。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public enum MediaSourceType : int
```

## 枚举值

| 值 | 说明 |
| --- | --- |
| `File` | 本地文件。 |
| `Network` | 网络流。 |
| `Stream` | 自定义流。 |
