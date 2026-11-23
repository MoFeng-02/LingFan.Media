---
title: Linux Deployment
sources: ["src/Backends/LingFan.Media.Backends.FFmpeg/Interop/Loading/FFmpegLibraryLoader.cs", "src/Renderers/LingFan.Media.Renderers.Vulkan/LingFan.Media.Renderers.Vulkan.csproj", "src/Renderers/LingFan.Media.Renderers.OpenGL/LingFan.Media.Renderers.OpenGL.csproj", "src/Outputs/LingFan.Media.Outputs/LingFan.Media.Outputs.csproj"]
symbols: ["IVideoRenderer", "IAudioOutput", "IFrameResource", "GPUApiType"]
lang: en
---

# Linux Deployment

This page covers the runtime prerequisites and hardware-decode setup for running
LingFan.Media on Linux. The validated zero-copy path is FFmpeg + VAAPI → Vulkan;
OpenGL is supported with a CPU-upload fallback where driver import is limited.
See [Platform Support](/guide/platforms) for the support matrix.

## Runtime prerequisites

| Component | Requirement |
|---|---|
| .NET | .NET 10 runtime (or a self-contained / AOT publish). |
| FFmpeg | Shared libraries (LGPL build): `libavutil`, `libavcodec`, `libavformat`, `libswscale`, `libswresample`, `libavfilter`, `libavdevice`, with the usual version-alias symlinks. |
| OpenAL | `libopenal1` for audio output. If absent, audio falls back to silence rather than failing playback. |
| Audio server | PulseAudio or PipeWire (whatever OpenAL targets). |

The FFmpeg loader searches, in order: the system library path, the application
directory, then a recursive scan (highest version wins). You can pin a specific
library with the `LF_FFMPEG_LIB` environment variable.

> When syncing FFmpeg between Windows and Linux, version symlinks degrade to plain
> text files. Extract the Linux tarball on the Linux side so the shared-object
> symlinks are real.

## Software decode (minimum viable)

With just the FFmpeg shared libraries and OpenAL installed, software decoding and
playback work out of the box — no GPU setup required. This is the simplest path to
verify a deployment.

## VAAPI hardware decode

Hardware decode through VAAPI needs three things:

1. **libva ≥ 2.21.** Older libva (e.g. 2.20) lacks `vaMapBuffer2`, which the
   zero-copy export path uses. Install a newer libva (your distribution's updates
   repository or a packaged build).
2. **An Intel media driver.** `intel-media-va-driver-non-free` (the iHD driver)
   for Intel GPUs.
3. **DRM render node access.** The process must be able to open
   `/dev/dri/renderD128` (add the user to the `render` / `video` groups). Verify
   with `vainfo`.

The native loader `dlopen`s `libva.so.2` and does not consume `DT_RUNPATH`, so a
non-system libva must be preloaded by absolute path (e.g. via `LD_LIBRARY_PATH`
or the loader's pinning mechanism).

## Windowed rendering

| Renderer | Packages | Zero-copy behavior |
|---|---|---|
| Vulkan | `mesa-vulkan-drivers` (ANV for Intel) | dma-buf import; the Vulkan physical device is auto-aligned to the decode GPU (same vendor). |
| OpenGL | `libegl1`, `libgl1` | Same dma-buf import path; falls back to CPU upload where Mesa's single-plane tiling import is limited. Picture remains correct. |

A `vaSyncSurface` fence is issued before a hardware surface is exported/consumed.
On mixed-GPU machines you can pin the EGL device with `LINGFAN_EGL_DEVICE_DRM`
(e.g. `renderD129`). Cross-vendor tiling import is not supported; the decoder and
renderer GPU are aligned to the same vendor automatically.

## Verification probes

The repository ships headless probes under `src/Tools/` that exercise the real
pipeline and print diagnostics to stdout. Run them on the target machine:

| Probe | What it checks |
|---|---|
| `LinuxHeadlessProbe` | Clock / sync progression (`[SYNC]` diagnostics). |
| `LinuxHeadlessVulkanProbe --hw` | Vulkan hardware decode + GPU texture frames (`[VULKAN-INIT]`). |
| `LinuxHeadlessOpenGlProbe` | OpenGL zero-copy / fallback path (`[OPENGL-ZEROCOPY]`). |

```bash
dotnet run --project src/Tools/LinuxHeadlessVulkanProbe -- --hw
```

## Known limitations

- LibVLC on Linux is implemented but validation is pending.
- Network-source playback on Linux is not yet fully validated.
- Cross-vendor GPU import (decode on one vendor, render on another) is not
  supported.
- Headless virtual displays (e.g. Xvfb) have limited GPU support.
- The Windows Media Foundation / D3D11 path does not apply to Linux.
