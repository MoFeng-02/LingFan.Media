// Global using directives for LingFan.Media.Outputs
// Abstractions 命名空间全局引入，避免每个文件重复声明
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 通过 Abstractions 传递依赖可用）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
// Extensions 命名空间（MediaBuilder）
global using LingFan.Media.Extensions;
// Marshal / COM interop
global using System.Runtime.InteropServices;
