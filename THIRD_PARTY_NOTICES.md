# 第三方组件与许可证声明（Third-Party Notices）

本仓库（**LingFan.Media**，以 Apache-2.0 许可证分发）是 .NET 平台的跨平台媒体基础设施。当前**首要验证目标为 Windows**；**Linux** 通过 **FFmpeg** 与 **LibVLC（VLC）**
两个跨平台后端提供播放（验证进行中）；**macOS / iOS / Android** 在路线图内、尚未验证。在上述已验证/在验证的
平台上均可经 **FFmpeg** 或 **VLC（LibVLC）** 提供媒体解封装与解码能力（二者均为可选的播放后端，由回退中间件按
注册顺序自动选择）；此外在 **Windows** 上还支持 **Media Foundation**（Windows 操作系统内置组件，无需单独分发的
第三方授权）。为遵守各自的 **LGPL**，本项目**仅以动态链接方式**使用 FFmpeg / LibVLC 的共享库（Windows 为 `.dll`、
Linux 为 `.so`；其余平台若未来启用，则为对应平台的共享库形态），**绝不**将它们的源码或静态库合并进本项目的任何
程序集。

---

## 1. FFmpeg（LGPL 2.1 或更高版本）

- **用途**：媒体解封装（`libavformat`）、解码（`libavcodec`）、缩放/像素格式转换
  （`libswscale`）、重采样（`libswresample`）、工具（`libavutil`）。
- **许可证**：LGPL 2.1+（本仓库使用的构建为 **LGPL 共享构建**，未启用 `--enable-gpl` /
  `--enable-nonfree`，不包含任何 GPL 专属编解码器）。
- **动态链接说明**：本项目通过 `FFmpeg.AutoGen` 的 `DynamicallyLoaded` 绑定在**运行时**动态加载
  FFmpeg 共享库（Windows 为 `avcodec-*.dll` 等，Linux 为 `libavcodec.so*` 等）。FFmpeg 的 **LGPL 共享构建
  在 Windows 与 Linux 上均可用**：Windows 采用 BtbN 的 `lgpl-shared` 变体（见下）；Linux 采用对应的
  `lgpl-shared` 变体（如 BtbN 提供的 `ffmpeg-master-latest-linux64-lgpl-shared`）。原生共享库不随本项目
  源码分发，而是由宿主/测试程序在部署时放置于可搜索路径（本仓库 `ThirdParty/ffmpeg/` 仅作本地开发用，已被
  `.gitignore` 忽略）。
- **版本对齐（ABI）**：本项目绑定 **FFmpeg.AutoGen 8.1.0**，其 ABI 与 **FFmpeg 8.x** 严格对齐（如
  `avutil-60`）。因此须使用与 FFmpeg.AutoGen 8.1.0 ABI 匹配的 **FFmpeg 8.x LGPL 共享构建**；BtbN 当前
  发布的 `ffmpeg-master-latest-*-lgpl-shared` 即跟踪该主版本线。请勿使用 `master` 之外的、主版本明显领先
  的构建，以免 ABI 不匹配。
- **可替换性（LGPL 要求）**：原生共享库的路径通过 `FFmpegOptions.FFmpegLibraryPath`
  显式配置（默认指向应用程序基目录）。用户可在不重新编译本项目的前提下，
  替换/升级这些共享库（例如修复安全漏洞或切换构建）。
- **获取源码与许可证文本**：FFmpeg 源码与 LGPL 许可证文本见 <https://ffmpeg.org/legal.html>（含 LGPL
  合规清单）。本仓库使用的二进制来自 **BtbN FFmpeg Builds** 的 `lgpl-shared` 变体
  （<https://github.com/BtbN/FFmpeg-Builds/releases>）。

> ⚠️ 注意：gyan.dev 提供的 `ffmpeg-*-full_build-shared` 与 `essentials_build`（静态）构建
> 含 `--enable-gpl` / `--enable-static`，**不符合**本项目的 LGPL 动态链接要求，请勿用于本项目。

---

## 2. FFmpeg.AutoGen（MIT 许可证）

