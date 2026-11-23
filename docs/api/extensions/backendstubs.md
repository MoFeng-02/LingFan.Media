---
title: "WebRTCOptions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/BackendStubs.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["WebRTCOptions", "EnableHardwareDecode"]
lang: "en-US"
---
# WebRTCOptions

Source: [`BackendStubs.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/BackendStubs.cs)

WebRTC 后端配置选项（桩，保留供 API 兼容）。

## Declaration

```csharp
namespace LingFan.Media.Extensions;

public sealed class WebRTCOptions
```

## Members

### EnableHardwareDecode

```csharp
public bool EnableHardwareDecode { get; set; } = true;
```

是否启用硬件解码（默认 true）。
