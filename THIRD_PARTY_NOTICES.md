# 第三方组件与许可证声明（Third-Party Notices）

本仓库（**LingFan.Media**，以 Apache2.0 许可证分发）是跨平台媒体基础设施层，目标平台为
**Windows / macOS / Android / iOS**（**Linux 不在「原生后端」目标内**：Linux 无标准第一方媒体 API，故不构建原生后端；但 FFmpeg / VLC 跨平台，仍可在 Linux 上提供播放，详见第 6 节「平台矩阵」）。在上述目标平台上均可通过 **FFmpeg** 或 **VLC（LibVLC）**
提供媒体解封装与解码能力（二者均为可选的播放后端，由回退中间件按注册顺序自动选择）；此外在 **Windows**
上还支持 **Media Foundation**（Windows 操作系统内置组件，无需单独分发的第三方授权）。为遵守各自的 **LGPL**，
本项目**仅以动态链接方式**使用 FFmpeg / LibVLC 的共享库（Windows 为 `.dll`、Android 为 `.so`、
macOS / iOS 为 `.dylib`；Linux 不在目标平台内，故不在此讨论），**绝不**将它们的源码或静态库合并进本项目的任何程序集。

---

## 1. FFmpeg（LGPL 2.1 或更高版本）

- **用途**：媒体解封装（`libavformat`）、解码（`libavcodec`）、缩放/像素格式转换
  （`libswscale`）、重采样（`libswresample`）、工具（`libavutil`）。
- **许可证**：LGPL 2.1+（本仓库使用的构建为 **LGPL 共享构建**，未启用 `--enable-gpl` /
  `--enable-nonfree`，不包含任何 GPL 专属编解码器）。
- **动态链接与跨平台说明**：本项目通过 `FFmpeg.AutoGen` 的 `DynamicallyLoaded` 绑定在**运行时**动态加载
  FFmpeg 共享库——Windows 为 `avcodec-*.dll` 等、Android 为 `libavcodec.so*` 等、
  macOS / iOS 为 `libavcodec.*.dylib` 等。FFmpeg 的 **LGPL 共享构建在全部目标平台可用**：
  Windows 采用 BtbN `lgpl-shared`（见下）；macOS / Android / iOS 采用各自平台对应的 LGPL 共享构建
  （如 FFmpegKit / MobileFFmpeg 或自构建的 `--enable-shared` 变体），均**不含** `--enable-gpl` /
  `--enable-nonfree`，不引入任何 GPL 专属编解码器。原生共享库不随本项目源码分发，而是由宿主/测试程序在部署时
  放置于可搜索路径（本仓库 `ThirdParty/ffmpeg/` 仅作本地开发用，已被 `.gitignore` 忽略）。
- **关于移动端链接形态（LGPL 合规要点）**：在 Android / iOS 上，若平台或应用商店要求以**静态库**形式链接
  FFmpeg（例如 iOS App 传统上以静态 framework 分发），LGPL 仍可通过「提供可重链接的目标文件 / 允许替换并重链」
  来满足（与本项目 Apache2.0 源码一同提供即构成可重链条件）。无论动态或静态链接，本项目均**不修改 FFmpeg
  源码、不将其合并进本项目程序集**，LGPL 义务（提供 FFmpeg 源码获取途径、允许替换/重链）始终成立。
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

- **许可证**：Apache2.0。
- **组合方式**：本项目自身的源码以 Apache2.0 许可证发布；对 FFmpeg / LibVLC 仅作**动态链接**调用，
  未修改其源码、未将其合并进本项目程序集、未采用静态链接。
- **授权性质声明（重要）**：由于 FFmpeg 与 LibVLC 均以 **LGPL 2.1+** 授权、且本项目仅以动态链接方式使用，
  根据 LGPL 2.1 第 6 条，**本项目的自有代码不构成对 LGPL 库的派生作品**，因此本项目的许可证
  **不会因为依赖这些 LGPL 后端而改变**——LingFan.Media 的源码与程序集始终以 **Apache2.0** 授权，
  保持纯粹。LGPL 所产生的义务仅附着于**被分发的 LGPL 原生二进制本身**（须随产品提供许可证文本、
  源码获取途径、并保证可被替换/重链），不影响调用方（本项目）的 Apache2.0 授权。
- **保持 Apache2.0 纯粹性的前提条件（须持续满足）**：
  1. 始终保持**动态链接**：不得改为静态合并 FFmpeg / LibVLC 进本项目程序集；
  2. 分发的原生二进制必须是**LGPL 共享构建**：FFmpeg 使用 `BtbN lgpl-shared`（不得改用 `gpl-shared`
     或自行以 `--enable-gpl`/`--enable-nonfree` 编译，否则会引入 GPL 义务）；VLC 仅加载 LGPL 的
     libvlc 核心与播放相关模块，不加载仅以 GPL 发布的流媒体/转码/界面/ DVD(libdvdcss) 等非播放模块；
  3. 随产品**附带本声明文件**（THIRD_PARTY_NOTICES），并提供 FFmpeg / LibVLC 的源码获取途径；
  4. 保证 LGPL 二进制的**可替换性（重链能力）**：桌面端通过可独立替换的共享库文件实现；
     **移动端（iOS / Android）说明**：受应用商店沙箱限制，终端用户无法在已安装应用内直接替换库，
     业界的 LGPL 合规做法是——随产品提供对应的 LGPL 库源码与可重链的目标文件（object files），
     使接受者**具备重链能力**即视为满足 LGPL 第 6 条；本项目在各平台均不修改这些库源码、不合并进
     程序集，故该义务始终成立。
- 本项目（Apache2.0）与 FFmpeg（LGPL 2.1+）/ LibVLC（LGPL 2.1+）的许可证互相兼容，组合分发合法。

