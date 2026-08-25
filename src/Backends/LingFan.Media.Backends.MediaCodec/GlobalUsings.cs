// Global using directives for LingFan.Media.Backends.MediaCodec
// Abstractions 命名空间全局引入（契约层零外部引用，后端只依赖此）
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 经 Abstractions 传递）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton / TryAddEnumerable 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.DependencyInjection.Extensions;
// 托管互操作命名空间（仅用 MemoryMarshal 等托管内存互操作辅助；本后端禁止手写 P/Invoke，
// 不含 LibraryImport / Marshal / StructLayout / GCHandle / UnmanagedCallersOnly）
global using System.Runtime.InteropServices;
// 平台标注命名空间（OperatingSystem.IsAndroid / SupportedOSPlatform）
global using System.Runtime.Versioning;
// 代码分析抑制命名空间（DoesNotReturn / MaybeNull 等）
global using System.Diagnostics.CodeAnalysis;
