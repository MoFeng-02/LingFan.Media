# Licensing & Third-Party Compliance

LingFan.Media's own source code is released under **Apache-2.0**. It optionally relies on two native backends — **FFmpeg** and **LibVLC (VLC)** — both distributed under **LGPL 2.1 or later**. This page explains how the project reaches LGPL compliance through *dynamic linking only*, and why its own license is never "infected" by the LGPL backends.

## Core principle: dynamic linking only

LingFan.Media never statically merges FFmpeg or LibVLC into its own assemblies, and never modifies their source. It loads them at runtime as shared libraries:

- **FFmpeg** is consumed through `FFmpeg.AutoGen.Bindings.DynamicallyLoaded` (MIT), which resolves the shared libraries (`avcodec-*.dll`, etc.) at runtime via `LoadLibrary`.
- **LibVLC** is consumed through `LibVLCSharp` (LGPLv2.1+), which dynamically loads `libvlc` / `libvlccore` at runtime.

Because the project only links dynamically, copies the libraries unmodified, and does not combine them into its own binary, **LingFan.Media's own code is not a derivative work of the LGPL libraries** (LGPL 2.1, section 6). Its source and assemblies remain **Apache-2.0** — pure and unencumbered. The LGPL obligations attach only to the distributed native binaries themselves.

## Only LGPL builds are used (no GPL contamination)

- **FFmpeg** is used as an **LGPL shared build** — the BtbN `lgpl-shared` variant, with neither `--enable-gpl` nor `--enable-nonfree`. This excludes every GPL-only codec. Builds such as gyan.dev `full_build-shared` / `essentials_build` (which enable GPL / static linking) must **not** be used.
- **LibVLC** loads only the LGPL-licensed engine and playback modules (protocol, demux, decode, filter, output). The few remaining GPLv2 modules (streaming / transcode, UI, and DVD's `libdvdcss`) are never loaded for local playback, so their GPL terms never reach the project.

## Compliance checklist

| Requirement | How LingFan.Media satisfies it |
| --- | --- |
| Dynamic linking (no static merge) | `FFmpeg.AutoGen.DynamicallyLoaded` loads shared DLLs at runtime |
| No GPL code path | LGPL shared builds only; wrappers call only public LGPL APIs |
| User may replace / upgrade the native libs | `FFmpegOptions.FFmpegLibraryPath` is configurable; DLLs are not embedded |
| License & attribution provided | This file (`THIRD_PARTY_NOTICES.md`) ships with the product |
| FFmpeg source not modified | Called only, never vendored or patched |
| LibVLC replaceable | Native shared libs live in an independent `libvlc/` directory, provided per-RID by NuGet, not committed |
| Project license not "infected" | Dynamic linking + unmodified sources ⇒ own code stays Apache-2.0 |

## Copyright & patent risk — evaluated by you

LGPL (and the codecs it covers) carry **patent and copyright considerations that vary by jurisdiction, media format, and use case**. LingFan.Media provides the technical compliance path described above; it makes **no representation about patent licensing**.

**You are responsible for assessing** whether your specific use — particular codecs (e.g., H.264 / H.265 / AV1), distribution region, or commercial deployment — requires separate patent licenses (such as via MPEG-LA, HEVC Advance, or similar pools). Consult legal counsel for production deployments.

## Authoritative notice

The version-stamped text — exact binary sources, per-module licensing, and the mobile (iOS / Android) relinking note — lives in [`THIRD_PARTY_NOTICES.md`](https://github.com/MoFeng-02/LingFan.Media/blob/main/THIRD_PARTY_NOTICES.md) at the repository root.
