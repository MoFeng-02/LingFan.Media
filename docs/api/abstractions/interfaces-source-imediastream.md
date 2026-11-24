---
title: "IMediaStream"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStream.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaStream", "Read", "ReadAsync", "ConnectAsync", "Length", "Position", "CanSeek", "Location", "Seek", "SuggestedOpenHeaders", "Close"]
lang: "en-US"
---
# IMediaStream

Source: [`IMediaStream.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStream.cs)

Media data stream interface.

## Declaration

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaStream
```

## Remarks

Both Read and ReadAsync use the caller-allocated buffer mode. Null buffer is not accepted.
Returns 0 when buffer size is 0.

Prioritize using `ReadAsync` (supports CancellationToken, does not block threads).
Synchronization `Read` is only used for synchronizing boundaries (such as FormatDetector detection, FFmpeg AVIO callback).

Network streams must call `ConnectAsync` before `Read` to establish a low-level connection—
Synchronous boundaries (C function pointers/synchronous probes) cannot be awaited, so connections must be pre-installed to asynchronous paths.

## Members

### Read

```csharp
int Read(Span<byte> buffer);
```

Synchronize data read to the specified buffer.

Synchronization boundaries (C function pointer / synchronous probe), cannot await.
Network streams must be called after `ConnectAsync`; otherwise, `InvalidOperationException` is thrown.
After connection, synchronous reads each frame will block the calling thread until data arrives (FFmpeg worker thread, not UI). Use only in synchronization contexts.

**Parameters**

- `buffer`: Target buffer (Span slice). Returns 0 when length is 0.

**Returns**: The actual number of bytes read, 0 indicates the end of the stream.

### ReadAsync

```csharp
ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
```

Asynchronously read data to the specified buffer.

**Connection Semantics**:`ReadAsync` Allows **implicit connection** (only true when the network stream is first read).
and synchronization `Read` must operate in an established connection state—enabling pre-establishment during the Open phase,
You can also postpone the establishment of the chain to the first `ReadAsync`.

If connection fails (DNS/connection/handshake), `IOException` must be thrown, and it cannot return 0 to end the impersonation flow.

**Parameters**

- `buffer`: Target buffer. Empty buffers are not accepted.
- `ct`: Cancel tokens (network streams must be supported, local files can be ignored).

**Returns**: The actual number of bytes read, 0 indicates the end of the stream.

### ConnectAsync

```csharp
Task ConnectAsync(CancellationToken ct = default);
```

Asynchronous pre-establishment of underlying connections (network flow establishes HTTP connections; File/Transparent Stream is no operation).

**Must be called before synchronizing `Read`**: FFmpeg AVIO native callback synchronizes probes with FormatDetector
All are synchronous boundaries, and you cannot await connections. If the network flow is not pre-established, `Read` will be dropped
`InvalidOperationException`。

**Parameters**

- `ct`: Cancel token.

**Returns**: Task that has been connected to the completed establishment.

### Length

```csharp
long Length { get; }
```

Stream length (-1 if not available).

### Position

```csharp
long Position { get; }
```

Current read position.

### CanSeek

```csharp
bool CanSeek { get; }
```

Whether it is positionable.

### Location

```csharp
string? Location { get; }
```

Stream's locating address (file path or network URL); Cannot open by address when null.

For backends that need to be opened by address (such as MediaFoundation's `MFCreateSourceReaderFromURL`).
File streams return file paths, network streams return URLs; Memory/pass-through stream returns null without address.

Neutral contract members (BCL strings) do not reference any specific backend/source types, following dependency inversion.

### Seek

```csharp
long Seek(long offset, SeekOrigin origin);
```

Locate to the specified offset.

Unlocating streams (such as unseekable live streams) must be cast `NotSupportedException`,
Cannot return 0 or silently ignore.

When the target position exceeds the boundary, `IOException` is thrown (consistent with the error on the read side).

After `Close`, call throw `ObjectDisposedException`.

**Parameters**

- `offset`: Byte offset relative to &lt;paramref name="origin"/&gt;.
- `origin`: Offset reference (stream head/current position/stream tail).

**Returns**: The new read position after positioning (absolute byte offset).

### SuggestedOpenHeaders

```csharp
IReadOnlyDictionary<string, string>? SuggestedOpenHeaders { get; }
```

When opening with the `Location` address, the included HTTP request header is recommended (default is User-Agent,
User-defined header merged with cookies); Non-network stream is null.

For the unencapsulated backend that opens directly by address (where the URL is handed to the backend's own network stack), it is transmitted when opening the address,
Avoid silently discarding custom headers/cookies on addressable open paths (required for anti-hotlink sources).

Neutral contract member (BCL dictionary type, zero external references); The implementation must explicitly provide this property (not dependent on the default implementation of the interface).

### Close

```csharp
void Close();
```

Close the stream.

**Idempotence**: Repeated calls must not throw exceptions (closing it again is an empty operation).

After closing `ReadAsync` and `Seek`, throw `ObjectDisposedException`.

Closing must release the underlying connection and native resources without waiting for incomplete reads to return.
