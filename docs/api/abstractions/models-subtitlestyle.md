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

Subtitle style configuration.

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

Font family (may be null, use default).

### FontSize

```csharp
public float? FontSize { get; init; }
```

Font size (may be null, use default).

### Color

```csharp
public string? Color { get; init; }
```

foreground color (e.g., "#FFFFFFFF").

### BackgroundColor

```csharp
public string? BackgroundColor { get; init; }
```

Background color (may be null).

### Position

```csharp
public SubtitlePosition Position { get; init; } = SubtitlePosition.Bottom;
```

Subtitle position.

### Alignment

```csharp
public SubtitleAlignment Alignment { get; init; } = SubtitleAlignment.Center;
```

Subtitle alignment.
