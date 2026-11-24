---
title: "MediaCookie"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/MediaCookie.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["MediaCookie", "Name", "Value", "Domain", "Path", "Secure", "HttpOnly"]
lang: "en-US"
---
# MediaCookie

Source: [`MediaCookie.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/MediaCookie.cs)

HTTP Cookie model, used for cookie delivery in web streaming.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public sealed class MediaCookie
```

## Members

### Name

```csharp
public string Name { get; }
```

Cookie name.

### Value

```csharp
public string Value { get; }
```

Cookie value.

### Domain

```csharp
public string? Domain { get; }
```

Field (possibly null).

### Path

```csharp
public string? Path { get; }
```

Path (may be null).

### Secure

```csharp
public bool Secure { get; }
```

Is it HTTPS only?

### HttpOnly

```csharp
public bool HttpOnly { get; }
```

Whether it is HTTP-only (not accessible via JS).

### Constructor

```csharp
public MediaCookie(
    string name,
    string value,
    string? domain = null,
    string? path = null,
    bool secure = false,
    bool httpOnly = false);
```

Initialize a new instance of `MediaCookie`.

**Parameters**

- `name`: Cookie name.
- `value`: Cookie value.
- `domain`: Field (can be null).
- `path`: Path (can be null).
- `secure`: Is it HTTPS only?
- `httpOnly`: Whether it is HTTP-only (not accessible via JS).
