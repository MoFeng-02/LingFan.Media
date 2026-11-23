---
title: "IMediaSourceMetadata"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaSourceMetadata.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaSourceMetadata", "Name", "ContentType", "IsLive", "ExtraFields"]
lang: "zh-CN"
---
# IMediaSourceMetadata

源码: [`IMediaSourceMetadata.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaSourceMetadata.cs)

媒体源元数据接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaSourceMetadata
```

## 成员

### Name

```csharp
string? Name { get; }
```

名称（可能为 null）。

### ContentType

```csharp
string? ContentType { get; }
```

MIME 类型（可能为 null）。

### IsLive

```csharp
bool IsLive { get; }
```

是否直播流。

### ExtraFields

```csharp
IReadOnlyDictionary<string, string> ExtraFields { get; }
```

自定义字段。
