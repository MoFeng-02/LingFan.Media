using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using LingFan.Media.Abstractions;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Avalonia;

// CompositionVideoRenderer 的 Layout 职责段（部分类，由 CompositionVideoRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/PresentUi/Dispose）在主文件；DirectComposition GPU 互操作导入与表面布局。
internal sealed partial class CompositionVideoRenderer
{
    private void OnControlSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _controlSize = new Vector(e.NewSize.Width, e.NewSize.Height);
        UpdateSurfaceLayout();
    }

    private void OnStretchChanged()
    {
        if (_attachedVisual is Control control)
            _stretch = control.GetValue(VideoView.StretchProperty);
        UpdateSurfaceLayout();
    }

    /// <summary>
    /// 根据 <see cref="VideoView.Stretch"/> 模式计算 <see cref="CompositionSurfaceVisual"/> 的目标尺寸与偏移。
    /// CompositionSurfaceVisual 没有内置 Stretch，须手动实现 Fill/Uniform/UniformToFill/None。
    /// </summary>
    private void UpdateSurfaceLayout()
    {
        if (_surfaceVisual is null)
            return;

        if (_frameSize.X <= 0 || _frameSize.Y <= 0 || _controlSize.X <= 0 || _controlSize.Y <= 0)
        {
            // 尚未拿到帧尺寸：先按控件大小 Fill，避免 0 尺寸不可见。
            _surfaceVisual.Size = _controlSize;
            _surfaceVisual.Offset = new Vector3D(0, 0, 0);
            return;
        }

        double frameW = _frameSize.X;
        double frameH = _frameSize.Y;
        double ctrlW = _controlSize.X;
        double ctrlH = _controlSize.Y;
        double targetW = ctrlW;
        double targetH = ctrlH;

        switch (_stretch)
        {
            case Stretch.None:
                targetW = frameW;
                targetH = frameH;
                break;
            case Stretch.Uniform:
                double uniformScale = Math.Min(ctrlW / frameW, ctrlH / frameH);
                targetW = frameW * uniformScale;
                targetH = frameH * uniformScale;
                break;
            case Stretch.UniformToFill:
                double fillScale = Math.Max(ctrlW / frameW, ctrlH / frameH);
                targetW = frameW * fillScale;
                targetH = frameH * fillScale;
                break;
            case Stretch.Fill:
            default:
                // targetW/H 已等于控件尺寸
                break;
        }

        double offsetX = (ctrlW - targetW) / 2.0;
        double offsetY = (ctrlH - targetH) / 2.0;

        _surfaceVisual.Size = new Vector(targetW, targetH);
        _surfaceVisual.Offset = new Vector3D(offsetX, offsetY, 0);
    }
}
