---
title: 设计哲学
sources: ["Directory.Build.props", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Core/Clock/MediaClock.cs", "src/Infrastructure/LingFan.Media.Core/MediaPlayer/MediaPlayer.Dispose.cs"]
symbols: ["IMediaPlayer", "IFrameResource", "IVideoRenderer", "IAudioOutput", "IMediaClock", "IMediaSession", "VideoFrame"]
lang: zh
---

# 设计哲学

这些原则解释了代码为何长成这样。它们是约束性条文，不是愿景——新代码应当遵从。

## 十条原则

1. **DI 驱动装配。** 组件显式注册：`AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()`。接线可见可控，不藏在反射或自动扫描背后。

2. **AOT 友好。** 零反射激活、sealed 类型、热路径用 `ValueTask`、编译期确定类型。所有原生调用使用源生成的 `[LibraryImport]`（绝不用 `[DllImport]`，其运行时封送不兼容 NativeAOT）。每个库声明 `IsAotCompatible`；启用 trim 与 AOT 分析器，警告视为错误。

3. **后端可替换。** FFmpeg、LibVLC、Media Foundation、MediaCodec、AVFoundation 在同一契约后可互换。新增后端 = 新增一个实现 + 一行注册。

4. **不绑定任何 UI 框架。** 核心不感知 Avalonia（或任何其他 UI）。只有可选 UI 层依赖框架，且它依赖核心——绝不反向。

5. **GPU 零拷贝。** 解码帧以 `IFrameResource` 承载，统一 CPU 与 GPU 资源。硬解帧在平台支持导入时留在 GPU 上；其余情形使用正确的 CPU 回落。

6. **无头渲染。** 库绝不创建独立原生窗口。帧经平台合成器合成进宿主表面（Windows 的 DirectComposition、Apple 平台的 CAMetalLayer、Android 的 SurfaceFlinger、Linux 的 Wayland 子表面）。每个平台在统一的 `IVideoRenderer` 契约后各有正确实现。Linux 是受支持目标，具备已验证的 FFmpeg + VAAPI + Vulkan 零拷贝路径。

7. **内存安全。** 帧所有权显式转移；数组池化；原生资源用 `SafeHandle` 加分层 `Dispose`。无悬垂引用：借用的帧不得活过其回调。

8. **管线同步，I/O 边界异步。** 管线控制方法（启动 / 暂停 / 停止 / 刷新）同步且纯内存；`Stop` 只发取消信号。真正的异步工作在 I/O 边界（打开、定位、读取、网络）与优雅释放中，后者以有界超时 join 管线线程。见[异步与同步纪律](/zh/guide/async-sync)。

9. **取消令牌在边界。** `CancellationToken` 在 I/O 与会话生命周期边界（`OpenAsync`、`SeekAsync`、`StopAsync`、读包）接受，不在解码 / 呈现 / 提交等热路径。

10. **会话隔离。** 每个 `IMediaPlayer` 有自己的时钟、缓冲与管线。DI 提供系统级工厂；会话对象按每次打开创建。并发播放器绝不共享可变播放状态。

## 契约层演进

`Abstractions` 是系统的法律，但并非冻结。确有需求时，可在接口与模型上新增方法签名。这避免两种失败模式：缺异步签名迫使调用方同步阻塞，或缺同步签名迫使在无法 await 的原生边界绕路。

每次增补必须满足四条硬规则：

| 规则 | 含义 |
|---|---|
| 零外部引用 | 签名只能使用 BCL 类型或已在 `Abstractions` 内定义的类型。后端、渲染器或 UI 类型不得出现在契约签名中。 |
| 零实现 | 只有签名、自动属性与纯数据模型。无业务逻辑，不构造具体实现。 |
| 异步/同步按本质 | 新签名是异步还是同步遵循真实 I/O 规则，而非图省事。 |
| 只增不改 | 优先新增签名而非破坏既有签名。确需改动时，所有实现与调用方一并核查。 |

例如一个中立的 `IDisposable?` 所有者字段，让后端交回原生帧所有权，而契约层从不指名任何后端特有的句柄类型。这正是依赖倒置的回报：只要契约层保持纯净，后端、渲染器、输出与 UI 可自由替换，下游各层围绕稳定契约适配。
