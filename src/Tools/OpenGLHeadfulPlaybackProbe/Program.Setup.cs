using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.FFmpeg;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Backends.VLCNative;
using LingFan.Media.Consumers;
using LingFan.Media.Composition;
using LingFan.Media.Extensions;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Renderers.OpenGL;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenGLHeadfulPlaybackProbe;

// Program 的 DI 构建段（部分类，由 Program.cs 按职责拆分，编译期等价）。
internal static partial class Program
{
    /// <summary>构建后端/OpenGL 渲染器/音频输出 DI 容器与播放器（含计数装饰器）。容器由调用方负责释放。</summary>
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
        // 后端选择由 --backend 决定（mf/ffmpeg/vlc），三者均强制软件解码，使帧恒为 CPU SoftwareFrameResource，
        // OpenGL 渲染器走「CPU 软解帧 → GL 纹理上传 → 着色器上屏」路径。
        MediaBuilder builder = (backendArg switch
        {
            "ffmpeg" => services.AddLingFanMedia().AddFFmpeg(o =>
            {
                // --hw 时开启 D3D11VA 零拷贝：ffmpeg 产 NV12→RGBA 共享句柄，OpenGLGpuFrameProducer 导入为 GL 纹理零拷贝上屏；
                // 默认关闭硬件加速使帧恒为 CPU SoftwareFrameResource。
                o.FFmpegLibraryPath = AppContext.BaseDirectory;
                o.LogLevel = verbose ? 32 /* AV_LOG_DEBUG */ : 16 /* AV_LOG_ERROR */;
                o.HardwareAcceleration = hwMode;
            }),
            "vlc" => services.AddLingFanMedia().AddVLCNative(o =>
            {
                // VLC 经 SetVideoCallbacks 内存捕获已解码帧，自身不依赖原生窗口。
                // Headless=true 注入 --vout=dummy 禁止 VLC 自建窗口；--hw 时 EnableHardwareDecoding 注入 --avcodec-hw=any
                // （有头 D3D11VA 真硬解但仍回拷 CPU BGRA32，设计上恒非零拷贝），由下方 OpenGL 渲染器上传上屏。
                o.EnableHardwareDecoding = hwMode;
                o.Headless = true;
            }),
            _ => services.AddLingFanMedia().AddMediaFoundation(o =>
            {
                // --hw 时开启 DXVA（半硬解：硬解激活但 MFT 把纹理读回系统内存，非真零拷贝）；
                // 默认关闭硬件解码使帧恒为 CPU SoftwareFrameResource。
                o.EnableHardwareDecoding = hwMode;
                o.EnableDxva = hwMode;
            }),
        }).AddOpenGLRenderer();

        // —— 视频侧 ——
        if (doVideo)
        {
            // 复用 AddOpenGLRenderer() 已注册的 OpenGLRendererFactory DI 单例（含工厂级离屏 GL 设备上下文单例），
            // 以 CountingVideoRendererFactory 装饰器包裹注册为 IVideoRendererFactory。
            // 关键：生产者(IGpuFrameProducer)与 on-screen 渲染器必须共享【同一】OpenGLOffscreenDeviceContext
            // （GL 共享组所有者），零拷贝导入的 GL 纹理才对渲染器可见。若此处另 new 一个 OpenGLRendererFactory，
            // 会产生第二个离屏上下文 → (1) RegisterClassExW 同名类重复注册报 1410；(2) 两个 GL 共享组互不连通 → 黑屏。
            // 故此处经 DI 单例解析同一工厂，仅用装饰器加计数（countingFactory 引用在 BuildServiceProvider 后取出）。
            builder.Services.AddSingleton<IVideoRendererFactory>(sp =>
            {
                var glFactory = sp.GetRequiredService<OpenGLRendererFactory>();
                glFactory.ScaleMode = scaleMode;
                return new CountingVideoRendererFactory(glFactory, saveFrames, saveDir);
            });
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
        // countingFactory 引用：IVideoRendererFactory 经 DI 解析为「装饰器包裹的 OpenGLRendererFactory 单例」，
        // 与生产者(IGpuFrameProducer)共享同一 OpenGLOffscreenDeviceContext（GL 共享组）。doAudio 用例不建视频工厂。
        CountingVideoRendererFactory? countingFactory = doVideo
            ? (CountingVideoRendererFactory)sp.GetRequiredService<IVideoRendererFactory>()
            : null;
        var player = sp.GetRequiredService<IMediaPlayer>();
        return (sp, player, countingFactory);
    }
}
