using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan 零拷贝帧生产者：把解码后端原生输出（Windows D3D11 共享句柄 / Linux VAAPI dma_buf）
/// 经 Vulkan 外部内存导入为 VkImage（<see cref="VulkanImageResource"/>），实现零拷贝上屏。
/// </summary>
/// <remarks>
/// <para>注册为 <see cref="IGpuFrameProducer"/>，供解码器经中立桥调用，严守依赖倒置（后端不感知 Vulkan 绑定细节）。</para>
/// <para><b>导入机制</b>：创建带 VK_KHR_external_memory 的 VkImage，经 vkAllocateMemory +
/// <c>ImportMemoryWin32HandleInfoKHR</c>（Windows）/ <c>ImportMemoryFdInfoKHR</c>（Linux）绑定原生共享句柄，
/// vkBindImageMemory 后即为零拷贝 Vulkan 纹理，由 <see cref="VulkanRenderer.BlitVulkanImageResource(VulkanImageResource, Image)"/> 直接 blit 到 SwapChain。</para>
/// <para><b>能力自报 + 行为副作用双判据（S_OK≠被接受）</b>：扩展不可用 / 句柄无效 / 导入失败 →
/// <see cref="TryImport"/> 返回 <see langword="false"/>，调用方回落软解并计入 CPU 拷贝统计，绝不报"已就绪"假绿。</para>
/// <para><b>AOT</b>：VulkanNative 为零反射绑定（vkGetDeviceProcAddr 解析外部内存函数指针）；
/// 无 [DllImport]/[ComImport]/反射；跨平台经 OperatingSystem.IsXxx() 运行时分发，无 #if。</para>
/// <para><b>支持范围</b>：Windows(D3D11→VK) + Linux(VAAPI→VK) + Android(AHB→VK，外部格式 YCbCr 采样转换)。
/// Apple(IOSurface) 为后续端点，当前返回 false（调用方回落软解），不阻断播放。</para>
    /// <para><b>多切片</b>：按 <see cref="GpuFrameImportSource.ArrayLayers"/> 创建整数组 VkImage，并据
    /// <see cref="GpuFrameImportSource.SubresourceIndex"/> 在 <see cref="VulkanRenderer.BlitVulkanImageResource(VulkanImageResource, Image)"/>
    /// 选 <c>baseArrayLayer</c>，正确处理 D3D11VA 纹理数组（切片索引=avFrame-&gt;data[1]）。</para>
/// <para><b>句柄所有权契约（单一责任人）</b>：原生共享句柄（NT HANDLE / dma_buf fd）的所有权自
/// <see cref="TryImport"/> 调用起转移至本生产者；无论导入成功或失败，生产者均在返回前
/// 经 <c>CloseHandle</c>（NT HANDLE）/ close（fd）关闭句柄（导入成功后资源引用已由 VkImage/VkDeviceMemory 持有，
/// 关闭句柄不销毁资源）。调用方（解码器）导出句柄后<b>不得</b>再关闭，避免双关。
/// Android AHardwareBuffer 为<b>例外</b>：AHB 指针由调用方借用（不转移所有权），生产者不 acquire/release/关闭，
/// 调用方在 TryImport 返回后自行释放 AImage/AHB 引用（详见 <c>TryImportAndroidAHardwareBuffer</c>）。</para>
/// </remarks>
public sealed unsafe partial class VulkanGpuFrameProducer : IGpuFrameProducer, IDisposable
{
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly ILogger? _logger;

    // NV12 导入转码资源（惰性创建，随生产者生命周期在 Dispose 释放；device/physicalDevice 为共享、不拥有）
    private VulkanNv12ToRgbaConverter? _converter;
    // Android AHB（外部格式 YCbCr）导入转码资源（惰性创建，随生产者生命周期在 Dispose 释放）
    private VulkanYcbcrToRgbaConverter? _ycbcrConverter;
    private CommandPool _commandPool;          // 默认 Handle==0：未创建
    private Queue _graphicsQueue;             // 默认 Handle==0：未创建
    private CommandBuffer _cmdBuffer;         // 默认 Handle==0：未创建
    private uint _graphicsQueueFamily = uint.MaxValue;

    // RGBA GPU→CPU 回读（Android Skia 合成路径消费 GPU 帧）
    // 独立命令池/命令缓冲/栅栏/staging buffer：与解码线程的 YCbCr/NV12 转换命令资源完全隔离。
    // 提交共用同一图形队列 —— VkQueue 非线程安全（vkQueueSubmit/QueueWaitIdle 须宿主同步），
    // 故与转换路径共享 _queueGate 串行化提交段（两段均为毫秒级，30fps 下无争用压力）。
    private readonly object _queueGate = new();
    private CommandPool _readbackCommandPool;
    private CommandBuffer _readbackCommandBuffer;
    private Fence _readbackFence;
    private Buffer _readbackStagingBuffer;
    private DeviceMemory _readbackStagingMemory;
    private void* _readbackStagingMapped;
    private ulong _readbackStagingSize;

    // 零拷贝导入诊断：仅前 3 次导入失败打印具体失败步骤 + Vulkan Result 码，避免逐帧刷屏。
    private int _diagRemain = 3;
    // AHB 首帧进入日志标记：仅首帧打印一次，与解码器侧 [AHB-TRACE] 日志对照可判断导入进行到的阶段（避免逐帧刷屏）。
    private bool _ahbEntryLogged;

    /// <inheritdoc/>
    public GPUApiType ApiType => GPUApiType.Vulkan;

