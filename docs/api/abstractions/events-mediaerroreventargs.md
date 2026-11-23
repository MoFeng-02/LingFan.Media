---
title: "MediaErrorEventArgs"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Events/MediaErrorEventArgs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaErrorEventArgs", "ErrorCode", "Message", "Exception", "IsFatal", "Recoverable"]
lang: "en-US"
---
# MediaErrorEventArgs

Source: [`MediaErrorEventArgs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Events/MediaErrorEventArgs.cs)

媒体错误事件参数。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaErrorEventArgs : EventArgs
```

## Members

### ErrorCode

```csharp
public MediaErrorCode ErrorCode { get; }
```

错误码。

### Message

```csharp
public string Message { get; }
```

错误消息。

### Exception

```csharp
public Exception? Exception { get; }
```

原始异常（如果有）。

### IsFatal

```csharp
public bool IsFatal { get; }
```

是否致命（不可恢复）。

### Recoverable

```csharp
public bool Recoverable { get; }
```

是否可恢复。

### Constructor

```csharp
public MediaErrorEventArgs(
    MediaErrorCode errorCode,
    string message,
    Exception? exception = null,
    bool isFatal = false,
    bool recoverable = false);
```

初始化 `MediaErrorEventArgs` 的新实例。

**Parameters**

- `errorCode`: 错误码。
- `message`: 错误消息。
- `exception`: 原始异常（可为 null）。
- `isFatal`: 是否致命（不可恢复）。
- `recoverable`: 是否可恢复。
