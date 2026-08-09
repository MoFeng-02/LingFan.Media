# Events & Exceptions

## Event argument types

All five live in `LingFan.Media.Abstractions`.

| Type | Carries | Used by |
|------|---------|---------|
| `MediaStateChangedEventArgs` | Old / new `MediaState` | `IMediaPlayer.StateChanged` |
| `MediaErrorEventArgs` | `MediaErrorCode` + message | `IMediaPlayer.ErrorOccurred` |
| `BufferProgressEventArgs` | Buffered duration / bytes / state | `IBufferManager.BufferProgressChanged` |
| `TrackChangedEventArgs` | Old / new `MediaTrack?` | Track selection changes |
| `LogEventArgs` | Level + message | Logging |

## Exceptions

| Type | Meaning |
|------|---------|
| `MediaBackendUnsupportedException` | Thrown by the fallback middleware when **every** registered backend fails to open the source. |
| `GpuDeviceLostException` | Raised by renderers/outputs on `DXGI_DEVICE_REMOVED` / `VK_ERROR_DEVICE_LOST`. The session should rebuild via `OpenAsync` + `Attach`; intra-session recovery is transparent in V3. |