    /// <summary>
    /// 预检导入能力（解码器据此决定是否启用对应零拷贝路径，如 Android ImageReader Surface configure）。
    /// Android AHB 双判据：VK_ANDROID_external_memory_android_hardware_buffer 扩展已启用（函数可解析）
    /// <b>且</b> samplerYcbcrConversion 特性已在设备创建期启用（HasSamplerYcbcrConversion 双判据——
    /// 特性未启用时 YCbCr 采样属规范违规，驱动未定义行为可致进程级原生崩溃，绝不可走）。
    /// </summary>
    public bool IsImportSupported(GpuFrameImportKind kind) => kind switch
    {
        GpuFrameImportKind.AndroidHardwareBuffer => OperatingSystem.IsAndroid()
            && VulkanNative.HasAndroidHardwareBufferProperties
            && VulkanNative.HasSamplerYcbcrConversion,
        GpuFrameImportKind.D3D11SharedHandle => OperatingSystem.IsWindows(),
        GpuFrameImportKind.LinuxDmaBufFd => OperatingSystem.IsLinux(),
        _ => false,
    };

    /// <summary>初始化 <see cref="VulkanGpuFrameProducer"/> 的新实例。</summary>
    /// <param name="device">共享 Vulkan 逻辑设备（借用引用，本生产者 Dispose 不销毁）。</param>
    /// <param name="physicalDevice">共享 Vulkan 物理设备（借用引用，本生产者 Dispose 不销毁）。</param>
    /// <param name="logger">可选日志器。</param>
    public VulkanGpuFrameProducer(Device device, PhysicalDevice physicalDevice, ILogger? logger = null)
    {
        _device = device;
        _physicalDevice = physicalDevice;
        _logger = logger;
    }

    /// <inheritdoc/>
    public unsafe bool TryImport(GpuFrameImportSource source, out IGpuTextureResource? texture)
    {
        texture = null;
        try
        {
            if (source.Handle == IntPtr.Zero)
                return false;
            if (source.Width <= 0 || source.Height <= 0)
            {
                CloseUnconsumedHandle(source); // 早退出口：句柄未被消费，须关闭防泄漏（单一责任人；AHB 借用例外）
                return false;
            }

            // Android AHB（YUV 外部格式）：先于 NV12 格式分支——AHB 句柄经
            // VK_ANDROID_external_memory_android_hardware_buffer 导入（UNDEFINED + externalFormat + YCbCr 采样），
            // 与共享句柄 / dma_buf 的 NV12 导入语义不同，不可落入 TryImportNv12。
            if (OperatingSystem.IsAndroid() && source.Kind == GpuFrameImportKind.AndroidHardwareBuffer)
                return TryImportAndroidAHardwareBuffer(source, out texture);

            // NV12 外部纹理（VAAPI·Android NV12 / D3D11VA NV12 共享句柄）：导入为 NV12 VkImage
            // → GPU 内转 RGBA（零 CPU 拷贝）→ 交付 RGBA VulkanImageResource。须先于 RGBA 分支。
            if (source.Format == PixelFormat.NV12)
                return TryImportNv12(source, out texture);

            if (OperatingSystem.IsWindows() && source.Kind == GpuFrameImportKind.D3D11SharedHandle)
                return TryImportWin32(source, out texture);
            if (OperatingSystem.IsLinux() && source.Kind == GpuFrameImportKind.LinuxDmaBufFd)
                return TryImportLinux(source, out texture);

            // iOS：后续端点（IOSurface），当前回落软解。
            CloseUnconsumedHandle(source); // 不支持端点出口：句柄未被消费，须关闭防泄漏（单一责任人；AHB 借用例外）
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Vulkan 零拷贝导入失败，回落软件解码（S_OK≠被接受：导入行为副作用未成立）。");
            texture?.Dispose();
            texture = null;
            return false;
        }
    }

    /// <summary>
    /// 释放 NV12 转码资源（命令池 + 中性转换器）。device/physicalDevice 为共享引用，不在此销毁。
    /// 由 DI 容器在 Singleton 生命周期结束时调用（接口 <see cref="IGpuFrameProducer"/> 不继承 <see cref="IDisposable"/>，
    /// 但具体类型实现后容器仍会释放）。
    /// </summary>
    public unsafe void Dispose()
    {
        if (_commandPool.Handle != 0)
        {
            VulkanNative.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }

        // 回读资源（staging 持久映射，先 Unmap 再销毁；命令池销毁级联释放其命令缓冲）
        if (_readbackStagingBuffer.Handle != 0)
        {
            VulkanNative.UnmapMemory(_device, _readbackStagingMemory);
            VulkanNative.DestroyBuffer(_device, _readbackStagingBuffer, null);
            VulkanNative.FreeMemory(_device, _readbackStagingMemory, null);
            _readbackStagingBuffer = default;
            _readbackStagingMemory = default;
            _readbackStagingMapped = null;
            _readbackStagingSize = 0;
        }
        if (_readbackFence.Handle != 0)
        {
            VulkanNative.DestroyFence(_device, _readbackFence, null);
            _readbackFence = default;
        }
        if (_readbackCommandPool.Handle != 0)
        {
            VulkanNative.DestroyCommandPool(_device, _readbackCommandPool, null);
            _readbackCommandPool = default;
        }

        _converter?.Dispose();
        _converter = null;
        _ycbcrConverter?.Dispose();
        _ycbcrConverter = null;
    }
}
