// Global using directives for LingFan.Media.Backends.VLCNative
// P/Invoke 必需
global using System.Runtime.InteropServices;
// [UnmanagedCallConv] 的 CallConvCdecl 标记类型所在命名空间
global using System.Runtime.CompilerServices;
// 基础设施契约命名空间（VLCNativeBackend 等后续文件依赖）
global using LingFan.Media.Abstractions;
// 日志（ILogger<T>）：后端/解复用器统一诊断输出
global using Microsoft.Extensions.Logging;
// VLC 两后端共享层：VLCOptions / Decoders / codec 映射（零 LibVLCSharp）
global using LingFan.Media.Backends.VLC.Abstractions;
// 帧交付信道（Channel<MediaPacket>）
global using System.Threading.Channels;
// DI 扩展（AddLazySupport 等）
global using LingFan.Media.Extensions;
