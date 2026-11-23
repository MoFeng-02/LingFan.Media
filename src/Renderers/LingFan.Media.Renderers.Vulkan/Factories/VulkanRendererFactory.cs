using LingFan.Media.Abstractions;
using LingFan.Media.GPUShare.D3D11;
using LingFan.Media.Renderers.Shared;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// <see cref="IVideoRendererFactory"/> 的 Vulkan 实现。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。持有共享的 <c>VkInstance</c>、
/// <c>VkPhysicalDevice</c>、<c>VkDevice</c>、<c>VkQueue</c>；
/// <see cref="Create"/> 返回<b>缓存单例</b> <see cref="VulkanRenderer"/>
/// （共享 GPU Device，与 D3D11RendererFactory 模式一致）。</para>
/// <para>Vulkan 是跨平台 API（Windows / Linux / Android；macOS/iOS 经 MoltenVK 覆盖——
/// 仅引入 MoltenVK 让 Vulkan 后端在 Apple 平台初始化/跑 SwapChain，无空域零拷贝上屏属第二类，待 Apple 合成栈落地）。
/// Surface 创建用 Vulkan 自己的 WSI 扩展（Win32 / Android / Metal），不需要平台互操作文件。</para>
    /// <para>WSI 扩展（Surface/Swapchain）由 <c>VulkanNative</c> 零反射绑定经三阶段解析
    /// （实例句柄解析实例级 / WSI 实例扩展，设备句柄解析设备级 / WSI 设备扩展），
    /// 不使用 Silk.NET 的 <c>Vk</c> / <c>Khr*</c> 对象与加载层。</para>
