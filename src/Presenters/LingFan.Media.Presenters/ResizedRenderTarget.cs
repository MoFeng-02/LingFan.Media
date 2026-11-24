using System;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Presenters;

/// <summary>
/// 保留原句柄形态、仅替换尺寸/缩放的渲染目标包装。
/// 供各 GPU Presenter 的 Resize 重建 SwapChain 使用：原生句柄形态随后端而异
/// （Windows/Android=IntPtr，Linux=X11/Wayland 中性句柄，Apple=CAMetalLayer*/NSView*），
/// 不能统一重包成仅接受 IntPtr 的 <see cref="GpuRenderTarget"/>。
/// </summary>
internal sealed class ResizedRenderTarget : IRenderTarget
{
    private readonly IRenderTarget _original;

    /// <summary>以原目标与新尺寸构造。</summary>
    /// <param name="original">原渲染目标（句柄形态与类型按其保留）。</param>
    /// <param name="width">新宽度（像素）。</param>
    /// <param name="height">新高度（像素）。</param>
    /// <param name="scale">新显示缩放系数。</param>
    public ResizedRenderTarget(IRenderTarget original, int width, int height, float scale)
    {
        _original = original ?? throw new ArgumentNullException(nameof(original));
        Width = width;
        Height = height;
        Scale = scale;
    }

    /// <inheritdoc/>
    public RenderTargetType Type => _original.Type;

    /// <inheritdoc/>
    public RenderHandleType HandleType => _original.HandleType;

    /// <inheritdoc/>
    public object NativeHandle => _original.NativeHandle;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public float Scale { get; }
}