namespace LingFan.Media.Avalonia;

/// <summary>
/// IRenderTarget 的 Avalonia 实现。封装 Avalonia 平台句柄供原生 GPU Renderer 使用。
/// </summary>
/// <remarks>
/// <para>由 VideoView 创建并传给 IVideoRenderer.Attach()。</para>
/// <para>UI 模式下 HandleType=None（Skia 路径不需要原生句柄）。</para>
/// <para>原生 GPU 模式下 HandleType=Surface 或 Context（传平台原生句柄给 SwapChain 创建）。</para>
/// <para><b>异步策略</b>：全部 config 分类——纯属性，无方法，无 I/O。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射。NativeHandle 为 object，运行时显式 cast。</para>
/// </remarks>
public sealed class AvaloniaRenderTarget : IRenderTarget
{
    /// <inheritdoc/>
    public RenderTargetType Type { get; }

    /// <inheritdoc/>
    public RenderHandleType HandleType { get; }

    /// <inheritdoc/>
    public object NativeHandle { get; }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public float Scale { get; }

    /// <summary>
    /// 初始化 <see cref="AvaloniaRenderTarget"/> 的新实例。
    /// </summary>
    /// <param name="type">渲染目标类型。</param>
    /// <param name="handleType">渲染句柄类型。</param>
    /// <param name="nativeHandle">原生句柄（运行时显式 cast，不用反射）。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <param name="scale">DPI 缩放比。</param>
    public AvaloniaRenderTarget(
        RenderTargetType type,
        RenderHandleType handleType,
        object nativeHandle,
        int width,
        int height,
        float scale)
    {
        Type = type;
        HandleType = handleType;
        NativeHandle = nativeHandle ?? throw new ArgumentNullException(nameof(nativeHandle));
        Width = width;
        Height = height;
        Scale = scale;
    }
}