- **用途**：.NET 与 FFmpeg 原生 API 之间的绑定（P/Invoke 生成）。
- **许可证**：MIT（<https://github.com/Ruslan-B/FFmpeg.AutoGen>）。
- **绑定变体**：本项目使用 `FFmpeg.AutoGen.Bindings.DynamicallyLoaded`（MIT），
  它在运行时动态解析 FFmpeg 共享库，与 LGPL 动态链接要求一致。
- **版本**：当前固定为 **8.1.0**（与 FFmpeg 8.x 的 ABI 对齐）。

---

## 3. 本项目（LingFan.Media）

- **许可证**：Apache-2.0。
- **组合方式**：本项目自身的源码以 Apache-2.0 许可证发布；对 FFmpeg / LibVLC 仅作**动态链接**调用，
  未修改其源码、未将其合并进本项目程序集、未采用静态链接。
- **授权性质声明（重要）**：由于 FFmpeg 与 LibVLC 均以 **LGPL 2.1+** 授权、且本项目仅以动态链接方式使用，
  根据 LGPL 2.1 第 6 条，**本项目的自有代码不构成对 LGPL 库的派生作品**，因此本项目的许可证
  **不会因为依赖这些 LGPL 后端而改变**——LingFan.Media 的源码与程序集始终以 **Apache-2.0** 授权，
  保持纯粹。LGPL 所产生的义务仅附着于**被分发的 LGPL 原生二进制本身**（须随产品提供许可证文本、
  源码获取途径、并保证可被替换/重链），不影响调用方（本项目）的 Apache-2.0 授权。
- **保持 Apache-2.0 纯粹性的前提条件（须持续满足）**：
  1. 始终保持**动态链接**：不得改为静态合并 FFmpeg / LibVLC 进本项目程序集；
  2. 分发的原生二进制必须是**LGPL 共享构建**：FFmpeg 使用 BtbN `lgpl-shared`（不得改用 `gpl-shared`
     或自行以 `--enable-gpl`/`--enable-nonfree` 编译，否则会引入 GPL 义务）；VLC 仅加载 LGPL 的
     libvlc 核心与播放相关模块，不加载仅以 GPL 发布的流媒体/转码/界面/ DVD(libdvdcss) 等非播放模块；
  3. 随产品**附带本声明文件**（THIRD_PARTY_NOTICES），并提供 FFmpeg / LibVLC 的源码获取途径；
  4. 保证 LGPL 二进制的**可替换性（重链能力）**：桌面端（Windows / Linux）通过可独立替换的共享库文件实现；
     **若未来启用移动端（iOS / Android）**，受应用商店沙箱限制，终端用户无法在已安装应用内直接替换库，
     业界的 LGPL 合规做法是——随产品提供对应的 LGPL 库源码与可重链的目标文件（object files），
     使接受者**具备重链能力**即视为满足 LGPL 第 6 条；本项目在各平台均不修改这些库源码、不合并进
     程序集，故该义务始终成立。
- 本项目（Apache-2.0）与 FFmpeg（LGPL 2.1+）/ LibVLC（LGPL 2.1+）的许可证互相兼容，组合分发合法。

---

## 4. 合规要点速查

| 要求 | 本项目做法 |
| --- | --- |
| 动态链接（非静态合并） | ✅ `FFmpeg.AutoGen.DynamicallyLoaded` 运行时 `LoadLibrary` 共享库 |
| 不引入 GPL 代码路径 | ✅ 仅用 LGPL 共享构建；wrapper 只调 FFmpeg 公共 LGPL API |
| 允许用户替换/升级共享库 | ✅ `FFmpegOptions.FFmpegLibraryPath` 可配置；共享库不内嵌 |
| 提供许可证与署名 | ✅ 本文件 |
| 不修改 FFmpeg / LibVLC 源码 | ✅ 仅调用，未 vendoring 或修改 |
| 允许用户替换/升级 LibVLC | ✅ 原生共享库由 `VideoLAN.LibVLC.Windows` 按 Windows RID 提供、不入库、可替换 |
| 本项目许可证不被 LGPL 传染 | ✅ 仅动态链接、不改源码、不合并；自有代码始终 Apache-2.0（见第 3 节） |

