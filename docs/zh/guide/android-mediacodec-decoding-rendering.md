# Android MediaCodec 解码·渲染架构设计

> 状态：**草案（待多轮评审确认后开工）** · 适用范围：`LingFan.Media.Backends.MediaCodec` + `LingFan.Media.Avalonia`（Skia 软渲）
>
> 原则：**先用权威共识把方案定对，再动代码**，降低返工率，保证「有据可查」。

---

## 1. 背景与目标

Android 真机上视频播放出现两类独立问题：

| 设备 | 症状 | 阶段 |
|---|---|---|
| vivo X200s（**天玑** / `c2.mtk.avc.decoder`） | **无画面（零产帧）** | 解码后端 |
| vivo V1981A（**骁龙** / iQOO Neo3） | **有画面但整体偏绿** | 渲染（色彩） |

目标：让两套异构硬件均能正确出画，并建立 **跨厂商/新旧设备兼容、可回落** 的权威架构路径。

---

## 2. 根因分析（附证据）

### 2.1 天玑零产帧 —— ByteBuffer 模式拿到厂商私有 GPU-only 色彩格式

- 日志（X200s）显示走 `ByteBuffer` 模式输出 `YUV420P`，但 `getOutputBuffer` 只能取到 **Y**，U/V 读不出或布局非标准，导致无法构成完整帧。
- 权威结论：硬件解码器在 ByteBuffer 模式下输出的色彩格式是**厂商私有/GPU-only**（高通 `COLOR_QCOM_FormatYUV420*`、MTK 同类 `0x7FA30C06`），直接按私有布局解析「兼容性极差、不推荐」；正解是 **Surface 输出 + ImageReader/YUV_420_888**，经 `Image.getPlanes()`（或 `AHardwareBuffer`）拿到真实三平面。
  - 依据：Android 官方 MediaCodec 文档（原生视频应走 **Surface**，需访问已解码帧用 **ImageReader**）；xckevin《深入 Android MediaCodec 视频编解码全链路》；火山引擎（基于 StackExchange）。

### 2.2 骁龙偏绿 —— 色彩空间矩阵不匹配

- 代码现状：消费者 `WriteYuvToBgra` 用**固定 BT.601 full-range (JFIF)** LUT，且解码器 `ReadOutputFormat` **从不读取** `KEY_COLOR_STANDARD` / `KEY_COLOR_RANGE` / `KEY_COLOR_TRANSFER`。
- 日志（X200s）实测解码器上报：`raw.color.matrix=1`（=`COLOR_STANDARD_BT709`）、`raw.color.range=2`（=`COLOR_RANGE_LIMITED`）。
- 结论：内容为 **BT.709 Limited**，却用 **BT.601 Full** 矩阵转换 → 高饱和度错误（偏绿）。Android 同一内容在中低端设备可能上报 BT.601，故**必须按输出 format 动态选矩阵**。
  - 依据：libyuv `kYuvCoefficientsRgb`（UB/UG/VG/VR 系数×64）；ExoPlayer `MediaCodecVideoRenderer` 从 `getOutputFormat()` 读三键组合映射成转换矩阵。

---

## 3. 目标架构（权威共识）

```
解码器(硬件优先)
   └─ Surface 输出 ──► AImageReader(AHardwareBuffer/YUV_420_888)
                          ├─ [期望] GPU 零拷贝导入（AHB→Vulkan/GLES，默认关闭，见 §4 风险）
                          └─ [落地] CPU 平面提取 标准 YUV（跨厂商默认安全路径）
                                └─ VideoFrame（携带 ColorInfo：standard/range/transfer）
                                      └─ SkiaVideoRenderer → WriteYuvToBgra 按 ColorInfo 选矩阵 → BGRA 上屏
失败回落 ▼
   ByteBuffer + 软件解码器(c2.android.avc.decoder) 兜底（非工艺主路径，仅硬解不可用时）
```

要点：
1. **主路径 = Surface 输出**，天然规避厂商私有 ByteBuffer 格式，获得标准 YUV。
2. **色彩信息随帧透传**，渲染端用 libyuv 对应矩阵 + range 偏移，不再硬编码 BT.601-Full。
3. **保留回落**：GPU 导入关闭时走 CPU 平面提取；硬件解码器不可用时回落软件解码器。

---

## 4. 关键设计决策

### 4.1 取帧路径

- `DrainOutputSurface`（现有，已正确）：`dequeueOutputBuffer → releaseOutputBuffer(idx, render=1) → AImageReader_acquireNextImage`。
- `TryCreateFrameFromReader`（现有）：优先 `AllowGpuZeroCopyImport`（**默认 false**），否则 **CPU 平面提取**为标准 YUV。**保留既有 CPU 平面路径作为跨厂商默认**；GPU 零拷贝并入独立安全路径（见 §5 风险）。

### 4.2 色彩信息载体（需改动 Abstractions）

- `VideoFrame` 当前**无颜色字段**。新增轻量只读结构 `VideoColorInfo { ColorStandard Standard; ColorRange Range; ColorTransfer Transfer; }` 挂到 `VideoFrame.ColorInfo`（可空，未指定时渲染端回退 BT.601-Full，保持旧行为兼容）。
- `AndroidVideoDecoder.ReadOutputFormat` 改为同时读 `KEY_COLOR_STANDARD/RANGE/TRANSFER` 并填充 `VideoFrame.ColorInfo`。
- 渲染端 `WriteYuvToBgra` 依 `frame.ColorInfo` 选矩阵。libyuv 的 UV 系数已核实（`kYuvCoefficientsRgb`，整数系数×64）：

