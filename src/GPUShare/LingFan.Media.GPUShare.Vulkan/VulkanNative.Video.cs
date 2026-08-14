using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// <see cref="VulkanNative"/> 的 VK_KHR_video_decode 设备级扩展解析（部分类）。
/// </summary>
/// <remarks>
/// <para>与渲染命令同源机制：经 <see cref="InitVideoDevice(Device)"/> 用 <c>vkGetDeviceProcAddr</c> 解析视频解码子集。
/// 解析为 null 时（设备未启用 VK_KHR_video_decode_* 扩展）对应包装方法抛 <see cref="InvalidOperationException"/>，
/// 调用方须回落软件解码——符合「S_OK≠被接受」与跨平台降级铁律。</para>
/// <para>视频解码函数指针独立于渲染函数指针存储（独立 <c>_videoReady</c> 标志），
/// 同一 VkDevice 上渲染与视频解码可各自按需解析，互不阻塞。</para>
/// </remarks>
public static unsafe partial class VulkanNative
{
    private static bool _videoReady;

    /// <summary>
    /// 阶段 3：视频解码设备级函数解析。须传入已创建（且已启用 VK_KHR_video_decode_queue 等）的 <see cref="Device"/>。
    /// 经 <c>vkGetDeviceProcAddr(device, name)</c> 解析 VK_KHR_video_decode 子集。幂等。
    /// </summary>
    public static unsafe void InitVideoDevice(Device device)
    {
        if (_videoReady) return;
        lock (_loadLock)
        {
            if (_videoReady) return;
            nint h = device.Handle;
            _createVideoSessionKHR = (delegate* unmanaged[Stdcall]<Device, VideoSessionCreateInfoKHR*, AllocationCallbacks*, VideoSessionKHR*, Result>)vkGetDeviceProcAddr(h, "vkCreateVideoSessionKHR");
            _destroyVideoSessionKHR = (delegate* unmanaged[Stdcall]<Device, VideoSessionKHR, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyVideoSessionKHR");
            _getVideoSessionMemoryRequirementsKHR = (delegate* unmanaged[Stdcall]<Device, VideoSessionKHR, uint*, VideoSessionMemoryRequirementsKHR*, Result>)vkGetDeviceProcAddr(h, "vkGetVideoSessionMemoryRequirementsKHR");
            _bindVideoSessionMemoryKHR = (delegate* unmanaged[Stdcall]<Device, VideoSessionKHR, uint, BindVideoSessionMemoryInfoKHR*, Result>)vkGetDeviceProcAddr(h, "vkBindVideoSessionMemoryKHR");
            _createVideoSessionParametersKHR = (delegate* unmanaged[Stdcall]<Device, VideoSessionParametersCreateInfoKHR*, AllocationCallbacks*, VideoSessionParametersKHR*, Result>)vkGetDeviceProcAddr(h, "vkCreateVideoSessionParametersKHR");
            _updateVideoSessionParametersKHR = (delegate* unmanaged[Stdcall]<Device, VideoSessionParametersKHR, VideoSessionParametersUpdateInfoKHR*, Result>)vkGetDeviceProcAddr(h, "vkUpdateVideoSessionParametersKHR");
            _destroyVideoSessionParametersKHR = (delegate* unmanaged[Stdcall]<Device, VideoSessionParametersKHR, AllocationCallbacks*, void>)vkGetDeviceProcAddr(h, "vkDestroyVideoSessionParametersKHR");
            _cmdBeginVideoCodingKHR = (delegate* unmanaged[Stdcall]<CommandBuffer, VideoBeginCodingInfoKHR*, void>)vkGetDeviceProcAddr(h, "vkCmdBeginVideoCodingKHR");
            _cmdDecodeVideoKHR = (delegate* unmanaged[Stdcall]<CommandBuffer, VideoDecodeInfoKHR*, void>)vkGetDeviceProcAddr(h, "vkCmdDecodeVideoKHR");
            _cmdEndVideoCodingKHR = (delegate* unmanaged[Stdcall]<CommandBuffer, VideoEndCodingInfoKHR*, void>)vkGetDeviceProcAddr(h, "vkCmdEndVideoCodingKHR");
            _cmdControlVideoCodingKHR = (delegate* unmanaged[Stdcall]<CommandBuffer, VideoCodingControlInfoKHR*, void>)vkGetDeviceProcAddr(h, "vkCmdControlVideoCodingKHR");
            _getPhysicalDeviceVideoCapabilitiesKHR = (delegate* unmanaged[Stdcall]<PhysicalDevice, VideoProfileInfoKHR*, VideoCapabilitiesKHR*, Result>)vkGetDeviceProcAddr(h, "vkGetPhysicalDeviceVideoCapabilitiesKHR");
            _videoReady = true;
        }
    }

    private static unsafe delegate* unmanaged[Stdcall]<Device, VideoSessionCreateInfoKHR*, AllocationCallbacks*, VideoSessionKHR*, Result> _createVideoSessionKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, VideoSessionKHR, AllocationCallbacks*, void> _destroyVideoSessionKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, VideoSessionKHR, uint*, VideoSessionMemoryRequirementsKHR*, Result> _getVideoSessionMemoryRequirementsKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, VideoSessionKHR, uint, BindVideoSessionMemoryInfoKHR*, Result> _bindVideoSessionMemoryKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, VideoSessionParametersCreateInfoKHR*, AllocationCallbacks*, VideoSessionParametersKHR*, Result> _createVideoSessionParametersKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, VideoSessionParametersKHR, VideoSessionParametersUpdateInfoKHR*, Result> _updateVideoSessionParametersKHR;
    private static unsafe delegate* unmanaged[Stdcall]<Device, VideoSessionParametersKHR, AllocationCallbacks*, void> _destroyVideoSessionParametersKHR;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, VideoBeginCodingInfoKHR*, void> _cmdBeginVideoCodingKHR;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, VideoDecodeInfoKHR*, void> _cmdDecodeVideoKHR;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, VideoEndCodingInfoKHR*, void> _cmdEndVideoCodingKHR;
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, VideoCodingControlInfoKHR*, void> _cmdControlVideoCodingKHR;
    private static unsafe delegate* unmanaged[Stdcall]<PhysicalDevice, VideoProfileInfoKHR*, VideoCapabilitiesKHR*, Result> _getPhysicalDeviceVideoCapabilitiesKHR;

    // ── 物理设备视频能力查询 ──

    public static unsafe Result GetPhysicalDeviceVideoCapabilitiesKHR(PhysicalDevice physicalDevice, VideoProfileInfoKHR* pVideoProfile, VideoCapabilitiesKHR* pCapabilities)
    {
        if (_getPhysicalDeviceVideoCapabilitiesKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetPhysicalDeviceVideoCapabilitiesKHR（VK_KHR_video_queue 不可用）。");
        return _getPhysicalDeviceVideoCapabilitiesKHR(physicalDevice, pVideoProfile, pCapabilities);
    }

    // ── 视频会话（Video Session）生命周期 ──

    public static unsafe Result CreateVideoSessionKHR(Device device, ref VideoSessionCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out VideoSessionKHR pVideoSession)
    {
        if (_createVideoSessionKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateVideoSessionKHR（VK_KHR_video_decode_queue 不可用）。");
        fixed (VideoSessionCreateInfoKHR* p = &pCreateInfo)
        {
            VideoSessionKHR tmp;
            var r = _createVideoSessionKHR(device, p, pAllocator, &tmp);
            pVideoSession = tmp;
            return r;
        }
    }

    public static unsafe void DestroyVideoSessionKHR(Device device, VideoSessionKHR videoSession, AllocationCallbacks* pAllocator)
    {
        if (_destroyVideoSessionKHR == null) return;
        _destroyVideoSessionKHR(device, videoSession, pAllocator);
    }

    public static unsafe Result GetVideoSessionMemoryRequirementsKHR(Device device, VideoSessionKHR videoSession, ref uint pMemoryRequirementsCount, VideoSessionMemoryRequirementsKHR* pMemoryRequirements)
    {
        if (_getVideoSessionMemoryRequirementsKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkGetVideoSessionMemoryRequirementsKHR（VK_KHR_video_decode_queue 不可用）。");
        fixed (uint* p = &pMemoryRequirementsCount)
            return _getVideoSessionMemoryRequirementsKHR(device, videoSession, p, pMemoryRequirements);
    }

    public static unsafe Result BindVideoSessionMemoryKHR(Device device, VideoSessionKHR videoSession, uint bindIndex, BindVideoSessionMemoryInfoKHR* pBindInfo)
    {
        if (_bindVideoSessionMemoryKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkBindVideoSessionMemoryKHR（VK_KHR_video_decode_queue 不可用）。");
        return _bindVideoSessionMemoryKHR(device, videoSession, bindIndex, pBindInfo);
    }

    // ── 视频会话参数（SPS/PPS 等 codec 参数） ──

    public static unsafe Result CreateVideoSessionParametersKHR(Device device, ref VideoSessionParametersCreateInfoKHR pCreateInfo, AllocationCallbacks* pAllocator, out VideoSessionParametersKHR pVideoSessionParameters)
    {
        if (_createVideoSessionParametersKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCreateVideoSessionParametersKHR（VK_KHR_video_decode_queue 不可用）。");
        fixed (VideoSessionParametersCreateInfoKHR* p = &pCreateInfo)
        {
            VideoSessionParametersKHR tmp;
            var r = _createVideoSessionParametersKHR(device, p, pAllocator, &tmp);
            pVideoSessionParameters = tmp;
            return r;
        }
    }

    public static unsafe Result UpdateVideoSessionParametersKHR(Device device, VideoSessionParametersKHR videoSessionParameters, VideoSessionParametersUpdateInfoKHR* pUpdateInfo)
    {
        if (_updateVideoSessionParametersKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkUpdateVideoSessionParametersKHR（VK_KHR_video_decode_queue 不可用）。");
        return _updateVideoSessionParametersKHR(device, videoSessionParameters, pUpdateInfo);
    }

    public static unsafe void DestroyVideoSessionParametersKHR(Device device, VideoSessionParametersKHR videoSessionParameters, AllocationCallbacks* pAllocator)
    {
        if (_destroyVideoSessionParametersKHR == null) return;
        _destroyVideoSessionParametersKHR(device, videoSessionParameters, pAllocator);
    }

    // ── 解码命令（须包在 vkCmdBeginVideoCodingKHR … vkCmdEndVideoCodingKHR 之间）──

    public static unsafe void CmdBeginVideoCodingKHR(CommandBuffer commandBuffer, VideoBeginCodingInfoKHR* pBeginInfo)
    {
        if (_cmdBeginVideoCodingKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCmdBeginVideoCodingKHR（VK_KHR_video_decode_queue 不可用）。");
        _cmdBeginVideoCodingKHR(commandBuffer, pBeginInfo);
    }

    public static unsafe void CmdDecodeVideoKHR(CommandBuffer commandBuffer, VideoDecodeInfoKHR* pFrameInfo)
    {
        if (_cmdDecodeVideoKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCmdDecodeVideoKHR（VK_KHR_video_decode_queue 不可用）。");
        _cmdDecodeVideoKHR(commandBuffer, pFrameInfo);
    }

    public static unsafe void CmdEndVideoCodingKHR(CommandBuffer commandBuffer, VideoEndCodingInfoKHR* pEndInfo)
    {
        if (_cmdEndVideoCodingKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCmdEndVideoCodingKHR（VK_KHR_video_decode_queue 不可用）。");
        _cmdEndVideoCodingKHR(commandBuffer, pEndInfo);
    }

    public static unsafe void CmdControlVideoCodingKHR(CommandBuffer commandBuffer, VideoCodingControlInfoKHR* pControlInfo)
    {
        if (_cmdControlVideoCodingKHR == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCmdControlVideoCodingKHR（VK_KHR_video_decode_queue 不可用）。");
        _cmdControlVideoCodingKHR(commandBuffer, pControlInfo);
    }
}
