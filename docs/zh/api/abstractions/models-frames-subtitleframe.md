---
title: "SubtitleFrame"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/SubtitleFrame.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SubtitleFrame", "Text", "Start", "End", "Style"]
lang: "zh-CN"
---
# SubtitleFrame

源码: [`SubtitleFrame.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/SubtitleFrame.cs)

字幕帧。不实现 `IDisposableFrame`（仅 string + TimeSpan，无原生资源）。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class SubtitleFrame
```

## 成员

### Text

```csharp
public string Text { get; init; }
```

字幕文本内容。

### Start

```csharp
public TimeSpan Start { get; init; }
```

显示开始时间。

### End

```csharp
public TimeSpan End { get; init; }
```

显示结束时间。

### Style

```csharp
public SubtitleStyle? Style { get; init; }
```

字幕样式（可能为 null，使用默认）。

### 构造函数

```csharp
public SubtitleFrame(string text, TimeSpan start, TimeSpan end, SubtitleStyle? style = null);
```

初始化 `SubtitleFrame` 的新实例。
