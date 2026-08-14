using Microsoft.Extensions.Logging;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Backends.VulkanVideo.Decoders;

/// <summary>
/// <see cref="IVideoDecoderFactory"/> 的 Vulkan 硬解（VK_KHR_video_decode_h264）实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂，无状态。每次 <see cref="Create"/> 返回新实例（decoder.Initialize 内部完成 GPU 会话建立）。</para>
/// <para><b>异步策略</b>：</para>
/// <list type="bullet">
/// <item><see cref="Create"/>：同步，手动 new + <see cref="IVideoDecoder.Initialize"/>。</item>
/// <item><see cref="CreateAsync"/>：接口契约，无 I/O（手动 new + 同步 Initialize，GPU 会话建立是同步原生调用），
/// 返回 <see cref="Task.FromResult"/>。优先使用（支持 CT）。</item>
/// </list>
/// <para><b>回落语义</b>：Initialize 在 Vulkan 视频硬解不可用（无 Vulkan 渲染器 / 设备未启用 VK_KHR_video_decode_* /
/// SPS/PPS 解析失败）时抛 <see cref="NotSupportedException"/>，由管线换下一个 IVideoDecoderFactory 回退；
/// 绝不静默假绿（S_OK≠被接受）。</para>
/// </remarks>
public sealed class VulkanVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IGpuDeviceContext? _gpuContext;
    private readonly VulkanVideoOptions? _options;

    /// <summary>
    /// 初始化 <see cref="VulkanVideoDecoderFactory"/> 的新实例。
    /// </summary>
    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="gpuContext">可选 GPU 设备上下文（注册了 Vulkan 渲染器时由 DI 注入，提供共享 VkDevice / PhysicalDevice / video 队列族）。</param>
    /// <param name="options">可选 Vulkan 后端配置（AddVulkanVideo 注册的 Singleton）。</param>
    public VulkanVideoDecoderFactory(ILoggerFactory loggerFactory, IGpuDeviceContext? gpuContext = null, VulkanVideoOptions? options = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _gpuContext = gpuContext;
        _options = options;
    }

    /// <inheritdoc/>
    public IVideoDecoder Create(VideoCodec codec, VideoSettings settings)
    {
        var decoder = new VulkanVideoDecoder(_loggerFactory.CreateLogger<VulkanVideoDecoder>(), _gpuContext, _options);
        decoder.Initialize(codec, settings);
        return decoder;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 接口契约：无 I/O（手动 new + 同步 Initialize，GPU 会话建立是同步原生调用），返回 <see cref="Task.FromResult"/>。
    /// 优先使用此方法（支持 CT）。
    /// </remarks>
    public Task<IVideoDecoder> CreateAsync(VideoCodec codec, VideoSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IVideoDecoder>(Create(codec, settings));
    }
}
