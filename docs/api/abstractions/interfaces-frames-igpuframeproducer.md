---
title: "IGpuFrameProducer"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuFrameProducer.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IGpuFrameProducer", "ApiType", "TryImport", "IsImportSupported", "null"]
lang: "en-US"
---
# IGpuFrameProducer

Source: [`IGpuFrameProducer.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Frames/IGpuFrameProducer.cs)

Neutral GPU Frame Producer Bridge: implemented by the renderer assembly and registered as a Singleton, the decoding backend relies solely on this abstraction.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IGpuFrameProducer
```

## Remarks

The decoder outputs its native decoded output (D3D11 shared handle / Linux dma_buf / AHardwareBuffer / IOSurface).
Import the texture (`IGpuTextureResource`) from the current renderer's GPU-API via this bridge to achieve zero-copy onscreen.
Along with `IGpuDeviceContext`, it is an Abstractions neutral bridge, strictly adhering to dependency inversion—
The decoding backend does not backreference renderer assemblies, nor is it aware of the specific details of GPU-API binding creation (VkImage creation is done by the producer).

**Ability self-report + behavioral side effects dual criteria (S_OK ≠ acceptance)**:`TryImport` Not available in expansion,
If the native handle is invalid or import fails, `false` is returned. The caller must decode the software and count it in the CPU copy statistics.
Never report "ready" fake green reports.

**Asynchronous Policy**: `TryImport` is a synchronous native call—GPU texture import without I/O await;
Implements synchronization without async (padding means pseudo-asynchronous).

## Members

### ApiType

```csharp
GPUApiType ApiType { get; }
```

GPU APIs targeted by this producer (must match the activated renderer and be matched by the decoder using `ApiType`).

### TryImport

```csharp
bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture);
```

Try importing the native decoded output as a renderer's GPU-API texture (zero-copy).

**Parameters**

- `source`: Decode native output descriptions (handle / size / format / slice).
- `texture`: On success, the renderer's GPU-API texture (&lt;see cref="IGpuTextureResource"/&gt;) is granted, and the caller acquires ownership; On failure&lt;see langword="null"/&gt;.

**Returns**: `true`=zero-copy import successful; `false`=Not available, the caller returns to soft resolution.

### IsImportSupported

```csharp
bool IsImportSupported(GpuFrameImportKind kind) => true;
```

Preflight: Whether this producer supports importing native decoding outputs of specified types (the decoder decides whether to enable the corresponding zero-copy path accordingly).

**Division of labor with `TryImport`**: This method is a pre-test of capabilities before path activation (such as Android decoders).
Determine AHB import feasibility before configuring to the ImageReader Surface), avoiding decoder consumption on paths destined to fail
Reconstruction costs; `TryImport` Still the behavior criterion imported at runtime (dual criterion for ability self-report + behavioral side effects, not fake green).

By default, optimistic returns `true` (compatible with existing consumer behavior: backed by the TryImport runtime).

**Requirements for producers**: Default `true` is only a transitional caliber compatible with existing implementations, **not **
Authorization for "Supported by All Types." New producers must override this method according to the actual supported `GpuFrameImportKind`;
Not overwriting is considered a promise to the decoder that "all types can be imported with zero copy," and the decoder will enable the path that is destined to fail accordingly.
It only fell back when TryImport returned `false` during runtime—during which the decoder rebuild cost was wasted.

**Parameters**

- `kind`: Decode native output types.

**Returns**: `true`=Supports importing this type (corresponding zero-copy path can be enabled).

### null

```csharp
byte[]? DecodeAdapterLuidHint => null;
```

Decoder device alignment prompt (optional): The producer declares "Which GPU should the decoder-side shared handle be produced from" for the decoder to create
Align adapters before your own decoding device, ensuring cross-API imports fall on the same GPU.

**Why do you need it?**: Importing shared handles across GPU/vendors will be rejected by the driver. Most renderers allow free card selection
(For example, Vulkan aligns LUID to the decoder card); But some rendering contexts are pinned to specific GPUs by the platform—typically
Windows WGL: GL context binds the GPU of the monitor where the window is located; renderers cannot move it. At this point, the alignment direction must be reversed:
The decoding device moves closer to the GPU where the rendering context is located. This property is the neutral channel to be reverse-aligned.

**Semantics**: Returns an 8-byte adapter LUID (little-endian, and DXGI_ADAPTER_DESC1. LUID consistent).
"It is recommended that the decoding device be placed on this adapter"; Returning `null` means no alignment requirement (the decoder retains its own pick strategy).
The prompt is **suggestion, not mandatory**: The decoder must first verify that the target adapter has the required decoding capability; if not, it ignores the prompt and falls back according to its own strategy.

**Asynchronous Policy**: Synchronous properties (pure query or lightweight native calls, no I/O await).

The default implementation returns `null` (no alignment requirement, compatible with existing producer behavior).
