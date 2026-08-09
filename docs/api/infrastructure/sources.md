# Sources & Streams (`LingFan.Media.Sources`)

Describes *what* to play and *how* to open it, plus the **SSRF guard** that protects network playback. Immutable, thread-safe source types implement both `IMediaSource` and `IMediaSourceMetadata`.

## MediaStreamFactory

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaStreamFactory`. Singleton; holds `IHttpClientFactory` for network streams. Uses a pattern-matching `switch` (AOT-friendly, no reflection).

```csharp
public MediaStreamFactory(IHttpClientFactory httpClientFactory)
```

| Method | Returns | Notes |
|--------|---------|-------|
| `Create(IMediaSource source)` | `IMediaStream` | Synchronous native boundary — connection is deferred to the first `Read`. |
| `CreateAsync(IMediaSource source, CancellationToken ct = default)` | `Task<IMediaStream>` | **Preferred for network sources.** Performs real DNS resolution + `SsrfGuard.Validate` *after* resolution (DNS-rebinding protection requires validation post-resolution), then passes the pre-validated IPs to the stream. Real async (I/O) — genuinely awaited, **not** pseudo-async. |

`CreateCore` maps `File` → `FileMediaStream`, `Network` → `NetworkMediaStream`, `Stream` → `PassThroughMediaStream`.

## FileMediaSource

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaSource, IMediaSourceMetadata` (immutable).

```csharp
public FileMediaSource(string path, string? name = null, string? contentType = null)
```

| Member | Type | Notes |
|--------|------|-------|
| `Path` | `string` | Normalized via `Path.GetFullPath`. Throws `ArgumentException` if empty/whitespace. |
| `Type` | `MediaSourceType` | `File`. |
| `Identifier` | `string` | `= Path`. |
| `Name` | `string?` | Defaults to the file name. |
| `ContentType` | `string?` | |
| `IsLive` | `bool` | `false`. |
| `ExtraFields` | `IReadOnlyDictionary<string,string>` | `FrozenDictionary.Empty`. |

## NetworkMediaSource

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaSource, IMediaSourceMetadata` (immutable). Built-in SSRF protection at construction time.

```csharp
public NetworkMediaSource(
    string url,
    IReadOnlyDictionary<string, string>? headers = null,
    TimeSpan? timeout = null,
    bool allowInsecureHttps = false,
    IReadOnlyList<MediaCookie>? cookies = null,
    string? name = null,
    string? contentType = null,
    bool isLive = false,
    IReadOnlyDictionary<string, string>? extraFields = null,
    bool allowPrivateAddresses = false)
```

| Member | Type | Notes |
|--------|------|-------|
| `Url` | `string` | Must be a valid absolute `http`/`https` URI. `file://` is **rejected**. |
| `Headers` | `IReadOnlyDictionary<string,string>` | |
| `Timeout` | `TimeSpan` | Default `30 s`. |
| `AllowInsecureHttps` | `bool` | Default `false`. Routes through the `"LingFanMedia_Insecure"` client. |
| `AllowPrivateAddresses` | `bool` | **SSRF switch.** Default `false` — rejects loopback / private / link-local / reserved IPs at construction (`IsPrivateOrLoopbackAddress`). Set `true` only for intentional intranet access. |
| `Cookies` | `IReadOnlyList<MediaCookie>?` | |
| `Type` | `MediaSourceType` | `Network`. |
| `Identifier` | `string` | `= Url`. |
| `IsLive` | `bool` | |

The constructor rejects `file://`, non-http(s) schemes, and (unless `allowPrivateAddresses`) any host resolving to a private/loopback/link-local address (including IPv4-mapped and IPv4-compatible IPv6 forms).

## StreamMediaSource

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaSource, IMediaSourceMetadata` (immutable). Wraps an external `Stream`.

```csharp
public StreamMediaSource(
    Stream stream,
    string identifier,
    bool ownsStream = false,
    string? name = null,
    string? contentType = null,
    bool isLive = false,
    IReadOnlyDictionary<string, string>? extraFields = null)
```

| Member | Type | Notes |
|--------|------|-------|
| `Stream` | `Stream` | The wrapped stream (thread-safety is the caller's responsibility). |
| `OwnsStream` | `bool` | When `true`, the stream is closed on player disposal. |
| `Type` | `MediaSourceType` | `Stream`. |
| `Identifier` | `string` | Required, non-empty. |

## SsrfGuard (Security helper)

Namespace: `LingFan.Media.Sources.Security`

`public static class`. **Pure CPU computation — no I/O, no state.** Safe to call on async paths (no pseudo-async, no blocking).

```csharp
public static void Validate(string host, IPAddress[] resolvedIps, bool allowPrivateAddresses);
public static bool IsPrivateOrLoopbackOrReserved(IPAddress ip);
public static bool TryParseIpLiteral(string host, [NotNullWhen(true)] out IPAddress? ip);
```

- `Validate` — throws `SecurityException` if any resolved IP (or a literal parsed from `host`) falls in private/loopback/link-local/reserved/CGNAT (`100.64.0.0/10`) and `allowPrivateAddresses` is `false`. This is the **DNS-rebinding** defense: it validates the *resolved* IPs, not just the hostname.
- `IsPrivateOrLoopbackOrReserved` — covers IPv4 (loopback, private A/B/C, link-local, `0.0.0.0/8`, CGNAT, multicast/reserved) and IPv6 (`::1`, link-local, site-local, ULA `fc00::/7`, multicast).
- `TryParseIpLiteral` — parses hex (`0x7f000001`), decimal (`2130706433`), and dotted-octal (`0177.0.0.1`) IP encodings that a DNS server might not resolve, so they cannot bypass the string check in `NetworkMediaSource`.

## SsrfConnectGuard (Security helper)

Namespace: `LingFan.Media.Sources.Security`

`public static class`. The **connect-level** defense: a `SocketsHttpHandler.ConnectCallback` that **closes the TOCTOU gap** left by `SsrfGuard.Validate`.

```csharp
public static async ValueTask<Stream> ConnectAsync(
    SocketsHttpConnectionContext context,
    CancellationToken ct);
```

- Carries the pre-validated IPs via three request options:
  - `ValidatedIpsKey` (`HttpRequestOptionsKey<IPAddress[]>` — `"LingFan.Media.ValidatedIps"`)
  - `ValidatedHostKey` (`HttpRequestOptionsKey<string>` — `"LingFan.Media.ValidatedHost"`)
  - `AllowPrivateAddressesKey` (`HttpRequestOptionsKey<bool>` — `"LingFan.Media.AllowPrivateAddresses"`)
- **DNS pinning:** if the pinned IPs match the current host, it connects only to those fixed IPs (no second resolution). On redirect to a different host, it re-resolves and re-validates (`SsrfGuard.Validate`) before connecting.
- Genuine async (DNS + socket I/O); exposes `CancellationToken`; no sync blocking, no pseudo-async. AOT-compatible (static, no reflection).

> **Two-layer SSRF model.** `SsrfGuard.Validate` (computation, called after DNS) + `SsrfConnectGuard.ConnectAsync` (DNS pinning, closes the rebinding window). Both are mounted automatically by `AddLingFanMedia` on the two named `HttpClient`s — you get this protection for free on every network source.
