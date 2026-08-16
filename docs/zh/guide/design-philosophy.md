# 设计哲学

LingFan.Media 中的每一个设计决策都可以追溯到十条原则。

## 1. DI 驱动

一切皆通过 `Microsoft.Extensions.DependencyInjection` 接线。你通过注册服务来组合一个媒体栈：`services.AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()`。没有静态初始化器，没有全局状态。

## 2. AOT 友好

该库必须以零 trim/analyze 警告的形式作为 NativeAOT 二进制发布。

- **零反射** —— 没有运行时类型发现，没有 `Dictionary<string, object>` 能力映射。
- **`[LibraryImport]`，绝不 `[DllImport]`** —— P/Invoke 使用源生成的封送（`[LibraryImport]`），这是唯一 AOT 正确的静态 P/Invoke。`[DllImport]` 依赖基于运行时反射的封送器，因此被禁用。
- **密封类型、`ValueTask` 热路径、编译期已知类型。**

### 当前的局限性

代码库本身是完整 AOT 正确编写的——零反射、`[LibraryImport]`、密封类型、编译期已知类型。目前主要的残余摩擦来自**第三方 GPU 互操作库**（Vortice / SharpGen 生成的 Direct3D 绑定），它们依赖生成的、基于反射的封送，因此在 `PublishAot` 下会产生 trim/analyze 警告。

今天对此是「受控而非消除」，通过：

- `TrimmerRootAssembly` 条目，避免相关绑定被激进裁剪；以及
- 针对这些绑定所引发的已知 IL2xxx 分析器 ID 进行定向 `NoWarn` 抑制。

这保证了发布的二进制完全可用且可 AOT 发布；警告是在工具链层面被抑制，而非在源头消除。

计划中的方向是**逐步用原生动态互操作替换基于反射的绑定层**——在裸函数指针之上做显式 vtable 分派（这也是我们所有 COM/P/Invoke 边界已采用的方式）。随着更多第三方绑定被纳入这一模型，根程序集垫片与警告抑制便可退役，从而在不依赖抑制的前提下达成零 trim/analyze 警告的目标。

总之：这一局限存在且已被认知，不会阻碍 AOT 发布，并且已处于明确的消除路径上。

## 3. 后端 可替换

FFmpeg / VLC / MediaFoundation 可互操作且可独立选择。你的应用代码永远不应指名某个 后端。

## 4. 不绑定于 Avalonia

`Core` 不知道 Avalonia 的存在。只有 `LingFan.Media.Avalonia` 模块引用 UI。同一个 `IMediaPlayer` 既能驱动无界面服务端，也能驱动桌面应用。

## 5. 通过 `IFrameResource` 实现 GPU 零拷贝

一个视频帧是一个 `IFrameResource`，可能携带 CPU 或 GPU 内存。帧 管线 对像素位于何处保持不可知；零拷贝呈现是一种 Sink 能力。

## 6. 无界面渲染

一个视频帧以 GPU 纹理的形式交付给平台合成器（Windows → DirectComposition，macOS/iOS → CAMetalLayer + CoreAnimation，Android → TextureView + SurfaceFlinger）。每个受支持平台都正确地实现一次；用户通过一个 `IVideoRenderer` 契约获得每个受支持平台的无界面体验。（Linux 已排除在目标表面之外——见[后端与平台路线](./backends)。）

## 7. 内存安全

帧所有权转移语义、`ArrayPool` 复用、`SafeHandle` + 显式 `Dispose` 分层。一个 Sink 以只读方式借用一帧，并且**绝不可 Dispose 它**；生命周期由生产者拥有。

## 8. 管线 同步方法，I/O 边界真正异步

`VideoPipeline` / `AudioPipeline` / `SubtitleProcessor` / `MediaPipelineHost` 把 `Start` / `Pause` / `Stop` / `Flush` 暴露为**同步 `void`**（纯内存工作；`Stop` 只发出取消信号，它不 join 线程）。线程 join（5 秒超时）发生在 `DisposeAsync` 中。`IMediaPlayer.PlayAsync` / `PauseAsync` / `StopAsync` 作为接口契约返回 `Task`，并且由于是纯内存操作，返回 `Task.CompletedTask`——这**不是**伪异步。真正的 `await` 留给真正的 I/O 边界（`OpenAsync`、`SeekAsync`、`ReadPacketAsync`、网络连接、`DisposeAsync` 的 join）。

## 9. CancellationToken 仅出现在 I/O 边界

`CancellationToken` 出现在 I/O 边界与会话生命周期（`OpenAsync`、`StopAsync`、`SeekAsync`、`ReadPacketAsync`）。它刻意**不出现在热路径**中（`DecodeAsync`、`Present`、`Submit`），因为在那些地方它会带来开销与争用。

## 10. 会话隔离

每个 `IMediaPlayer` 都拥有一个独立的会话——它自己的时钟、缓冲与 管线。DI 提供系统级工厂（Singleton）；会话是 Transient 的，并在 `OpenAsync` 内部创建。

## 契约层演进

`Abstractions` 层**并非冻结**。当确有需要时，方法签名（同步或异步）可以*添加*——这避免了两种失败模式：(a) 缺少某个异步签名会迫使调用方陷入 `.Wait()` / `.Result` 硬性阻塞或伪异步 后端；(b) 缺少某个同步签名会迫使原生边界的调用方绕过 `await` 工作。

任何添加都遵循两条原则：

1. **零外部引用。** 新签名的参数/返回类型必须是 BCL 类型（`IDisposable`、`Memory<byte>`、`Stream`、`CancellationToken`、`Task` / `ValueTask`）或已在 `Abstractions` 中定义的类型。绝不引用 后端 / 渲染器 / UI 的具体类型。*依赖反转真正的回报：* 让契约保持零外部引用，后端 / 渲染器 / UI 就可以自由替换。
2. **零实现。** `Abstractions` 只持有签名、自动属性与纯数据模型（包括 `Dispose` 释放其自身中立资源）。没有业务逻辑，没有对具体实现进行 `new`。

> 优先*添加*签名，而非修改已有签名。如果必须修改签名，请审计每一个实现与调用方。
