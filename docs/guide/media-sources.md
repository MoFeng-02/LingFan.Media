# Media Sources

A **media source** describes **what** to play — a local file, a remote URL, or an in-memory stream. It is fully decoupled from **how** it is read: the `MediaStreamFactory` turns any `IMediaSource` into an `IMediaStream`, and everything downstream (demuxer, decoders, synchronizer) is source-agnostic.

## Source types

| Source | Class | Stream produced | Status |
| --- | --- | --- | --- |
| **File** | `FileMediaSource` | `FileMediaStream` | ✅ Implemented — primary, most battle-tested |
| **Network** (HTTP / HTTPS) | `NetworkMediaSource` | `NetworkMediaStream` | ✅ Validated end-to-end — async connect, SSRF guard, range seeking, custom headers / cookies / timeout |
| **In-memory stream** | `StreamMediaSource` | `PassThroughMediaStream` | ✅ Implemented — wraps any `System.IO.Stream`; exercised through the same path as the file source |

File is the reference path. Network sources are validated end-to-end as well: playback, range seeking and mid-playback seeking were exercised against an HTTP source supporting `Accept-Ranges`, each run reaching its natural end with zero dropped frames. The in-memory stream path is exercised wherever the file source is wrapped as a stream.

All three are wired end-to-end: `MediaPlayer.OpenAsync(IMediaSource)` calls `streamFactory.CreateAsync(source)`, which returns an `IMediaStream` that every backend demuxer consumes uniformly. There is **no per-source branching** anywhere in the pipeline.

## How a source becomes a stream

```mermaid
flowchart TD
    A["IMediaSource<br/>FileMediaSource · NetworkMediaSource · StreamMediaSource"] -->|"MediaStreamFactory.CreateAsync (pattern-match on Type)"| B["IMediaStream<br/>FileMediaStream · NetworkMediaStream · PassThroughMediaStream"]
    B -->|"consumed uniformly by every backend demuxer"| C["Demuxer → Decoders → Synchronizer"]
    style A stroke:#3b82f6,stroke-width:2px
```

## Implementation notes

- **`NetworkMediaSource`** includes built-in **SSRF protection**: it rejects `file://` and, by default, private / internal IP ranges. DNS resolution happens *before* the guard, with the resolved IP passed through to avoid a re-resolution window.
- **`StreamMediaSource`** hands an external `Stream` to the pipeline; thread-safety of that stream is the caller's responsibility.
- **`IsLive`** is derived from `source.Type == MediaSourceType.Network` at session creation.
- For non-seekable sources (live network streams, arbitrary `Stream`s), seeking support depends on what the underlying stream provides; `FileMediaSource` is the reference path for full seek / trick-play behavior.
