---
title: "WebRTCOptions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/BackendStubs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["WebRTCOptions", "EnableHardwareDecode"]
lang: "zh-CN"
---
# WebRTCOptions

源码: [`BackendStubs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/BackendStubs.cs)

WebRTC 后端配置选项（桩，保留供 API 兼容）。

## 声明

```csharp
namespace LingFan.Media.Extensions;

public sealed class WebRTCOptions
```

## 成员

### EnableHardwareDecode

```csharp
public bool EnableHardwareDecode { get; set; } = true;
```

是否启用硬件解码（默认 true）。
