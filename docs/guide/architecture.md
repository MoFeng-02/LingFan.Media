# 架构总览

LingFan.Media 采用单 `MediaGraph` + 能力协商 Sink 的架构。

## 核心分层

- **契约层（Abstractions）**：只放跨层契约，零外部引用
- **后端实现**：FFmpeg / VLC / MediaFoundation
- **渲染层**：D3D11 + Skia（V1 Windows only）

## 帧投递原语

唯一帧路由：`frame => _frameChannel.Emit`。Sink 订阅后只读借用，绝不 Dispose。

> 详细架构文档待补充。
