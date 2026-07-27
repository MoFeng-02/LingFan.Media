// Global using directives for LingFan.Media.Backends.MediaFoundation
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 通过 Abstractions 传递依赖可用）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
// COM 互操作命名空间
global using System.Runtime.InteropServices;
// 平台标注命名空间
global using System.Runtime.Versioning;
// 代码分析抑制命名空间
global using System.Diagnostics.CodeAnalysis;

// 程序集级平台标注：MediaFoundation 仅 Windows 可用
[assembly: SupportedOSPlatform("windows")]
