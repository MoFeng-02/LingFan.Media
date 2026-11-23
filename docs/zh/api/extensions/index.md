---
title: "组合根扩展"
generated: true
generator: "scripts/gen-api.mjs"
---
# 组合根扩展

组合根：依赖注入注册入口（AddLingFanMedia 与各模块 Add* 扩展）。

## 类型目录

| 类型 | 类别 | 摘要 |
| --- | --- | --- |
| [`BackendStubs`](/zh/api/extensions/backendstubs) | class | 不支持后端的桩 DI 扩展方法（GStreamer / WebRTC）。 |
| [`GStreamerOptions`](/zh/api/extensions/backendstubs) | class | GStreamer 后端配置选项（桩，保留供 API 兼容）。 |
| [`MediaBuilderPipelineExtensions`](/zh/api/extensions/mediabuilderpipelineextensions) | class | `MediaBuilder` 的管线后处理配置扩展（组合根侧）。 |
| [`ModuleExtensions`](/zh/api/extensions/moduleextensions) | class | 各子模块内部服务注册的辅助扩展方法。 |
| [`ServiceCollectionExtensions`](/zh/api/extensions/servicecollectionextensions) | class | DI 注册主入口。提供 `AddLingFanMedia` 扩展方法注册核心媒体服务。 |
| [`WebRTCOptions`](/zh/api/extensions/backendstubs) | class | WebRTC 后端配置选项（桩，保留供 API 兼容）。 |
