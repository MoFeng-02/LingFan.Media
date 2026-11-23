using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VlcHeadfulPlaybackProbe;

// Program 的 DI 构建段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>构建 VLC 后端/D3D11 渲染器/音频输出 DI 容器与播放器（含计数装饰器）。容器由调用方负责释放。</summary>
    private static (ServiceProvider Sp, IMediaPlayer Player, CountingVideoRendererFactory? Counting) BuildPlayer(
        bool verbose, bool useHw, bool doVideo, bool doAudio,
        bool enableCategory, bool exclusiveMode, bool pollingMode, int saveFrames, string saveDir)
    {
        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        var builder = services.AddLingFanMedia()
            .AddVLCNative(o =>
            {
                // VLC 经 SetVideoCallbacks 内存捕获已解码帧，自身不依赖原生窗口。
                // Headless=true 会注入 --vout=dummy 禁止 VLC 自建窗口，视频帧经回调全部交给本管线，
                // 由下方 D3D11Renderer 的 Present Sink 上屏。
                // 区别于 --no-video（会停止解码），Headless 仅屏蔽 VLC 自有绘图。
                // --hw 仅让 VLC 内部走 D3D11VA，回调仍交付 CPU BGRA32，与 ffmpeg 的 D3D11VA 零拷贝不同。
                o.EnableHardwareDecoding = useHw;
                o.Headless = true;
            });

        // —— 视频侧：手动装饰 D3D11RendererFactory 统计 Present，并补回 IGpuDeviceContext ——
        // VLC 经回调交付 BGRA32 软件帧，D3D11Renderer 走 CopyResource 快路径上传上屏（非零拷贝）。
        CountingVideoRendererFactory? countingFactory = null;
        if (doVideo)
        {
            var d3d11Factory = new D3D11RendererFactory(loggerFactory);
            countingFactory = new CountingVideoRendererFactory(d3d11Factory, saveFrames, saveDir);
            builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
            // 保住 IGpuDeviceContext：VLC 帧为 BGRA32 CPU 内存，D3D11Renderer 用自己的共享设备上传纹理上屏。
            builder.Services.AddSingleton<IGpuDeviceContext>(sp => d3d11Factory.Context);
        }
        else
        {
            builder.AddHeadlessRenderer();
        }

        // —— 音频侧 ——
        if (doAudio)
            builder.AddWasapiOutput(o =>
            {
                o.EnableBackgroundCapableSession = enableCategory;
                o.ExclusiveMode = exclusiveMode;
                o.EventDrivenMode = !pollingMode;
            });
        else
            builder.AddSilentAudioOutput();

        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();
        return (sp, player, countingFactory);
    }
}
