// Global using directives for LingFan.Media.Renderers.Vulkan
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（csproj 已显式声明 Microsoft.Extensions.Logging.Abstractions）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
// Extensions 命名空间（MediaBuilder）
global using LingFan.Media.Extensions;
// Silk.NET Vulkan 纯数据结构（struct/enum/handle 类型，零反射、ABI 精确，仅作数据类型复用）
global using Silk.NET.Vulkan;
// 共享渲染层（中性窗口句柄传输类型 X11WindowHandle/WaylandWindowHandle 等，非契约层）
global using LingFan.Media.Renderers.Shared;
// 消除命名歧义：Vulkan 的 Semaphore/Buffer 优先于 System.Threading.Semaphore/System.Buffer
global using Semaphore = Silk.NET.Vulkan.Semaphore;
global using Buffer = Silk.NET.Vulkan.Buffer;
