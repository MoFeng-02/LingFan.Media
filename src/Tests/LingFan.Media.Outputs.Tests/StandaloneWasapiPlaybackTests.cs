using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Outputs.Wasapi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace LingFan.Media.Outputs.Tests;

/// <summary>
/// 完全独立的 WASAPI 渲染测试：<b>不依赖 MF 后端</b>，直接构造本地正弦波 PCM（440Hz / 2ch / S16），
/// 经 WASAPI 真实推送到默认音频端点出声，验证 WASAPI 渲染链路自身
/// （设备枚举 / 格式协商 / 实时 Submit 排队 / IAudioClient.Start / 播放时钟推进）正常。
/// 覆盖用户关注的「WASAPI 独立是否正常」——与 MF 解码链路彻底解耦。
/// </summary>
/// <remarks>
/// ⚠️ 需要真实音频端点（CI/无头无声卡环境 → Skip）。<see cref="WasapiOutput"/> 为 internal，
/// 因 <c>LingFan.Media.Outputs</c> 工程对测试工程开放 <c>InternalsVisibleTo</c>，可直接构造。
/// </remarks>
[Trait("Category", "RequiresAudioDevice")]
[SupportedOSPlatform("windows")]
public sealed class StandaloneWasapiPlaybackTests
{
    private readonly ITestOutputHelper _output;
    public StandaloneWasapiPlaybackTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 直接驱动 WASAPI 出一段 2 秒正弦音，断言播放位置真实推进 ≥1.5s（设备与时钟正常）。
    /// </summary>
    [Fact]
    public async Task PlayAsync_StandaloneWasapi_SineTone_Sustained()
    {
        const int sampleRate = 44100;
        const int channels = 2;
        const int seconds = 2;
        const int frameSamples = 1024; // 每帧 1024 样本（单声道）；远小于 WASAPI 缓冲总大小，安全

        // 直接 new（internal + InternalsVisibleTo），完全绕过 MF
        var output = new WasapiOutput(new WasapiOptions(), NullLogger<WasapiOutput>.Instance);

        try
        {
            await output.InitializeAsync(TestContext.Current.CancellationToken);
            output.Initialize(sampleRate, channels); // 同步 COM 边界：设备枚举 + 格式协商
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException
            or PlatformNotSupportedException or NotSupportedException)
        {
            // 无音频端点 / 设备不支持请求格式（无头/CI/受限声卡环境）→ 跳过，不在 CI 中制造虚假失败
            Assert.Skip($"无音频端点或设备不支持请求格式，跳过独立 WASAPI 测试：{ex.Message}");
        }

        try
        {
            // 构造 2 秒 440Hz 正弦波（S16，L=R），切成小帧
            int totalSamples = sampleRate * seconds;
            var frames = new List<AudioFrame>();
            for (int off = 0; off < totalSamples; off += frameSamples)
            {
                int n = Math.Min(frameSamples, totalSamples - off);
                var samples = new short[n * channels];
                for (int i = 0; i < n; i++)
                {
                    double t = (off + i) / (double)sampleRate;
                    short s = (short)(Math.Sin(2 * Math.PI * 440.0 * t) * 0.3 * 32767);
                    samples[i * channels] = s;
                    samples[i * channels + 1] = s; // 双声道同相
                }
                var data = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, data, 0, data.Length);
                frames.Add(new AudioFrame(
                    data, sampleRate, channels, SampleFormat.S16,
                    TimeSpan.Zero, TimeSpan.FromSeconds((double)n / sampleRate), n));
            }

            // WASAPI IAudioClient.Start 仅在 Resume() 内触发——必须先启动设备，否则不出声
            output.Resume();

            // 后台持续提交（Submit 阻塞等缓冲空间，自然 pacing）；主线程轮询播放位置
            var submitTask = Task.Run(() =>
            {
                foreach (var frame in frames)
                    output.Submit(frame);
            }, TestContext.Current.CancellationToken);

            var start = DateTime.UtcNow;
            double maxPos = 0;
            while (DateTime.UtcNow - start < TimeSpan.FromSeconds(seconds + 3))
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                var pos = output.GetPlaybackPosition();
                if (pos.TotalSeconds > maxPos) maxPos = pos.TotalSeconds;
                if (maxPos >= 1.5) break;
            }
            await submitTask;

            _output.WriteLine($"[WASAPI-STANDALONE] submittedFrames={frames.Count} maxPlaybackPos={maxPos:F2}s");
            maxPos.Should().BeGreaterThan(1.5,
                "独立 WASAPI：应真实出声并推进播放位置 ≥1.5s（设备枚举/格式协商/实时提交/时钟均正常）");
        }
        finally
        {
            output.Dispose(); // 停设备 + 释放 COM + CoUninitialize（渲染线程 Shutdown）
        }
    }
}
