# Linux 部署与播放指南（FFmpeg 路线）

> 本页基于 **Ubuntu 24.04 LTS + Intel UHD 630（双卡机器）** 实测整理；其他发行版按同一清单类推。范围：FFmpeg 后端（VLC 后端在 Linux 已实现、待验证）；本地文件播放已端到端验证，网络源与流式播放尚未验证。

## 1. 运行时前置

| 依赖 | 说明 |
| --- | --- |
| **.NET 10 运行时** | 或使用自包含发布 |
| **FFmpeg 共享库** | 需要 n8.x 共享构建（`libavutil` / `libavcodec` / `libavformat` / `libswscale` / `libswresample` / `libavfilter` / `libavdevice`，含版本别名文件）。加载器按「**系统库优先 → 应用目录 → 递归扫描应用目录树（同组件取版本最高者）**」解析，也可用环境变量 `LF_FFMPEG_LIB` 显式指定库目录。加载后执行版本门禁与结构镜像自检，失败会给出明确错误 |
| **OpenAL**（音频） | `sudo apt install libopenal1`；缺库时自动回落静音输出（播放正常，无声） |
| 桌面会话音频服务 | 可闻输出需要 PulseAudio / PipeWire 在运行 |

::: warning 跨平台同步的坑
Windows ↔ Linux 同步工程时，FFmpeg 库的**版本符号链接会退化为文本文件**（如 `libavcodec.so.60` 变成几十字节的文本）。请在 Linux 侧用 `tar` 解包 FFmpeg 发布包得到真实 ELF 文件，或直接以真实文件副本部署。
:::

## 2. 纯软件解码播放（最小可用）

满足第 1 节后，**本地文件即可正常播放**：FFmpeg 软解 → 渲染器 CPU 上传 → 窗口呈现，音频经 OpenAL 输出。无显示环境（服务器/Xvfb）下同样可跑通解码与音频链路（无头探针验证）。

## 3. VAAPI 硬件解码（Intel 实测，可选）

硬件解码需要以下三项，缺一会在启动时回落软件解码（给出明确日志）：

1. **libva ≥ 2.21**。Ubuntu 24.04（noble）源内置 2.20，缺少 `vaMapBuffer2`（FFmpeg 的 VAAPI 蹦床需要）。升级方式：本仓库 `ThirdParty/va/` 提供 2.24.1 的 deb（`sudo dpkg -i ThirdParty/va/libva2_2.24.1-*.deb ThirdParty/va/libva-drm2_2.24.1-*.deb`），或改用 noble-updates 的 2.22+。加载器会优先探测系统 libva 是否满足（探测 `vaMapBuffer2`），满足则直接复用系统库。
2. **VA-API 驱动**：Intel 用 `sudo apt install intel-media-va-driver-non-free`（iHD）。
3. **设备访问权限**：当前用户需在 `render` / `video` 组（访问 `/dev/dri/renderD128`）。可用 `vainfo` 自检。

实现细节（遇到问题时参考）：FFmpeg 的 VAAPI 蹦床通过 `dlopen("libva.so.2")` 取符号，而 glibc 的 `dlopen` **不消费** ffmpeg 库自身的 `DT_RUNPATH`——因此加载器在加载 ffmpeg 前按绝对路径预载 libva（系统库满足版本时复用系统库，否则预载应用目录内的副本），该细节无需手工干预。

## 4. 窗口呈现

| 渲染器 | 前置 | 说明 |
| --- | --- | --- |
| **Vulkan**（推荐，零拷贝已验证） | `mesa-vulkan-drivers`（Intel ANV） | 解码出的 dma_buf 直接导入为 Vulkan 图像上屏；**渲染器自动选择与解码 GPU 同厂商的设备**（跨厂商导入不支持——tiling 布局含厂商私有语义） |
| **OpenGL** | `libegl1` / `libgl1` | 同一 dma_buf 导入路线；部分 Mesa 驱动对单平面 tiling 组合的支持面有限——不支持时自动回落 CPU 上传，**画面始终正确**（无零拷贝加速） |

- 呈现需要 X11 会话；Xvfb 等无头显示可跑通完整链路（无可见输出，适合自动化验证），查看画面请用真实显示或截图工具。
- 多卡机器：渲染器与解码 GPU 的厂商对齐自动完成；OpenGL 路线可用 `LINGFAN_EGL_DEVICE_DRM=renderD129` 显式指定 EGL 设备（`1` = 自动选择）。
- 所有导出路径在导出前执行 `vaSyncSurface` 栅栏，确保外部消费读到完整帧。

## 5. 验证与诊断

仓库 `src/Tools/` 提供三个 Linux 探针（`dotnet run --project ...`）：

| 探针 | 覆盖 | 关键判读 |
| --- | --- | --- |
| `LinuxHeadlessProbe` | 解码 + 音频（无呈现） | `[SYNC]` 快照、"判定" 汇总行 |
| `LinuxHeadlessVulkanProbe` | 解码 + Vulkan 呈现（`--hw` 启用 VAAPI 硬解 + 零拷贝） | "GPU 纹理帧" 计数、`[VULKAN-INIT]` 设备名 |
| `LinuxHeadlessOpenGlProbe` | 解码 + OpenGL 呈现（同上） | `[OPENGL-ZEROCOPY]` 导入结果与显示支持面 |

诊断日志含逐级踪迹（加载器、解码、同步、呈现），原生层失败时最后一行踪迹即定位点。

## 6. 已知限制

- **LibVLC 后端**在 Linux 已实现、待验证；网络源与流式播放尚未运行时验证。
- **跨厂商** dma_buf 导入不支持（tiling 布局含厂商私有语义）——渲染器已自动对齐解码 GPU。
- **Xvfb** 下软件渲染栈的能力有限（ES 上下文、导入支持面窄），适合链路验证；看画面请用真实显示。
- Windows 侧 MediaFoundation / D3D11 能力不适用于 Linux（平台 API 差异，属设计边界）。
