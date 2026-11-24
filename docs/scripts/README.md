# Docs maintenance guide (maintainers and AI assistants)

This file is **not part of the reader-facing site** (it is excluded from the
VitePress build via `srcExclude`). It documents how the documentation is produced
and kept unified with the source. The public site under `docs/guide/` and
`docs/api/` is reader-facing and carries only usage, explanation and examples.

## Core principle: one source of truth

The single source of truth for the public API is the **source code itself**: its
`///` XML doc comments and its public signatures. The API reference is *derived*
from that source, never hand-copied. Hand-copying signatures and remarks into
prose is what causes drift — the copy goes stale while the code moves on.

The documentation has two layers with different rules:

| Layer | Location | How it is produced | Rule |
|---|---|---|---|
| API reference | `docs/api/`, `docs/zh/api/` | Generated from source by `scripts/gen-api.mjs`. | Never hand-edit. Regenerate after changing source; committed and gated by CI. |
| Guide | `docs/guide/`, `docs/zh/guide/` | Hand-written, single-topic, desensitized. | Carries provenance frontmatter validated against source. |

## The generated API reference

`scripts/gen-api.mjs` parses the contract layer (`LingFan.Media.Abstractions`) and
the composition root (`LingFan.Media.Extensions`):

- It extracts only `///` doc comments and public signatures. Ordinary `//` body
  comments are deliberately not extracted, so internal implementation notes never
  leak into the public reference.
- One source file becomes one reference page (roughly one type per page), mirroring
  the source folder structure.
- English and Chinese pages are rendered from the same parse in one pass: the
  signatures and source-derived remarks are identical, only the section labels
  differ. This makes English/Chinese drift impossible.
- It also emits two machine-readable artifacts:
  - `docs/api/manifest.json` — the AI index: every public type with its kind,
    namespace, source path, page paths, member names and a source hash.
  - `docs/.vitepress/api-sidebar.json` — the navigation tree, imported by
    `config.ts`. Navigation is generated, so it cannot list a type that no longer
    exists or miss one that was added.

Each generated page carries `generated: true` frontmatter and a link back to its
source file. Do not edit generated pages — edit the source and regenerate.

## The hand-written guide

Guide pages explain concepts that cannot be derived from a signature: the
architecture, the async/sync discipline, platform support, deployment, licensing.
They follow two rules:

1. Atomic, single-topic pages. One subject per file, with a landing page for
   navigation. No monolithic document.
2. Provenance frontmatter. Each guide page declares the source files and symbols
   it describes:

   ```yaml
   ---
   title: Architecture
   sources: ["src/Infrastructure/LingFan.Media.Core/MediaPlayer/MediaPlayer.cs"]
   symbols: ["IMediaPlayer", "IFrameChannel", "IFrameSink"]
   lang: en
   ---
   ```

   The checker verifies that every `sources` path exists and every `symbols`
   identifier exists in the codebase. A guide page that cites a deleted file or an
   invented symbol fails the gate.

Facts that have a single home stay in one place: the platform/backend support
matrix lives only in `guide/platforms.md`; other pages link to it instead of
restating it. Enum values and type signatures live only in the generated reference.

## The drift gate

`scripts/check-docs.mjs` runs five checks and exits non-zero on any failure, so it
can be used as a pre-commit or CI gate:

| Check | What it catches |
|---|---|
| Freshness | A source file changed since the reference was generated (re-hash mismatch). |
| Coverage | A public type has no generated page, a generated page is missing, or the manifest references a removed source. |
| Provenance | A guide page cites a non-existent source path or symbol. |
| Symbol drift | An interface-like token in prose (a backticked `I...` name) does not resolve to a real source identifier. |
| Desensitization | Absolute paths, internal codenames, crash hex codes, emoji, credentials, private IPs or internal narrative reached the docs. |

## Maintenance workflow

```bash
pnpm docs:gen      # regenerate the API reference + manifest + sidebar from source
pnpm docs:check    # run the drift gate (freshness / coverage / provenance / symbols / desensitize)
pnpm docs:build    # build the static site
pnpm docs:dev      # local dev server
pnpm docs:ci       # the full CI pipeline locally: check -> gen -> build
```

After changing any contract source, run `docs:gen` then `docs:check`. After writing
or editing a guide page, run `docs:check`. The build is the final verification that
links and diagrams resolve.

## Translation memory (non-source locales)

The Chinese prose in C# `///` comments is the single source of truth. Non-source
locales (currently `en`) render from a per-segment translation memory:

