namespace LingFan.Media.Backends.VulkanVideo;

/// <summary>
/// Vulkan 硬解后端入口（Singleton）。
/// </summary>
/// <remarks>
/// <para>仅持有选项与平台能力标记，无原生全局状态需释放——与 <see cref="LingFan.Media.Backends.MediaCodec.AndroidBackend"/>
/// 同源设计（注册 ≠ 立刻要 native 库；实际平台/能力检查在 decoder.Initialize 内执行）。</para>
/// <para>依赖倒置：本后端只依赖 Abstractions 契约与 GPUShare.Vulkan 绑定，绝不引用任何 Renderers 程序集。</para>
/// </remarks>
public sealed class VulkanVideoBackend
{
    /// <summary>后端选项（注册时创建，Singleton 生命周期）。</summary>
    public VulkanVideoOptions Options { get; }

    /// <summary>初始化 <see cref="VulkanVideoBackend"/> 的新实例。</summary>
    public VulkanVideoBackend(VulkanVideoOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }
}
