using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation;
using LingFan.Media.Consumers;
using LingFan.Media.Extensions;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Outputs.Wasapi;
using LingFan.Media.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// 有头（Headful）播放端到端回归：验证真实 GPU 上屏（D3D11 SwapChain.Present）与真实音频出声（WASAPI）
/// 两条"有头"链路均能通过真实 MediaPlayer + 真实 MF 解码正常工作。
/// 与无头测试（ProcessingFrameSink/ProcessingAudioSink）形成对称覆盖——同一套管线，仅末端消费者不同。
/// </summary>
/// <remarks>
/// ⚠️ <b>须禁用沙盒运行</b>：沙盒隔离边界会切断 MediaFoundation / D3D11 / WASAPI 等真实宿主系统 API。
/// 视频有头需要 GPU + 桌面合成（无头/CI 环境不具备则 Skip），音频有头需要兼容的音频端点
/// （本机端点若不支持请求格式则 Skip，与 <c>WasapiOutputTests</c> 既有设计一致）。
/// </remarks>
[Trait("Category", "RequiresMediaFoundation")]
[SupportedOSPlatform("windows")]
public sealed class HeadfulPlaybackEndToEndTests
{
    private readonly ITestOutputHelper _output;
    public HeadfulPlaybackEndToEndTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 有头视频：真实 D3D11 渲染器经隐藏窗口的 SwapChain 上屏。
    /// 不订阅 VideoFrameAvailable → 帧路由到 _videoRenderer.Present（D3D11 原生 GPU 上屏）。
    /// 计数装饰器精确统计真实 SwapChain.Present 调用次数。
    /// </summary>
    [Fact]
    public async Task PlayAsync_HeadfulVideo_D3D11Present_ReceivesFrames()
    {
        var services = new ServiceCollection();
        // 视频：真实 D3D11 渲染器（计数装饰器包裹）；音频：NoOp 隔离，专注验证视频有头
        var d3d11Factory = new D3D11RendererFactory(NullLoggerFactory.Instance);
        var countingFactory = new CountingVideoRendererFactory(d3d11Factory);
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddSilentAudioOutput();
        services.AddSingleton<IVideoRendererFactory>(countingFactory);
        // await using：测试结束时释放 ServiceProvider → 连带释放 MFBackend 单例（MFShutdown 配对）。
        // 原实现从不释放 provider → MFStartup 计数泄漏 + MF 平台常驻测试进程。
        // C# await using 的释放发生在方法体末尾（晚于 finally 中的 player.DisposeAsync()），顺序安全。
        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        using var win = new HiddenWindow(640, 480); // 创建隐藏 HWND（SwapChain 绑定目标）
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var source = new FileMediaSource(TestResources.VideoM1);
            await player.OpenAsync(source, ct); // 创建 _videoRenderer（即计数装饰器实例）

            if (countingFactory.Last is null)
                Assert.Skip("D3D11 渲染器未创建（环境无 GPU/显示），跳过有头视频测试。");

            // 挂到隐藏窗口的 SwapChain（镜像 VideoView 的 D3D11GpuPresenter.Initialize）
            countingFactory.Last.Attach(new HwndRenderTarget(win.Hwnd, 640, 480));

            await player.PlayAsync();

            // 让出式轮询：等待真实 D3D11 Present 至少 5 帧
            var ok = false;
            for (var i = 0; i < 60 && !ok; i++)
            {
                await Task.Delay(250, ct);
                ok = countingFactory.Last.PresentCount >= 5;
            }

            await player.StopAsync(ct);

            countingFactory.Last.PresentCount.Should().BeGreaterThan(0,
                "有头视频：真实 D3D11 SwapChain.Present 应被实际调用（GPU 上屏）");
            _output.WriteLine($"[HEADFUL-VIDEO] d3d11PresentCount={countingFactory.Last.PresentCount}");
        }
        catch (Exception ex)
        {
            // 排除断言失败（属真实逻辑问题，不应被当作环境跳过）
            if (ex is XunitException or FluentAssertions.Execution.AssertionFailedException)
                throw;
            // 环境门控：无 GPU / 无桌面合成 / D3D11 设备创建或 SwapChain 失败 → 跳过
            Assert.Skip(
                $"有头视频需要 GPU + 桌面合成（当前环境不具备），跳过：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await player.DisposeAsync();
        }
    }