```bash
pnpm docs:translate            # fill docs/i18n/tm.en.json (free Microsoft/Bing edge endpoint)
pnpm docs:translate -- --dry-run   # list untranslated/stale segments without network
pnpm docs:translate -- --locale ja # additional locales use the same pipeline
```

- Each prose segment is keyed by `sha1(source)`. Editing a comment invalidates
  only that segment; everything else keeps its cached translation.
- `docs:gen` reads the memory **offline** (zero network in CI). Missing/stale
  segments fall back to the source text; `docs:check` reports the gap as a
  warning (never blocks the build).
- Inline code spans and contract phrases are masked before machine translation;
  math/arrow symbols (`→ × ≈ ≠ ≥ ≤ ± ∈ …`) must survive verbatim (conservation
  check per segment). A domain glossary corrects known term drift.
- Human-proofread entries can be marked `"reviewed": true` in the TM file;
  the machine engine never overwrites them while the source hash is unchanged.
- The free endpoint is an internal Microsoft interface and may change or
  disappear; the TM keeps the site working regardless, and the engine is a
  pluggable adapter in `scripts/translate.mjs`.

## Continuous integration (GitHub Actions)

The generated reference is committed to the repository, so the AI index
`api/manifest.json` and every reference page are readable without running anything.
CI keeps that committed copy honest. Two workflows live under `.github/workflows/`:

| Workflow | Trigger | What it does |
|---|---|---|
| `docs-ci.yml` (Docs CI) | Pull request touching `docs/**` or the contract source | Install, then check -> gen -> build. No deployment. |
| `deploy-docs.yml` (Deploy Docs) | Push to `main` touching `docs/**` or the contract source | Same check -> gen -> build, then publish to GitHub Pages. |

Both also trigger on the contract source directories
(`src/Infrastructure/LingFan.Media.Abstractions/`,
`src/Infrastructure/LingFan.Media.Extensions/`), so a code change that moves the
public surface re-validates and re-deploys the docs even when no doc file changed.

The order matters: the drift gate runs against the committed state before
regeneration. If a source file changed but the generated reference was not
regenerated and committed, the freshness check fails and the pipeline goes red —
the stale copy is caught, not silently overwritten. Regeneration then happens only
to build the site. `pnpm docs:ci` reproduces this exact pipeline locally.

## Guide for AI assistants

- Find a type fast: read `docs/api/manifest.json`. It maps every public type to its
  source path and reference page. Do not grep prose for signatures.
- Never hand-edit generated pages (`generated: true`). Change the source `///`
  comments or signature, then regenerate.
- Adding a guide page: create one atomic, single-topic file under `guide/` (and its
  `zh/guide/` mirror), add provenance frontmatter with real `sources` and `symbols`,
  and register it in the guide outline in `config.ts` for both locales. Keep English
  and Chinese symmetric.
- Keep facts single-homed: support status goes in `platforms.md`; signatures and
  enum values come from the generated reference. Link, do not duplicate.
- Desensitize: the public docs are reader-facing. No internal codenames, decision
  narratives, debugging stories, temporary measured numbers, absolute paths,
  credentials or emoji. Use placeholders (`example.com`, `<token>`) in examples.
  Internal root-cause and decision records belong in the memory store, not in the
  public docs.
- Verify before finishing: run `docs:check` (and `docs:build` for link/diagram
  integrity). A green gate is the definition of "unified with source".

## File map

| Path | Role |
|---|---|
| `scripts/lib/csharp-doc.mjs` | Source parser: extracts `///` docs + public surface (Node built-ins only, zero dependencies). |
| `scripts/gen-api.mjs` | Generates reference pages, `manifest.json`, `api-sidebar.json`. |
| `scripts/check-docs.mjs` | The five-check drift gate. |
| `api/manifest.json` | AI index of the public surface (generated, committed). |
| `.vitepress/api-sidebar.json` | Generated navigation tree (imported by `config.ts`). |
| `.vitepress/config.ts` | Site config; guide outline is hand-maintained, API sidebar is generated. |
| `guide/`, `zh/guide/` | Hand-written reader-facing concept pages with provenance. |
| `api/`, `zh/api/` | Generated reference pages (committed, gated by CI). |
| `.github/workflows/docs-ci.yml` | Pull-request validation: check -> gen -> build. |
| `.github/workflows/deploy-docs.yml` | Main-branch build + GitHub Pages deployment. |
