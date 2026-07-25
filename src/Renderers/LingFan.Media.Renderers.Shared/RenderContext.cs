namespace LingFan.Media.Renderers.Shared;

/// <summary>
/// 渲染上下文。持有 GPU 设备/上下文共享信息。
/// </summary>
/// <remarks>
/// <para>由 <see cref="IVideoRendererFactory"/> 持有（Singleton 共享 GPU Device），
/// 但 SwapChain / CommandQueue 是 Session 级（每个 Renderer 实例独立）。</para>
/// <para>V1 最小实现——仅持有 GPU API 类型和共享设备引用。
/// V2 扩展为完整的 <c>IGpuDeviceContext</c>，包含设备能力查询、内存预算等。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class RenderContext
{
    /// <summary>GPU API 类型。</summary>
    public GPUApiType GpuApiType { get; }

    /// <summary>共享 GPU 设备对象（运行时显式 cast，如 ID3D11Device / VkDevice）。</summary>
    /// <remarks>
    /// V1 可为 null（软件渲染路径不使用 GPU 设备）。
    /// 具体类型由各 Renderer 模块决定，调用方通过 pattern matching 或显式 cast 获取。
    /// </remarks>
    public object? SharedDevice { get; }

    /// <summary>
    /// 初始化 <see cref="RenderContext"/> 的新实例。
    /// </summary>
    /// <param name="gpuApiType">GPU API 类型。</param>
    /// <param name="sharedDevice">共享 GPU 设备对象（可为 null）。</param>
    public RenderContext(GPUApiType gpuApiType, object? sharedDevice = null)
    {
        GpuApiType = gpuApiType;
        SharedDevice = sharedDevice;
    }
}
