using System;
using Vector3 = System.Numerics.Vector3;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace LingFan.Media.Avalonia;

/// <summary>
/// Skia GPU 直绘的帧绘制操作骨架：把共享表面描述符经宿主呈现适配器包装为可采样图像并绘制
/// （Avalonia 渲染线程，经 <see cref="DrawingContext.Custom"/> 挂入合成树）。
/// </summary>
/// <remarks>
/// <para>骨架职责：lease 获取、适配器选择结果执行、几何/旋转/拉伸、释放与遥测——
/// 「如何把描述符包装为可采样图像」由 <see cref="IHostSurfacePresenter"/> 实现承担，
/// 本骨架不含任何句柄类型/GPU API 分支。</para>
/// <para>包装生命周期：绘制内现场创建、绘制后立即释放——Ganesh 持有已记录命令的代理引用，
/// 释放包装不影响本帧已记录的采样；不跨帧缓存，规避上下文重建悬挂与线程亲和问题。</para>
/// <para>采样同步契约（生产者保证，见共享表面源实现）：交付布局已对采样可见、生产/消费
/// 共用同一队列（同队列提交按序串行）。本侧不做任何布局转换或同步原语。</para>
/// </remarks>
internal sealed class SkiaGpuVideoDrawOp : ICustomDrawOperation
{
    private readonly SkiaGpuVideoRenderer _owner;
    private static bool _drawGeomLogged; // 一次性几何对账打点标志（static：DrawOp 每帧新建实例，实例字段会每帧打点）
    private static uint _dbgFrameSeq; // 绘制帧序号（跨 DrawOp 实例单调；[DRAW-OP] 心跳与 wrap 遥测共用，Trace 级）

    /// <summary>初始化帧绘制操作。描述符与适配器在每次重放时经 owner 实时拉取（不快照）。</summary>
    /// <param name="owner">宿主渲染器（最新待呈现帧、失败计数回调用）。</param>
    public SkiaGpuVideoDrawOp(SkiaGpuVideoRenderer owner)
    {
        _owner = owner;
    }

    /// <summary>脏区边界（控件可视区）。</summary>
    public Rect Bounds { get; set; }

    /// <inheritdoc/>
    public void Render(ImmediateDrawingContext context)
    {
        // 绘制阶段心跳（Trace 级）：DrawOp.Render 才是真正往画布画东西的回调（渲染线程）。
        // 冻结窗口内心跳持续 ⇒ 绘制在跑但未上屏（提交/交换链停摆）；
        // 心跳停而管线侧心跳仍在 ⇒ 渲染线程被楔死（GPU 等待永不返回）；
        // wrap=FAIL ⇒ SKImage.FromTexture 拒收（声明与创建 usage/layout 不一致的经典症状）。
        // 实时拉取最新待呈现帧与适配器（合成器每帧重放本 op——绝不快照旧帧）。
        if (!_owner.TryGetLatestPresentation(out var descriptor, out var presenter, out var controlW, out var controlH, out var stretch))
            return;

        uint n = Interlocked.Increment(ref _dbgFrameSeq);
        if ((n % 60) == 1)
            _owner.LogDrawGeometry(
                $"[DRAW-OP] #{n} desc={descriptor.Width}x{descriptor.Height} layout={descriptor.NativeImageLayout}");
        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
        {
            _owner.OnDrawFailure("无 ISkiaSharpApiLeaseFeature（非 Skia 渲染后端）");
            return;
        }

        using ISkiaSharpApiLease lease = leaseFeature.Lease();
        GRContext? gr = lease.GrContext;
        SKCanvas? canvas = lease.SkCanvas;
        if (gr is null || canvas is null)
        {
            _owner.OnDrawFailure("Lease 缺 GrContext/SkCanvas");
            return;
        }

        SkiaWrappedSurface? wrapped = null;
        try
        {
            // 共享表面 → 可采样图像：由宿主呈现适配器完成（骨架不含任何句柄类型/GPU API 分支）。
            if (!presenter.TryWrap(gr, descriptor, out wrapped, out string? wrapFail))
            {
                _owner.OnDrawFailure(wrapFail ?? "共享表面包装失败");
                return;
            }
            // 【包装遥测】每 60 帧报一次（与 DRAW-OP 心跳同 n 对齐），让包装成败在任何窗口都可见。
            if ((n % 60) == 1)
                _owner.LogDrawGeometry(
                    $"[DRAW-OP] #{n} wrap=OK layout={descriptor.NativeImageLayout} usage=0x{descriptor.NativeImageUsage:X}");

            var image = wrapped.Image;

            // 旋转呈现：容器声明的显示旋转（90/180/270）。源纹理像素本身不旋转，
            // 以 canvas 变换把已算好的 dest 矩形（按旋转后的显示宽高适配）转回源方向绘制。
            int rot = ((int)descriptor.RotationDegrees % 360 + 360) % 360;
            SKRect dest = ComputeDestRect(descriptor, controlW, controlH, stretch);
            // 【诊断】一次性对账坐标单位（放大溢出定位）：desc/控件(DIP)/dest/画布本地与设备裁剪盒。
            // LocalClipBounds=canvas 当前变换下的本地单位；DeviceClipBounds=物理像素。
            // 两者比值 = 画布有效缩放。dest 落在 Local 内 = 单位一致（DIP）；溢出 Device = 单位错位。
            if (!_drawGeomLogged)
            {
                _drawGeomLogged = true;
                _owner.LogDrawGeometry(
                    $"desc={descriptor.Width}x{descriptor.Height} control={controlW:F0}x{controlH:F0} " +
                    $"dest={dest} localClip={canvas.LocalClipBounds} deviceClip={canvas.DeviceClipBounds}");
            }
            if (rot == 90 || rot == 180 || rot == 270)
            {
                int save = canvas.Save();
                canvas.Translate(dest.MidX, dest.MidY);
                canvas.RotateDegrees(rot);
                // 旋转后源图占据的矩形：宽高随角度互换。
                float rw = (rot == 90 || rot == 270) ? dest.Height : dest.Width;
                float rh = (rot == 90 || rot == 270) ? dest.Width : dest.Height;
                canvas.DrawImage(image, new SKRect(-rw / 2, -rh / 2, rw / 2, rh / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                canvas.RestoreToCount(save);
            }
            else
            {
                // 线性过滤（视频缩放平滑）；无 Mipmap（单级纹理）。
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            }

            // 本帧采样命令已成功记录：向宿主回报渲染线程心跳（管线侧据此判定画面是否定格）。
            _owner.OnDrawn();
        }
        catch (Exception ex)
        {
            _owner.OnDrawFailure($"包装/绘制异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 释放包装（先图像后纹理包装；借用的 VkImage/VkDeviceMemory 等共享表面不受影响——
            // Ganesh 不拥有外来纹理，已记录的本帧采样命令由 Ganesh 代理引用保活）。
            wrapped?.Dispose();
        }
    }

    private SKRect ComputeDestRect(in SharedGpuSurfaceDescriptor descriptor, double controlW, double controlH, Stretch stretch)
        => SkiaGpuVideoRenderer.ComputeDestRectCore(descriptor, controlW, controlH, stretch);

    /// <inheritdoc/>
    public bool HitTest(Point p) => false;

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    bool IEquatable<ICustomDrawOperation>.Equals(ICustomDrawOperation? other) => false;
}
