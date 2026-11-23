---
title: "LogEventArgs"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Events/LogEventArgs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["LogEventArgs", "Level", "Message", "Source", "Exception"]
lang: "zh-CN"
---
# LogEventArgs

源码: [`LogEventArgs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Events/LogEventArgs.cs)

日志事件参数。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class LogEventArgs : EventArgs
```

## 成员

### Level

```csharp
public LogLevel Level { get; }
```

日志级别。

### Message

```csharp
public string Message { get; }
```

日志消息。

### Source

```csharp
public string? Source { get; }
```

来源模块（可能为 null）。

### Exception

```csharp
public Exception? Exception { get; }
```

关联异常（如果有）。

### 构造函数

```csharp
public LogEventArgs(LogLevel level, string message, string? source = null, Exception? exception = null);
```

初始化 `LogEventArgs` 的新实例。
