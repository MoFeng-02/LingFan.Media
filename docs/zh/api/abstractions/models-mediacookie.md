---
title: "MediaCookie"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/MediaCookie.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaCookie", "Name", "Value", "Domain", "Path", "Secure", "HttpOnly"]
lang: "zh-CN"
---
# MediaCookie

源码: [`MediaCookie.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/MediaCookie.cs)

HTTP Cookie 模型，用于网络流媒体的 Cookie 传递。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaCookie
```

## 成员

### Name

```csharp
public string Name { get; }
```

Cookie 名称。

### Value

```csharp
public string Value { get; }
```

Cookie 值。

### Domain

```csharp
public string? Domain { get; }
```

域（可能为 null）。

### Path

```csharp
public string? Path { get; }
```

路径（可能为 null）。

### Secure

```csharp
public bool Secure { get; }
```

是否仅 HTTPS。

### HttpOnly

```csharp
public bool HttpOnly { get; }
```

是否仅 HTTP（不可通过 JS 访问）。

### 构造函数

```csharp
public MediaCookie(
    string name,
    string value,
    string? domain = null,
    string? path = null,
    bool secure = false,
    bool httpOnly = false);
```

初始化 `MediaCookie` 的新实例。
