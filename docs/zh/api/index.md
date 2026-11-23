---
title: API 参考
sources: ["docs/scripts/gen-api.mjs", "docs/api/manifest.json", "src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs"]
symbols: ["IMediaPlayer", "IMediaDemuxer", "IVideoRenderer", "IAudioOutput", "IFrameChannel"]
lang: zh
---

# API 参考

LingFan.Media 的公共 API：契约层（接口、模型、枚举、事件、异常）与 DI 组合根，含精确签名与用法说明。

## 分区

| 分区 | 内容 |
|---|---|
| [Abstractions](/zh/api/abstractions/) | 契约层：接口、模型、枚举、事件、异常。 |
| [Extensions](/zh/api/extensions/) | DI 组合根：`AddLingFanMedia()` 与模块注册扩展。 |

每个分区内按子域（Interfaces、Models、Enums、Events……）分组。

## 每页包含什么

- 类型声明（命名空间 + 签名）。
- 摘要与备注：前置条件、幂等、失败语义、取消行为、帧借用规则。
- 每个公共成员及其签名与文档。
- 指向 GitHub 源文件的链接。

提供英文与中文版本。
