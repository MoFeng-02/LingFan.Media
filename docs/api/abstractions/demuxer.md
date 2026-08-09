# Source & Demux

## IFormatDetector

Namespace: `LingFan.Media.Abstractions`

The dependency-inversion point for format detection. Implemented in `LingFan.Media.Formats`; consumed by the `Playback` middleware, which depends only on this contract (never on the concrete type).

```csharp
public interface IFormatDetector
{
    MediaFormatProfile DetectProfile(IMediaStream stream);
    Task<MediaFormatProfile> DetectProfileAsync(IMediaStream stream, CancellationToken ct = default);
}
```

Reads only the stream header (magic bytes / codec tag) — no full session. Returns a `MediaFormatProfile` (container, primary video codec). For non-seekable streams, returns `Unknown` / `Unknown`. Used by the fallback scheduler to hit **format-level memory** and skip known-bad backends.

## IMediaDemuxer

Namespace: `LingFan.Media.Abstractions`

Splits a container (MP4 / MKV / …) into per-track packets.

```csharp
public interface IMediaDemuxer : IMediaComponent
{
    Task OpenAsync(IMediaStream stream, CancellationToken ct = default);
    IReadOnlyList<MediaTrack> Tracks { get; }
    MediaMetadata Metadata { get; }
    ValueTask<MediaPacket?> ReadPacketAsync(CancellationToken ct = default);
    Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);
    void Close();
}
```

| Member | Notes |
|--------|-------|
| `OpenAsync` | Probe format and parse tracks. |
| `Tracks` / `Metadata` | Parsed tracks / container metadata (safe to read cross-thread after open). |
| `ReadPacketAsync` | Next packet; `null` = end of stream. `ValueTask` hot path. |
| `SeekAsync` | Seek; returns success. |
| `Close` | Close the demuxer. |

> `ReadPacketAsync` / `SeekAsync` must not be called concurrently.

## IMediaDemuxerFactory

Namespace: `LingFan.Media.Abstractions`

```csharp
IMediaDemuxer Create(IMediaStream stream);
Task<IMediaDemuxer> CreateAsync(IMediaStream stream, CancellationToken ct = default);
```

## IMediaSource

Namespace: `LingFan.Media.Abstractions`

Describes *what* to play.

| Member | Type | Notes |
|--------|------|-------|
| `Type` | `MediaSourceType` | `File` / `Network` / `Stream`. |
| `Identifier` | `string` | Source identifier (path / URL). |
| `ConnectAsync(ct)` | `Task` | Open the underlying stream. |
| `Read` / `ReadAsync` | methods | Read bytes. |
| `Length` / `Position` | `long` | Stream length / position. |
| `CanSeek` / `Seek` | bool / method | Seekability. |
| `Close()` | `void` | Close. |

`FileMediaSource` / `NetworkMediaSource` / `StreamMediaSource` implement it.

## IMediaStream & IMediaStreamFactory

| Type | Role |
|------|------|
| `IMediaStream` | A readable byte stream: `Read` / `ReadAsync` / `ConnectAsync` / `Length` / `Position` / `CanSeek` / `Seek` / `Close`. |
| `IMediaStreamFactory` | Creates `IMediaStream` from an `IMediaSource` (Singleton). |
