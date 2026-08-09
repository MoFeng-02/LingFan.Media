# Formats (`LingFan.Media.Formats`)

Container detection and demuxer routing. The `FormatDetector` is the only type the upper layers depend on (via the `IFormatDetector` contract); the rest are implementation detail or internal helpers.

## DemuxerFactory

Namespace: `LingFan.Media.Formats`

`sealed class : IMediaDemuxerFactory`. Singleton, stateless. Each `Create` returns a **new** demuxer instance (built per playback).

```csharp
public DemuxerFactory(
    ILogger<DemuxerFactory> logger,
    Func<IMediaStream, IMediaDemuxer>? fallbackFactory = null)
```

| Method | Returns | Notes |
|--------|---------|-------|
| `Create(IMediaStream stream)` | `IMediaDemuxer` | Probes the container via `FormatDetector.Detect` (non-fatal failures degrade to `Unknown`), then delegates to `fallbackFactory`. Throws `InvalidOperationException` if no backend `fallbackFactory` is registered. |
| `CreateAsync(IMediaStream stream, CancellationToken ct = default)` | `Task<IMediaDemuxer>` | Async variant — probes via `FormatDetector.DetectAsync`. |

> If a backend (e.g. `AddFFmpeg()`) overrides `IMediaDemuxerFactory` registration, this class is not used. It is the V1 default router.

## FormatDetector

Namespace: `LingFan.Media.Formats.Detection`

`public class : IFormatDetector`. Magic-number container identification. Uses `ArrayPool<byte>` (zero heap allocation); probes a fixed `4096`-byte window and `Seek`s back to start so the demuxer re-reads from the beginning. Non-seekable streams are skipped (returns `Unknown`, letting the backend self-detect).

| Member | Returns | Notes |
|--------|---------|-------|
| `Detect(IMediaStream stream)` (static) | `ContainerFormat` | Synchronous probe (used inside `DemuxerFactory.Create`). |
| `DetectAsync(IMediaStream stream, CancellationToken ct = default)` (static) | `Task<ContainerFormat>` | Async probe. |
| `DetectProfile(IMediaStream stream)` | `MediaFormatProfile` | `(container, videoCodec)` — the lightweight profile used by the fallback middleware's **format-level memory**. |
| `DetectProfileAsync(IMediaStream stream, CancellationToken ct = default)` | `Task<MediaFormatProfile>` | Async profile probe. |

Detection covers: **MP4** (`ftyp`@4), **EBML/Matroska/WebM** (EBML magic@0 → DocType `webm`/`matroska`), **AVI** (`RIFF`+`AVI `@0/8), **MPEG-TS** (sync byte `0x47` every 188 bytes, scanning multiple offsets, ≥3 consecutive hits to avoid false positives), **FLV** (`FLV`@0).

`ProbeVideoCodec` scans the probe buffer for known codec strings per container (`hvc1`/`avc1`/`av01`/`vp09`/`mp4v` for MP4; `V_MPEGH/ISO/HEVC`/`V_MPEG4/ISO/AVC`/`V_AV1`/`V_VP9`/`V_MPEG2V` for MKV/WebM; FLV tag `codecId` 7=`H264`, 12=`H265`). Unknown → `VideoCodec.Unknown` (harmless; the middleware falls back to full scan).

## FormatSignature (internal)

Namespace: `LingFan.Media.Formats.Detection` — `internal static class`.

The magic-number table used by `FormatDetector`. Exposed as `static readonly byte[]` + `ReadOnlySpan<byte>` properties (zero-allocation comparison, AOT-friendly). Documented read-only; do not reference directly.

| Format | Signature | Offset |
|--------|-----------|--------|
| MP4 | `"ftyp"` | 4 |
| MKV / WebM | `0x1A 0x45 0xDF 0xA3` (EBML) | 0 |
| AVI | `"RIFF"` + `"AVI "` | 0 / 8 |
| TS | `0x47` (sync byte, every 188 B) | 0 |
| FLV | `"FLV"` | 0 |
| EBML DocType | `0x42 0x82`, values `"webm"` / `"matroska"` | — |

## MetadataExtractor

Namespace: `LingFan.Media.Formats.Metadata`

`public static class`. Stateless, AOT-friendly. Builds a `MediaMetadata` from a container format, track list, duration, and extra fields.

```csharp
public static MediaMetadata Extract(
    ContainerFormat format,
    IReadOnlyList<MediaTrack> tracks,
    TimeSpan duration,
    IReadOnlyDictionary<string, string>? extraFields = null)
```

- Extracts `title` / `artist` / `album` / `year` / `genre` with **case-insensitive** key lookup (tolerant of backend output style).
- `year` parsing takes the first 4 digits (tolerates `"2024-01-01"`).
- Returns a `MediaMetadata` with `Duration`, `ContainerFormat`, and the resolved fields + `ExtraFields`.
