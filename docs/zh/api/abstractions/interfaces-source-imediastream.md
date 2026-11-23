---
title: "IMediaStream"
type: "interface"
namespace: "LingFan.Media.Abstractions"
source: "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStream.cs"
generated: true
generator: "scripts/gen-api.mjs"
symbols: ["IMediaStream", "Read", "ReadAsync", "ConnectAsync", "Length", "Position", "CanSeek", "Location", "Seek", "SuggestedOpenHeaders", "Close"]
lang: "zh-CN"
---
# IMediaStream

源码: [`IMediaStream.cs`](https://github.com/MoFeng-02/LingFan.Media/blob/main/src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Source/IMediaStream.cs)

媒体数据流接口。

## 声明

```csharp
namespace LingFan.Media.Abstractions;

public interface IMediaStream
```

## 备注

Read 和 ReadAsync 均采用 caller-allocated buffer 模式。不接受 null buffer。
buffer 大小为 0 时返回 0。

优先使用 `ReadAsync`（支持 CancellationToken，不阻塞线程）。
同步 `Read` 仅用于同步边界（如 FormatDetector 探测、FFmpeg AVIO 回调）。

网络流必须在 `Read` 之前调用 `ConnectAsync` 建立底层连接——
同步边界（C 函数指针/同步探测）无法 await，故建连必须前置到异步路径。

## 成员

### Read

```csharp
int Read(Span<byte> buffer);
```

同步读取数据到指定 buffer。

同步边界（C 函数指针 / 同步探测），无法 await。
网络流必须在 `ConnectAsync` 之后调用，否则抛 `InvalidOperationException`。
已连接后每帧同步读取会阻塞调用线程直到数据到达（FFmpeg 工作线程，非 UI）。仅在同步上下文使用。

**参数**

- `buffer`：目标 buffer（Span 切片）。长度为 0 时返回 0。

**返回**：实际读取的字节数，0 表示流结束。

### ReadAsync

```csharp
ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
```

异步读取数据到指定 buffer。

**建连语义**：`ReadAsync` 允许**隐式建连**（网络流首次读取时才真正
连到对端），而同步 `Read` 必须工作在已建连状态——实现可在 Open 阶段预先建连，
也可把建连推迟到首次 `ReadAsync`。

建连失败（DNS/连接/握手）须抛出 `IOException`，不得返回 0 冒充流结束。

**参数**

- `buffer`：目标 buffer。不接受空 buffer。
- `ct`：取消令牌（网络流必须支持，本地文件可忽略）。

**返回**：实际读取的字节数，0 表示流结束。

### ConnectAsync

```csharp
Task ConnectAsync(CancellationToken ct = default);
```

异步预建立底层连接（网络流建立 HTTP 连接；文件/透传流为无操作）。

**必须在同步 `Read` 之前调用**：FFmpeg AVIO 原生回调与 FormatDetector 同步探测
均为同步边界，无法 await 建连。网络流若不预建连，`Read` 会抛
`InvalidOperationException`。

**参数**

- `ct`：取消令牌。

**返回**：连接建立完成的 Task。

### Length

```csharp
long Length { get; }
```

流长度（不可求时为 -1）。

### Position

```csharp
long Position { get; }
```

当前读取位置。

### CanSeek

```csharp
bool CanSeek { get; }
```

是否可定位。

### Location

```csharp
string? Location { get; }
```

流的可定位地址（文件路径或网络 URL）；无法以地址方式打开时为 null。

供需要按地址打开的 backend（如 MediaFoundation 的 `MFCreateSourceReaderFromURL`）使用。
文件流返回文件路径，网络流返回 URL；内存/透传流无地址返回 null。

中性契约成员（BCL string），不引用任何具体后端/源类型，遵循依赖倒置。

### Seek

```csharp
long Seek(long offset, SeekOrigin origin);
```

定位到指定偏移。

不可定位的流（如不可 seek 的直播流）须抛 `NotSupportedException`，
不得返回 0 或静默忽略。

目标位置越界时抛 `IOException`（与读取端错误统一）。

已 `Close` 后调用抛 `ObjectDisposedException`。

**参数**

- `offset`：相对 &lt;paramref name="origin"/&gt; 的字节偏移。
- `origin`：偏移基准（流首/当前位置/流尾）。

**返回**：定位后的新读取位置（绝对字节偏移）。

### SuggestedOpenHeaders

```csharp
IReadOnlyDictionary<string, string>? SuggestedOpenHeaders { get; }
```

按 `Location` 地址打开时建议随附的 HTTP 请求头（默认 User-Agent、
用户自定义头与 Cookie 的合并结果）；非网络流为 null。

供按地址直开的解封装后端（URL 交给后端自身网络栈的场景）在打开地址时透传，
避免自定义头/Cookie 在地址式打开路径上被静默丢弃（防盗链源必需）。

中性契约成员（BCL 字典类型，零外部引用）；实现须显式提供该属性（不依赖接口默认实现）。

### Close

```csharp
void Close();
```

关闭流。

**幂等**：重复调用不得抛异常（已关闭再关闭是空操作）。

关闭后 `ReadAsync` 与 `Seek` 抛 `ObjectDisposedException`。

关闭须释放底层连接与原生资源，且不等待未完成的读取返回。
