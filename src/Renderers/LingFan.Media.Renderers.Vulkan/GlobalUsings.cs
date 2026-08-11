// Global using directives for LingFan.Media.Renderers.Vulkan
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Logging 命名空间全局引入（csproj 已显式声明 Microsoft.Extensions.Logging.Abstractions）
global using Microsoft.Extensions.Logging;
// DI 命名空间全局引入（AddSingleton 扩展方法）
global using Microsoft.Extensions.DependencyInjection;
// Extensions 命名空间（MediaBuilder）
global using LingFan.Media.Extensions;
// Silk.NET Vulkan API
global using Silk.NET.Vulkan;
// Silk.NET 原生互操作（SilkMarshal 字符串分配/释放）
global using Silk.NET.Core.Native;
// Silk.NET Vulkan KHR 扩展（WSI: Surface/Swapchain/Present 等扩展方法）
global using Silk.NET.Vulkan.Extensions.KHR;
// 消除命名歧义：Vulkan 的 Semaphore/Buffer 优先于 System.Threading.Semaphore/System.Buffer
global using Semaphore = Silk.NET.Vulkan.Semaphore;
global using Buffer = Silk.NET.Vulkan.Buffer;
