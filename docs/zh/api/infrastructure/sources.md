# 源与流（`LingFan.Media.Sources`）

描述要播放的*内容*以及*如何*打开它，外加保护网络播放的 **SSRF 保护**。不可变、线程安全的源类型同时实现 `IMediaSource` 与 `IMediaSourceMetadata`。

## MediaStreamFactory

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaStreamFactory`。Singleton；持有 `IHttpClientFactory` 用于网络流。使用模式匹配的 `switch`（AOT 友好，无反射）。

```csharp
public MediaStreamFactory(IHttpClientFactory httpClientFactory)
```

| 方法 | 返回 | 说明 |
|--------|---------|-------|
| `Create(IMediaSource source)` | `IMediaStream` | 同步原生边界 —— 连接被推迟到首次 `Read`。 |
| `CreateAsync(IMediaSource source, CancellationToken ct = default)` | `Task<IMediaStream>` | **网络源首选。** 执行真实的 DNS 解析 + 在解析*之后*进行 `SsrfGuard.Validate`（DNS 重绑定保护要求在解析后校验），然后将预校验的 IP 传给流。真正的异步（I/O）—— 真正被 awaited，**不**是伪异步。 |

`CreateCore` 将 `File` → `FileMediaStream`、`Network` → `NetworkMediaStream`、`Stream` → `PassThroughMediaStream`。

## FileMediaSource

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaSource, IMediaSourceMetadata`（不可变）。

```csharp
public FileMediaSource(string path, string? name = null, string? contentType = null)
```

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Path` | `string` | 通过 `Path.GetFullPath` 规范化。若为空/空白则抛出 `ArgumentException`。 |
| `Type` | `MediaSourceType` | `File`。 |
| `Identifier` | `string` | `= Path`。 |
| `Name` | `string?` | 默认为文件名。 |
| `ContentType` | `string?` | |
| `IsLive` | `bool` | `false`。 |
| `ExtraFields` | `IReadOnlyDictionary<string,string>` | `FrozenDictionary.Empty`。 |

## NetworkMediaSource

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaSource, IMediaSourceMetadata`（不可变）。构造时内置 SSRF 保护。

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

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Url` | `string` | 必须是有效的绝对 `http`/`https` URI。`file://` 被**拒绝**。 |
| `Headers` | `IReadOnlyDictionary<string,string>` | |
| `Timeout` | `TimeSpan` | 默认 `30 s`。 |
| `AllowInsecureHttps` | `bool` | 默认 `false`。经由 `"LingFanMedia_Insecure"` 客户端路由。 |
| `AllowPrivateAddresses` | `bool` | **SSRF 开关。** 默认 `false` —— 在构造时拒绝回环 / 私有 / 链路本地 / 保留 IP（`IsPrivateOrLoopbackAddress`）。仅在有意访问内网时设为 `true`。 |
| `Cookies` | `IReadOnlyList<MediaCookie>?` | |
| `Type` | `MediaSourceType` | `Network`。 |
| `Identifier` | `string` | `= Url`。 |
| `IsLive` | `bool` | |

构造函数拒绝 `file://`、非 http(s) 方案，以及（除非 `allowPrivateAddresses`）任何解析到私有/回环/链路本地地址的主机（包括 IPv4 映射与 IPv4 兼容的 IPv6 形式）。

## StreamMediaSource

Namespace: `LingFan.Media.Sources`

`sealed class : IMediaSource, IMediaSourceMetadata`（不可变）。包装一个外部 `Stream`。

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

| 成员 | 类型 | 说明 |
|--------|------|-------|
| `Stream` | `Stream` | 被包装的流（线程安全由调用方负责）。 |
| `OwnsStream` | `bool` | 为 `true` 时，流在播放器处置时关闭。 |
| `Type` | `MediaSourceType` | `Stream`。 |
| `Identifier` | `string` | 必填，非空。 |

## SsrfGuard（安全辅助类）

Namespace: `LingFan.Media.Sources.Security`

`public static class`。**纯 CPU 计算 —— 无 I/O，无状态。** 在异步路径上调用是安全的（无伪异步，无阻塞）。

```csharp
public static void Validate(string host, IPAddress[] resolvedIps, bool allowPrivateAddresses);
public static bool IsPrivateOrLoopbackOrReserved(IPAddress ip);
public static bool TryParseIpLiteral(string host, [NotNullWhen(true)] out IPAddress? ip);
```

- `Validate` —— 若任何已解析 IP（或从 `host` 解析出的字面量）落入私有/回环/链路本地/保留/CGNAT（`100.64.0.0/10`）且 `allowPrivateAddresses` 为 `false`，则抛出 `SecurityException`。这是**DNS 重绑定**防御：它校验*已解析*的 IP，而非仅校验主机名。
- `IsPrivateOrLoopbackOrReserved` —— 涵盖 IPv4（回环、私有 A/B/C、链路本地、`0.0.0.0/8`、CGNAT、组播/保留）与 IPv6（`::1`、链路本地、站点本地、ULA `fc00::/7`、组播）。
- `TryParseIpLiteral` —— 解析十六进制（`0x7f000001`）、十进制（`2130706433`）与点点八进制（`0177.0.0.1`）这些 DNS 服务器可能无法解析的 IP 编码，从而它们无法绕过 `NetworkMediaSource` 中的字符串检查。

## SsrfConnectGuard（安全辅助类）

Namespace: `LingFan.Media.Sources.Security`

`public static class`。**连接级**防御：一个 `SocketsHttpHandler.ConnectCallback`，**关闭** `SsrfGuard.Validate` 遗留下来的 TOCTOU 间隙。

```csharp
public static async ValueTask<Stream> ConnectAsync(
    SocketsHttpConnectionContext context,
    CancellationToken ct);
```

- 通过三个请求选项携带预校验的 IP：
  - `ValidatedIpsKey`（`HttpRequestOptionsKey<IPAddress[]>` —— `"LingFan.Media.ValidatedIps"`）
  - `ValidatedHostKey`（`HttpRequestOptionsKey<string>` —— `"LingFan.Media.ValidatedHost"`）
  - `AllowPrivateAddressesKey`（`HttpRequestOptionsKey<bool>` —— `"LingFan.Media.AllowPrivateAddresses"`）
- **DNS 固定：** 若固定的 IP 与当前主机匹配，则仅连接到那些固定 IP（不再二次解析）。重定向到不同主机时，它在连接前重新解析并重新校验（`SsrfGuard.Validate`）。
- 真正的异步（DNS + 套接字 I/O）；暴露 `CancellationToken`；无同步阻塞，无伪异步。AOT 兼容（静态，无反射）。

> **双层 SSRF 模型。** `SsrfGuard.Validate`（计算，DNS 之后调用）+ `SsrfConnectGuard.ConnectAsync`（DNS 固定，关闭重绑定窗口）。二者都由 `AddLingFanMedia` 自动挂载到两个命名 `HttpClient` 上 —— 你在每个网络源上都免费获得此保护。
