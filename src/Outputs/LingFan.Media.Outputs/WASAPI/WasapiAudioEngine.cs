using System.Diagnostics;
using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 音频引擎（Infrastructure 级 <b>Singleton</b>）：持有跨播放会话共享的长期原生资源
/// （<c>IMMDeviceEnumerator</c> / <c>IMMDevice</c> / 一个保活用的 anchor <c>IAudioClient</c>），
/// 使操作系统音频引擎（audiodg.exe）在进程生命周期内保持热态。
/// </summary>
/// <remarks>
/// <para><b>解决的问题</b>：进程内首个 <c>IAudioClient.Initialize</c> 需付出一次性冷启动开销（约数秒，
/// audiodg 冷启动，与共享/独占/事件驱动/轮询模式全部无关）。此前的 throwaway 预热
/// （建一个临时客户端 → Initialize → Dispose）已被数据证伪：Dispose 后设备回冷，
/// 正式播放的客户端照样再付一次冷启动。<b>唯一有效的形态是让一个客户端持续存活</b>，
/// 这正是本类的职责。</para>
/// <para><b>分层依据（DI 设计原则）</b>：长期原生资源 → Infrastructure Singleton；Session 状态 → Transient。
/// 与 GPU 侧完全同构：<c>ID3D11Device</c> 是 Singleton 共享，<c>SwapChain</c>/<c>RenderTarget</c> 是 Session 级。
/// 对应到音频：<b>引擎/端点句柄 Singleton，<see cref="WasapiOutput"/> 每次 OpenAsync 经工厂 new</b>。
/// 绝不把 Session 对象升 Singleton（它持有播放线程、缓冲区、音量，升单例会跨播放互相污染）。</para>
/// <para><b>与 Session 的耦合度 = 零</b>：本类不向 <see cref="WasapiOutput"/> / <see cref="WasapiRenderLoop"/>
/// 暴露任何 COM 指针，二者也不引用本类。Session 只是<b>间接</b>受益于"OS 音频引擎已热"这个进程级事实。
/// 这样多轨播放（N 个并发 Session、各自 <c>ChannelMask</c>/音量）天然由 DI 各自 new/dispose，
/// 无需任何手工引用计数，也不存在跨会话状态污染。</para>
/// <para><b>COM 单元亲和</b>：全部 COM 对象都在 <see cref="StaComWorker"/> 的专用 STA 线程上创建，
/// 并在同一线程、先于 <c>CoUninitialize</c> 释放。</para>
/// <para><b>anchor 为什么用 mix format + 共享模式</b>：mix format 必被引擎接受（无需 AUTOCONVERTPCM），
/// 共享模式保证不抢占设备（独占会让其他应用与后续 Session 全部失声）。anchor 不写任何数据，
/// 因而<b>完全静音</b>，仅维持一条活跃流让引擎不回休眠。</para>
/// <para><b>AOT</b>：零反射、零 <c>[ComImport]</c>，纯 vtable 委托 + <c>[LibraryImport]</c>。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiAudioEngine : IAudioEngine
{
    private readonly WasapiOptions _options;
    private readonly ILogger<WasapiAudioEngine> _logger;
    private readonly Lock _gate = new();

    private StaComWorker? _worker;

    // 长期原生资源（仅在 worker 线程上创建/使用/释放）
    private IntPtr _enumeratorPtr;
    private IntPtr _devicePtr;
    private IntPtr _anchorClientPtr;
    private IAudioClient_Stop? _anchorStop;
    private bool _anchorStarted;

    private volatile bool _warm;
    private volatile bool _disposed;

    /// <summary>
    /// 初始化 <see cref="WasapiAudioEngine"/> 的新实例（不做任何 COM 调用；真正的初始化在 <see cref="Warmup"/>）。
    /// </summary>
    /// <param name="options">WASAPI 配置选项（读取 <see cref="WasapiOptions.KeepEngineAnchorRunning"/>）。</param>
    /// <param name="logger">日志器。</param>
    public WasapiAudioEngine(WasapiOptions options, ILogger<WasapiAudioEngine> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool IsWarm => _warm;

    /// <inheritdoc/>
    public void Warmup()
    {
        if (_warm || _disposed) return;
        var worker = EnsureWorker();
        if (worker is null) return;
        worker.Run(WarmupCore);
    }

    /// <inheritdoc/>
    public Task WarmupAsync(CancellationToken ct = default)
    {
        if (_warm || _disposed) return Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        var worker = EnsureWorker();
        if (worker is null) return Task.CompletedTask;
        return worker.RunAsync(WarmupCore, ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        StaComWorker? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
            _worker = null;
        }

        if (worker is not null)
        {
            // 释放动作交给 worker，在 STA 线程内执行完毕后线程 proc 才 CoUninitialize。
            worker.Shutdown(ReleaseOnWorker);
            worker.Dispose();
        }

        _warm = false;
        _logger.LogDebug("[WASAPI-ENGINE] 已释放（音频引擎保活结束）");
    }

    /// <inheritdoc/>
    /// <remarks>COM 释放是快速同步调用，无 I/O 可 await：委托 <see cref="Dispose"/> 后返回已完成的 ValueTask，非伪异步。</remarks>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>惰性创建 STA 工作线程（Dispose 后返回 null，使预热成为安全 no-op）。</summary>
    private StaComWorker? EnsureWorker()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            return _worker ??= new StaComWorker("WasapiAudioEngine");
        }
    }

    /// <summary>
    /// 在 STA 工作线程内建立长期资源并付一次 audiodg 冷启动成本。幂等：已热则直接返回。
    /// </summary>
    /// <remarks>
    /// 失败一律降级为"未预热"并只记 Warning：预热是纯优化，绝不能影响宿主启动或后续播放。
    /// </remarks>
    private void WarmupCore()
    {
        if (_warm || _disposed) return;

        var sw = Stopwatch.StartNew();
        try
        {
            // 1. IMMDeviceEnumerator
            var clsid = WasapiInterop.CLSID_MMDeviceEnumerator;
            var iidEnum = WasapiInterop.IID_IMMDeviceEnumerator;
            int hr = WasapiInterop.CoCreateInstance(
                ref clsid, IntPtr.Zero, WasapiInterop.CLSCTX_ALL, ref iidEnum, out IntPtr pEnumerator);
            if (hr < 0)
            {
                LogWarmupFailure("CoCreateInstance(MMDeviceEnumerator)", hr);
                return;
            }
            _enumeratorPtr = pEnumerator;
            long tEnum = sw.ElapsedMilliseconds;

            // 2. 默认渲染端点
            var getDefault = ComVTable.Get<IMMDeviceEnumerator_GetDefaultAudioEndpoint>(pEnumerator, 1);
            hr = getDefault(pEnumerator, WasapiInterop.EDataFlow_Render, WasapiInterop.ERole_Console, out IntPtr pDevice);
            if (hr < 0)
            {
                LogWarmupFailure("GetDefaultAudioEndpoint", hr);
                ReleaseOnWorker();
                return;
            }
            _devicePtr = pDevice;
            long tDevice = sw.ElapsedMilliseconds;

            // 3. anchor IAudioClient
            var iidClient = WasapiInterop.IID_IAudioClient;
            var activate = ComVTable.Get<IMMDevice_Activate>(pDevice, 0);
            hr = activate(pDevice, ref iidClient, WasapiInterop.CLSCTX_ALL, IntPtr.Zero, out IntPtr pClient);
            if (hr < 0)
            {
                LogWarmupFailure("Activate(IAudioClient)", hr);
                ReleaseOnWorker();
                return;
            }
            _anchorClientPtr = pClient;
            long tActivate = sw.ElapsedMilliseconds;

            // 4. mix format（必被引擎接受，无需 AUTOCONVERTPCM）
            var getMixFormat = ComVTable.Get<IAudioClient_GetMixFormat>(pClient, 5);
            hr = getMixFormat(pClient, out IntPtr pMixFormat);
            if (hr < 0)
            {
                LogWarmupFailure("GetMixFormat", hr);
                ReleaseOnWorker();
                return;
            }
            // CoTaskMemFree 只与「成功的」GetMixFormat 配对，且恰好一次。
            long tInit;
            try
            {
                // 5. Initialize —— audiodg 冷启动开销就在这一步（首次开销明显，后续复用）
                var initialize = ComVTable.Get<IAudioClient_Initialize>(pClient, 0);
                var sessionGuid = Guid.Empty;
                hr = initialize(
                    pClient,
                    WasapiInterop.AUDCLNT_SHAREMODE_SHARED,  // anchor 永远共享：绝不能独占设备
                    0,                                        // 无 EVENTCALLBACK / 无 AUTOCONVERTPCM（格式即 mix format）
                    0,                                        // 缓冲时长交给引擎默认周期，占用最小
                    0,
                    pMixFormat,
                    ref sessionGuid);
                if (hr < 0)
                {
                    LogWarmupFailure("IAudioClient.Initialize", hr);
                    ReleaseOnWorker();
                    return;
                }
                tInit = sw.ElapsedMilliseconds;
            }
            finally
            {
                WasapiInterop.CoTaskMemFree(pMixFormat);
            }

            // 6. 可选 Start：让流真正处于活跃态（不写任何数据 ⇒ 全静音），最大化"引擎不回休眠"的概率。
            _anchorStop = ComVTable.Get<IAudioClient_Stop>(pClient, 8);
            if (_options.KeepEngineAnchorRunning)
            {
                var start = ComVTable.Get<IAudioClient_Start>(pClient, 7);
                int startHr = start(pClient);
                if (startHr < 0)
                    _logger.LogWarning("[WASAPI-ENGINE] anchor Start 失败：HRESULT=0x{HR:X8}（已初始化的流仍可保活，忽略）", startHr);
                else
                    _anchorStarted = true;
            }

            _warm = true;
            _logger.LogInformation(
                "[WASAPI-ENGINE] 预热完成 总计 {Total}ms | Enumerator {T1}ms → Device {T2}ms → Activate {T3}ms → Initialize {T4}ms | anchorRunning={Running}",
                sw.ElapsedMilliseconds, tEnum, tDevice, tActivate, tInit, _anchorStarted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WASAPI-ENGINE] 预热异常（降级为未预热，不影响播放）");
            ReleaseOnWorker();
        }
    }

    private void LogWarmupFailure(string step, int hr) =>
        _logger.LogWarning("[WASAPI-ENGINE] 预热在 {Step} 失败：HRESULT=0x{HR:X8}（降级为未预热，不影响播放）", step, hr);

    /// <summary>
    /// 释放全部长期 COM 资源。<b>必须在 worker 线程上调用</b>（由 <see cref="StaComWorker.Shutdown"/> 保证）。
    /// </summary>
    private void ReleaseOnWorker()
    {
        if (_anchorClientPtr != IntPtr.Zero)
        {
            // Start/Stop 配对：只有成功 Start 过才 Stop。
            if (_anchorStarted && _anchorStop is not null)
            {
                try { _anchorStop(_anchorClientPtr); } catch { /* 关闭期忽略 */ }
                _anchorStarted = false;
            }
            Marshal.Release(_anchorClientPtr);
            _anchorClientPtr = IntPtr.Zero;
        }
        _anchorStop = null;

        if (_devicePtr != IntPtr.Zero)
        {
            Marshal.Release(_devicePtr);
            _devicePtr = IntPtr.Zero;
        }

        if (_enumeratorPtr != IntPtr.Zero)
        {
            Marshal.Release(_enumeratorPtr);
            _enumeratorPtr = IntPtr.Zero;
        }

        _warm = false;
    }
}