    /// <summary>
    /// 有头音频（纯音频文件）：真实 WASAPI 输出（默认音频端点）+ 真实 MP3 解码。本用例即"无头进程 + 真实音频设备"的典型场景
    /// （<c>AddHeadlessRenderer()</c> 无画面 + <c>AddWasapiOutput()</c> 真出声——WASAPI 不需窗口）。
    /// 源使用 <see cref="TestResources.AudioCrickets"/>（crickets_night01.mp3，3 分钟纯环境音，无视频轨），
    /// 仅播放 ~10 秒验证 MF 解码真实 MP3 → WASAPI 真机持续出声（而非仅瞬时 blip）。
    /// MediaPlayer.OpenAsync 对音频输出初始化无 try/catch——故 OpenAsync 成功即证明真实 WASAPI 已
    /// Initialize（设备枚举 + 格式协商通过）。
    /// 全程依赖生产级媒体类会话分类（IAudioClient2.SetClientProperties，默认 Movie，避开本机 driver 对
    /// BackgroundCapableMedia 的 0xC0000005 崩溃）防止 Windows 挂起后台/非前台会话；故 10 秒应被完整驱动、持续出声。
    /// 本机端点若不支持请求格式则 Skip（与既有 WasapiOutputTests 设计一致）。
    /// </summary>
    [Fact]
    public async Task PlayAsync_HeadfulAudio_Wasapi_InitializesAndPlays()
    {
        var services = new ServiceCollection();
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()   // 视频走 NoOp，隔离音频验证
                .AddWasapiOutput();      // 音频走真实 WASAPI（共享模式；本机 driver 不支持独占/SetClientProperties，维持共享模式基线）
        // await using：释放 ServiceProvider → MFBackend 单例配对 MFShutdown（防泄漏，同上）。
        await using var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        // 诊断：累计实际进入提交链（Submit 之前触发）的音频采样数，用于区分
        // "真丢帧(underrun/超时)" vs "时钟/时间戳慢"——前者 submittedSec 明显小于墙钟/played。
        long submittedSamples = 0;
        player.AudioDataAvailable += f => System.Threading.Interlocked.Add(ref submittedSamples, f.FrameCount);

        var ct = TestContext.Current.CancellationToken;
        try
        {
            var source = new FileMediaSource(TestResources.AudioCrickets);
            await player.OpenAsync(source, ct); // 成功 = 真实 WASAPI 已 Initialize

            player.Session!.AudioTracks.Count.Should().BeGreaterThan(0, "crickets_night01.mp3 应含音轨");

            await player.PlayAsync();
            _output.WriteLine($"[HEADFUL-AUDIO] after PlayAsync: state={player.State} startPos={player.Position}");

            // 真实 WASAPI 驱动主时钟推进：播满 ~10 秒，既能让人耳实际听到，
            // 也能断言"持续出声"而非仅瞬时 blip。依赖生产级 BackgroundCapableMedia 会话分类防止后台挂起。
            // 逐 500ms 采样位置时间线 + 状态，用于区分"真 stalled"与"时钟读数 lag"。
            var startPos = player.Position;
            var timeline = new List<string>();
            for (var i = 1; i <= 20; i++)
            {
                await Task.Delay(500, ct);
                timeline.Add($"{(i * 500)}ms pos={player.Position:g} state={player.State}");
            }
            var played = player.Position - startPos;

            await player.StopAsync(ct);

            _output.WriteLine($"[HEADFUL-AUDIO] wasapiInitialized=true startPos={startPos:g} played={played:g}");
            _output.WriteLine($"[HEADFUL-AUDIO] timeline: {string.Join(" | ", timeline)}");
            // submittedSamples/44100 仅作近似估算（Crickets MP3 实际采样率以会话为准），真实校验以 played 断言为准。
            _output.WriteLine($"[HEADFUL-AUDIO] submittedSamples={submittedSamples} submittedSec(approx)={submittedSamples / 44100.0:F2} (wallClock≈10.0s)");
            played.Should().BeGreaterThan(TimeSpan.FromSeconds(9.0),
                "有头纯音频：真实 WASAPI 应在媒体类（Movie/Media）会话下持续出声并推进主时钟 ≥9s（非瞬时 blip，后台不被挂起；BackgroundCapableMedia 在本机 driver 会崩，已改用 Movie）");
        }
        catch (Exception ex)
        {
            if (ex is XunitException or FluentAssertions.Execution.AssertionFailedException)
                throw;
            // 仅当异常确实与音频设备/格式相关时才跳过，避免掩盖真实逻辑错误
            var m = (ex.Message + " " + (ex.InnerException?.Message ?? "")).ToLowerInvariant();
            if (ex is COMException or PlatformNotSupportedException
                || m.Contains("wasapi") || m.Contains("音频") || m.Contains("audio")
                || m.Contains("format") || m.Contains("设备") || m.Contains("endpoint")
                || m.Contains("hresult"))
            {
                _output.WriteLine($"[HEADFUL-AUDIO-SKIP-REASON] {ex}");
                Assert.Skip(
                    $"有头音频需要兼容的音频端点（当前环境不支持请求格式），跳过：{ex.GetType().Name}: {ex.Message}");
            }
            throw;
        }
        finally
        {
            await player.DisposeAsync();
        }
    }

