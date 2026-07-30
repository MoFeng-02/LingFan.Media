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
    /// 注册无头（NoOp）音频输出工厂，使 MediaPlayer 在无 <c>VideoView</c> / 无音频设备 / CI 环境下运行。
    /// 替代 <c>AddWasapiOutput()</c>（WASAPI 需先 <see cref="IAudioOutput.InitializeAsync"/> 枚举设备，且要求真实音频端点），
    /// 与 <see cref="AddHeadlessRenderer"/> 对称（C-9.4：无头 = 无 GPU 设备 + 无音频设备）。
    /// 配合 <see cref="ProcessingFrameSink"/> 实现无头帧消费（无头 A）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>构建器（链式调用）。</returns>
    /// <remarks>此方法为同步配置（config 分类），无 I/O。</remarks>
    public static MediaBuilder AddHeadlessAudioOutput(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IAudioOutputFactory, NoOpAudioOutputFactory>();

        // 无头不打开音频设备：不注册 WASAPI。MediaPlayer 经 IMediaComponent 生命周期管理 NoOp 输出。
        // 依赖倒置保持：MediaPlayer 仅通过 IAudioOutputFactory 抽象解耦，NoOp 实现零外部引用。
        return builder;
    }
}
