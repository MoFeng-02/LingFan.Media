using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProbeShared;

namespace FfmpegHeadfulPlaybackProbe;

// Program 的 DI 构建段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>构建 FFmpeg 后端/D3D11 渲染器/音频输出 DI 容器与播放器（含计数装饰器）。容器由调用方负责释放。</summary>
    private static (ServiceProvider Sp, IMediaPlayer Player, CountingVideoRendererFactory? Counting) BuildPlayer(
        bool verbose, bool useHw, bool doVideo, bool doAudio,
        bool enableCategory, bool exclusiveMode, bool pollingMode, int saveFrames, string saveDir)
    {
        var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "[HH:mm:ss.fff] "; })
            .SetMinimumLevel(verbose ? LogLevel.Trace : LogLevel.Information));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        var builder = services.AddLingFanMedia()
            .AddFFmpeg(o =>
            {
                o.FFmpegLibraryPath = AppContext.BaseDirectory;
                o.LogLevel = verbose ? 32 /* AV_LOG_DEBUG */ : 16 /* AV_LOG_ERROR */;
                // 两套测试的关键差异：默认软件解码(测试1)，--hw 走 D3D11VA 零拷贝(测试2)。
                o.HardwareAcceleration = useHw;
            });

        // —— 视频侧（同 MF 探针：手动装饰 D3D11RendererFactory 以精确统计 Present，并补回 IGpuDeviceContext）——
        CountingVideoRendererFactory? countingFactory = null;
        if (doVideo)
        {
            var d3d11Factory = new D3D11RendererFactory(loggerFactory);
            countingFactory = new CountingVideoRendererFactory(d3d11Factory, saveFrames, saveDir);
            builder.Services.AddSingleton<IVideoRendererFactory>(countingFactory);
            // 保住 IGpuDeviceContext：--hw 时 FFmpeg D3D11VA 取它作共享设备进行零拷贝；软解时它供上屏上传。
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
