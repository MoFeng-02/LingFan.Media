using System.Threading;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// 无头帧消费 Sink 端到端回归（C-9.5）。
/// 真实 MF 解码 → <see cref="ProcessingFrameSink"/> 经 <c>videoFrameSink</c> 路由消费，
/// 断言帧数与帧信封格式，并验证无头不触发渲染（NoOp 渲染器 <c>Present</c> 为 no-op）。
/// </summary>
/// <remarks>
/// ⚠️ <b>须禁用沙盒运行</b>：沙盒隔离边界会切断 MediaFoundation 等真实宿主系统 API（"楚门的世界"），
/// 导致解码无法真实进行；且 <c>dotnet test</c> 的 test host 多进程模型在沙盒内会卡死（环境限制）。
/// 仅 Windows 且系统已注册 H264 解码 MFT（Windows 10/11 默认）。
/// 容器注册：<c>AddLingFanMedia().AddMediaFoundation().AddHeadlessRenderer().AddHeadlessAudioOutput()</c>——
/// <c>AddHeadlessRenderer</c> 注册 NoOp 渲染器工厂，替代 D3D11/Vulkan，使播放器无 GPU 设备运行；
/// <c>AddHeadlessAudioOutput</c> 注册 NoOp 音频输出，按真实节奏节流以驱动主时钟（无音频硬件依赖）。
/// </remarks>
[Trait("Category", "RequiresMediaFoundation")]
public sealed class HeadlessFrameSinkEndToEndTests
{
    private readonly ITestOutputHelper _output;
    public HeadlessFrameSinkEndToEndTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PlayAsync_HeadlessSink_ReceivesFrames()
    {
        var services = new ServiceCollection();
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()
                .AddHeadlessAudioOutput();
        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        var frameCount = 0;
        var lastW = 0;
        var lastH = 0;

        using var sink = new ProcessingFrameSink(
            onFrame: frame =>
            {
                Interlocked.Increment(ref frameCount);
                lastW = frame.Width;
                lastH = frame.Height;
            });

        var source = new FileMediaSource(TestResources.VideoM1);
        try
        {
            sink.Attach(player);
            await player.OpenAsync(source, TestContext.Current.CancellationToken);
            await player.PlayAsync();

            // 让出式轮询至多 15s，等待无头 sink 收到至少 5 帧。
            // 必须用 await Task.Delay 让出线程：MediaPlayer 内部视频管线/异步续体
            // 共享同一线程池，若用 SpinWait 忙等会霸占线程池线程导致解码续体饿死、收不到帧。
            var received = false;
            for (var i = 0; i < 60 && !received; i++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                received = frameCount >= 5;
            }

            await player.StopAsync(TestContext.Current.CancellationToken);

            received.Should().BeTrue("无头 sink 应在超时内收到至少 5 帧");
            frameCount.Should().BeGreaterThan(0, "无头 sink 应收到帧");
            lastW.Should().BeGreaterThan(0, "帧宽度应 > 0");
            lastH.Should().BeGreaterThan(0, "帧高度应 > 0");
        }
        finally
        {
            sink.Detach();
            await player.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlayAsync_HeadlessSink_FastestMode_ReceivesFrames()
    {
        var services = new ServiceCollection();
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()
                .AddHeadlessAudioOutput();
        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // 最快模式：关掉音视频同步（同步器放行所有视频帧）、无头音频输出不实时节流，
        // 适用于转码 / 离线 ML 等批量处理。验证此模式下视频帧仍到达 sink（不被同步卡住）。
        player.Mode = ProcessingMode.Fastest;

        var frameCount = 0;
        using var sink = new ProcessingFrameSink(onFrame: _ => Interlocked.Increment(ref frameCount));

        var source = new FileMediaSource(TestResources.VideoM1);
        try
        {
            sink.Attach(player);
            await player.OpenAsync(source, TestContext.Current.CancellationToken);
            await player.PlayAsync();

            // 让出式轮询（同上一用例：SpinWait 忙等会饿死视频管线续体）。
            var received = false;
            for (var i = 0; i < 60 && !received; i++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                received = frameCount >= 5;
            }

            await player.StopAsync(TestContext.Current.CancellationToken);

            received.Should().BeTrue("最快模式下无头 sink 仍应收到至少 5 帧（同步器放行）");
            frameCount.Should().BeGreaterThan(0, "最快模式下无头 sink 应收到帧");
        }
        finally
        {
            sink.Detach();
            await player.DisposeAsync();
        }
    }

    /// <summary>
    /// 轨道探针：精确判定 m1.mp4 是否含音轨及其编码，为"带音频视频能否无头处理"提供事实依据。
    /// </summary>
    [Fact]
    public async Task Probe_VideoM1_ReportsVideoAndAudioStreams()
    {
        var services = new ServiceCollection();
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()
                .AddHeadlessAudioOutput();
        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        var source = new FileMediaSource(TestResources.VideoM1);
        try
        {
            await player.OpenAsync(source, TestContext.Current.CancellationToken);
            var session = player.Session!;
            var vCount = session.VideoTracks.Count;
            var aCount = session.AudioTracks.Count;
            var vCodec = vCount > 0 ? session.VideoTracks[0].VideoCodec : null;
            var aCodec = aCount > 0 ? session.AudioTracks[0].AudioCodec : null;
            var aInfo = aCount > 0 ? session.AudioTracks[0].AudioInfo : null;

            _output.WriteLine($"[PROBE] Duration={player.Duration} VideoTracks={vCount}({vCodec}) " +
                              $"AudioTracks={aCount}({aCodec}) Audio={aInfo?.SampleRate}Hz/{aInfo?.Channels}ch/{aInfo?.BitsPerSample}bit");
            _output.WriteLine($"[PROBE] hasAudio={aCount > 0}");

            vCount.Should().BeGreaterThan(0, "m1.mp4 应至少含 1 个视频轨");
        }
        finally
        {
            await player.DisposeAsync();
        }
    }

    /// <summary>
    /// 真实场景：视频（带音频）能否无头正确处理——同时挂视频 sink 与音频 sink，
    /// 断言两路帧都到达无头消费者。音频存在时音频 sink 应收到 PCM 帧（音频驱动主时钟、视频跟随同步）。
    /// </summary>
    [Fact]
    public async Task PlayAsync_HeadlessSink_VideoWithAudio_ReceivesBothStreams()
    {
        var services = new ServiceCollection();
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()
                .AddHeadlessAudioOutput();
        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        var videoCount = 0;
        var audioCount = 0;
        using var videoSink = new ProcessingFrameSink(onFrame: _ => Interlocked.Increment(ref videoCount));
        using var audioSink = new ProcessingAudioSink(onAudio: _ => Interlocked.Increment(ref audioCount));

        var source = new FileMediaSource(TestResources.VideoM1);
        try
        {
            videoSink.Attach(player);
            audioSink.Attach(player);
            await player.OpenAsync(source, TestContext.Current.CancellationToken);

            var hasAudio = player.Session!.AudioTracks.Count > 0;
            _output.WriteLine($"[SCENARIO] hasAudio={hasAudio} Duration={player.Duration}");

            await player.PlayAsync();

            // 让出式轮询：视频须 ≥5 帧；若文件确含音频轨，则音频 sink 也须 ≥5 帧。
            var ok = false;
            for (var i = 0; i < 60 && !ok; i++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                ok = videoCount >= 5 && (!hasAudio || audioCount >= 5);
            }

            await player.StopAsync(TestContext.Current.CancellationToken);

            videoCount.Should().BeGreaterThan(0, "无头视频 sink 应收到帧");
            if (hasAudio)
            {
                audioCount.Should().BeGreaterThan(0,
                    "文件含音频轨，无头音频 sink 应收到 PCM 帧（证明带音频视频可无头处理）");
            }

            _output.WriteLine($"[RESULT] videoFrames={videoCount} audioFrames={audioCount}");
        }
        finally
        {
            videoSink.Detach();
            audioSink.Detach();
            await player.DisposeAsync();
        }
    }
}
