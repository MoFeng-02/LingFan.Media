// Global using directives for LingFan.Media.Backends.Apple
// Abstractions 命名空间全局引入（契约层零外部引用，后端只依赖此）
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 经 Abstractions 传递）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton / TryAddEnumerable 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.DependencyInjection.Extensions;
// 原生互操作命名空间（LibraryImport / Marshal / StructLayout / GCHandle / UnmanagedCallersOnly）
global using System.Runtime.InteropServices;
// 平台标注命名空间（OperatingSystem.IsMacOS / IsIOS）
global using System.Runtime.Versioning;
// 代码分析抑制命名空间（DoesNotReturn / MaybeNull 等）
global using System.Diagnostics.CodeAnalysis;
// Apple 共享绑定层（全仓唯一 Apple objc/Core* 绑定源）
global using LingFan.Media.Apple.Shared;
