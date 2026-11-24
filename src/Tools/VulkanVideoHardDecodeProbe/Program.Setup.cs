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
using LingFan.Media.Backends.VulkanVideo;
using LingFan.Media.Consumers;
using LingFan.Media.Composition;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.Vulkan;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VulkanVideoHardDecodeProbe;

// Program 的 DI 构建段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>构建 FFmpeg D3D11VA/Vulkan 渲染器/音频输出 DI 容器与播放器（含计数装饰器）。容器由调用方负责释放。</summary>
    private static (ServiceProvider Sp, IMediaPlayer Player, CountingVideoRendererFactory? Counting) BuildPlayer(
        bool verbose, bool doVideo, bool doAudio,
        bool enableCategory, bool exclusiveMode, bool pollingMode, int saveFrames, string saveDir,
        AspectRatioMode scaleMode)
    {
        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        // AddVulkanVideo / AddFFmpeg / AddWasapiOutput 都是 MediaBuilder 的扩展，
        // 必须接在 builder 链上调用，不能对 ServiceCollection 直接调用。
        // 解码后端注册顺序即运行时回退优先级：VulkanVideo → FFmpeg。
        //    VulkanVideo 经 VK_KHR_video_decode_h264 硬解出 NV12 VkImage，与渲染器同设备 → 零拷贝上屏；
        //    仅 H.264，且要求 IGpuDeviceContext.VideoQueueFamilyIndex 有效（设备启用 video-decode 扩展）；
        //    不满足（非 H.264 / GPU 无 video-decode）时 Initialize 抛 NotSupportedException 由管线回落 FFmpeg。
        //    FFmpeg 作下家：解封装 + 音频解码 + H.264 软解回退（VulkanVideo 不可用时）。
        var builder = services.AddLingFanMedia().AddVulkanVideo().AddFFmpeg(options => options.FFmpegLibraryPath = AppContext.BaseDirectory);

        // —— 视频侧 ——
        CountingVideoRendererFactory? countingFactory = null;
        IGpuDeviceContext? gpuCtx = null;
        if (doVideo)
        {
            // 必须显式注册 Vulkan 工厂（装饰器包裹）。AddLingFanMedia / AddVulkanRenderer 默认不注册具体渲染器；
            // 缺失会在解析 IMediaPlayer 时抛 "No service for type 'IVideoRendererFactory'"。
            // 装饰器包裹真实 VulkanRendererFactory 以精确统计 Present 调用。
            // 关键：渲染器 Context 实现 IGpuDeviceContext，须注册为单例供 VulkanVideoDecoder 注入——
            // 两者共用同一 VkDevice + video-decode 队列族，零拷贝闭环成立。
            // （本探针手动注册，不调 AddVulkanRenderer()，以免与下方 IVideoRendererFactory 双重注册冲突。）
            var vulkanFactory = new VulkanRendererFactory(loggerFactory);
            vulkanFactory.ScaleMode = scaleMode;
            gpuCtx = vulkanFactory.Context;
            countingFactory = new CountingVideoRendererFactory(vulkanFactory, saveFrames, saveDir);
            builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
            builder.Services.AddSingleton<IGpuDeviceContext>(gpuCtx);
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

        // —— 硬解能力诊断（先于 OpenAsync）：直接读渲染器设备的 VideoQueueFamilyIndex ——
        if (doVideo && gpuCtx is not null)
        {
            bool videoDecodeCapable = gpuCtx.VideoQueueFamilyIndex != uint.MaxValue;
            Console.WriteLine($"[HEADFUL-VULKANVIDEO] 渲染器设备 VideoQueueFamilyIndex={gpuCtx.VideoQueueFamilyIndex} " +
                              $"video-decode 能力={(videoDecodeCapable ? "有效（硬解零拷贝可期）" : "无效（将回落 FFmpeg 软件解码）")}");
            Console.WriteLine($"[HEADFUL-VULKANVIDEO] 组合根：AddVulkanVideo() 先于 AddFFmpeg() → H.264 优先走 VK_KHR_video_decode_h264 硬解");
        }

        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();
        return (sp, player, countingFactory);
    }
}