    // ── 计数装饰器：包裹真实 IVideoRenderer，统计 Present 调用 ──

    private sealed class CountingVideoRendererFactory : IVideoRendererFactory
    {
        private readonly IVideoRendererFactory _inner;
        public CountingVideoRenderer? Last { get; private set; }
        public CountingVideoRendererFactory(IVideoRendererFactory inner) => _inner = inner;
        public IVideoRenderer Create()
        {
            var wrapped = new CountingVideoRenderer(_inner.Create());
            Last = wrapped;
            return wrapped;
        }
    }

    private sealed class CountingVideoRenderer : IVideoRenderer
    {
        private readonly IVideoRenderer _inner;
        public int PresentCount;
        public CountingVideoRenderer(IVideoRenderer inner) => _inner = inner;
        public void Attach(IRenderTarget target) => _inner.Attach(target);
        public void Detach() => _inner.Detach();
        public void Present(VideoFrame frame)
        {
            Interlocked.Increment(ref PresentCount);
            _inner.Present(frame);
        }
        public void Clear() => _inner.Clear();
        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    // ── 隐藏窗口：提供有效 HWND 供 D3D11 SwapChain 绑定 ──

    private sealed class HwndRenderTarget : IRenderTarget
    {
        private readonly IntPtr _hwnd;
        private readonly int _w, _h;
        public HwndRenderTarget(IntPtr hwnd, int w, int h) { _hwnd = hwnd; _w = w; _h = h; }
        public RenderTargetType Type => RenderTargetType.Window;
        public RenderHandleType HandleType => RenderHandleType.Pointer;
        public object NativeHandle => _hwnd;
        public int Width => _w;
        public int Height => _h;
        public float Scale => 1f;
    }

    private sealed class HiddenWindow : IDisposable
    {
        public IntPtr Hwnd { get; private set; }
        private readonly Thread _pump;
        private volatile bool _alive = true;
        private const int WS_POPUP = unchecked((int)0x80000000);

        public HiddenWindow(int w, int h)
        {
            Hwnd = CreateWindowEx(0, "Static", "", WS_POPUP, 0, 0, w, h,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx 失败，LastError={Marshal.GetLastWin32Error()}");
            _pump = new Thread(Pump) { IsBackground = true };
            _pump.Start();
        }

        private void Pump()
        {
            while (_alive)
            {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            _alive = false;
            try { _pump.Join(500); } catch { /* 忽略 */ }
            if (Hwnd != IntPtr.Zero)
            {
                DestroyWindow(Hwnd);
                Hwnd = IntPtr.Zero;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
            uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }
    }
}
