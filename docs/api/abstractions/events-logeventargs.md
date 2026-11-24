---
title: "LogEventArgs"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Events/LogEventArgs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["LogEventArgs", "Level", "Message", "Source", "Exception"]
lang: "en-US"
---
# LogEventArgs

Source: [`LogEventArgs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Events/LogEventArgs.cs)

Log event parameters.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class LogEventArgs : EventArgs
```

## Members

### Level

```csharp
public LogLevel Level { get; }
```

Log level.

### Message

```csharp
public string Message { get; }
```

Log messages.

### Source

```csharp
public string? Source { get; }
```

Source module (may be null).

### Exception

```csharp
public Exception? Exception { get; }
```

Association exception (if any).

### Constructor

```csharp
public LogEventArgs(LogLevel level, string message, string? source = null, Exception? exception = null);
```

Initialize a new instance of `LogEventArgs`.

**Parameters**

- `level`: Log level.
- `message`: Log messages.
- `source`: Source module (can be null).
- `exception`: Association exception (can be null).