| ColorStandard | ColorRange | libyuv 系数(×64) UB / UG / VG / VR | Y 补偿 |
|---|---|---|---|
| BT.601 | Full (JPEG) | 113 / 22 / 46 / 90 | 无（Y 直接用） |
| BT.601 | Limited | 128 / 25 / 52 / 102 | Y−16，乘以 1.1644 |
| BT.709 | Limited | 128 / 14 / 34 / 115 | Y−16，乘以 1.1644 |
| BT.2020 | Limited | 128 / 12 / 42 / 107 | Y−16，乘以 1.1644 |

  **完整公式（limited 须补偿 Y，full 不补偿）**——令 `Y'=(Y−16)·1.1644`（limited）或 `Y'=Y`（full），`U'=U−128`、`V'=V−128`：
  - `R = Y' + (VR/64)·V'`
  - `G = Y' − (UG/64)·U' − (VG/64)·V'`
  - `B = Y' + (UB/64)·U'`（记 `U'=U−128`）
  - 依据：libyuv `YuvPixel`：`R=(Y−YB)·YG + V·VR`、`G=(Y−YB)·YG − U·UG − V·VG`、`B=(Y−YB)·YG + U·UB`，UV 入参已减 128；limited 时 `Y` 先做偏移与 219/255 缩放（`YG≈18997`、`YB=−1160`，×64 定点）。
  - 当前消费者 `WriteYuvToBgra` 的 LUT 即 BT.601-full 形式（`Rv=1.402`、`Gu=−0.3441`、`Gv=−0.7141`、`Bu=1.772`），与上面 BT.601-full 一致；切换到 BT.709-limited 时要把 Y 补偿 + 全部 UV 系数一起替换。

### 4.3 回落策略（权威）

- 硬件优先：`MediaCodecList.findDecoderForFormat` 优先硬件；失败/Error 态必须 `release()` 后**重建**，不可原地恢复。
- 软解兜底：`createByCodecName("c2.android.avc.decoder")`。
- 需要在「Surface 输出创建失败 / gralloc 拒绝用途组合」时优雅落回 ByteBuffer（当前已有该逻辑）。

---

## 5. 风险与约束

- **GPU 零拷贝导入（AHB→Vulkan）存在版权/硬件限制**：带 `CPU_*` usage 的 buffer 在 Vulkan 内**可能不可 `vkMapMemory`**；部分 buffer 无对应 `VkFormat` 只能走 `VkExternalFormatANDROID` 采样。本机曾出现进程级 `SIGBUS` → **默认关闭导入**，解码线程只走安全 CPU 平面路径；独立安全导入路径再启用。
- 彼时 `IMAGE.crop`、`stride`、`sliceHeight` 处理沿用 AOSP 语义（crop 右/下为开区间）。
- Skia 软渲为 Android 控件内唯一落地渲染器（GPU 渲染器因需 HWND 在控件内必失败）。

---

## 6. 变更清单（现状 → 目标）

| 文件 | 现状 | 目标 |
|---|---|---|
| `Abstractions/.../VideoFrame.cs` | 无颜色字段 | 新增 `VideoColorInfo ColorInfo` |
| `Abstractions/Enums/PixelFormat.cs` 旁（新增枚举） | — | `ColorStandard`/`ColorRange`/`ColorTransfer` 枚举 |
| `AndroidVideoDecoder.ReadOutputFormat` | 只读 `KEY_COLOR_FORMAT` | 追加读三键并填充 `ColorInfo`；主路径定为 Surface 输出 |
| `AndroidVideoDecoder.Initialize` | `EnableHardwareBufferZeroCopy && AllowGpuZeroCopyImport` 才建 reader | 保持；确认 Surface 主路径在硬件解码器可用时默认启用（不含 GPU 导入） |
| `SkiaVideoPresenter.WriteYuvToBgra` | 硬编码 BT.601-Full | 依 `ColorInfo` 选 libyuv 矩阵 + range 偏移 |
| `Backends.MediaCodec.MapCodecColorFormat` | 仅映射像素格式 | 视需要补充私有格式→标准 YUV 的识别 |

> 注：`KEY_COLOR_*` 三键为**可选**，部分解码器不上报；缺失时回退 BT.601-Full（现状行为）。

---

## 7. 验证计划（多轮）

1. **X200s（天玑）**：切 Surface 路径后应能出帧出画。
2. **X200s**：校验 `ColorInfo` 上报 BT.709-Limited → 颜色正常。
3. **V1981A（骁龙）**：颜色从绿恢复正常。
4. **软路由**：强制软解（`createByCodecName c2.android.avc.decoder`）仍出画。
5. **GPU 导入**：保持默认关闭，解码线程无 `SIGBUS`。
6. 回归桌面（Windows D3D11/FFmpeg）不受 `VideoFrame.ColorInfo` 新增字段影响。

---

## 8. 权威参考资料

- Android MediaCodec 官方文档（Surface vs ByteBuffer、ImageReader）：https://developer.android.com/reference/android/media/MediaCodec
- libyuv `kYuvCoefficientsRgb`：https://android.googlesource.com/platform/external/libyuv/+/refs/heads/main/source/row_common.cc
- MediaFormat `KEY_COLOR_STANDARD/KEY_COLOR_RANGE/KEY_COLOR_TRANSFER`：https://developer.android.com/reference/android/media/MediaFormat
- ExoPlayer `MediaCodecVideoRenderer.java`（色彩透传范式，本项目参考资料）。
- xckevin《深入 Android MediaCodec 视频编解码全链路》：https://xckevin.com/blog/2026-05-18-深入_android_mediacodec_视频编解码全链路_从_mediaextractor_解封装/
- Vulkan `VK_ANDROID_external_memory_android_hardware_buffer`：https://docs.vulkan.org/refpages/latest/source/VK_ANDROID_external_memory_android_hardware_buffer.html