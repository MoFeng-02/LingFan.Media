// Global using directives for LingFan.Media.Renderers.D3D11
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（ILogger<T> 通过 Abstractions 传递依赖可用）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
// Vortice 命名空间
global using Vortice.Direct3D11;
global using Vortice.DXGI;
global using Vortice.Direct3D;
global using Vortice.Mathematics;
// SafeHandle 和 Marshal 所在命名空间
global using System.Runtime.InteropServices;
// Extensions 命名空间（MediaBuilder）
global using LingFan.Media.Extensions;

// 整个 D3D11 渲染器项目为 Windows 专用（Vortice.Direct3D11 仅支持 Windows）
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
