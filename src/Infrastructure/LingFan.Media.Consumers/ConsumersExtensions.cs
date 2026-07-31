using System;
using LingFan.Media.Abstractions;
using LingFan.Media.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LingFan.Media.Consumers;

/// <summary>
/// 无头消费模块 DI 注册扩展。
/// </summary>
public static class ConsumersExtensions
{
    /// <summary>
    /// 注册无头（NoOp）视频渲染器工厂，使 MediaPlayer 在无 <c>VideoView</c> / 无窗口 / 无 GPU 设备下运行。
    /// 替代 <c>AddD3D11Renderer()</c> / <c>AddVulkanRenderer()</c> 等具体渲染器注册（C-9.4）。
    /// 配合 <c>player.VideoFrameAvailable</c> 订阅或 <see cref="ProcessingFrameSink"/> 实现无头帧消费（无头 A）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <remarks>此方法为同步配置（config 分类），无 I/O。</remarks>
    public static MediaBuilder AddHeadlessRenderer(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IVideoRendererFactory, NoOpVideoRendererFactory>();

        // 无头不创建 GPU 设备上下文：不注册 IGpuDeviceContext（具体渲染器工厂才注册）。
        // 依赖倒置保持：MediaPlayer 仅通过 IVideoRendererFactory 抽象解耦，NoOp 实现零外部引用。
        return builder;
    }

    /// <summary>
    /// 注册静音（NoOp）音频输出工厂，使 MediaPlayer 在"不想出声"的场景下运行——例如无 <c>VideoView</c> / CI / 转码 / ML 分析。
    /// 与 <see cref="AddHeadlessRenderer"/> 对称（C-9.4）。注意：本方法只是把音频采样丢弃（NoOp），
    /// 与"宿主有没有音频设备"无关——无头进程若有真实音频设备、想让声音真的播放出来，应改用 <c>AddWasapiOutput()</c>
    /// （WASAPI 不依赖窗口，无头服务 / 控制台进程同样可用）。
    /// 配合 <see cref="ProcessingFrameSink"/> 实现无头帧消费（无头 A）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <remarks>此方法为同步配置（config 分类），无 I/O。</remarks>
    public static MediaBuilder AddSilentAudioOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IAudioOutputFactory, NoOpAudioOutputFactory>();

        // 静音模式不打开音频设备：不注册 WASAPI。MediaPlayer 经 IMediaComponent 生命周期管理 NoOp 输出。
        // 依赖倒置保持：MediaPlayer 仅通过 IAudioOutputFactory 抽象解耦，NoOp 实现零外部引用。
        return builder;
    }
}
