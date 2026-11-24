---
title: "IMediaStreamFactory"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStreamFactory.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaStreamFactory", "Create", "CreateAsync"]
lang: "en-US"
---
# IMediaStreamFactory

Source: [`IMediaStreamFactory.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStreamFactory.cs)

Media stream factory interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaStreamFactory
```

## Remarks

Singleton Factory. Each time Create() returns a new instance.

Holds an IHttpClientFactory reference (for network stream connection pool management, not purely stateless).

Prioritize using `CreateAsync` (network flow scenarios do not block threads, supports CT).

## Members

### Create

```csharp
IMediaStream Create(IMediaSource source);
```

Create the corresponding IMediaStream instance based on the media source type.

**Parameters**

- `source`: Media source.

**Returns**: Newly created media stream instance (network streams must be read synchronously with `ConnectAsync` first).

### CreateAsync

```csharp
Task<IMediaStream> CreateAsync(IMediaSource source, CancellationToken ct = default);
```

Asynchronously create the corresponding IMediaStream instance based on the media source type.

**Parameters**

- `source`: Media source.
- `ct`: Cancel token.

**Returns**: Newly created media stream instance.
