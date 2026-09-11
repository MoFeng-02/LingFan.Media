// Global using directives for LingFan.Media.Renderers.OpenGL
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 通过 Abstractions 传递依赖可用）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
// Extensions 命名空间（MediaBuilder）
global using LingFan.Media.Extensions;
// 共享渲染层（中性窗口句柄传输类型 X11WindowHandle 等，非契约层）
global using LingFan.Media.Renderers.Shared;
// EGL 互操作底座（EglNative / EglDmaBufImport，GPUShare.EGL 唯一真源）
global using LingFan.Media.GPUShare.EGL;
