# 简介

LingFan.Media 是 LingFan 引擎的媒体子系统，对标 Unreal Media Framework / Unity VideoPlayer。

## 目标

- 基于 **.NET 10** 构建，目标 **AOT 100%**
- 提供可替换的播放后端（FFmpeg / VLC / MediaFoundation）
- 统一契约层与帧投递原语，后端无 per-backend 分支
- 无空域渲染：GPU 纹理直接交给平台合成器

## 下一步

- 阅读 [架构总览](/guide/architecture)
