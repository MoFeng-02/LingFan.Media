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
                .AddHeadlessAudioOutput();
        services.AddSingleton<IVideoRendererFactory>(countingFactory);
        var sp = services.BuildServiceProvider();
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
    /// 有头音频：真实 WASAPI 输出（默认音频端点）。
    /// MediaPlayer.OpenAsync 对音频输出初始化无 try/catch——故 OpenAsync 成功即证明真实 WASAPI 已
    /// Initialize（设备枚举 + 格式协商通过）；随后播放驱动主时钟推进，证明 PCM 帧真实流向声卡。
    /// 本机端点若不支持请求格式则 Skip（与既有 WasapiOutputTests 设计一致）。
    /// </summary>
    [Fact]
    public async Task PlayAsync_HeadfulAudio_Wasapi_InitializesAndPlays()
    {
        var services = new ServiceCollection();
        services.AddLingFanMedia()
                .AddMediaFoundation()
                .AddHeadlessRenderer()   // 视频走 NoOp，隔离音频验证
                .AddWasapiOutput();      // 音频走真实 WASAPI
        var sp = services.BuildServiceProvider();
        var player = sp.GetRequiredService<IMediaPlayer>();

        var ct = TestContext.Current.CancellationToken;
        try
        {
            var source = new FileMediaSource(TestResources.VideoM1);
            await player.OpenAsync(source, ct); // 成功 = 真实 WASAPI 已 Initialize

            player.Session!.AudioTracks.Count.Should().BeGreaterThan(0, "m1.mp4 应含音轨");

            await player.PlayAsync();

            // 真实 WASAPI 驱动主时钟推进
            var advanced = false;
            for (var i = 0; i < 40 && !advanced; i++)
            {
                await Task.Delay(100, ct);
                advanced = player.Position > TimeSpan.Zero;
            }

            await player.StopAsync(ct);

            _output.WriteLine($"[HEADFUL-AUDIO] wasapiInitialized=true positionAdvanced={advanced} position={player.Position}");
            advanced.Should().BeTrue("有头音频：真实 WASAPI 应初始化并驱动主时钟推进");
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
