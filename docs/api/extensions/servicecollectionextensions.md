---
title: "ServiceCollectionExtensions"
type: "class"
namespace: "LingFan.Media.Extensions"
source: "src/Infrastructure/LingFan.Media.Extensions/ServiceCollectionExtensions.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["ServiceCollectionExtensions", "AddLingFanMedia"]
lang: "en-US"
---
# ServiceCollectionExtensions

Source: [`ServiceCollectionExtensions.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Extensions/ServiceCollectionExtensions.cs)

DI registration main portal. Provides `AddLingFanMedia(IServiceCollection, Action{MediaOptions})` extension method to register core media services.

## Declaration

```csharp
namespace LingFan.Media.Extensions;

public static class ServiceCollectionExtensions
```

## Remarks

Usage Mode:

```csharp
services. AddLingFanMedia(). AddFFmpeg(). AddD3D11Renderer(). AddWasapiOutput();
```

**Lifecycle Model**:

Infrastructure (Singleton): stateless factory / shared resource.

- `IMediaStreamFactory` → `MediaStreamFactory` (Hold `IHttpClientFactory`)

- `IMediaDemuxerFactory` → Registered by each backend `AddXxx()` via the TryAddEnumerable collection (no longer default registration, supports multiple backends concurrent and rollback)

- `IMediaPlayerFactory` → `BackendFallbackMediaPlayerFactory` (runtime rollback scheduling;
The same instance is simultaneously exposed with `IBackendRegistry`. Core assembly factory `MediaPlayerFactory` registered as keyed `"composer"` for the backend scheduler to select a backend group and then create a session)

Session (Transient): Only register `IMediaPlayer`; internal components are manually new by the Factory without going through DI.

**The following factories are registered by submodule extension methods (not in AddLingFanMedia)**:

- `AddFFmpeg()` / `AddMediaFoundation()` / `AddVLCNative()` → Each registered as a TryAddEnumerable collection
`IMediaDemuxerFactory` / `IVideoDecoderFactory` / `IAudioDecoderFactory` / `ISubtitleDecoderFactory` (Multiple backends can coexist, rollback in registration order)

- `AddD3D11Renderer()` → `IVideoRendererFactory`

- `AddWasapiOutput()` → `IAudioOutputFactory`

This method is a synchronous configuration with no I/O or asynchronous.

## Members

### AddLingFanMedia

```csharp
public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    Action<MediaOptions>? configure = null);
```

Register the LingFan.Media core service (factory + player) and return `MediaBuilder` for chain registration.

Register for Infrastructure Lifetime (Singleton) services:

- `IMediaStreamFactory` → `MediaStreamFactory`

- `IFormatDetector` → `FormatDetector` (Lightweight probe format before rollback scheduler open)

- `ICodecRegistry` → `CodecRegistry` (static mapping table, pure memory)

- `IMediaPlayerFactory` → `BackendFallbackMediaPlayerFactory` (the same instance also uses `IBackendRegistry` externally);
Core assembly factory `MediaPlayerFactory` registered with keyed `"composer"`

- `IMediaDemuxerFactory` **Not registered here**: Registered by each backend `AddXxx()` via the TryAddEnumerable collection;
Open throw `MediaBackendUnsupportedException` when no backend is registered

Register for Session Lifetime (Transient) Player:

- `IMediaPlayer` (Create each parsing via Factory.Create())

Register `IHttpClientFactory`(`AddHttpClient()`) for `MediaStreamFactory` network flow connection pool management,
And configure the named client `"LingFanMedia"` and `"LingFanMedia_Insecure"` (both equipped with SSRF connection protection).

Register `IOptions<MediaOptions>` and `IOptions<MediaPlayerOptions>` (the latter propagates the host's default volume).

Log configuration is handled by the consumer-side app's Host (the Extensions layer only relies on `Logging.Abstractions`).

**Parameters**

- `services`: DI service collection.
- `configure`: Global media configuration callback (optional).

**Returns**: Media builder for chained calls `AddFFmpeg()` / `AddD3D11Renderer()`, etc.

### AddLingFanMedia

```csharp
public static MediaBuilder AddLingFanMedia(
    this IServiceCollection services,
    MediaOptions options);
```

Register the LingFan.Media core service and return `MediaBuilder` for chain registration.

**Parameters**

- `services`: DI service collection.
- `options`: Global media configuration.

**Returns**: Media builder for chained calls `AddFFmpeg()` / `AddD3D11Renderer()`, etc.
