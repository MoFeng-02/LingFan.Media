---
title: "SubtitleFrame"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/SubtitleFrame.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["SubtitleFrame", "Text", "Start", "End", "Style"]
lang: "en-US"
---
# SubtitleFrame

Source: [`SubtitleFrame.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/SubtitleFrame.cs)

Subtitle frame. Do not implement `IDisposableFrame` (string + TimeSpan only, no native resources).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class SubtitleFrame
```

## Members

### Text

```csharp
public string Text { get; init; }
```

Subtitle text content.

### Start

```csharp
public TimeSpan Start { get; init; }
```

Display start time.

### End

```csharp
public TimeSpan End { get; init; }
```

Display the end time.

### Style

```csharp
public SubtitleStyle? Style { get; init; }
```

Subtitle style (may be null, use default).

### Constructor

```csharp
public SubtitleFrame(string text, TimeSpan start, TimeSpan end, SubtitleStyle? style = null);
```

Initialize a new instance of `SubtitleFrame`.

**Parameters**

- `text`: Subtitle text content.
- `start`: Display start time.
- `end`: Display the end time.
- `style`: Subtitle style (can be null, use default).
