using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Android.Graphics;       // SurfaceTexture / Surface
using Android.Views;
using LingFan.Media.GPUShare.Android.Egl; // EglImageInterop.EglImagePreservedKhr（EGL_KHR_image_base 唯一真源）

namespace LingFan.Media.GPUShare.Android.Bridge;

// AndroidAhbRgbaBridge 的请求循环职责段（部分类，按职责拆分，编译期等价）。
// GL 线程请求消费循环与 ConvertLatest 跨线程移交仲裁（超时兜底、孤儿 AHB 回收）；
// 线程编排与 GL 环境在主文件及 GlSetup 段，产帧在 Produce 段。
public sealed unsafe partial class AndroidAhbRgbaBridge
{
    /// <summary>GL 线程主循环：等待请求，串行消费（产帧或停止）。</summary>
    private void RunLoop()
    {
        long lastWorkQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            // 空闲等待带 2s 超时心跳：区分「GL 线程空闲等请求」与「GL 线程卡死」
            // （无产帧请求时仍能周期性报平安，配合解码侧 ConvertLatest 前后打点定位跨线程等待）。
            if (!_workSignal.WaitOne(2000))
            {
                double idleSec = System.Diagnostics.Stopwatch.GetElapsedTime(lastWorkQpc).TotalSeconds;
                _logger?.LogInformation(
                    "[ANDROID-AHB] GL 线程空闲等待产帧请求（最近完成={Idle:F2}s前，队列积压={Pending}）",
                    idleSec, _requestQueue.Count);
                continue;
            }
            _workSignal.Reset();
            while (_requestQueue.TryDequeue(out var req))
            {
                // 等待方已超时放弃的请求：跳过产帧（省一次 gralloc 分配+渲染），直接记账完成。
                if (Volatile.Read(ref req.Handover) == 2)
                {
                    req.Tcs.TrySetResult(nint.Zero);
                    continue;
                }
                if (req.IsStop)
                {
                    TeardownGlOnThisThread();
                    // stop 后排空残留请求：Dispose 竞态窗口内迟到入队者此后永不被消费——
                    // fail-fast 返回 0，防止其等待方永久阻塞（解码线程挂死 → 会话残留拖累后续播放）。
                    while (_requestQueue.TryDequeue(out var orphan))
                        orphan.Tcs.TrySetResult(nint.Zero);
                    return;
                }
                try
                {
                    req.Result = ProduceFrameOnThisThread();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[ANDROID-AHB] 产帧异常，返回 0（丢弃该帧）。");
                    req.Result = nint.Zero;
                }
                // AHB 移交仲裁（唯一裁决点）：Exchange 返回 2 = 等待方已超时放弃 → 产物自回收
                //（移交协议详见 FrameRequest 注释，杜绝超时与发布竞态的孤儿缓冲）。
                if (Interlocked.Exchange(ref req.Handover, 1) == 2)
                {
                    if (req.Result != nint.Zero) AHardwareBufferRelease(req.Result);
                    req.Result = nint.Zero;
                }
                req.Tcs.TrySetResult(req.Result);
                lastWorkQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// 闩住最新一帧并渲染进新建的 RGBA AHardwareBuffer。调用方线程经 GL 线程串行化执行——
    /// 本方法只在调用线程入队请求并阻塞等待结果，真正的 GL 工作在 GL 线程（上下文常驻）上完成。
    /// 返回 AHardwareBuffer*（引用所有权移交调用方；帧资源 Dispose 时释放）；失败返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    /// <remarks>
    /// <para><b>超时兜底</b>：等待超过 <see cref="ConvertLatestTimeout"/> 返回 0（与「GL 异常态」同义，
    /// 解码侧保留待闩帧标志下轮重试）——覆盖 GL 线程中途死亡、stop 后迟到入队（RunLoop 已 fail-fast
    /// 排空）、设备级产帧停摆三类情形，杜绝解码线程永久挂死。放弃后 GL 线程若完成产帧，产物经
    /// <see cref="FrameRequest.Handover"/> 仲裁由 GL 侧或本侧回收，孤儿 AHB 不泄漏。</para>
    /// </remarks>
    public nint ConvertLatest()
    {
        if (!_initialized) return nint.Zero;

        var req = new FrameRequest();
        _requestQueue.Enqueue(req);
        _workSignal.Set();
        // 阻塞等 GL 线程产帧结果（调用线程为解码读循环后台线程，阻塞无死锁风险：GL 线程不回调调用方）。
        if (!req.Tcs.Task.Wait(ConvertLatestTimeout))
        {
            // 超时放弃（移交仲裁）：CE(2,0)==0 → GL 未发布，置 2 令其发布时自回收；
            // CE(2,0)==1 → GL 恰已发布并移交，产物在此回收（等待方已走，无人接管即泄漏）。
            if (Interlocked.CompareExchange(ref req.Handover, 2, 0) == 1)
            {
                nint orphan = req.Result;
                if (orphan != nint.Zero)
                {
                    AHardwareBufferRelease(orphan);
                    _logger?.LogWarning("[ANDROID-AHB] ConvertLatest 超时放弃时 GL 线程恰好移交，孤儿 AHB 已回收 0x{Ahb:X}。", (ulong)orphan);
                }
            }
            _logger?.LogWarning("[ANDROID-AHB] ConvertLatest 等待产帧超时（{Ms}ms），返回 0 兜底（GL 线程可能已死亡/停摆）。", (int)ConvertLatestTimeout.TotalMilliseconds);
            return nint.Zero;
        }
        return req.Tcs.Task.Result;
    }
}
