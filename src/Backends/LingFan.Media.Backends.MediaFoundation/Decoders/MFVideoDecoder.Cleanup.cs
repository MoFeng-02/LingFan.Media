using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.GPUShare.D3D11;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

// MFVideoDecoder 的 Cleanup 职责段（部分类，由 MFVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/DecodeAsync/ProcessOutputOnce/FlushAsync/Reset/Dispose）在主文件；
// 原生关闭/泄漏兜底/COM 释放，同步 COM 调用，COM 线程亲和。
internal sealed partial class MFVideoDecoder
{
    private void CloseNativeSync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _transformGate.BeginClose();
        bool drained = _transformGate.WaitDrain(MediaPipelineTimeouts.NativeDrain);
        // 排空成功即无在途解码，此时释放积压帧安全（帧为托管资源，泄漏路径下同样应释放）
        if (drained) DiscardPendingOutputs();
        if (drained)
            ReleaseComObjects();
        else
            LeakTransform();
    }

    private async ValueTask CloseNativeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _transformGate.BeginClose();
        if (await _transformGate.WaitDrainAsync(MediaPipelineTimeouts.NativeDrain).ConfigureAwait(false))
            ReleaseComObjects();
        else
            LeakTransform();
    }

    private void LeakTransform()
    {
        if (_leakedOnClose) return;
        _leakedOnClose = true;
        _logger.LogError("MFVideoDecoder 关闭超时：仍有在途原生调用。已【有意保留】IMFTransform，避免因释放导致原生堆损坏（COR_E_EXECUTIONENGINE）。");
    }

    private void ReleaseComObjects()
    {
        // 收尾帧路径统计（DXVA 零拷贝验证计数）：先于字段复位打印一次，确认零拷贝是否真生效。
        if (!_frameSummaryLogged)
        {
            _frameSummaryLogged = true;
            long totalZeroCopy = _gpuZeroCopyFrames + _passthroughGpuFrames;
            long totalCpu = _cpuFallbackFrames + _passthroughCpuFrames;
            if (totalZeroCopy > 0 || totalCpu > 0)
                _logger.LogInformation(
                    "[DXVA-FRAMEPATH] 解码帧路径统计：GPU零拷贝={Gpu} 帧（MFT自解={MftGpu} / SourceReader直通={PtGpu}） / " +
                    "CPU拷贝={Cpu} 帧（MFT软解={MftCpu} / 直通半DXVA={PtCpu}） | 硬解激活={Hw} | 零拷贝生效={Verdict}",
                    totalZeroCopy, _gpuZeroCopyFrames, _passthroughGpuFrames,
                    totalCpu, _cpuFallbackFrames, _passthroughCpuFrames,
                    IsHardwareAccelerated,
                    totalZeroCopy > 0 ? "是" : "否(全程回落CPU拷贝)");
        }

        if (_transform != IntPtr.Zero)
        {
            Marshal.Release(_transform);
            _transform = IntPtr.Zero;

            // 与 MFDemuxer.ReleaseNativeResources 置 _readSample=null 对称。
            // 缓存的 vtable 委托持有的是已释放对象的函数指针；置 null 后，万一有漏网调用点，
            // 得到的是可诊断的 NullReferenceException（`_processInput!` 处），而非静默的 use-after-free。
            _getOutputStreamInfo = null;
            _setInputType = null;
            _setOutputType = null;
            _processMessage = null;
            _processInput = null;
            _processOutput = null;
        }
        if (_inputTypePtr != IntPtr.Zero)
        {
            Marshal.Release(_inputTypePtr);
            _inputTypePtr = IntPtr.Zero;
        }
        if (_outputTypePtr != IntPtr.Zero)
        {
            Marshal.Release(_outputTypePtr);
            _outputTypePtr = IntPtr.Zero;
        }
        // DXVA 管理器释放（与 _transform 同生命周期）：MFCreateDXGIDeviceManager 创建的 COM 对象。
        // 配对的 D3D11 设备由 IGpuDeviceContext 持有（渲染器或无头 MfGpuDeviceContext），此处仅释放管理器自身。
        if (_dxvaManager != IntPtr.Zero)
        {
            Marshal.Release(_dxvaManager);
            _dxvaManager = IntPtr.Zero;
        }
        // Store MFT 激活上下文（ActivateObject 时保留其 IMFActivate*；CloseNativeSync 不触碰，此处统一释放）
        if (_decoderActivate != IntPtr.Zero)
        {
            Marshal.Release(_decoderActivate);
            _decoderActivate = IntPtr.Zero;
        }
        _dxvaResetToken = 0;
        _dxvaActive = false;
        _width = 0;
        _height = 0;
        _initialized = false;

        // MFShutdown 配对（引用计数落地）：
        // MFStartup/MFShutdown 经 MFPlatform 做成进程级引用计数——本解码器每次 Initialize 都 +1，释放时 -1；
        // 平台真正拆除仅发生在计数归 0 时（即 MFBackend 解封装器也释放之后），配对本身安全，不再踩 in-flight 原生调用。
        if (_mfStartupAcquired)
        {
            _mfStartupAcquired = false;
            try { MFPlatform.Shutdown(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "MFShutdown 配对调用异常（忽略，不影响释放流程）");
            }
        }
    }
}
