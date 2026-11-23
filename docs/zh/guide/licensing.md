---
title: 许可
sources: ["README.zh.md", "THIRD_PARTY_NOTICES.md", "src/Backends/LingFan.Media.Backends.FFmpeg/FFmpegOptions.cs", "src/Backends/LingFan.Media.Backends.VLCNative/LingFan.Media.Backends.VLCNative.csproj"]
symbols: ["FFmpegOptions"]
lang: zh
---

# 许可

LingFan.Media 自有代码以 **Apache-2.0** 许可。可选原生后端（FFmpeg、LibVLC）为 **LGPL 2.1+**，仅经动态链接使用，因此基于 LingFan.Media 构建的产品依据 LGPL 2.1 第 6 条，不构成这些库的衍生作品。

## 原生库的使用方式

| 库 | 许可 | 加载 |
|---|---|---|
| FFmpeg | LGPL 2.1+ | 手写 `[LibraryImport]` 绑定 + `NativeLibrary` 版本自适应加载（`avcodec-*.dll` / `.so` / `.dylib`）。动态链接，绝不静态。 |
| LibVLC | LGPL 2.1+ | VLCNative 后端动态加载 `libvlc` / `libvlccore`。 |

由于这些库动态链接且可由用户替换，LGPL 条款得以满足，而不会把 LGPL 强加于你自己的应用代码。

## 仅 LGPL 构建——无 GPL

- **FFmpeg：** 使用 LGPL 共享构建（如 BtbN `lgpl-shared` 产物），配置不含 `--enable-gpl` / `--enable-nonfree`。**不要**使用 gyan.dev `full_build-shared` / `essentials_build` 等 GPL 构建。
- **LibVLC：** 仅加载 LGPL 引擎播放模块。不加载 GPL 组件（流媒体 / 转码 / UI 模块、`libdvdcss`）。

## 合规清单

| 要求 | 如何满足 |
|---|---|
| 动态链接 | FFmpeg 与 LibVLC 运行时加载，不静态链接。 |
| 无 GPL 代码路径 | 仅使用 LGPL 构建 / 模块。 |
| 用户可替换库 | `FFmpegOptions.FFmpegLibraryPath`（及加载器搜索序）让用户指向自己的构建。 |
| 声明随产品分发 | `THIRD_PARTY_NOTICES.md` 随发行版包含。 |
| FFmpeg 源码未改 | 共享构建原样使用；不改 FFmpeg 源码。 |
| LibVLC 保持独立 | `libvlc` / `libvlccore` 仍为独立动态库。 |
| per-RID 原生库不提交 | 原生运行时目录不提交入库；按运行时标识符获取。 |
| 自有代码 Apache-2.0 | 全部 LingFan.Media 源码为 Apache-2.0。 |

## 专利风险

编解码器专利（H.264、H.265 / HEVC、AV1 等）与版权许可是不同事项，由用户自行评估其分发（如 MPEG-LA、HEVC Advance 专利池）。LingFan.Media 不授予任何专利许可。

## 权威声明

权威第三方声明文件是仓库根目录的 [`THIRD_PARTY_NOTICES.md`](https://github.com/MoFeng-02/LingFan.Media/blob/main/THIRD_PARTY_NOTICES.md)。任何捆绑原生后端的产品都应随附它。
