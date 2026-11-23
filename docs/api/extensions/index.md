---
title: "Extensions"
generated: true
generator: "scripts/gen-api.mjs"
---
# Extensions

The composition root: dependency-injection registration entry points (AddLingFanMedia and the per-module Add* extensions).

## Type catalog

| Type | Kind | Summary |
| --- | --- | --- |
| [`BackendStubs`](/api/extensions/backendstubs) | class | 不支持后端的桩 DI 扩展方法（GStreamer / WebRTC）。 |
| [`GStreamerOptions`](/api/extensions/backendstubs) | class | GStreamer 后端配置选项（桩，保留供 API 兼容）。 |
| [`MediaBuilderPipelineExtensions`](/api/extensions/mediabuilderpipelineextensions) | class | `MediaBuilder` 的管线后处理配置扩展（组合根侧）。 |
| [`ModuleExtensions`](/api/extensions/moduleextensions) | class | 各子模块内部服务注册的辅助扩展方法。 |
| [`ServiceCollectionExtensions`](/api/extensions/servicecollectionextensions) | class | DI 注册主入口。提供 `AddLingFanMedia(IServiceCollection, Action{MediaOptions})` 扩展方法注册核心媒体服务。 |
| [`WebRTCOptions`](/api/extensions/backendstubs) | class | WebRTC 后端配置选项（桩，保留供 API 兼容）。 |
