namespace LingFan.Media.Renderers.Vulkan;

// VulkanSharedSurfaceSource 的共享表面释放与内存类型职责段（部分类，按职责拆分，编译期等价）。
// 双槽释放上界与建槽循环同源；内存类型查询（含外部兼容偏好 DeviceLocal）。创建路径在 Resources 主段。
internal sealed unsafe partial class VulkanSharedSurfaceSource
{
    private void ReleaseSharedSurface()
    {
        if (_device.Handle == 0) return;
        // 释放上界与建槽循环（EnsureSharedSurface）同源：双槽形态必须两槽都释放，
        // 否则槽 1 的 VkImageView/VkImage/VkDeviceMemory 永久泄漏（且 _sharedImages[1] 残留非零句柄）。
        for (int slot = 0; slot < _slotCount; slot++)
        {
            if (_sharedImageViews[slot].Handle != 0)
            {
                VulkanNative.DestroyImageView(_device, _sharedImageViews[slot], null);
                _sharedImageViews[slot] = default;
            }
            if (_sharedImages[slot].Handle != 0)
            {
                VulkanNative.DestroyImage(_device, _sharedImages[slot], null);
                _sharedImages[slot] = default;
            }
            if (_sharedMemories[slot].Handle != 0)
            {
                VulkanNative.FreeMemory(_device, _sharedMemories[slot], null);
                _sharedMemories[slot] = default;
            }
            _sharedMemorySizes[slot] = 0;
        }
        if (_convertView.Handle != 0)
        {
            VulkanNative.DestroyImageView(_device, _convertView, null);
            _convertView = default;
        }
        if (_convertImage.Handle != 0)
        {
            VulkanNative.DestroyImage(_device, _convertImage, null);
            _convertImage = default;
        }
        if (_convertMemory.Handle != 0)
        {
            VulkanNative.FreeMemory(_device, _convertMemory, null);
            _convertMemory = default;
        }
        // 导出的外部句柄随内存释放自动失效，无需 CloseHandle/dup；_version 保留递增语义。
        _exportedMemoryHandle = IntPtr.Zero;
        // 复制完成标志与槽一一对应：按槽数清（双槽形态两槽都清），再复位活动槽。
        for (int slot = 0; slot < _slotCount; slot++)
            _sharedCopyReady[slot] = false;
        _sharedActive = 0;
        _texW = 0;
        _texH = 0;
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProps;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProps);
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }
        throw new InvalidOperationException("未找到合适的 Vulkan 内存类型（共享表面离屏）。");
    }

    private unsafe uint ExternalCompatibleMemoryType(uint memoryTypeBits)
    {
        PhysicalDeviceMemoryProperties props;
        VulkanNative.GetPhysicalDeviceMemoryProperties(_physicalDevice, &props);
        uint fallback = uint.MaxValue;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((memoryTypeBits & (1u << (int)i)) == 0) continue;
            if (fallback == uint.MaxValue) fallback = i;
            if ((props.MemoryTypes[(int)i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                return i;
        }
        return fallback == uint.MaxValue ? 0u : fallback;
    }
}