---

## 5. LibVLC / VLC（LGPL 2.1+）

- **用途**：可选媒体播放后端（解封装 / 解码 / 输出），在 FFmpeg 后端不可用或失败时由回退中间件自动切换；
  **当前在 Windows 上可用**，Linux 上的 LibVLC 后端随 FFmpeg 一同在验证中。在 .NET 体系下统一通过
  `LibVLCSharp`（LGPLv2.1+，VideoLAN 官方跨平台 .NET/Mono 绑定）动态加载 `libVLC`。
- **许可证**：**libVLC 引擎（`libvlc` + `libvlccore`）以及绝大多数播放相关模块（协议、解封装、解码、滤镜、
  输出）均为 LGPL 2.1+**（VideoLAN 官方公告：引擎于 2011–2012 年从 GPL 重新授权为 LGPL 2.1+；随后绝大多数
  播放模块也完成同样重新授权，见 <https://www.videolan.org/press/lgpl-modules.html> 与
  <https://www.videolan.org/press/lgpl-libvlc.html>）。因此本项目以 libVLC 作为播放后端整体处于 LGPL 授权
  之下，可按 LGPL 条款嵌入到非 GPL（含专有）应用中。
- **动态链接说明（当前范围）**：本项目经由 `LibVLCSharp`（LGPLv2.1+）在**运行时**动态加载 libVLC 共享库——
  Windows 为 `libvlc.dll` / `libvlccore.dll`。原生二进制当前由 `VideoLAN.LibVLC.Windows` NuGet 提供
  （Windows RID），部署时落在输出目录并由 `.gitignore` 忽略（不随源码提交、不内嵌）。**若未来启用 macOS /
  iOS / Android**，将改用具对应 RID 的 `VideoLAN.LibVLC.*` 包，但本项目目前仅验证了 Windows 路径。
- **可替换性（LGPL 要求）**：用户可在不重新编译本项目的前提下，替换这些共享库（例如升级 VLC 或修复安全漏洞）。
- **关于其余少数 GPL 模块**：VLC 中仍有**少数与播放无关的模块**保持 GPLv2（主要为流媒体/转码模块、界面模块，
  以及 DVD 相关的 libdvdcss）。本项目**仅将 libVLC 用于本地播放**，不会加载这些非播放模块，其 GPL 条款不影响
  本项目的授权，本仓库亦不随产品再分发这些模块。如未来启用转码/流媒体能力，再另行评估。
- **获取源码与许可证文本**：
  - LibVLCSharp：<https://github.com/videolan/libvlcsharp>
  - VLC / LibVLC（LGPL 引擎与播放模块）：<https://www.videolan.org/legal.html> 与 <https://code.videolan.org/videolan/vlc>
  - libVLC 重新授权公告：<https://www.videolan.org/press/lgpl-libvlc.html> 与 <https://www.videolan.org/press/lgpl-modules.html>

---

## 6. 后端矩阵与平台支持

本项目以 `IMediaPlayerFactory`（回退中间件）统一编排多种播放后端，运行时按 DI 注册顺序自动回退：

| 后端 | 授权 | 当前可用平台 | 说明 |
| --- | --- | --- | --- |
| FFmpeg | LGPL 2.1+（共享构建） | Windows（已验证）/ Linux（验证中） | 主后端，经 `FFmpeg.AutoGen` 动态链接 |
| LibVLC / VLC | LGPL 2.1+（引擎+播放模块） | Windows（已验证）/ Linux（验证中） | 备用后端，经 `LibVLCSharp` 动态链接 |
| Media Foundation | Windows 操作系统内置组件 | Windows only | 由系统提供，无需随产品分发独立第三方授权 |

所有第三方后端均仅以**动态链接**方式使用，用户可在不重编译本项目的前提下替换/升级对应共享库。
macOS / iOS / Android 平台的后端支持在路线图内，尚未验证；待启用时再补充对应平台的原生库分发与合规说明。
