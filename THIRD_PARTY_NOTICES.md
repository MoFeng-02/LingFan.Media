# 第三方组件与许可证声明（Third-Party Notices）

本仓库（**LingFan.Media**，以 Apache-2.0 许可证分发）是 .NET 平台的跨平台媒体基础设施。当前**首要验证目标为 Windows**；**Linux** 通过 **FFmpeg** 与 **LibVLC（VLC）**
两个跨平台后端提供播放（验证进行中）；**macOS / iOS / Android** 在路线图内、尚未验证。在上述已验证/在验证的
平台上均可经 **FFmpeg** 或 **VLC（LibVLC）** 提供媒体解封装与解码能力（二者均为可选的播放后端，由回退中间件按
注册顺序自动选择）；此外在 **Windows** 上还支持 **Media Foundation**（Windows 操作系统内置组件，无需单独分发的
第三方授权）。为遵守各自的 **LGPL**，本项目**仅以动态链接方式**使用 FFmpeg / LibVLC 的**原生共享库**（Windows 为 `.dll`、
Linux 为 `.so`；其余平台若未来启用，则为对应平台的共享库形态），**绝不**将它们的源码或静态库合并进本项目的任何
程序集。

> **托管绑定层需单独判断**：上述结论针对的是**原生共享库**。位于 .NET 侧的托管绑定程序集另有各自的许可证——
> `FFmpeg` 的托管绑定层为本项目**自有自写 P/Invoke（Apache-2.0）**，无第三方程序集（无 copyleft）；而 VLC 后端所用的 `VLCNative` 绑定亦为本项目**自有代码、以 Apache-2.0 授权**
> （自写 P/Invoke，零第三方 LGPL 托管依赖）。因此两个后端的托管绑定层在 **NativeAOT 发布**下均不产生附加 copyleft 义务；
> 受 LGPL 约束的仅为运行时动态加载的 `libvlc` 原生共享库，详见 [第 6 节](#6-nativeaot-发布与托管绑定层)。

---

## 1. FFmpeg（LGPL 2.1 或更高版本）

- **用途**：媒体解封装（`libavformat`）、解码（`libavcodec`）、缩放/像素格式转换
  （`libswscale`）、重采样（`libswresample`）、工具（`libavutil`）。
- **许可证**：LGPL 2.1+（本仓库使用的构建为 **LGPL 共享构建**，未启用 `--enable-gpl` /
  `--enable-nonfree`，不包含任何 GPL 专属编解码器）。
- **动态链接说明**：本项目通过**自写原生绑定**（[LibraryImport] + `NativeLibrary` 版本自适应加载）在**运行时**动态加载
  FFmpeg 共享库（Windows 为 `avcodec-*.dll` 等，Linux 为 `libavcodec.so*` 等）。FFmpeg 的 **LGPL 共享构建
  在 Windows 与 Linux 上均可用**：Windows 采用 BtbN 的 `lgpl-shared` 变体（见下）；Linux 采用对应的
  `lgpl-shared` 变体（如 BtbN 提供的 `ffmpeg-master-latest-linux64-lgpl-shared`）。原生共享库不随本项目
  源码分发，而是由宿主/测试程序在部署时放置于可搜索路径（本仓库 `ThirdParty/ffmpeg/` 仅作本地开发用，已被
  `.gitignore` 忽略）。
- **版本覆盖（ABI）**：本项目自写绑定**覆盖 FFmpeg 4.x–9.0**（avutil 主版本 56–61），加载器按带版本号主版本成组探测
  （如 `avutil-61.dll` / `libavutil.so.61` / `libavutil.61.dylib`），加载后调用 `avutil_version()` 做版本门禁。
  推荐使用与最新兼容的 **FFmpeg 8.x / 9.0 LGPL 共享构建**（BtbN `lgpl-shared`）；若使用较旧 4.x–7.x 构建，须确保同一发布内各组件主版本一致。
- **可替换性（LGPL 要求）**：原生共享库的路径通过 `FFmpegOptions.FFmpegLibraryPath`
  显式配置（默认指向应用程序基目录）。用户可在不重新编译本项目的前提下，
  替换/升级这些共享库（例如修复安全漏洞或切换构建）。
- **获取源码与许可证文本**：FFmpeg 源码与 LGPL 许可证文本见 <https://ffmpeg.org/legal.html>（含 LGPL
  合规清单）。本仓库使用的二进制来自 **BtbN FFmpeg Builds** 的 `lgpl-shared` 变体
  （<https://github.com/BtbN/FFmpeg-Builds/releases>）。

> ⚠️ 注意：gyan.dev 提供的 `ffmpeg-*-full_build-shared` 与 `essentials_build`（静态）构建
> 含 `--enable-gpl` / `--enable-static`，**不符合**本项目的 LGPL 动态链接要求，请勿用于本项目。

---

## 2. FFmpeg 托管绑定层（本项目自有，Apache-2.0）

- **用途**：.NET 与 FFmpeg 原生 API 之间的绑定（本项目**自写** P/Invoke，基于 `[LibraryImport]` 源生成，零第三方托管包）。
- **许可证**：Apache-2.0（与本项目同源，自有代码）。
- **绑定形态**：不使用任何第三方自动生成绑定（如 `FFmpeg.AutoGen`），亦无 `PackageReference` 依赖；所有 P/Invoke 声明均为项目自有代码，
  经 `NativeLibrary.SetDllImportResolver` 把 `[LibraryImport("avutil")]` 等解析到已加载的原生句柄，与 LGPL 动态链接要求一致。
- **NativeAOT 友好性**：Apache-2.0 不含 copyleft 条款，该绑定层即便被 AOT 静态编译进单一可执行文件，
  也**不产生任何附加分发义务**；同时真正受 LGPL 约束的 FFmpeg 原生共享库始终为运行时动态加载、可独立替换。
  故 **FFmpeg 后端在 NativeAOT 下是完全干净的路径**。

---

## 3. 本项目（LingFan.Media）

- **许可证**：Apache-2.0。
- **组合方式**：本项目自身的源码以 Apache-2.0 许可证发布；对 FFmpeg / LibVLC 的**原生共享库**仅作**动态链接**调用，
  未修改其源码、未将其合并进本项目程序集、未采用静态链接。
- **本项目自身的分发形态**：本项目在 NuGet 上分发的是**中间语言（IL）程序集**；FFmpeg 的托管绑定层**为本项目自有源代码**（Apache-2.0），随本项目一同编译分发，并非第三方托管程序集，亦无 `PackageReference` 依赖。VLC 后端的 `VLCNative` 绑定为本项目
  **自有源代码（Apache-2.0）**，随本项目一同编译分发，并非第三方托管程序集。因此就本项目自身的分发行为而言，
  不存在对任何 LGPL 托管层的静态链接。是否触发 LGPL 静态链接义务，取决于**下游最终应用的发布方式**
  （详见第 6 节；当前 VLC 后端已在 AOT 下无此类义务）。
- **授权性质声明（重要）**：由于 FFmpeg 与 **libvlc（LibVLC）原生共享库**均以 **LGPL 2.1+** 授权、且本项目仅以动态链接方式使用，
  根据 LGPL 2.1 第 6 条，**本项目的自有代码不构成对 LGPL 库的派生作品**，因此本项目的许可证
  **不会因为依赖这些 LGPL 后端而改变**——LingFan.Media 的源码与程序集始终以 **Apache-2.0** 授权，
  保持纯粹。LGPL 所产生的义务仅附着于**被分发的 LGPL 原生二进制本身**（须随产品提供许可证文本、
  源码获取途径、并保证可被替换/重链），不影响调用方（本项目）的 Apache-2.0 授权。
- **保持 Apache-2.0 纯粹性的前提条件（须持续满足）**：
  1. 始终保持**动态链接**：不得改为静态合并 FFmpeg / libvlc 的原生库进本项目程序集；VLC 后端的托管绑定
     `VLCNative` 为本项目自有 Apache-2.0 代码（已替代原先的 `LibVLCSharp` 第三方 LGPL 绑定），不存在第三方
     LGPL 托管层义务，亦不得将其许可证改为 LGPL 或并入他方程序集；
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
| 动态链接（非静态合并） | ✅ 自写 FF 绑定（`NativeLibrary` 版本自适应加载）运行时动态加载共享库 |
| 不引入 GPL 代码路径 | ✅ 仅用 LGPL 共享构建；wrapper 只调 FFmpeg 公共 LGPL API |
| 允许用户替换/升级共享库 | ✅ `FFmpegOptions.FFmpegLibraryPath` 可配置；共享库不内嵌 |
| 提供许可证与署名 | ✅ 本文件 |
| 不修改 FFmpeg / LibVLC 源码 | ✅ 仅调用，未 vendoring 或修改 |
| 允许用户替换/升级 LibVLC | ✅ 原生共享库由 `VideoLAN.LibVLC.Windows` 按 Windows RID 提供、不入库、可替换 |
| 本项目许可证不被 LGPL 传染 | ✅ 仅动态链接、不改源码、不合并；自有代码始终 Apache-2.0（见第 3 节） |
| 托管绑定层独立 / 自有 | ✅ FFmpeg 绑定为本项目自有代码（无 `PackageReference` 第三方托管依赖）；`VLCNative` 为本项目自有 Apache-2.0 代码，二者均未 ILMerge / ILRepack；NuGet 分发 IL 程序集 |
| NativeAOT 静态链接情形 | ✅ VLC 后端的 `VLCNative` 绑定为本项目自有 Apache-2.0 代码，AOT 下无 copyleft 义务；`libvlc` 原生库始终运行时动态加载、不受 AOT 影响；见第 6 节 |

---

## 5. LibVLC / VLC（LGPL 2.1+）

- **用途**：可选媒体播放后端（解封装 / 解码 / 输出），在 FFmpeg 后端不可用或失败时由回退中间件自动切换；
  **当前在 Windows 上可用**，Linux 上的 LibVLC 后端随 FFmpeg 一同在验证中。在 .NET 体系下统一通过
  **`VLCNative`** 绑定（Apache-2.0 自有代码、P/Invoke）动态加载 `libVLC`。
- **许可证**：**libVLC 引擎（`libvlc` + `libvlccore`）以及绝大多数播放相关模块（协议、解封装、解码、滤镜、
  输出）均为 LGPL 2.1+**（VideoLAN 官方公告：引擎于 2011–2012 年从 GPL 重新授权为 LGPL 2.1+；随后绝大多数
  播放模块也完成同样重新授权，见 <https://www.videolan.org/press/lgpl-modules.html> 与
  <https://www.videolan.org/press/lgpl-libvlc.html>）。因此本项目以 libVLC 作为播放后端整体处于 LGPL 授权
  之下，可按 LGPL 条款嵌入到非 GPL（含专有）应用中。
- **动态链接说明（当前范围）**：本项目经由 `VLCNative`（Apache-2.0 自有 P/Invoke 绑定）在**运行时**动态加载 libVLC 共享库——
  Windows 为 `libvlc.dll` / `libvlccore.dll`。原生二进制当前由 `VideoLAN.LibVLC.Windows` NuGet 提供
  （Windows RID），部署时落在输出目录并由 `.gitignore` 忽略（不随源码提交、不内嵌）。**若未来启用 macOS /
  iOS / Android**，将改用具对应 RID 的 `VideoLAN.LibVLC.*` 包，但本项目目前仅验证了 Windows 路径。
- **可替换性（LGPL 要求）**：用户可在不重新编译本项目的前提下，替换这些共享库（例如升级 VLC 或修复安全漏洞）。
- **关于其余少数 GPL 模块**：VLC 中仍有**少数与播放无关的模块**保持 GPLv2（主要为流媒体/转码模块、界面模块，
  以及 DVD 相关的 libdvdcss）。本项目**仅将 libVLC 用于本地播放**，不会加载这些非播放模块，其 GPL 条款不影响
  本项目的授权，本仓库亦不随产品再分发这些模块。如未来启用转码/流媒体能力，再另行评估。
- **获取源码与许可证文本**：
  - VLC / LibVLC（LGPL 引擎与播放模块）：<https://www.videolan.org/legal.html> 与 <https://code.videolan.org/videolan/vlc>
  - libVLC 重新授权公告：<https://www.videolan.org/press/lgpl-libvlc.html> 与 <https://www.videolan.org/press/lgpl-modules.html>

---

## 6. NativeAOT 发布与托管绑定层

本项目自身 **100% 兼容 NativeAOT**。但「AOT 兼容性」与「AOT 发布后的授权义务」是两个彼此独立的问题，
本节说明后者，供下游在选择发布方式时评估。

### 6.1 NativeAOT 在授权意义上等同于静态链接

NativeAOT 由 ILC 将**全部托管程序集**（含所有 NuGet 依赖）提前编译为机器码，并链接为**单一原生可执行文件**。
其结果是：被引用的托管库不再以独立 `.dll` 形式存在，终端用户**无法替换其中任何单个托管程序集**。
就 LGPL 而言，这与传统意义上的**静态链接**性质一致，LGPL 2.1 第 6 条 (b) 款所依赖的「共享库机制」不再成立。

> **这不会改变任何一方的许可证。** LGPL 的 copyleft 不会传染至调用方代码——LingFan.Media 与下游应用的
> 自有代码仍各自保持原有许可证。此处产生的是**分发义务**（须使接受者具备替换该库并重新链接的能力），
> 而非许可证变更。

### 6.2 各后端在 NativeAOT 下的影响

| 后端 | 托管绑定层 | 绑定层许可证 | AOT 静态链接后 |
| --- | --- | --- | --- |
| **Media Foundation** | 本项目自有 P/Invoke | Apache-2.0 | ✅ 无第三方义务（系统内置组件） |
| **FFmpeg** | 自写 P/Invoke（[LibraryImport]，零托管包） | 原生 LGPL 2.1+（运行时动态链接）；托管层 Apache-2.0 自有 | ✅ 无 copyleft，无附加义务 |
| **VLC** | `VLCNative`（本项目自有，Apache-2.0） | **Apache-2.0** | ✅ 无 copyleft，无附加义务（原生 libvlc 始终动态链接） |

三者的**原生**共享库（`avcodec-*.dll` / `libvlc.dll` 等）在任何发布模式下都是运行时动态加载、可独立替换，
**不受 AOT 影响**。受影响的仅是**托管绑定层**这一层。

### 6.3 VLC 后端的 NativeAOT 立场（已根治国产绑定）

自 VLC 后端改用本项目自写的 Apache-2.0 P/Invoke 绑定 `VLCNative`（替代原先的 `LibVLCSharp` 第三方 LGPL 托管绑定）后，
**VLC 后端在 NativeAOT 下已无任何附加 copyleft 义务**：

- 托管绑定层 `VLCNative` 为本项目自有 Apache-2.0 代码，与 FFmpeg 自写绑定（Apache-2.0）一样，AOT 静态编入单一可执行文件时不产生任何 copyleft 分发义务；
- 受 LGPL 2.1 约束的 `libvlc` 原生共享库始终在运行时由 `VLCNative` 动态加载、可独立替换，**不受 AOT 影响**，始终满足 LGPL 2.1 第 6 条 (b) 款的共享库条件。

因此「FFmpeg / VLC / Media Foundation」三个后端在 NativeAOT 发布下的授权影响一致：**均无第三方 copyleft 义务**。下游只需按常规 LGPL 要求随产品附带 libvlc 的许可证文本与源码获取途径（见第 5 节）。

> 若未来引入**其他**第三方 LGPL 托管绑定（非本项目自有代码），则须重新按本节早期评估其 AOT 影响。

### 6.4 本项目的立场

本项目在 NuGet 上分发的是 **IL 程序集**，且未合并任何第三方程序集，**其自身不构成静态链接、不触发上述义务**。
就当前 VLC 后端（Apache-2.0 自有绑定 `VLCNative` + 运行时动态链接的 `libvlc`）而言，**不存在** LibVLCSharp 时期的 AOT 托管层义务；受 LGPL 约束的仅为原生 `libvlc` 始终承担的动态链接义务（提供许可证文本等，见第 5 节）。
本节的目的是提前明确这一边界，避免下游在不知情的情况下产生合规缺口。

> 本节为工程层面的合规说明，不构成法律意见。涉及商业分发时，建议由贵方法务或专业律师作最终判断。

---

## 7. 后端矩阵与平台支持

本项目以 `IMediaPlayerFactory`（回退中间件）统一编排多种播放后端，运行时按 DI 注册顺序自动回退：

| 后端 | 授权 | 当前可用平台 | 说明 |
| --- | --- | --- | --- |
| FFmpeg | LGPL 2.1+（共享构建） | Windows（已验证）/ Linux（验证中） | 主后端，经自写原生绑定动态链接 |
| LibVLC / VLC | 原生库 LGPL 2.1+（引擎+播放模块）；托管绑定 `VLCNative` Apache-2.0（自有） | Windows（已验证）/ Linux（验证中） | 备用后端，经自写 `VLCNative`（Apache-2.0）P/Invoke 动态链接 libvlc；NativeAOT 下无附加义务（见第 6 节） |
| Media Foundation | Windows 操作系统内置组件 | Windows only | 由系统提供，无需随产品分发独立第三方授权 |

所有第三方后端的**原生共享库**均仅以**动态链接**方式使用，用户可在不重编译本项目的前提下替换/升级对应共享库。
托管绑定层在常规（非 AOT）部署下同样是独立可替换的程序集；**NativeAOT 发布下的差异见第 6 节**。
macOS / iOS / Android 平台的后端支持在路线图内，尚未验证；待启用时再补充对应平台的原生库分发与合规说明。
