---
title: "struct"
type: "record"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSurfaceDescriptor.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["struct", "0"]
lang: "en-US"
---
# struct

Source: [`SharedGpuSurfaceDescriptor.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Models/Gpu/SharedGpuSurfaceDescriptor.cs)

Share GPU surface descriptors (neutral value objects). Describes a cross-device shared texture that can be imported by the host compositor.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public readonly record struct SharedGpuSurfaceDescriptor(
    IntPtr Handle,
    SharedGpuHandleKind Kind,
    int Width,
    int Height,
    SharedGpuSurfaceFormat Format,
    ulong Version,
    SharedGpuSyncMode SyncMode,
    ulong MemorySize = 0,
    ulong MemoryOffset = 0,
    IntPtr NativeImage = default,
    IntPtr NativeDeviceMemory = default,
    uint NativeImageLayout = 0,
    uint NativeVkFormat = 0,
    uint NativeQueueFamilyIndex = 0,
    uint NativeImageUsage = 0,
    uint NativeImageTiling = 0,
    int RotationDegrees = 0)
```

## Remarks

**Pure Data**: No ownership, no release—underlying textures/memory lifecycles are produced by it
`ISharedGpuSurfaceSource` Management.

**Field semantic table (assigned by `Kind`, unique convention)**:

```csharp
Kind                              Handle                     NativeImage        NativeDeviceMemory  MemorySize            MemoryOffset
D3D11TextureGlobalSharedHandle D3D11 Global shared handle 0 0 can be 0 (no validation) 0
D3D11TextureNtHandle D3D11 NT handle (no producer available) 0 0 can be 0 0
VulkanOpaqueNtHandle external memory Win32 handle 0 0 Required (strict equality check) 0
VulkanOpaquePosixFileDescriptor external memory fd 0 0 Required (strict equality check) 0
IOSurfaceRef IOSurfaceRef Reference 0 0 Enter 0 as needed for the import path
AndroidHardwareBuffer AHB reference (traceability/diagnostics) 0 0 allocated bytes 0
VulkanNativeImage VkImage (and NativeImage) VkImage VkDeviceMemory allocate bytes 0
GlTexture source AHB handle (traceability/diagnosis) EGLImageKHR 0 0 0
```

**Table Filling Rules**: Native fields not listed in the table must remain 0—they are meaningless for that Kind,
Non-zero will cause the consumer (or guards like `HasNativeImage`) to misjudge the path as available.
The same field has different meanings under different Kinds; consumers must first look at `Kind` before interpreting the reference handle.

**Handle Lifetime**: The handle is only valid for the "producer-held" period—the native handle in the table
(Especially the fd of `VulkanOpaquePosixFileDescriptor` and `IOSurfaceRef` citation) is held by the producer until the next time
`TryWriteFrame` or the source `Dispose`.
If the consumer wishes to hold across multiple accounts, they must dup/CFRetain themselves and bear the corresponding release responsibility; If used only within this frame, no copying is required.

**AOT Compatible**: readonly record struct, no reflection, no boxing path.

## Members

### 0

```csharp
public bool IsValid => Handle != IntPtr.Zero && Width > 0 && Height > 0;
```

Whether the handle is valid (not empty and dimensionally positive).

**Verification Scope**: Only verify `Handle` non-null and width/height are positive, **do not press `Kind`
Verify that the required native fields for this type are complete.

**Complete criteria for Kind** See the field semantics table in `SharedGpuSurfaceDescriptor`:
Before actual import, consumers must check the required fields for this Kind themselves against the table (such as
`VulkanNativeImage` Must simultaneously have NativeImage / NativeDeviceMemory /
NativeVkFormat / NativeImageLayout / Queue Family and MemorySize; `GlTexture`
Must have NativeImage and NativeVkFormat). This attribute alone determines that descriptors with "handles not empty but native parameters are incomplete" will be overlooked.

### 0

```csharp
public bool HasNativeImage =>
    NativeImage != IntPtr.Zero
    && NativeDeviceMemory != IntPtr.Zero
    && NativeVkFormat != 0
    && NativeImageLayout != 0;
```

Whether to carry the full set of native parameters required for direct sampling on the same device (VkImage + VkDeviceMemory + format + layout).

**Purpose**: For consumers to act as guards before import—for `false` descriptors are insufficient
`VulkanNativeImage` Direct sampling path, which should be backtracked to the export/import path.

**Criteria only look at the above four fields, not `Kind`**: Semantic table by field, not The kind of `VulkanNativeImage` should not be filled in these fields, so the result is naturally `false`。
