---
title: "IGpuDeviceContext"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/IGpuDeviceContext.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IGpuDeviceContext", "ApiType", "DeviceHandle", "ContextHandle", "SharedDevice", "SharedPhysicalDevice", "VideoQueueFamilyIndex", "GraphicsQueueFamilyIndex", "IsInitialized", "InitializeAsync", "GetCapabilities"]
lang: "en-US"
---
# IGpuDeviceContext

Source: [`IGpuDeviceContext.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Gpu/IGpuDeviceContext.cs)

Neutral GPU device context contract (cross-layer sharing, zero external references).

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IGpuDeviceContext
```

## Remarks

Acts as a neutral bridge for Abstractions, allowing layers like Avalonia / Outputs to query GPU capabilities or obtain texture handles,
Without referencing specific renderer modules (e.g., Renderers.D3D11), strictly adhere to dependency inversion.

**Asynchronous Strategy**: `InitializeAsync` is an interface contract—when the underlying shared GPU device has already been used
When a factory (such as `D3D11RendererFactory`) is created, this method only performs capability queries (synchronous COM calls) **no real I/O await**,
Implements return `CompletedTask` (non-pseudo-asynchronous, since there are no hidden blocks). If asynchronous initialization is indeed required in a backend implementation,
Then one path `await` achieves true asynchronous. Diagnosis Mnemonic: Whether the method body has a true await → If yes, async; if not, Task.CompletedTask.

## Members

### ApiType

```csharp
GPUApiType ApiType { get; }
```

GPU API type.

### DeviceHandle

```csharp
IntPtr DeviceHandle { get; }
```

Share native GPU device handles (such as pointers to ID3D11Device*). When no device is present, it is `Zero`.

**Pointer semantics determined by `ApiType`**: This property only commits to `ContextHandle`
"The integer form of the native handle" does not promise which object it points to—the consumer must first assign and then decrypt according to `ApiType`,
Do not hard-crack according to the shape of a single backend.

### ContextHandle

```csharp
IntPtr ContextHandle { get; }
```

Share the GPU device context native handle (such as the ID3D11DeviceContext* pointer).
Used for hardware decoder initialization (D3D11VA requires device + device_context). When there is no context, it is `Zero`.

**Pointer semantics are determined by `ApiType`**:D 3D11 is the device context; Under the OpenGL family backend
The actual host is `EGLDisplay` (GL does not have an independent object corresponding to the device context). The consumer must comply with the requirements
`ApiType` Used after assignment; do not apply the same explanation across backends.

### SharedDevice

```csharp
object? SharedDevice { get; }
```

Shared GPU device objects (runtime explicit cast, such as ID3D11Device / Device (Vulkan)).
Allows decoder multiplexing renderer shared devices for zero copying; When no device is present, it is `null`.

### SharedPhysicalDevice

```csharp
object? SharedPhysicalDevice { get; }
```

Shared GPU physical device objects (provided when Vulkan and others require zero copies of "Physical Device alignment," boxed PhysicalDevice).
Non-Vulkan backends are set to `null`.

### VideoQueueFamilyIndex

```csharp
uint VideoQueueFamilyIndex { get; }
```

Video decoding queue family index (corresponding to `VK_QUEUE_VIDEO_DECODE_BIT_KHR`).
The video-decode extension is enabled when the device is created and the queue family exists for it to be `MaxValue`; Otherwise, it is `MaxValue` (decoder drops software to decode).

### GraphicsQueueFamilyIndex

```csharp
uint GraphicsQueueFamilyIndex { get; }
```

Graphics rendering queue family index (corresponding to `VK_QUEUE_GRAPHICS_BIT`).
Vulkan hard-coded DPB images must be shared between the "video-decode queue family" and the "graphic queue family" (cross-queue family zero-copy read/write upload).
This value is used by the decoder to correctly set the queue family list for `VK_SHARING_MODE_CONCURRENT` when creating the DPB image.
Only the Vulkan backend makes sense; When not a Vulkan backend or a single queue family, it is `MaxValue` (decoder degenerates to a family exclusive).

### IsInitialized

```csharp
bool IsInitialized { get; }
```

Whether the device has been initialized (shared device created).

### InitializeAsync

```csharp
Task InitializeAsync(CancellationToken ct = default);
```

Ensure the GPU device context is ready (binding device / query capability).
Interface contract: Returns `CompletedTask` (non-pseudo-asynchronous) when only performing synchronous COM capability queries.

**Parameters**

- `ct`: Cancel token.

**Returns**: Device-ready tasks (achieving `CompletedTask` when no I/O is achieved).

### GetCapabilities

```csharp
GpuDeviceCapabilities GetCapabilities();
```

Obtain GPU device capability (pure memory query, synchronized consumption).

**Returns**: Device capability snapshot.
