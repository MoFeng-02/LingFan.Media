// Global using directives for LingFan.Media.Avalonia
// Abstractions 命名空间全局引入（IMediaPlayer / VideoFrame / VideoCodec 等）
global using LingFan.Media.Abstractions;
// 消除 IRenderTarget 歧义：Avalonia.Platform 也有 IRenderTarget
global using IRenderTarget = LingFan.Media.Abstractions.IRenderTarget;
// Logging 命名空间全局引入（ILogger<T> 通过 Abstractions 传递依赖可用）
global using Microsoft.Extensions.Logging;
