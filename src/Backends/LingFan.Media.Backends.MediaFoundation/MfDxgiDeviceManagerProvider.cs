using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Backends.MediaFoundation;

/// <summary>
/// 进程级共享的 <c>IMFDXGIDeviceManager</c> 提供者（A 方案：SourceReader 自带硬解 + DXGI 出样的关键前置）。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：创建一个绑定到 <see cref="IGpuDeviceContext"/> D3D11 设备的 MF DXGI 设备管理器，
/// 供 <c>MFDemuxer</c> 在 <c>MFCreateSourceReaderFromURL</c> 的 attributes 上以
/// <c>MF_SOURCE_READER_D3D_MANAGER</c> 挂载。SourceReader 拿到管理器后会替我们完成
/// 「选硬件 MFT → 发 MFT_MESSAGE_SET_D3D_MANAGER → 分配 DXGI 输出表面池」的全套编排，
/// <c>ReadSample</c> 直接吐出可 QI 成 <c>IMFDXGIBuffer</c> 的样本 ⇒ 解码→上屏全程 GPU 纹理，零系统内存往返。</para>
///
/// <para><b>为什么独立成单例而不是塞进 <see cref="MfGpuDeviceContext"/></b>：
/// <see cref="IGpuDeviceContext"/> 是 Abstractions 中立契约，有头模式由 D3D11 渲染器实现并胜出。
/// 若把 MF 专有的 DXGI 管理器塞进该契约，等于让渲染器契约背上 MF 概念，污染依赖倒置边界。
/// 独立 provider 只依赖契约暴露的 <c>DeviceHandle</c>，有头/无头都能复用同一设备（同设备才是真零拷贝）。</para>
///
/// <para>🔴 <b>开箱即用铁律</b>：构造期只存引用，**绝不触碰原生**。设备与管理器均在首次
/// <see cref="TryGetManager"/> 时延迟创建 —— 注册 MF 后端 ≠ 立刻要 D3D11/MF 原生库。</para>
///
/// <para>🔴 <b>失败语义</b>：任何一步失败都只记 Warning 并返回 <see cref="IntPtr.Zero"/>，绝不抛异常。
/// 调用方据此走软解兜底（宪法：硬解优先、软解兜底）。同时用 <c>_attempted</c> 做一次性闸门，
/// 避免每次打开媒体都重试一遍必然失败的设备创建。</para>
///
/// <para><b>多线程保护</b>：DXVA 共享设备必须开 <c>ID3D10Multithread::SetMultithreadProtected(TRUE)</c>——
/// SourceReader 内部解码线程与渲染线程会并发访问同一 ID3D11Device，未开保护时 D3D11 不做内部同步 ⇒ 竞态/设备移除。</para>
///
/// <para><b>生命周期</b>：Singleton。管理器为长期原生资源，随容器释放（DI 分层：长期原生资源 → Singleton）。</para>
/// <para><b>异步策略</b>：全同步（native 分类）——COM 创建/绑定无 I/O await，不补 async（补即伪异步）。</para>
/// <para><b>AOT 兼容</b>：<c>[LibraryImport]</c> + 原始 vtable 委托，无反射、无 <c>[ComImport]</c>。</para>
/// <para>仅 Windows 可用。</para>
/// <para><b>可见性</b>：类型 <c>public</c> 仅因 <c>MFDemuxerFactory</c>（public，供 DI 激活）的构造函数需要引用它
/// （CS0051 一致性可访问性约束），与同为 DI 入口的 <see cref="MFBackend"/> 同理；
/// 真正的能力入口 <see cref="TryGetManager"/> 保持 <c>internal</c>，外部无法误用裸 COM 指针。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MfDxgiDeviceManagerProvider : IDisposable
{
    private readonly IGpuDeviceContext _gpuContext;
    private readonly ILogger<MfDxgiDeviceManagerProvider> _logger;
    private readonly object _lock = new();

    private IntPtr _manager;      // IMFDXGIDeviceManager*（Dispose 时 Release）
    private uint _resetToken;     // 与 ResetDevice 配对的 token
    private bool _attempted;      // 一次性闸门：失败后不再重试
    private bool _disposed;

    /// <summary>初始化提供者。构造期不触碰任何原生 API。</summary>
    /// <param name="gpuContext">GPU 设备上下文契约（有头=渲染器设备，无头=MF 自备设备）。</param>
    /// <param name="logger">日志。</param>
    public MfDxgiDeviceManagerProvider(IGpuDeviceContext gpuContext, ILogger<MfDxgiDeviceManagerProvider> logger)
    {
        _gpuContext = gpuContext ?? throw new ArgumentNullException(nameof(gpuContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 取得已绑定 D3D11 设备的 <c>IMFDXGIDeviceManager*</c>（首次调用时延迟创建）。
    /// </summary>
    /// <returns>成功返回管理器 COM 指针（<b>调用方不得 Release</b>，所有权归本单例）；不可用返回 <see cref="IntPtr.Zero"/>。</returns>
    /// <remarks>
    /// 返回的指针供 <c>IMFAttributes::SetUnknown</c> 使用——属性 store 会自行 AddRef，
    /// 与本单例持有的引用互不干扰，故调用方无需（也不得）配对 Release。
    /// </remarks>
    internal IntPtr TryGetManager()
    {
        if (_disposed) return IntPtr.Zero;
        if (_manager != IntPtr.Zero) return _manager;

        lock (_lock)
        {
            if (_disposed || _manager != IntPtr.Zero) return _manager;
            if (_attempted) return IntPtr.Zero; // 已失败过，不再重试
            _attempted = true;

            try
            {
                // ① 取 D3D11 设备（延迟创建；无头由 MfGpuDeviceContext 自备，有头复用渲染器设备 ⇒ 同设备才零拷贝）
                IntPtr device = _gpuContext.DeviceHandle;
                if (device == IntPtr.Zero)
                {
                    _logger.LogWarning("[MF-D3D] IGpuDeviceContext 未提供 D3D11 设备 → SourceReader 硬解不可用，回落软解");
                    return IntPtr.Zero;
                }

                // ② 多线程保护（DXVA 共享设备硬性要求；不支持只告警，不阻断）
                if (!MfDxvaInterop.TryEnableMultithreadProtection(device))
                    _logger.LogWarning("[MF-D3D] D3D11 设备不支持 ID3D10Multithread，未开启多线程保护（DXVA 下存在竞态风险）");

                // ③ 创建 DXGI 设备管理器
                int hr = MfDxvaInterop.MFCreateDXGIDeviceManager(out _resetToken, out IntPtr manager);
                if (hr < 0 || manager == IntPtr.Zero)
                {
                    _logger.LogWarning("[MF-D3D] MFCreateDXGIDeviceManager 失败 HRESULT=0x{HR:X8} → 回落软解", hr);
                    return IntPtr.Zero;
                }

                // ④ 绑定设备：ResetDevice 在绝对槽 7 ⇒ MfVTable slotIndex = 4
                //    （vtable: CloseDeviceHandle=3, GetVideoService=4, LockDevice=5, OpenDeviceHandle=6,
                //      ResetDevice=7, TestDevice=8, UnlockDevice=9；以 SDK mfobjects.h 为权威，勿手算）
                var resetDevice = MfVTable.Get<MfDxvaInterop.IMFDXGIDeviceManager_ResetDevice>(manager, 4);
                hr = resetDevice(manager, device, _resetToken);
                if (hr < 0)
                {
                    _logger.LogWarning("[MF-D3D] IMFDXGIDeviceManager.ResetDevice 失败 HRESULT=0x{HR:X8} → 回落软解", hr);
                    Marshal.Release(manager); // R5 配对：创建成功但绑定失败，须释放
                    return IntPtr.Zero;
                }

                // ⑤ 决定性验证：S_OK ≠ 被接受。从管理器内部取回解码器实际会用的视频设备并复测能力，
                //    区分「绑定真正生效」与「ResetDevice 静默失败（token 不匹配 / 设备缺 VIDEO_SUPPORT）」。
                string? diag = MfDxvaInterop.ProbeManagerBoundDevice(manager, MFConstants.D3D11_DECODER_PROFILE_H264_VLD_NOFGT);
                if (diag != null) _logger.LogInformation("{Diag}", diag);

                _manager = manager;
                _logger.LogInformation("[MF-D3D] DXGI 设备管理器已创建并绑定 D3D11 设备（resetToken={Token}）→ SourceReader 可走硬解 DXGI 出样", _resetToken);
                return _manager;
            }
            catch (Exception ex)
            {
                // 绝不向上抛：调用方走软解兜底
                _logger.LogWarning(ex, "[MF-D3D] DXGI 设备管理器创建异常 → 回落软解");
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>释放 DXGI 设备管理器（配对的 D3D11 设备由 <see cref="IGpuDeviceContext"/> 持有，此处不释放）。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_manager != IntPtr.Zero)
            {
                Marshal.Release(_manager);
                _manager = IntPtr.Zero;
            }
        }
    }
}
