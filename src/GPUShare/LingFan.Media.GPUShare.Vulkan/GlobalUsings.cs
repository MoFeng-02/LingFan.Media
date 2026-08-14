// Global using directives for LingFan.Media.GPUShare.Vulkan
// Abstractions 命名空间全局引入
global using LingFan.Media.Abstractions;
// Silk.NET Vulkan 纯数据结构（struct/enum/handle 类型，零反射、ABI 精确，仅作数据类型复用）
global using Silk.NET.Vulkan;
// 消除命名歧义：Vulkan 的 Semaphore/Buffer 优先于 System.Threading.Semaphore/System.Buffer
global using Semaphore = Silk.NET.Vulkan.Semaphore;
global using Buffer = Silk.NET.Vulkan.Buffer;
