// Global using directives for LingFan.Media.Backends.VLCNative
// P/Invoke 必需
global using System.Runtime.InteropServices;
// [UnmanagedCallConv] 的 CallConvCdecl 标记类型所在命名空间
global using System.Runtime.CompilerServices;
// 基础设施契约命名空间（VLCNativeBackend 等后续文件依赖）
global using LingFan.Media.Abstractions;
// 日志（ILogger<T>）：后端/解复用器统一诊断输出
global using Microsoft.Extensions.Logging;
// 帧交付信道（Channel<MediaPacket>）
global using System.Threading.Channels;
// DI 扩展（AddLazySupport 等）
global using LingFan.Media.Extensions;
// VLCNative 内部子命名空间：原生边界（Interop）、解封装（Demuxer）、直通解码器（Decoders），
// 以及根命名空间（VLCOptions / VlcCodecMapping），供根与跨子目录互引。
global using LingFan.Media.Backends.VLCNative;
global using LingFan.Media.Backends.VLCNative.Interop;
global using LingFan.Media.Backends.VLCNative.Demuxer;
global using LingFan.Media.Backends.VLCNative.Decoders;
