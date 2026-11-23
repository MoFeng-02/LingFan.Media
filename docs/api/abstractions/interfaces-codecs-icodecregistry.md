---
title: "ICodecRegistry"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Codecs/ICodecRegistry.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ICodecRegistry", "IsCodecSupported", "GetDefaultVideoCodec", "GetDefaultAudioCodec"]
lang: "en-US"
---
# ICodecRegistry

Source: [`ICodecRegistry.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Codecs/ICodecRegistry.cs)

编解码器注册表契约（跨层共享，零外部引用）。

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface ICodecRegistry
```

## Remarks

提供容器格式 → 编解码器的静态映射查询，供后端自动选择
（`MediaOptions.EnableAutoBackendSelection`）与能力探测使用。

纯内存静态表，全部同步（sync 分类）：无 I/O、无 await，实现返回普通 `bool` /
可空枚举，绝不包装为 `Task`（否则即伪异步）。

仅引用跨层枚举 `ContainerFormat` / `VideoCodec` / `AudioCodec`
（均为 Abstractions 已有类型），零外部引用，依赖倒置严守。

## Members

### IsCodecSupported

```csharp
bool IsCodecSupported(ContainerFormat container, VideoCodec videoCodec);
```

判断指定容器格式是否支持给定视频编解码器。

**Parameters**

- `container`: 容器格式。
- `videoCodec`: 视频编解码器。

**Returns**: 支持则返回 `true`。

### IsCodecSupported

```csharp
bool IsCodecSupported(ContainerFormat container, AudioCodec audioCodec);
```

判断指定容器格式是否支持给定音频编解码器。

**Parameters**

- `container`: 容器格式。
- `audioCodec`: 音频编解码器。

**Returns**: 支持则返回 `true`。

### GetDefaultVideoCodec

```csharp
VideoCodec? GetDefaultVideoCodec(ContainerFormat container);
```

获取指定容器格式的默认视频编解码器（未知容器返回 `null`）。

**Parameters**

- `container`: 容器格式。

**Returns**: 默认视频编解码器，或 `null`。

### GetDefaultAudioCodec

```csharp
AudioCodec? GetDefaultAudioCodec(ContainerFormat container);
```

获取指定容器格式的默认音频编解码器（未知容器返回 `null`）。

**Parameters**

- `container`: 容器格式。

**Returns**: 默认音频编解码器，或 `null`。
