// Global using directives for LingFan.Media.Backends.FFmpeg
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 通过 Abstractions 传递依赖可用）
global using Microsoft.Extensions.Logging;
// FFmpeg 自绑定命名空间全局引入（替代 FFmpeg.AutoGen）
global using LingFan.Media.Backends.FFmpeg.Interop;
// SafeHandle 和 Marshal 所在命名空间
global using System.Runtime.InteropServices;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
