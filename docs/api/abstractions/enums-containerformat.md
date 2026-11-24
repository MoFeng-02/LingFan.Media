---
title: "ContainerFormat"
type: "enum"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Enums/ContainerFormat.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ContainerFormat", "MP4", "MKV", "AVI", "TS", "WebM", "FLV", "Unknown"]
lang: "en-US"
---
# ContainerFormat

Source: [`ContainerFormat.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Enums/ContainerFormat.cs)

Container format type.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public enum ContainerFormat : int
```

## Values

| Value | Description |
| --- | --- |
| `MP4` | MP4 / MOV containers. |
| `MKV` | Matroska / WebM container. |
| `AVI` | AVI container. |
| `TS` | MPEG-TS transport stream. |
| `WebM` | WebM containers (based on EBML/Matroska). |
| `FLV` | Flash Video container. |
| `Unknown` | Unknown container format. |
