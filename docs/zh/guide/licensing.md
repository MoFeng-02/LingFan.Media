# 许可与第三方合规

LingFan.Media 自身的源代码以 **Apache-2.0** 许可证发布。它可选地依赖两个原生后端——**FFmpeg** 与 **LibVLC（VLC）**，二者均以 **LGPL 2.1 或更高版本** 授权。本页说明项目如何通过「仅动态链接」达到 LGPL 合规，以及为何其自有许可证不会被 LGPL 后端「传染」。

## 核心原则：仅动态链接

LingFan.Media 绝不会把 FFmpeg 或 LibVLC 静态合并进自身程序集，也绝不修改其源码。它在运行时以共享库方式动态加载：

- **FFmpeg** 经由本项目自写的 Apache-2.0 P/Invoke 绑定（自写 `[LibraryImport]` + `NativeLibrary` 版本自适应加载）消费，在运行时解析共享库（`avcodec-*.dll` 等）。
- **LibVLC** 经由本项目自写的 Apache-2.0 P/Invoke 绑定 `VLCNative` 消费，在运行时动态加载 `libvlc` / `libvlccore`。

由于项目仅作动态链接、原样复制库、且未将其合并进自身二进制，依据 LGPL 2.1 第 6 条，**LingFan.Media 的自有代码不构成对 LGPL 库的派生作品**。其源代码与程序集始终保持 **Apache-2.0**——纯粹且无负担。LGPL 义务仅附着于被分发的原生二进制本身。

## 仅使用 LGPL 构建（不引入 GPL 污染）

- **FFmpeg** 以 **LGPL 共享构建** 使用——即 BtbN 的 `lgpl-shared` 变体，既不带 `--enable-gpl` 也不带 `--enable-nonfree`，从而排除所有仅 GPL 的编解码器。诸如 gyan.dev 的 `full_build-shared` / `essentials_build`（启用 GPL / 静态链接）的构建**不得**使用。
- **LibVLC** 仅加载以 LGPL 授权的引擎与播放相关模块（协议、解封装、解码、滤镜、输出）。少数仍以 GPLv2 发布的模块（流媒体 / 转码、界面，以及 DVD 的 `libdvdcss`）在本地播放时从不加载，因此其 GPL 条款不会触及本项目。

## 合规速查

| 要求 | LingFan.Media 的做法 |
| --- | --- |
| 动态链接（非静态合并） | 自写 FF 绑定（`NativeLibrary` 版本自适应加载）在运行时动态加载共享库 |
| 不引入 GPL 代码路径 | 仅用 LGPL 共享构建；wrapper 只调 FFmpeg 公共 LGPL API |
| 用户可替换 / 升级原生库 | `FFmpegOptions.FFmpegLibraryPath` 可配置；DLL 不内嵌 |
| 提供许可证与署名 | 随产品分发本文件（`THIRD_PARTY_NOTICES.md`） |
| 不修改 FFmpeg 源码 | 仅调用，从不 vendoring 或打补丁 |
| LibVLC 可替换 | 原生共享库位于独立的 `libvlc/` 目录，由 NuGet 按 RID 提供、不入库 |
| 项目许可证不被 LGPL 传染 | 仅动态链接、不改源码、不合并；自有代码始终 Apache-2.0 |

## 版权与专利风险——由使用者自行评估

LGPL（及其覆盖的编解码器）所涉及的**专利与版权考量因司法辖区、媒体格式和使用场景而异**。LingFan.Media 仅提供上述技术合规路径，**不对专利许可作任何陈述**。

**使用者有责任评估**：特定用途——例如特定编解码器（H.264 / H.265 / AV1）、分发地区或商业部署——是否需要单独的专利许可（如通过 MPEG-LA、HEVC Advance 或类似专利池）。生产环境部署请咨询法律顾问。

## 权威声明

带版本戳的完整文本——确切的二进制来源、逐模块许可，以及移动端（iOS / Android）重链说明——位于仓库根目录的 [`THIRD_PARTY_NOTICES.md`](https://github.com/MoFeng-02/LingFan.Media/blob/main/THIRD_PARTY_NOTICES.md)。
