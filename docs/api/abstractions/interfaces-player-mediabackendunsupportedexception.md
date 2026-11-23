---
title: "MediaBackendUnsupportedException"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/MediaBackendUnsupportedException.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaBackendUnsupportedException"]
lang: "en-US"
---
# MediaBackendUnsupportedException

Source: [`MediaBackendUnsupportedException.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/MediaBackendUnsupportedException.cs)

当所有已注册后端均无法打开给定媒体源时抛出（运行时回退穷尽）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaBackendUnsupportedException : Exception
```

## Members

### Constructor

```csharp
public MediaBackendUnsupportedException(string source)
    : base($"没有已注册的后端能够打开该媒体源：{source}") { }

/// <summary>构造异常并携带导致穷尽回退的内部异常。</summary>
/// <param name="source">媒体源标识。</param>
/// <param name="innerException">导致穷尽回退的内部异常。</param>
public MediaBackendUnsupportedException(string source, Exception innerException)
    : base($"没有已注册的后端能够打开该媒体源：{source}", innerException) { }
```

构造异常，source 为媒体源标识（`Identifier`）。

**Parameters**

- `source`: 媒体源标识。
