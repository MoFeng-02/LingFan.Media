# Linux Deployment & Playback Guide (FFmpeg route)

> Compiled from real testing on **Ubuntu 24.04 LTS + Intel UHD 630 (dual-GPU machine)**; other distributions follow the same checklist. Scope: the FFmpeg backend (the VLC backend is implemented on Linux but pending validation); local-file playback is validated end-to-end, network sources and streaming are not yet verified.

## 1. Runtime prerequisites

| Dependency | Notes |
| --- | --- |
| **.NET 10 runtime** | Or a self-contained publish |
| **FFmpeg shared libraries** | An n8.x shared build is required (`libavutil` / `libavcodec` / `libavformat` / `libswscale` / `libswresample` / `libavfilter` / `libavdevice`, including version-alias files). The loader resolves in this order: **system libraries → application directory → recursive scan of the application tree (highest version wins)**; alternatively point `LF_FFMPEG_LIB` at the library directory explicitly. A version gate and structural mirror self-check run after loading and produce clear errors on failure |
| **OpenAL** (audio) | `sudo apt install libopenal1`; without it the probe falls back to a silent output (playback works, no sound) |
| Desktop audio service | Audible output requires PulseAudio / PipeWire to be running |

::: warning Cross-platform sync pitfall
When syncing the workspace between Windows ↔ Linux, FFmpeg's **version symbolic links degrade into text files** (e.g. `libavcodec.so.60` becomes a few dozen bytes of text). Extract the FFmpeg release tarball on the Linux side to get real ELF files, or deploy plain file copies.
:::

## 2. Software-decode playback (minimum viable)

With section 1 satisfied, **local files play out of the box**: FFmpeg software decode → renderer CPU upload → windowed presentation, audio through OpenAL. Display-less environments (servers / Xvfb) can still exercise the decode and audio chain (validated by the headless probe).

## 3. VAAPI hardware decode (optional, validated on Intel)

Hardware decode needs all three of the following; otherwise startup falls back to software decode (with a clear log):

1. **libva ≥ 2.21**. Ubuntu 24.04 (noble) ships 2.20, which lacks `vaMapBuffer2` (required by FFmpeg's VAAPI trampoline). Upgrade options: the repository ships 2.24.1 debs under `ThirdParty/va/` (`sudo dpkg -i ThirdParty/va/libva2_2.24.1-*.deb ThirdParty/va/libva-drm2_2.24.1-*.deb`), or use noble-updates 2.22+. The loader probes the system libva first (via `vaMapBuffer2`) and reuses it when adequate.
2. **VA-API driver**: `sudo apt install intel-media-va-driver-non-free` (iHD) for Intel.
3. **Device access**: the current user must be in the `render` / `video` groups (access to `/dev/dri/renderD128`). Verify with `vainfo`.

Implementation detail (for troubleshooting): FFmpeg's VAAPI trampoline resolves symbols via `dlopen("libva.so.2")`, and glibc's `dlopen` does **not** consume the ffmpeg libraries' own `DT_RUNPATH` — so the loader preloads libva by absolute path before loading ffmpeg (reusing the system library when it satisfies the version, otherwise preloading the bundled copy). No manual steps are required.

## 4. Windowed presentation

| Renderer | Prerequisites | Notes |
| --- | --- | --- |
| **Vulkan** (recommended; zero-copy validated) | `mesa-vulkan-drivers` (Intel ANV) | The decoded dma_buf is imported directly as a Vulkan image; **the renderer automatically selects a device from the same vendor as the decode GPU** (cross-vendor imports are not supported — tiled layouts carry vendor-private semantics) |
| **OpenGL** | `libegl1` / `libgl1` | Same dma_buf import route; some Mesa drivers have limited support for single-plane tiled combinations — the renderer falls back to CPU upload automatically and **the picture always stays correct** (no zero-copy acceleration) |

- Presentation requires an X11 session; headless displays such as Xvfb can exercise the full chain (no visible output — good for automated verification). To watch the picture, use a real display or a screenshot tool.
- Multi-GPU machines: renderer/decoder GPU vendor alignment is automatic; the OpenGL route accepts `LINGFAN_EGL_DEVICE_DRM=renderD129` to pin an EGL device explicitly (`1` = auto).
- Every export path issues a `vaSyncSurface` fence before export so external consumers always observe fully decoded frames.

## 5. Verification & diagnostics

Three Linux probes live under `src/Tools/` (`dotnet run --project ...`):

| Probe | Coverage | Key reads |
| --- | --- | --- |
| `LinuxHeadlessProbe` | Decode + audio (no presentation) | `[SYNC]` snapshots, the final "verdict" line |
| `LinuxHeadlessVulkanProbe` | Decode + Vulkan presentation (`--hw` enables VAAPI hardware decode + zero-copy) | the "GPU texture frames" counter, `[VULKAN-INIT]` device name |
| `LinuxHeadlessOpenGlProbe` | Decode + OpenGL presentation (same) | `[OPENGL-ZEROCOPY]` import result and display support surface |

Diagnostic logs contain layered traces (loader, decode, sync, presentation); on native-layer failures the last trace line is the location anchor.

## 6. Known limitations

- The **LibVLC backend** is implemented on Linux but pending validation; network sources and streaming playback are not yet runtime-verified.
- **Cross-vendor** dma_buf imports are not supported (tiled layouts carry vendor-private semantics) — the renderer aligns to the decode GPU automatically.
- **Xvfb** software stacks have limited capabilities (ES contexts, narrow import support) — fine for chain verification; watch the picture on a real display.
- Windows-side MediaFoundation / D3D11 capabilities do not apply to Linux (platform API difference, by design).
