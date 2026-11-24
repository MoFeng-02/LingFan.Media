---
title: "Bt601FullRange"
type: "class"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/Bt601FullRange.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["Bt601FullRange"]
lang: "en-US"
---
# Bt601FullRange

Source: [`Bt601FullRange.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Frames/Bt601FullRange.cs)

BT.601 Full Range (JFIF) YUV→RGB Conversion Coefficient — the unique constant true source for the full-chamber CPU conversion path.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public static class Bt601FullRange
```

## Remarks

Formula (Y/U/V is 0..255 integer field, chroma center 128):
R = Y + `Rv`· (V−128)；
G = Y − `Gu`· (U−128) − `Gv`· (V−128)；
B = Y + `Bu`· (U−128)； The result was clamped down to 0..255.

Normalized domain (0.. 1. Chromaticity center 0.5) Use the same set of coefficients. GPU shader path
(D3D11 HLSL / GLSL / MSL / SPIR-V) embeds the same set of values. When modifying this class, you must simultaneously verify all shader sources,
Otherwise, the CPU backing and GPU main path color drift.

This class only covers the BT.601-Full default matrix; BT.709/BT.2020 with limited-range compensation factors
Selected separately by the consumer according to `VideoColorInfo`, not covered by this category.

## Members

### 402

```csharp
public const double Rv = 1.402;
```

R channel V coefficient (+1.402).

### 344136

```csharp
public const double Gu = 0.344136;
```

G channel U coefficient (−0.344136, negative in the formula).

### 714136

```csharp
public const double Gv = 0.714136;
```

G channel V coefficient (−0.714136, negative in the formula).

### 772

```csharp
public const double Bu = 1.772;
```

B channel U coefficient (+1.772).

### 128

```csharp
public const int ChromaCenter = 128;
```

Chroma center (8-bit integer field, 128).
