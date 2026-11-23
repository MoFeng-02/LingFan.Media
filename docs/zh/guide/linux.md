---
title: Linux 部署
sources: ["src/Backends/LingFan.Media.Backends.FFmpeg/Interop/Loading/FFmpegLibraryLoader.cs", "src/Renderers/LingFan.Media.Renderers.Vulkan/LingFan.Media.Renderers.Vulkan.csproj", "src/Renderers/LingFan.Media.Renderers.OpenGL/LingFan.Media.Renderers.OpenGL.csproj", "src/Outputs/LingFan.Media.Outputs/LingFan.Media.Outputs.csproj"]
symbols: ["IVideoRenderer", "IAudioOutput", "IFrameResource", "GPUApiType"]
lang: zh
---

# Linux 部署

本页涵盖在 Linux 上运行 LingFan.Media 的运行时前置条件与硬解配置。已验证的零拷贝路径是 FFmpeg + VAAPI → Vulkan；OpenGL 受支持，在驱动导入受限处回落 CPU 上传。支持矩阵见[平台支持](/zh/guide/platforms)。

## 运行时前置条件

| 组件 | 要求 |
|---|---|
| .NET | .NET 10 运行时（或自包含 / AOT 发布）。 |
| FFmpeg | 共享库（LGPL 构建）：`libavutil`、`libavcodec`、`libavformat`、`libswscale`、`libswresample`、`libavfilter`、`libavdevice`，带常规版本别名符号链接。 |
| OpenAL | `libopenal1` 用于音频输出。缺失时音频回落静音而非使播放失败。 |
| 音频服务器 | PulseAudio 或 PipeWire（OpenAL 所指向者）。 |

FFmpeg 加载器按序搜索：系统库路径、应用目录、然后递归扫描（最高版本胜出）。可用 `LF_FFMPEG_LIB` 环境变量钉住特定库。

> 在 Windows 与 Linux 之间同步 FFmpeg 时，版本符号链接会退化为纯文本文件。请在 Linux 侧解压 Linux tarball，使共享对象符号链接为真实链接。

## 软解（最小可行）

仅安装 FFmpeg 共享库与 OpenAL，软解与播放即开箱可用——无需 GPU 配置。这是验证部署的最简路径。

## VAAPI 硬解

经 VAAPI 硬解需要三样：

1. **libva ≥ 2.21。** 较旧 libva（如 2.20）缺 `vaMapBuffer2`，零拷贝导出路径用到它。安装更新的 libva（发行版更新仓库或打包构建）。
2. **Intel 媒体驱动。** Intel GPU 用 `intel-media-va-driver-non-free`（iHD 驱动）。
3. **DRM 渲染节点访问。** 进程须能打开 `/dev/dri/renderD128`（把用户加入 `render` / `video` 组）。用 `vainfo` 验证。

原生加载器 `dlopen` `libva.so.2` 且不消费 `DT_RUNPATH`，因此非系统 libva 须以绝对路径预加载（如经 `LD_LIBRARY_PATH` 或加载器的钉住机制）。

## 窗口化渲染

| 渲染器 | 软件包 | 零拷贝行为 |
|---|---|---|
| Vulkan | `mesa-vulkan-drivers`（Intel 用 ANV） | dma-buf 导入；Vulkan 物理设备自动对齐解码 GPU（同厂商）。 |
| OpenGL | `libegl1`、`libgl1` | 同一 dma-buf 导入路径；Mesa 单平面 tiling 导入受限处回落 CPU 上传。画面仍正确。 |

硬件表面导出/消费前发出 `vaSyncSurface` 栅栏。混合显卡机器可用 `LINGFAN_EGL_DEVICE_DRM` 钉住 EGL 设备（如 `renderD129`）。不支持跨厂商 tiling 导入；解码与渲染 GPU 自动对齐到同厂商。

## 验证探针

仓库在 `src/Tools/` 下附带无头探针，跑真实管线并把诊断打印到 stdout。在目标机器上运行：

| 探针 | 检查内容 |
|---|---|
| `LinuxHeadlessProbe` | 时钟 / 同步推进（`[SYNC]` 诊断）。 |
| `LinuxHeadlessVulkanProbe --hw` | Vulkan 硬解 + GPU 纹理帧（`[VULKAN-INIT]`）。 |
| `LinuxHeadlessOpenGlProbe` | OpenGL 零拷贝 / 回落路径（`[OPENGL-ZEROCOPY]`）。 |

```bash
dotnet run --project src/Tools/LinuxHeadlessVulkanProbe -- --hw
```

## 已知限制

- Linux 上的 LibVLC 已实现但验证待排期。
- Linux 上的网络源播放尚未完整验证。
- 不支持跨厂商 GPU 导入（一家解码、另一家渲染）。
- 无头虚拟显示（如 Xvfb）的 GPU 支持有限。
- Windows Media Foundation / D3D11 路径不适用于 Linux。
