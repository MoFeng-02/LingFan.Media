namespace LingFan.Media.GPUShare.Vulkan;

/// <summary>
/// <see cref="VulkanNative"/> 的 synchronization2 屏障命令绑定（部分类）。
/// </summary>
/// <remarks>
/// <para><c>vkCmdPipelineBarrier2</c> 是 Vulkan 1.3 核心函数（源自 VK_KHR_synchronization2），
/// 经 <c>vkGetDeviceProcAddr</c> 解析（核心名→KHR 别名回退由派发层统一处理）。
/// 相比核心 <c>vkCmdPipelineBarrier</c>，其阶段/访问掩码为 64 位（<see cref="PipelineStageFlags2"/> /
/// <see cref="AccessFlags2"/>），可精确表达 video-decode 阶段位与访问位——
/// 核心 32 位掩码不含这些位，通用 AllCommands 屏障对视频解码引擎的缓存可见性不保证等价。</para>
/// <para>解析为 null 时（设备未启用 synchronization2 且核心版本低于 1.3）包装方法抛
/// <see cref="InvalidOperationException"/>，调用方回落核心屏障路径。</para>
/// </remarks>
public static unsafe partial class VulkanNative
{
    private static unsafe delegate* unmanaged[Stdcall]<CommandBuffer, DependencyInfo*, void> _cmdPipelineBarrier2;
    private static bool _sync2Tried;

    /// <summary>
    /// 解析 synchronization2 屏障命令（幂等；设备级函数，须在设备创建后调用）。
    /// </summary>
    /// <param name="device">已创建的逻辑设备。</param>
    public static unsafe void InitSync2Device(Device device)
    {
        if (_sync2Tried) return;
        lock (_loadLock)
        {
            if (_sync2Tried) return;
            _cmdPipelineBarrier2 = (delegate* unmanaged[Stdcall]<CommandBuffer, DependencyInfo*, void>)
                vkGetDeviceProcAddr(device.Handle, "vkCmdPipelineBarrier2");
            _sync2Tried = true;
        }
    }

    /// <summary>synchronization2 屏障命令是否可用（设备已解析到 vkCmdPipelineBarrier2）。</summary>
    public static bool HasPipelineBarrier2 => _cmdPipelineBarrier2 != null;

    /// <summary>
    /// 录制 64 位阶段/访问掩码的管线屏障（vkCmdPipelineBarrier2；未解析时抛异常，调用方须先经
    /// <see cref="InitSync2Device"/> 解析并以 <see cref="HasPipelineBarrier2"/> 判定可用性）。
    /// </summary>
    /// <param name="commandBuffer">正在记录的命令缓冲。</param>
    /// <param name="pDependencyInfo">依赖信息（内存/缓冲/图像屏障数组与依赖标志）。</param>
    public static unsafe void CmdPipelineBarrier2(CommandBuffer commandBuffer, DependencyInfo* pDependencyInfo)
    {
        if (_cmdPipelineBarrier2 == null)
            throw new InvalidOperationException("VulkanNative 未解析 vkCmdPipelineBarrier2（synchronization2 不可用）。");
        _cmdPipelineBarrier2(commandBuffer, pDependencyInfo);
    }
}