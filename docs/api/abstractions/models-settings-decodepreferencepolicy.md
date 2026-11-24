---
title: "DecodePreferencePolicy"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/DecodePreferencePolicy.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["DecodePreferencePolicy", "ComputeScore", "ExceedsSoftwareDecodeCapability", "BitDepthOf"]
lang: "en-US"
---
# DecodePreferencePolicy

Source: [`DecodePreferencePolicy.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Settings/DecodePreferencePolicy.cs)

Decoding preference strategy: Use the "decoding cost score" instead of the pure resolution threshold to determine whether the content exceeds the software's real-time decoding capability,
Thus, it was decided that "hardware acceleration should be prioritized." Cost score = Wide × High × Frame rate × Encoding complexity weight × bit depth weight.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public static class DecodePreferencePolicy
```

## Remarks

Design motivation: Pure resolution thresholds miss scenes with high frame rates (such as 1440p@120) and high complexity coding (AV1/HEVC);
10-bit software decoding is about 30% more expensive than 8-bit software. Cost analysis combines all three factors to provide more stable judgments. Experience points:
1080p@60 H265≈199M, 1440p@60 H265≈354M, 4K@60 H264≈498M, 4K@60 H265≈796M (unit: weighted pixels per second).

Semantics are "Preferred Hardware" rather than "Force": This policy only determines whether hard solves should be attempted;
Whether hardware decryption can be done depends on whether GPU device context is available at runtime, and if hardware decryption fails, it must gracefully switch back to soft decryption (guaranteed by default).
The user's intention to explicitly disable hardware acceleration always takes precedence—the policy does not override false in `EnableHardwareAcceleration`.

Pure static, non-reflective, and unallocated to meet AOT Zero Warning requirements.

## Members

### 300_000_000L

```csharp
public const long DefaultSoftwareDecodeThreshold = 300_000_000L;
```

Default software decoding capability threshold (cost score). ≈ 2K (2560×1440) @ 60 H265 8-bit weighted value,
Used as the default starting point for "Recommended Priority Hard Decoding." Exceeding this is considered software decoding difficult in real time on typical CPUs.

### ComputeScore

```csharp
public static long ComputeScore(VideoTrackInfo? info, VideoCodec? codec);
```

Calculate the decoding cost of video tracks (weighted pixels per second). Returning 0 indicates insufficient information (e.g., track information is empty).

**Parameters**

- `info`: Video track details (width/height/frame rate/pixel format).
- `codec`: Video encoding; If unknown, calculate based on the baseline weight.

### ExceedsSoftwareDecodeCapability

```csharp
public static bool ExceedsSoftwareDecodeCapability(VideoTrackInfo? info, VideoCodec? codec, long? threshold = null);
```

Whether the content exceeds the software decoding real-time capability threshold (hardware acceleration should be prioritized).

**Parameters**

- `info`: Video track details.
- `codec`: Video encoding.
- `threshold`: Threshold; null uses &lt;see cref="DefaultSoftwareDecodeThreshold"/&gt;.

### BitDepthOf

```csharp
public static int BitDepthOf(PixelFormat format) => format switch;
```

Bit depth for pixel format (video field: 10 for 10-bit format, 8 for others).

**Parameters**

- `format`: Pixel format.

**Returns**: Bit depth: 10 returns 10 in 10-bit format, 8 for others.
