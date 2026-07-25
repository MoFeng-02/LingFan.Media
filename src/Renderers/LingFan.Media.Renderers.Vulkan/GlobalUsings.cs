// Global using directives for LingFan.Media.Renderers.Vulkan
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 通过 Abstractions 传递依赖可用）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
// SafeHandle 所在命名空间
global using System.Runtime.InteropServices;
// Extensions 命名空间（MediaBuilder）
global using LingFan.Media.Extensions;
