---
title: "SubtitleStyle"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/SubtitleStyle.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SubtitleStyle", "FontFamily", "FontSize", "Color", "BackgroundColor", "Position", "Alignment"]
lang: "en-US"
---
# SubtitleStyle

Source: [`SubtitleStyle.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/SubtitleStyle.cs)

字幕样式配置。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class SubtitleStyle
```

## Members

### FontFamily

```csharp
public string? FontFamily { get; init; }
```

字体族（可能为 null，使用默认）。

### FontSize

```csharp
public float? FontSize { get; init; }
```

字号（可能为 null，使用默认）。

### Color

```csharp
public string? Color { get; init; }
```

前景色（如 "#FFFFFFFF"）。

### BackgroundColor

```csharp
public string? BackgroundColor { get; init; }
```

背景色（可能为 null）。

### Position

```csharp
public SubtitlePosition Position { get; init; } = SubtitlePosition.Bottom;
```

字幕位置。

### Alignment

```csharp
public SubtitleAlignment Alignment { get; init; } = SubtitleAlignment.Center;
```

字幕对齐方式。
