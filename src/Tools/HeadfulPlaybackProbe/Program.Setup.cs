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
using LingFan.Media.Composition;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Renderers.OpenGL;
using LingFan.Media.Renderers.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProbeShared;

namespace HeadfulPlaybackProbe;

// Program 的 DI 构建段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>构建后端/渲染器/音频输出 DI 容器与播放器（含计数装饰器）。容器由调用方负责释放。</summary>
    private static (ServiceProvider Sp, IMediaPlayer Player, CountingVideoRendererFactory? Counting) BuildPlayer(
        string? backendArg, string rendererArg, bool hwMode, bool verbose, bool doVideo, bool doAudio,
        bool enableCategory, bool exclusiveMode, bool pollingMode, int saveFrames, string saveDir)
    {
        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        // AddHeadlessRenderer / AddWasapiOutput / AddSilentAudioOutput 都是 MediaBuilder 的扩展，
        // 必须接在 builder 链上调用，不能对 ServiceCollection 直接调用。
        // 后端注册：--backend 指定 → 只注册该后端；未指定（null）→ 全部注册（mf+ffmpeg+vlc 链式，
        // BackendFallbackMediaPlayerFactory 按序自动回退，选首个能打开媒体源的后端）。
        // 三者默认强制软件解码（帧恒为 CPU SoftwareFrameResource，渲染器上传上屏）；--hw 时按后端能力开启硬解：
        //   ffmpeg → D3D11VA 零拷贝（D3D11 直连 / Vulkan·OpenGL 经 D3D11 共享句柄导入，走 GPUShare.D3D11）；
        //   mf     → DXVA（半硬解，MFT 读回系统内存，非真零拷贝）；
        //   vlc    → 硬解回拷 CPU BGRA32（设计上恒非零拷贝）。
        MediaBuilder builder = backendArg switch
        {
            null => services.AddLingFanMedia()
                .AddMediaFoundation(o =>
                {
                    o.EnableHardwareDecoding = hwMode;
                    o.EnableDxva = hwMode;
                })
                .AddFFmpeg(o =>
                {
                    o.FFmpegLibraryPath = AppContext.BaseDirectory;
                    o.LogLevel = verbose ? 32 /* AV_LOG_DEBUG */ : 16 /* AV_LOG_ERROR */;
                    o.HardwareAcceleration = hwMode;
                })
                .AddVLCNative(o =>
                {
                    o.EnableHardwareDecoding = hwMode;
                    o.Headless = true;
                }),
            "ffmpeg" => services.AddLingFanMedia().AddFFmpeg(o =>
            {
                o.FFmpegLibraryPath = AppContext.BaseDirectory;
                o.LogLevel = verbose ? 32 /* AV_LOG_DEBUG */ : 16 /* AV_LOG_ERROR */;
                o.HardwareAcceleration = hwMode;
            }),
            "vlc" => services.AddLingFanMedia().AddVLCNative(o =>
            {
                o.EnableHardwareDecoding = hwMode;
                o.Headless = true;
            }),
            _ => services.AddLingFanMedia().AddMediaFoundation(o =>
            {
                o.EnableHardwareDecoding = hwMode;
                o.EnableDxva = hwMode;
            }),
        };

        // —— 视频侧：渲染器按 --renderer 注册（d3d11|vulkan|opengl，缺省 d3d11）——
        // 必须显式注册渲染器工厂（装饰器包裹）。AddLingFanMedia 只注册契约骨架，
        // 不注册具体渲染器；缺失会在解析 IMediaPlayer 时抛 "No service for type 'IVideoRendererFactory'"。
        // 装饰器包裹真实工厂以精确统计 Present 调用；IGpuDeviceContext 须与工厂同源注册
        // （D3D11/Vulkan 手动补回；OpenGL 由 AddOpenGLRenderer() 注册）。
        CountingVideoRendererFactory? countingFactory = null;
        if (doVideo)
        {
            if (rendererArg == "vulkan")
            {
                var vulkanFactory = new VulkanRendererFactory(loggerFactory);
                // Vulkan 渲染器默认独显优先（NVIDIA），与 ffmpeg 自有 D3D11 设备（NVIDIA 适配器优先）
                // 同 GPU，共享句柄导入成立；不设 AlignToD3D11DefaultAdapter（那是对齐核显，会破坏零拷贝）。
                countingFactory = new CountingVideoRendererFactory(vulkanFactory, saveFrames, saveDir);
                builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
                builder.Services.AddSingleton<IGpuDeviceContext>(sp => vulkanFactory.Context);
                // 零拷贝关键：注册 IGpuFrameProducer，否则 ffmpeg 的 _gpuProducer 为 null → --hw 回落软解。
                builder.Services.AddSingleton<IGpuFrameProducer>(sp => vulkanFactory.CreateFrameProducer());
            }
            else if (rendererArg == "opengl")
            {
                // OpenGL：复用 AddOpenGLRenderer() 已注册的 OpenGLRendererFactory DI 单例
                // （含工厂级离屏 GL 设备上下文 + GL 共享组，零拷贝导入的 GL 纹理才对其可见；
                // 若另 new 一个工厂，共享组不一致会导致导入纹理对渲染器不可见）。
                builder.AddOpenGLRenderer();
                builder.Services.AddSingleton<IVideoRendererFactory>(sp =>
                    new CountingVideoRendererFactory(
                        sp.GetRequiredService<OpenGLRendererFactory>(), saveFrames, saveDir));
            }
            else
            {
                var d3d11Factory = new D3D11RendererFactory(loggerFactory);
                countingFactory = new CountingVideoRendererFactory(d3d11Factory, saveFrames, saveDir);
                builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
                builder.Services.AddSingleton<IGpuDeviceContext>(sp => d3d11Factory.Context);
            }
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
        // OpenGL 分支的计数装饰器实例经 DI 惰性创建（工厂依赖容器解析 OpenGLRendererFactory），
        // 须在容器构建后取回同一实例引用供下方 Attach/Present 计数（其余分支注册时已持有）。
        countingFactory ??= sp.GetService<IVideoRendererFactory>() as CountingVideoRendererFactory;
        var player = sp.GetRequiredService<IMediaPlayer>();
        return (sp, player, countingFactory);
    }
}