/// <para>AOT 兼容：sealed 类，零反射、无 Silk.NET 运行期依赖。</para>
/// <para><b>资源安全</b>：<see cref="EnsureDeviceCreated"/> 内部使用局部变量 + try-catch 统一清理，
/// 任何步骤失败都按创建逆序释放已分配的 Vulkan 资源，杜绝泄漏。所有字段仅在全部成功后赋值。</para>
/// </remarks>
public sealed unsafe partial class VulkanRendererFactory : IVideoRendererFactory, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VulkanRendererFactory> _logger;
    private readonly object _deviceLock = new();
    private readonly object _singletonLock = new();

    // 共享 Vulkan 资源（Singleton，由本工厂管理生命周期）
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;

    // 外部共享 device：宿主注入后 EnsureDeviceCreated 直接采用，不自建
    private Instance _externalInstance;
    private PhysicalDevice _externalPhysicalDevice;
    private Device _externalDevice;
    private uint _externalQueueFamilyIndex;
    private uint _queueFamilyIndex;
    private Queue _videoQueue;
    private uint _videoQueueFamilyIndex = uint.MaxValue;
    private RenderContext? _renderContext;

    // 所选物理设备的身份（供 no-airspace 源验证「同 GPU 对齐」）。
    private byte[] _physicalDeviceUuid = [];
    private byte[] _physicalDeviceLuid = [];
    // 零拷贝跨 API 导入对齐：优先选择 LUID 与此一致的物理设备。
    // ffmpeg 的 D3D11VA 共享纹理由「默认 D3D11 适配器」创建，Vulkan 必须选同一 GPU 才能导入
    // （跨厂商/跨 GPU 导入会被驱动拒绝，报 ErrorOutOfDeviceMemory）。默认 null = 不强制，
    // 沿用「独显优先」启发式（纯 Vulkan 渲染性能最优）。
    private byte[]? _preferredAdapterLuid;
    // 零拷贝跨 API 导入对齐开关（自动路径）：true 时 EnsureDeviceCreated 内将自动查询默认 D3D11 适配器
    // LUID 并注入 _preferredAdapterLuid（与 PreferredAdapterLuid 手动 set 二选一，手动 set 优先）。
    // 仅 Windows 生效；非 Windows 跳过（无 DXGI LUID 概念）。
    private bool _alignToD3D11DefaultAdapter;
    // 是否已为 no-airspace 共享表面启用外部内存/信号量扩展。
    private bool _externalSharingEnabled;
    // Apple / MoltenVK：是否已启用 VK_EXT_metal_objects（无空域零拷贝经其导出 IOSurface / MTLSharedEvent）。
    private bool _metalObjectsSharingEnabled;

    // 无锁快路径发布哨兵（volatile，最后赋值，保证其余字段写入全部可见）
    private volatile bool _deviceReady;

    private VulkanRenderer? _singleton;
    private bool _disposed;

    /// <summary>软帧缩放模式（契约层 <see cref="AspectRatioMode"/>）。默认 <see cref="AspectRatioMode.Uniform"/>（信箱）。</summary>
    /// <remarks>创建渲染器单例时透传至其实例；运行时改此值对缓存单例立即生效。</remarks>
    public AspectRatioMode ScaleMode { get; set; } = AspectRatioMode.Uniform;

    /// <summary>
    /// 零拷贝跨 API 导入对齐：优先选择 LUID 与此字节数组（8 字节）一致的 Vulkan 物理设备。
    /// </summary>
    /// <remarks>
    /// <para>用于 D3D11VA 零拷贝场景——ffmpeg 的 D3D11 共享纹理由「默认 D3D11 适配器」创建，
    /// 若 Vulkan 渲染器选了另一张 GPU（如独显优先选中与 D3D11 默认适配器不同的卡），
    /// 导入 D3D11 共享句柄会被驱动拒绝（<c>ErrorOutOfDeviceMemory</c>）。设此值可强制 Vulkan
    /// 选与 D3D11 纹理同 GPU 的设备，使零拷贝成立。</para>
    /// <para>默认 null：不强制对齐，沿用「独显优先」启发式（纯 Vulkan 渲染性能最优）。</para>
    /// </remarks>
    public byte[]? PreferredAdapterLuid
    {
        get => _preferredAdapterLuid;
        set => _preferredAdapterLuid = value;
    }

    /// <summary>
    /// 零拷贝跨 API 导入对齐（自动路径）开关：为 <see langword="true"/> 时，
    /// <see cref="EnsureDeviceCreated"/> 内将自动查询默认 D3D11 适配器 LUID 并注入
    /// <see cref="PreferredAdapterLuid"/>（与手动 set <see cref="PreferredAdapterLuid"/> 二选一，手动优先）。
    /// </summary>
    /// <remarks>仅 Windows 生效；非 Windows 跳过（无 DXGI LUID 概念）。默认 <see langword="false"/>。</remarks>
    public bool AlignToD3D11DefaultAdapter
    {
        get => _alignToD3D11DefaultAdapter;
        set => _alignToD3D11DefaultAdapter = value;
    }

    /// <summary>初始化 <see cref="VulkanRendererFactory"/> 的新实例。</summary>
    /// <param name="loggerFactory">日志器工厂（创建本工厂及其派生资源的日志器）。</param>
    public VulkanRendererFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<VulkanRendererFactory>();
    }

    /// <summary>共享渲染上下文（首次访问触发 <see cref="EnsureDeviceCreated"/> 延迟创建 Vulkan 设备）。</summary>
    public RenderContext Context
    {
        get
        {
            EnsureDeviceCreated();
            return _renderContext!;
        }
    }

    // no-airspace 共享表面源（VulkanSharedSurfaceSource）访问共享 Vulkan 资源的内部入口
    // 仅同程序集（Vulkan 模块）可见：源经工厂构造，直接复用本工厂的 VkInstance/Device/Queue，
    // 严守「各 Renderer 管好自身（无头/有头/无空域）」架构原则，不跨界泄露给其它层。
    internal Instance SharedInstance => _instance;
    internal PhysicalDevice SharedPhysicalDevice => _physicalDevice;
    internal Device SharedDevice => _device;

    /// <summary>
    /// 注入宿主共享的 Vulkan device。注入后 <see cref="EnsureDeviceCreated"/> 不再自建
    /// instance/device，直接复用外部句柄（仅重新解析函数表），从而与宿主处于同一 device ——
    /// 共享表面源的 dma_buf fd 导入从「跨实例」变为「同 device」，规避 Adreno 跨实例导入缺陷。
    /// 必须在首次使用渲染器（EnsureDeviceCreated 触发）之前调用。
    /// </summary>
    public void UseExternalDevice(nint instance, nint physicalDevice, nint device, uint graphicsQueueFamilyIndex)
    {
        if (_deviceReady)
            throw new InvalidOperationException("Vulkan 设备已创建，不能再注入外部 device（须在使用前注入）。");
        _externalInstance = new Instance(instance);
        _externalPhysicalDevice = new PhysicalDevice(physicalDevice);
        _externalDevice = new Device(device);
        _externalQueueFamilyIndex = graphicsQueueFamilyIndex;
    }
    internal Queue SharedQueue => _queue;
    internal uint SharedQueueFamilyIndex => _queueFamilyIndex;
    internal ReadOnlyMemory<byte> PhysicalDeviceUuid => _physicalDeviceUuid;
    internal ReadOnlyMemory<byte> PhysicalDeviceLuid => _physicalDeviceLuid;
    internal bool ExternalSharingEnabled => _externalSharingEnabled;
    internal bool MetalObjectsSharingEnabled => _metalObjectsSharingEnabled;

    /// <inheritdoc/>
    public IVideoRenderer Create()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDeviceCreated();
        lock (_singletonLock)
        {
            if (_singleton is null || _singleton.IsDisposed)
            {
                _singleton = new VulkanRenderer(
                    _instance, _physicalDevice, _device, _queue, _queueFamilyIndex,
                    _loggerFactory.CreateLogger<VulkanRenderer>());
                _singleton.ScaleMode = this.ScaleMode;
                _logger.LogDebug("Vulkan 渲染器单例已创建（ScaleMode={ScaleMode}）", this.ScaleMode);
            }
            return _singleton;
        }
    }

    /// <summary>
    /// 创建零拷贝帧生产者（<see cref="IGpuFrameProducer"/>），供解码后端经中立桥把原生解码输出导入为 Vulkan 纹理。
    /// </summary>
    /// <remarks>
    /// 确保共享 GPU 设备已创建（延迟、线程安全）；返回单例生产者（持有本工厂的 VkDevice / VkPhysicalDevice）。
    /// 解码后端仅依赖 <see cref="IGpuFrameProducer"/> 抽象，不感知 Vulkan 绑定细节（依赖倒置严守）。
    /// </remarks>
    public VulkanGpuFrameProducer CreateFrameProducer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDeviceCreated();
        return new VulkanGpuFrameProducer(
            _device, _physicalDevice, _loggerFactory.CreateLogger<VulkanGpuFrameProducer>());
    }

    // 队列族查找 / 物理设备身份 / 设备名读取统一走 GPUShare.Vulkan 的 VulkanDeviceSelector（选卡与身份查询唯一实现）。

    /// <summary>
    /// 释放共享 Vulkan 设备、实例与缓存的渲染器单例。
    /// </summary>
    /// <remarks>
    /// 已知限制：无 finalizer 兜底。Vulkan 原生资源（VkInstance/VkDevice）不使用 SafeHandle 封装，
    /// 若 DI 容器未正确 Dispose（如应用异常退出）将泄漏。DI Singleton 生命周期确保正常场景 Dispose 被调用。
    /// 可考虑用 SafeHandle 封装 VkInstance/VkDevice 以获得 CLR Critical Finalizer 兜底。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _singleton?.Dispose();
        _singleton = null;

        if (_device.Handle != 0)
        {
            // 先 DeviceWaitIdle 确保 GPU 完成所有工作再释放
            VulkanNative.DeviceWaitIdle(_device);
            VulkanNative.DestroyDevice(_device, null);
        }
        if (_instance.Handle != 0)
            VulkanNative.DestroyInstance(_instance, null);

        _logger.LogDebug("Vulkan 设备已释放");
    }
}