---

## 4. 合规要点速查

| 要求 | 本项目做法 |
| --- | --- |
| 动态链接（非静态合并） | ✅ `FFmpeg.AutoGen.DynamicallyLoaded` 运行时 `LoadLibrary` 共享 DLL |
| 不引入 GPL 代码路径 | ✅ 仅用 LGPL 共享构建；wrapper 只调 FFmpeg 公共 LGPL API |
| 允许用户替换/升级 DLL | ✅ `FFmpegOptions.FFmpegLibraryPath` 可配置；DLL 不内嵌 |
| 提供许可证与署名 | ✅ 本文件 |
| 不修改 FFmpeg 源码 | ✅ 仅调用，未 vendoring 或修改 ffmpeg |
| 允许用户替换/升级 LibVLC | ✅ 原生共享库在 `libvlc/` 独立目录、由 NuGet 按 RID 提供、不入库、可替换 |
| 本项目许可证不被 LGPL 传染 | ✅ 仅动态链接、不改源码、不合并；自有代码始终 Apache2.0（见第 3 节） |

---

## 5. LibVLC / VLC（LGPL 2.1+）

- **用途**：可选媒体播放后端（解封装 / 解码 / 输出），在 FFmpeg 后端不可用或失败时由回退中间件自动切换；**跨 Windows / macOS / Android / iOS 可用**（Linux 无标准原生后端、故不将其列为原生后端目标平台，但 FFmpeg / LibVLC 跨平台仍可在 Linux 上播放）。在 .NET 体系下统一通过 `LibVLCSharp`（LGPLv2.1+，VideoLAN 官方跨平台 .NET/Mono 绑定，仓库元数据明确标注 `crossplatform xamarin ios android`）动态加载 `libVLC`；在 iOS 上也可经 `VLCKit` / `MobileVLCKit`（LGPLv2.1+）调用，在 Android 上经 `libvlcjni`（LGPLv2.1+）调用——**这些官方绑定与 libVLC 引擎本身均为 LGPL 2.1+**，故 libVLC 作为播放后端在移动端同样整体处于 LGPL 授权之下（VLC 官方站点亦声明 libVLC「Run on every platform, from desktop … to mobile (Android, iOS) … It is under the LGPL2.1 license」）。
- **许可证**：**libVLC 引擎（`libvlc` + `libvlccore`）以及绝大多数播放相关模块（协议、解封装、解码、滤镜、输出）均为 LGPL 2.1+**（VideoLAN 官方公告：引擎于 2011–2012 年从 GPL 重新授权为 LGPL 2.1+；随后绝大多数播放模块也完成同样重新授权，见 <https://www.videolan.org/press/lgpl-modules.html>）。因此本项目以 libVLC 作为播放后端整体处于 LGPL 授权之下，可按 LGPL 条款嵌入到非 GPL（含专有）应用中。
- **动态链接说明（跨平台）**：本项目经由 `LibVLCSharp`（LGPLv2.1+）在**运行时**动态加载 libVLC 共享库——Windows 为 `libvlc.dll` / `libvlccore.dll`、Android 为 `libvlc.so` / `libvlccore.so`、macOS / iOS 为 `libvlc.dylib` / `libvlccore.dylib`（iOS 以动态 framework 形式）。原生二进制由对应平台的 `VideoLAN.LibVLC.*` NuGet 提供（按 RID 分发 Windows / macOS / **Android / iOS**；Linux 无原生后端目标，故未列入，但 FFmpeg / LibVLC 跨平台库仍可于 Linux 运行），部署时落在 `bin/<tfm>/libvlc/<rid>/`，且已被 `.gitignore` 忽略（不随源码提交、不内嵌）。
- **可替换性（LGPL 要求）**：用户可在不重新编译本项目的前提下，替换这些共享库（例如升级 VLC 或修复安全漏洞）。
- **关于其余少数 GPL 模块**：VLC 中仍有**少数与播放无关的模块**保持 GPLv2（主要为流媒体/转码模块、界面模块，以及 DVD 相关的 libdvdcss）。本项目**仅将 libVLC 用于本地播放**，不会加载这些非播放模块，其 GPL 条款不影响本项目的授权，本仓库亦不随产品再分发这些模块。如未来启用转码/流媒体能力，再另行评估。
- **获取源码与许可证文本**：
  - LibVLCSharp：<https://github.com/videolan/libvlcsharp>
  - VLC / LibVLC（LGPL 引擎与播放模块）：<https://www.videolan.org/legal.html> 与 <https://code.videolan.org/videolan/vlc>
  - libVLC 重新授权公告：<https://www.videolan.org/press/lgpl-libvlc/> 与 <https://www.videolan.org/press/lgpl-modules.html>

---

## 6. 后端矩阵与平台支持

本项目以 `IMediaPlayerFactory`（回退中间件）统一编排多种播放后端，运行时按 DI 注册顺序自动回退：

| 后端 | 授权 | 平台 | 说明 |
| --- | --- | --- | --- |
| FFmpeg | LGPL 2.1+（共享构建） | Windows / macOS / **Android / iOS** | 跨目标平台主后端，经 `FFmpeg.AutoGen` 动态链接 |
| LibVLC / VLC | LGPL 2.1+（引擎+播放模块+移动端绑定） | Windows / macOS / **Android / iOS** | 备用后端，经 `LibVLCSharp` 动态链接（移动端亦对应经 VLCKit / libvlcjni 调用） |
| Media Foundation | Windows 操作系统内置组件 | Windows only | 由系统提供，无需随产品分发独立第三方授权 |

所有第三方后端均仅以**动态链接**方式使用，用户可在不重编译本项目的前提下替换/升级对应共享库。
