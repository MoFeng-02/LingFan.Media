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

// SkiaGpuVideoRenderer 的 Composition 职责段（部分类，由 SkiaGpuVideoRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/Render/Dispose）在主文件；GPU 共享表面合成呈现与诊断。
internal sealed partial class SkiaGpuVideoRenderer
{
    /// <summary>把 custom visual 尺寸同步为宿主当前尺寸（零尺寸会被合成器跳过，必须非 0）。</summary>
    private void ApplyCompositionVisualSize()
    {
        if (_compositionVisual is null || _visual is null)
            return;
        var b = _visual.Bounds;
        if (b.Width > 0 && b.Height > 0)
            _compositionVisual.Size = new Vector(b.Width, b.Height);
    }

    /// <summary>把合成 Custom Visual 挂为控件子视觉并启动帧循环。</summary>
    private void AttachCompositionCustomVisual(Visual visual)
    {
        try
        {
            var compositor = ElementComposition.GetElementVisual(visual)?.Compositor;
            if (compositor is null)
            {
                _logger.LogWarning("无法取得 Compositor，CompositionCustomVisual 上屏路径不可用（回退 DrawOp 路径）。");
                return;
            }

            _compositionHandler = new VideoCompositionCustomVisualHandler();
            _compositionVisual = compositor.CreateCustomVisual(_compositionHandler);
            ElementComposition.SetElementChildVisual(visual, _compositionVisual);
            _compositionVisual.Offset = new Vector3(0, 0, 0);
            // 尺寸：Attach 时布局可能尚未完成（Bounds 仍为 0）——零尺寸子视觉不会被合成器渲染，
            // 故延后到下一 UI 循环读取 post-layout 尺寸，并持续订阅尺寸变化（同 Composition 渲染器做法）。
            if (visual is Control control)
            {
                control.SizeChanged += OnCompositionHostSizeChanged;
                _compositionSizeHost = control;
            }
            Dispatcher.UIThread.Post(ApplyCompositionVisualSize);
            // 消息顺序：先绑帧来源，再启动帧循环。
            _compositionVisual.SendHandlerMessage(_compositionHandler);
            _compositionVisual.SendHandlerMessage(VideoCompositionCustomVisualHandler.StartMessage);
            _logger.LogInformation("[SKIA-GPU] CompositionCustomVisual 已挂载（render loop 每帧驱动上屏）。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CompositionCustomVisual 挂载失败，回退 DrawOp 路径。");
            _compositionHandler = null;
            _compositionVisual = null;
        }
    }

    /// <summary>停止帧循环并解除子视觉挂载。</summary>
    private void DetachCompositionCustomVisual()
    {
        if (_compositionVisual is not null)
        {
            _compositionVisual.SendHandlerMessage(VideoCompositionCustomVisualHandler.StopMessage);
            if (_visual is not null)
                ElementComposition.SetElementChildVisual(_visual, null);
            _compositionVisual = null;
        }
        if (_compositionSizeHost is not null)
        {
            _compositionSizeHost.SizeChanged -= OnCompositionHostSizeChanged;
            _compositionSizeHost = null;
        }
        _compositionHandler = null;
    }

    /// <summary>
    /// 合成 Custom Visual 的渲染回调（渲染线程，Avalonia render loop 每帧驱动）：
    /// 经宿主呈现适配器采样最新共享表面并绘制。Android EGL 后端的主上屏路径——
    /// 该后端的合成循环不响应跨线程 InvalidateVisual 驱动的 Custom op 重渲染。
    /// </summary>
    public void RenderCompositionFrame(ImmediateDrawingContext drawingContext, Vector effectiveSize)
    {
        if (_disposed)
            return;

        if (!TryGetLatestPresentation(out var descriptor, out var presenter, out var controlW, out var controlH, out var stretch))
            return;

        if (presenter is null)
        {
            OnDrawFailure("无可呈现该描述符的宿主呈现适配器（须注册 IHostSurfacePresenter 实现）");
            return;
        }

        if (drawingContext.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
        {
            OnDrawFailure("无 ISkiaSharpApiLeaseFeature（非 Skia 渲染后端）");
            return;
        }

        using ISkiaSharpApiLease lease = leaseFeature.Lease();
        GRContext? gr = lease.GrContext;
        SKCanvas? canvas = lease.SkCanvas;
        if (gr is null || canvas is null)
        {
            OnDrawFailure("Lease 缺 GrContext/SkCanvas");
            return;
        }

        SkiaWrappedSurface? wrapped = null;
        try
        {
            if (!presenter.TryWrap(gr, descriptor, out wrapped, out string? wrapFail))
            {
                OnDrawFailure(wrapFail ?? "共享表面包装失败");
                return;
            }

            var image = wrapped.Image;
            var dest = ComputeDestRectCore(descriptor, effectiveSize.X, effectiveSize.Y, stretch);
            int rot = ((int)descriptor.RotationDegrees % 360 + 360) % 360;
            if (rot == 90 || rot == 180 || rot == 270)
            {
                int save = canvas.Save();
                canvas.Translate(dest.MidX, dest.MidY);
                canvas.RotateDegrees(rot);
                float rw = (rot == 90 || rot == 270) ? dest.Height : dest.Width;
                float rh = (rot == 90 || rot == 270) ? dest.Width : dest.Height;
                canvas.DrawImage(image, new SKRect(-rw / 2, -rh / 2, rw / 2, rh / 2),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
                canvas.RestoreToCount(save);
            }
            else
            {
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            }

            OnDrawn();
        }
        catch (Exception ex)
        {
            OnDrawFailure($"包装/绘制异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            wrapped?.Dispose();
        }
    }

    /// <summary>按拉伸模式计算目标矩形（DIP），画面恒不越控件边界。
    /// 90/270° 旋转时以「旋转后的显示宽高」（互换）参与适配——dest 矩形是画面最终呈现区域。</summary>
    internal static SKRect ComputeDestRectCore(in SharedGpuSurfaceDescriptor descriptor, double controlW, double controlH, Stretch stretch)
    {
        double fw = descriptor.Width;
        double fh = descriptor.Height;
        int rot = ((int)descriptor.RotationDegrees % 360 + 360) % 360;
        if (rot == 90 || rot == 270)
            (fw, fh) = (fh, fw);
        if (fw <= 0 || fh <= 0 || controlW <= 0 || controlH <= 0)
            return SKRect.Empty;

        double scaleX = controlW / fw;
        double scaleY = controlH / fh;
        double scale, dx, dy;
        switch (stretch)
        {
            case Stretch.Fill:
                return new SKRect(0, 0, (float)controlW, (float)controlH);
            case Stretch.UniformToFill:
                scale = Math.Max(scaleX, scaleY);
                dx = (controlW - fw * scale) / 2;
                dy = (controlH - fh * scale) / 2;
                break;
            default: // Uniform（默认）
                scale = Math.Min(scaleX, scaleY);
                dx = (controlW - fw * scale) / 2;
                dy = (controlH - fh * scale) / 2;
                break;
        }

        double w = fw * scale;
        double h = fh * scale;
        // 裁剪保险：UniformToFill 可能越界，夹回控件内。
        double left = Math.Max(0, Math.Min(dx, controlW));
        double top = Math.Max(0, Math.Min(dy, controlH));
        double right = Math.Max(left, Math.Min(dx + w, controlW));
        double bottom = Math.Max(top, Math.Min(dy + h, controlH));
        return new SKRect((float)left, (float)top, (float)right, (float)bottom);
    }
}
