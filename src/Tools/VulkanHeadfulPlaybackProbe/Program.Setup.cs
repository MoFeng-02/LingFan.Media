using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.Vulkan;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace VulkanHeadfulPlaybackProbe;

// Program 的 DI 构建段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>构建后端/Vulkan 渲染器/音频输出 DI 容器与播放器（含计数装饰器与跨 GPU LUID 对齐）。容器由调用方负责释放。</summary>
    private static (ServiceProvider Sp, IMediaPlayer Player, CountingVideoRendererFactory? Counting) BuildPlayer(
        string? backendArg, bool hwMode, bool verbose, bool doVideo, bool doAudio,
        bool enableCategory, bool exclusiveMode, bool pollingMode, int saveFrames, string saveDir,
        AspectRatioMode scaleMode)
    {
        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        // AddHeadlessRenderer / AddWasapiOutput / AddSilentAudioOutput 都是 MediaBuilder 的扩展，
        // 必须接在 builder 链上调用，不能对 ServiceCollection 直接调用。
        // 后端选择由 --backend 决定（mf/ffmpeg/vlc）。默认（无 --hw）三者强制软件解码，使帧恒为 CPU SoftwareFrameResource，
        // Vulkan 渲染器走「CPU 软解帧 → VkImage 上传 → SwapChain 上屏」路径（GPU shader 做 YUV→RGB + 缩放）。
        // --hw 时：ffmpeg 经 D3D11VA 产 NV12 → 共享句柄由 VulkanGpuFrameProducer 导入零拷贝上屏；
        // VLC 恒回拷 CPU / MF 半 DXVA 不走零拷贝（仅作 GPU/CPU 计数参考，不判 PASS）。
        MediaBuilder builder = backendArg switch
        {
            "ffmpeg" => services.AddLingFanMedia().AddFFmpeg(o =>
            {
                // --hw 时开启 D3D11VA 硬解：产 NV12 → 共享句柄由 VulkanGpuFrameProducer 导入零拷贝上屏；
                // 无 --hw 则强制软解，帧恒为 CPU SoftwareFrameResource，Vulkan 走「VkImage 上传」路径上屏。
                o.FFmpegLibraryPath = AppContext.BaseDirectory;
                o.LogLevel = verbose ? 32 /* AV_LOG_DEBUG */ : 16 /* AV_LOG_ERROR */;
                o.HardwareAcceleration = hwMode;
            }),
            "vlc" => services.AddLingFanMedia().AddVLCNative(o =>
            {
                // VLC 经 SetVideoCallbacks 内存捕获已解码帧，自身不依赖原生窗口。
                // Headless=true 注入 --vout=dummy 禁止 VLC 自建窗口；即便 --hw 置 EnableHardwareDecoding=true，
                // 本仓 VLCVideoDecoder.IsHardwareAccelerated 硬编码 false、帧恒包成 CPU BGRA32（设计如此，非缺陷），
                // 故 Vulkan 仍走「VkImage 上传」路径（非零拷贝）——本探针仅作 GPU/CPU 计数参考，不判 PASS。
                o.EnableHardwareDecoding = hwMode;
                o.Headless = true;
            }),
            _ => services.AddLingFanMedia().AddMediaFoundation(o =>
            {
                // MF 为半 DXVA（硬解激活但 MFT 把纹理读回系统内存，GPU 零拷贝=0），非真零拷贝，故本探针不判 PASS。
                // --hw 时开启 EnableHardwareDecoding/EnableDxva 作对照，帧仍回拷 CPU；无 --hw 则纯软解。
                o.EnableHardwareDecoding = hwMode;
                o.EnableDxva = hwMode;
            }),
        };

        // —— 视频侧 ——
        CountingVideoRendererFactory? countingFactory = null;
        if (doVideo)
        {
            // 必须显式注册 Vulkan 工厂（装饰器包裹）。AddLingFanMedia 只注册契约骨架，
            // 不注册具体渲染器；缺失会在解析 IMediaPlayer 时抛 "No service for type 'IVideoRendererFactory'"。
            // 装饰器包裹真实 VulkanRendererFactory 以精确统计 Present 调用。
            // Vulkan 走「CPU 软解帧 → VkImage 上传 → SwapChain 上屏」，无法消费 D3D11 DXVA 纹理，
            // 故本探针恒强制软件解码（见下方 AddMediaFoundation 的 EnableHardwareDecoding/EnableDxva=false）。
            var vulkanFactory = new VulkanRendererFactory(loggerFactory);
            vulkanFactory.ScaleMode = scaleMode;
            // 零拷贝跨 GPU 对齐：ffmpeg 的 D3D11VA 共享纹理由「默认 D3D11 适配器」创建
            // （D3D11CreateDevice(DriverType.Hardware) 不指定适配器 → 默认适配器），而 Vulkan 渲染器
            // 默认「独显优先」。在 AMD 集显 + 3060 这类混合显卡上二者会落到不同 GPU，
            // 跨 GPU/厂商导入 D3D11 共享句柄会被驱动拒绝（vkAllocateMemory ErrorOutOfDeviceMemory）。
            // 取默认 D3D11 适配器 LUID 注入工厂，强制 Vulkan 选同一张卡使零拷贝成立。
            // 纯软解（无 --hw）无 D3D11 互操作，无需对齐，跳过以免改变纯 Vulkan 性能选择。
            if (hwMode)
            {
                byte[]? d3d11Luid = QueryDefaultD3D11AdapterLuid();
                if (d3d11Luid is not null)
                {
                    vulkanFactory.PreferredAdapterLuid = d3d11Luid;
                    Console.WriteLine("[HEADFUL-GPUALIGN] 已对齐 Vulkan 物理设备选择到 D3D11 默认适配器 LUID（零拷贝跨 GPU 修复）");
                }
                else
                {
                    Console.WriteLine("[HEADFUL-GPUALIGN] 取 D3D11 默认适配器 LUID 失败，零拷贝对齐跳过（纯独显机器通常无需对齐）");
                }
            }
            countingFactory = new CountingVideoRendererFactory(vulkanFactory, saveFrames, saveDir);
            builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
            // 保住 IGpuDeviceContext 注册：AddVulkanRenderer() 原本会注册它（cast 回 VulkanRendererFactory），
            // 手动装饰时必须补回，否则依赖它的层拿不到 GPU 能力上下文。
            builder.Services.AddSingleton<IGpuDeviceContext>(sp => vulkanFactory.Context);
            // 零拷贝关键：注册 IGpuFrameProducer，否则 ffmpeg 的 _gpuProducer 为 null → --hw 回落软解。
            // 这与 AddVulkanRenderer()/OpenGLExtensions 的注册同源（vulkanFactory.CreateFrameProducer()）。
            builder.Services.AddSingleton<IGpuFrameProducer>(sp => vulkanFactory.CreateFrameProducer());
        }
        else
        {
            // 纯音频用例：用 NoOp 视频渲染器隔离变量
            builder.AddHeadlessRenderer();
        }

        // —— 音频侧 ——
        if (doAudio)
            builder.AddWasapiOutput(o =>
            {
                o.EnableBackgroundCapableSession = enableCategory;
                o.ExclusiveMode = exclusiveMode;       // --exclusive 强制独占
                o.EventDrivenMode = !pollingMode;       // --polling 退回轮询模式
            });
        else
            builder.AddSilentAudioOutput();

        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();
        return (sp, player, countingFactory);
    }
}
