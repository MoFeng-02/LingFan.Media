---
title: API Reference
sources: ["docs/scripts/gen-api.mjs", "docs/api/manifest.json", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs"]
symbols: ["IMediaPlayer", "IMediaDemuxer", "IVideoRenderer", "IAudioOutput", "IFrameChannel"]
lang: en
---

# API Reference

The public API of LingFan.Media: the contract layer (interfaces, models, enums,
events, exceptions) and the DI composition root, with exact signatures and usage
notes.

## Sections

| Section | Contents |
|---|---|
| [Abstractions](/api/abstractions/) | The contract layer: interfaces, models, enums, events, exceptions. |
| [Extensions](/api/extensions/) | The DI composition root: `AddLingFanMedia()` and module registration extensions. |

Within each section, pages are grouped by subdomain (Interfaces, Models, Enums,
Events, ...).

## What each page contains

- The type declaration (namespace + signature).
- Summary and remarks: preconditions, idempotency, failure semantics, cancellation
  behavior, frame-borrowing rules.
- Every public member with its signature and documentation.
- A link to the source file on GitHub.

Available in English and Chinese.
