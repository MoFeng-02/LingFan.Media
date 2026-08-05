# 第三方组件与许可证声明（Third-Party Notices）

本仓库（**LingFan.Media**，以 MIT 许可证分发）在 Windows 上通过 **FFmpeg** 提供媒体解封装与解码能力。
为遵守 **LGPL**，本项目**仅以动态链接方式**使用 FFmpeg 的共享库（`.dll`），**绝不**将 FFmpeg 的
源码或静态库合并进本项目的任何程序集。

---

## 1. FFmpeg（LGPL 2.1 或更高版本）

- **用途**：媒体解封装（`libavformat`）、解码（`libavcodec`）、缩放/像素格式转换
  （`libswscale`）、重采样（`libswresample`）、工具（`libavutil`）。
- **许可证**：LGPL 2.1+（本仓库使用的构建为 **LGPL 共享构建**，未启用 `--enable-gpl` /
  `--enable-nonfree`，不包含任何 GPL 专属编解码器）。
- **动态链接说明**：本项目通过 `FFmpeg.AutoGen` 的 `DynamicallyLoaded` 绑定在**运行时**加载
  `avcodec-*.dll`、`avutil-*.dll`、`avformat-*.dll`、`swscale-*.dll`、`swresample-*.dll`。
  原生 DLL 不随本项目源码分发，而是由宿主/测试程序在部署时放置于可搜索路径
  （本仓库 `ThirdParty/ffmpeg/` 仅作本地开发用，已被 `.gitignore` 忽略）。
- **可替换性（LGPL 要求）**：原生 DLL 的路径通过 `FFmpegOptions.FFmpegLibraryPath`
  显式配置（默认指向应用程序基目录）。用户可在不重新编译本项目的前提下，
  替换/升级这些共享库（例如修复安全漏洞或切换构建）。
- **获取源码**：FFmpeg 源码与 LGPL 许可证文本见 <https://ffmpeg.org/legal.html>。
  本仓库使用的二进制来自 **BtbN FFmpeg Builds** 的 `lgpl-shared` 变体
  （<https://github.com/BtbN/FFmpeg-Builds/releases>）。为与 `FFmpeg.AutoGen 8.1.0` 的
  ABI 严格对齐，本仓库固定使用 **`ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`**
  （FFmpeg 8.1 共享构建）；`ffmpeg-master-latest-win64-lgpl-shared.zip` 也可用，但主版本
  可能领先 8.1，若 AutoGen 绑定未同步升级则存在 ABI 不匹配风险，故不推荐。两种变体均为
  LGPL 构建。

> ⚠️ 注意：gyan.dev 提供的 `ffmpeg-*-full_build-shared` 与 `essentials_build`（静态）构建
> 含 `--enable-gpl` / `--enable-static`，**不符合**本项目的 LGPL 动态链接要求，请勿用于本项目。

---

## 2. FFmpeg.AutoGen（MIT 许可证）

- **用途**：.NET 与 FFmpeg 原生 API 之间的绑定（P/Invoke 生成）。
- **许可证**：MIT（<https://github.com/FFmpegAutoGen/FFmpeg.AutoGen>）。
- **绑定变体**：本项目使用 `FFmpeg.AutoGen.Bindings.DynamicallyLoaded`（MIT），
  它在运行时动态解析 FFmpeg 共享库，与 LGPL 动态链接要求一致。

---

## 3. 本项目（LingFan.Media）

- **许可证**：MIT。
- **组合方式**：本项目自身的源码以 MIT 许可证发布；对 FFmpeg 仅作动态链接调用，
  未修改 FFmpeg 源码、未将其合并进本项目程序集，因此本项目整体可按 MIT 分发，
  同时保持对 FFmpeg 部分的 LGPL 义务（提供 FFmpeg 源码获取途径、允许替换 DLL）。

---

## 4. 合规要点速查

| 要求 | 本项目做法 |
| --- | --- |
| 动态链接（非静态合并） | ✅ `FFmpeg.AutoGen.DynamicallyLoaded` 运行时 `LoadLibrary` 共享 DLL |
| 不引入 GPL 代码路径 | ✅ 仅用 LGPL 共享构建；wrapper 只调 FFmpeg 公共 LGPL API |
| 允许用户替换/升级 DLL | ✅ `FFmpegOptions.FFmpegLibraryPath` 可配置；DLL 不内嵌 |
| 提供许可证与署名 | ✅ 本文件 |
| 不修改 FFmpeg 源码 | ✅ 仅调用，未 vendoring 或修改 ffmpeg |
