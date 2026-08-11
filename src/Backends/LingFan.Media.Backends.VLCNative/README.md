# LingFan.Media.Backends.VLCNative

## 定位
自写 **Apache-2.0** libvlc P/Invoke 绑定，用于替代 `LingFan.Media.Backends.VLC`（LibVLCSharp，LGPL-2.1）。

**动机**：NativeAOT 100% 会把全部托管程序集静态链入单一 exe，触发 LibVLCSharp 的 LGPL-2.1 义务（第 6 条(b)款共享库豁免失效，义务落到下游发布者）。自写 Apache-2.0 绑定从根源规避该义务。
库本体**不打包**原生 libvlc，由宿主从 `VideoLAN.LibVLC.Windows` 等 NuGet 引入，运行时由 `LibVlcInstance` 规则驱动 + 有界递归（深度 ≤6，按 OS 命名 + 含 libvlccore + plugins 评分 + 架构过滤）定位。

## 与原 LibVLCSharp 后端的差异
仅替换引擎：`LibVLC`（LGPL）→ `LibVlcInstance`（Apache-2.0）。
解码 / 渲染模型、命令行参数、轨道与帧捕获逻辑**逐字对齐**旧 `VLCBackend` / `VLCDemuxer`——本后端同样是**回调式 CPU 帧**模型（`libvlc_video_set_callbacks` 经 lock/unlock 拿 BGRA 内存）。

## ⚠️ 无头场景的 `get_buffer() failed` 是良性噪声（不是 bug）
`--avcodec-hw=any` 语义为「可用就硬解」，与旧库一致：

- **有头**（真实显示设备）：走 D3D11VA 真硬解 + 回拷 CPU BGRA，干净无错误。
- **无头**（`--vout=dummy`，无显示设备）：GPU 表面无法映射回 CPU → ffmpeg 报 `get_buffer() failed / no frame!`，但 VLC **自动回退软解**，帧仍以 CPU BGRA 交付（功能无损，仅日志有噪声）。

因此无头下出现的 `[h264] get_buffer() failed` 日志是 VLC 硬解回退的预期 chatter，**不要**为消除它改成 `--avcodec-hw=none`——那会剥夺有头真硬解能力，背离「与旧库对齐」初衷。零拷贝硬解由 MF / ffmpeg 主路径承担，VLC 后端在此架构仅作「开箱即用回退中间件」。

## 已知坑（已修复，留痕）
- `TrackType` 枚举须与 VLC 实际枚举一致：`audio=0 / video=1`。若误写为 `audio=1/video=2`，会导致轨道分类错位、codec 全落 `Unknown`。
- 帧路由唯一：`frame => _frameChannel.Emit`，不另开有头分支。
- 本地裸路径（如 `E:\x.mp4`）须走 `libvlc_media_new_path`；合法 MRL（`file:///...` / `http://`）才走 `libvlc_media_new_location`，否则 VLC 拒开报 `unable to open the MRL`。
