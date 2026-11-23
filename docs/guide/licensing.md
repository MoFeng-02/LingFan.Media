---
title: Licensing
sources: ["README.zh.md", "THIRD_PARTY_NOTICES.md", "src/Backends/LingFan.Media.Backends.FFmpeg/FFmpegOptions.cs", "src/Backends/LingFan.Media.Backends.VLCNative/LingFan.Media.Backends.VLCNative.csproj"]
symbols: ["FFmpegOptions"]
lang: en
---

# Licensing

LingFan.Media's own code is licensed under **Apache-2.0**. The optional native
backends (FFmpeg, LibVLC) are **LGPL 2.1+** and are used through dynamic linking
only, so a product built on LingFan.Media is not a derivative work of those
libraries under LGPL 2.1 §6.

## How the native libraries are used

| Library | License | Loading |
|---|---|---|
| FFmpeg | LGPL 2.1+ | Hand-written `[LibraryImport]` bindings + `NativeLibrary` version-adaptive loading (`avcodec-*.dll` / `.so` / `.dylib`). Dynamically linked, never static. |
| LibVLC | LGPL 2.1+ | The VLCNative backend dynamically loads `libvlc` / `libvlccore`. |

Because the libraries are dynamically linked and replaceable by the user, the
LGPL terms are satisfied without imposing LGPL on your own application code.

## Only LGPL builds — no GPL

- **FFmpeg:** use an LGPL shared build (e.g. the BtbN `lgpl-shared` artifacts),
  configured without `--enable-gpl` / `--enable-nonfree`. Do **not** use GPL
  builds such as gyan.dev `full_build-shared` / `essentials_build`.
- **LibVLC:** only the LGPL engine playback modules are loaded. GPL components
  (streaming / transcode / UI modules, `libdvdcss`) are not loaded.

## Compliance checklist

| Requirement | How it is met |
|---|---|
| Dynamic linking | FFmpeg and LibVLC are loaded at runtime, not statically linked. |
| No GPL code path | Only LGPL builds / modules are used. |
| User can replace the library | `FFmpegOptions.FFmpegLibraryPath` (and the loader search order) lets the user point at their own build. |
| Notices ship with the product | `THIRD_PARTY_NOTICES.md` is included with distributions. |
| FFmpeg source unmodified | The shared build is used as-is; no FFmpeg source changes. |
| LibVLC kept separate | `libvlc` / `libvlccore` remain independent dynamic libraries. |
| Per-RID natives not committed | Native runtime directories are not committed to the repository; they are fetched per runtime identifier. |
| Own code Apache-2.0 | All LingFan.Media source is Apache-2.0. |

## Patent risk

Codec patents (H.264, H.265 / HEVC, AV1, etc.) are a separate matter from
copyright licensing and are the user's responsibility to assess for their
distribution (e.g. MPEG-LA, HEVC Advance pools). LingFan.Media does not grant any
patent license.

## Authoritative notices

The authoritative third-party notice file is
[`THIRD_PARTY_NOTICES.md`](https://github.com/MoFeng-02/LingFan.Media/blob/main/THIRD_PARTY_NOTICES.md)
in the repository root. Ship it with any product that bundles the native
backends.
