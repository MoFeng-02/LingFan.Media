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
| [`BackendStubs`](/api/extensions/backendstubs) | class | Backend stub DI extension methods (GStreamer / WebRTC) are not supported. |
| [`GStreamerOptions`](/api/extensions/backendstubs) | class | GStreamer backend configuration options (stubs, reserved for API compatibility). |
| [`MediaBuilderPipelineExtensions`](/api/extensions/mediabuilderpipelineextensions) | class | Pipeline post-processing configuration extension (combined root side) for `MediaBuilder`. |
| [`ModuleExtensions`](/api/extensions/moduleextensions) | class | Auxiliary extension methods for service registration within each submodule. |
| [`ServiceCollectionExtensions`](/api/extensions/servicecollectionextensions) | class | DI registration main portal. Provides `AddLingFanMedia(IServiceCollection, Action{MediaOptions})` extension method to register core media services. |
| [`WebRTCOptions`](/api/extensions/backendstubs) | class | WebRTC backend configuration options (stubs, reserved for API compatibility). |
