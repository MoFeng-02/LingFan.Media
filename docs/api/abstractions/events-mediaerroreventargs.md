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

Media error event parameters.

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

Error code.

### Message

```csharp
public string Message { get; }
```

Error message.

### Exception

```csharp
public Exception? Exception { get; }
```

Original exception (if any).

### IsFatal

```csharp
public bool IsFatal { get; }
```

Whether it is fatal (irreversible).

### Recoverable

```csharp
public bool Recoverable { get; }
```

Is it recoverable?

### Constructor

```csharp
public MediaErrorEventArgs(
    MediaErrorCode errorCode,
    string message,
    Exception? exception = null,
    bool isFatal = false,
    bool recoverable = false);
```

Initialize a new instance of `MediaErrorEventArgs`.

**Parameters**

- `errorCode`: Error code.
- `message`: Error message.
- `exception`: Original exception (can be null).
- `isFatal`: Whether it is fatal (irreversible).
- `recoverable`: Is it recoverable?
