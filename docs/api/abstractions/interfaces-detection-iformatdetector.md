---
title: "IFormatDetector"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Detection/IFormatDetector.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IFormatDetector", "DetectProfile", "DetectProfileAsync"]
lang: "en-US"
---
# IFormatDetector

Source: [`IFormatDetector.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Detection/IFormatDetector.cs)

Media format detector contract (dependent inversion).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IFormatDetector
```

## Remarks

Before Open, perform lightweight head probes on the locator stream, returning `MediaFormatProfile`(container, main video encoding),
Allows the rollback scheduler to preemptively hit "format-level memory" and skip known bad backends, preventing the reverse drive of the same format from running an abnormal driver backward every time.

The specific implementation is located at the infrastructure layer (`FormatDetector` in LingFan.Media.Formats).
High-level middleware (LingFan.Media.Playback) relies solely on this contract, **does not **cite specific implementations**, and strictly adheres to dependency inversion (DIP).

Only read the magic number/encoding identifier at the stream header, without creating a full session; Unlocating stream returns Unknown/Unknown.

## Members

### DetectProfile

```csharp
MediaFormatProfile DetectProfile(IMediaStream stream);
```

Synchronous lightweight detection (container, video encoding). Unlocating stream returns Unknown/Unknown.

**Parameters**

- `stream`: Media data stream (should be located).

**Returns**: Detected `MediaFormatProfile`.

### DetectProfileAsync

```csharp
Task<MediaFormatProfile> DetectProfileAsync(IMediaStream stream, CancellationToken ct = default);
```

Asynchronous lightweight detection (container, video encoding). Unlocating stream returns Unknown/Unknown.

**Parameters**

- `stream`: Media data stream (should be located).
- `ct`: Cancel token.

**Returns**: Detected `MediaFormatProfile`.
