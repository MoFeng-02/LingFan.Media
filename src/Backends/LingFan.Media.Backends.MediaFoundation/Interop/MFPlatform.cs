namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// MediaFoundation 平台初始化/反初始化的<b>进程级引用计数</b>封装。
/// </summary>
/// <remarks>
/// <para><b>为什么需要（2026-07-31 根因修复）</b>：<c>MFStartup</c>/<c>MFShutdown</c> 是进程级全局 API——
/// 任意一处 <c>MFShutdown</c> 都会把整个 MF 平台（含所有仍存活的 <c>IMFSourceReader</c>/MFT 的内部原生状态）拆除。
/// 本引擎有三个互不协调的调用者：<see cref="MFBackend"/>（后端入口，构造 Startup / Dispose Shutdown）、
/// <see cref="LingFan.Media.Backends.MediaFoundation.Decoders.MFVideoDecoder"/>（解码侧，Initialize Startup / ReleaseComObjects Shutdown）
/// 与 <c>MFDemuxer</c>（解封装侧，OpenCore Startup / ReleaseNativeResources Shutdown；二轮审计 C-7 补入——
/// 此前它只持有 <see cref="MFBackend"/> 的<b>对象引用</b>，那只防 GC、不防对方被 Dispose，
/// 于是「有意泄漏」路径护住了 IMFSourceReader 却护不住平台本身）。
/// <b>不变量</b>：凡持有 MF 原生对象者，必须自己持一份平台引用，且该引用与其原生对象<b>同进退</b>——
/// 释放路径成对递减，泄漏路径（R4）一律不递减。
/// <see cref="MediaPlayer"/> 释放顺序为「先 Dispose 解码器（→Shutdown）→ 后 Close 解封装器」，导致解码器那次
/// <c>MFShutdown</c> 在解封装器读取线程仍可能有 in-flight 原生 <c>ReadSample</c> 时拆掉平台 →
/// 原生访问违规（0xC0000005）→ <c>COR_E_EXECUTIONENGINE</c> / 0x80131506 非确定性崩溃
/// （MF 冷启动 flaky 崩溃的真正根因，<c>AssemblyInfo</c> 的 <c>DisableTestParallelization</c> 只防跨测试、防不住此竞态）。</para>
/// <para>本封装把 Startup/Shutdown 做成引用计数：仅当<b>最后一个</b>消费者释放（计数归 0）时才真正 <c>MFShutdown</c>。
/// 因此解码器释放只让计数 −1、不拆平台，in-flight 的 <c>ReadSample</c> 安全；平台只在解封装器也释放后才拆除。
/// 同时修复真实多播放器场景（两个 MediaPlayer 并存，关闭其一不应拆掉另一个仍在用的 MF）。</para>
/// <para>线程安全：内部用 <see langword="lock"/> 保护计数（并行测试 / 多播放器共用进程）。</para>
/// <para>AOT 兼容：纯 BCL + 既有 [LibraryImport] P/Invoke，无反射。</para>
/// </remarks>
internal static class MFPlatform
{
    private static int _refCount;
    private static readonly object _gate = new();

    /// <summary>
    /// MTA 保活凭据（<c>CoIncrementMTAUsage</c>）。<see cref="IntPtr.Zero"/> 表示未持有。
    /// </summary>
    /// <remarks>
    /// <para><b>纵深防御（2026-08-01 二次根因修复的配套）</b>：本引擎的
    /// <see cref="LingFan.Media.Backends.MediaFoundation.Concurrency.SingleThreadTaskScheduler"/> 是手动创建的裸线程，
    /// 退出时会 <c>CoUninitialize</c>。若它当时恰是唯一的 MTA 成员，整个 MTA 会被拆除、in-proc server 被卸载，
    /// 殃及其它组件仍持有的 COM 对象。<c>MFDemuxer</c> 的 I7（Release 先于 CoUninitialize、且同线程）解决了
    /// <b>自身</b>指针的安全性；本保活则把 <b>进程级单元</b>的存活与任何具体线程解耦，覆盖跨组件的连带风险。</para>
    /// <para>与平台引用计数同生命周期：0→1 时取得，1→0 时归还，故不构成永久泄漏。</para>
    /// </remarks>
    private static IntPtr _mtaCookie;

    /// <summary>获取一次 MF 平台引用。计数从 0→1 时锁定 MTA 并真正 <c>MFStartup</c>；后续仅计数 +1（幂等）。</summary>
    /// <exception cref="InvalidOperationException"><c>MFStartup</c> 失败。</exception>
    internal static void Startup()
    {
        lock (_gate)
        {
            if (_refCount == 0)
            {
                // 先锁 MTA、后启平台：确保 MFStartup 及其后所有 COM 活动都处于一个不会被任何线程退出拆掉的单元中。
                // 失败不致命（旧系统或极端情形）——退化为原行为，由 I7 单独保证 MFDemuxer 自身安全。
                if (_mtaCookie == IntPtr.Zero &&
                    MFInterop.CoIncrementMTAUsage(out IntPtr cookie) >= 0)
                {
                    _mtaCookie = cookie;
                }

                int hr = MFInterop.MFStartup(MFConstants.MF_VERSION, MFConstants.MFSTARTUP_FULL);
                if (hr < 0)
                {
                    ReleaseMtaLock(); // 启动失败则不保留保活凭据，避免与计数脱钩
                    throw new InvalidOperationException($"MFStartup 失败: HRESULT=0x{hr:X8}");
                }
            }
            _refCount++;
        }
    }

    /// <summary>释放一次 MF 平台引用。计数从 1→0 时才真正 <c>MFShutdown</c> 并解锁 MTA；计数 &gt;0 时为 no-op（平台仍被其他消费者占用）。</summary>
    internal static void Shutdown()
    {
        lock (_gate)
        {
            if (_refCount == 0)
                return; // 防御双释放 / 失衡：无引用可释放
            _refCount--;
            if (_refCount == 0)
            {
                try { MFInterop.MFShutdown(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // 平台拆除失败不应中断上层释放流程；仅记录（无 ILogger 依赖，保持互操作层纯净）
                }
                // 平台已关，最后解除 MTA 保活（顺序不可颠倒：MFShutdown 自身仍需有效单元）。
                ReleaseMtaLock();
            }
        }
    }

    /// <summary>归还 MTA 保活凭据（幂等）。调用方须持 <c>_gate</c>。</summary>
    private static void ReleaseMtaLock()
    {
        if (_mtaCookie == IntPtr.Zero) return;
        try { MFInterop.CoDecrementMTAUsage(_mtaCookie); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 解锁失败仅意味着 MTA 多存活一段时间（安全侧），不影响释放流程
        }
        _mtaCookie = IntPtr.Zero;
    }
}
