using LingFan.Media.GPUShare.Android;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanSharedSurfaceSource 的 Android RGBA AHB 转换职责段（部分类，按职责拆分，编译期等价）。
// 非外部格式 RGBA AHB 的 concrete 导入直渲路径（绕开 Adreno YCbCr 崩溃）与分步提交诊断；
// YCbCr 转换路径在 Android 主段，直采样路径在 AndroidDirect 段。
internal sealed unsafe partial class VulkanSharedSurfaceSource
{
    /// <summary>
    /// 记录 RGBA AHB 帧的 GPU 零拷贝转换（绕开 Adreno YCbCr 崩溃路径）：把 MediaCodec 产出的
    /// <b>非外部格式</b> RGBA AHardwareBuffer 经 <c>VK_ANDROID_external_memory_android_hardware_buffer</c>
    /// 以 concrete <see cref="Format.R8G8B8A8Unorm"/> 导入为普通 RGBA VkImage（不带 externalFormat、无 YCbCr 转换），
    /// 再经 <see cref="VulkanRgbaToRgbaConverter"/> 用普通（可变）采样器采样，把 RGBA 直渲进共享离屏表面
    /// （<c>_sharedImage</c>），全程零 CPU 像素拷贝、零跨队列竞态。
    /// </summary>
    /// <returns>是否成功记录命令（true 时命令缓冲由本方法自提交，TryWriteFrame 跳过公共提交段）。</returns>
    private bool TryRecordAhbConversionRgba(AndroidHardwareBufferFrameResource ahb, int w, int h)
    {
        // 1) 查询 AHB 属性（AllocationSize / MemoryTypeBits，供专用内存导入；formatProps 取 format/externalFormat 诊断）。
        AndroidHardwareBufferFormatPropertiesANDROID formatProps = new()
        {
            SType = StructureType.AndroidHardwareBufferFormatPropertiesAndroid,
        };
        AndroidHardwareBufferPropertiesANDROID props = new()
        {
            SType = StructureType.AndroidHardwareBufferPropertiesAndroid,
            PNext = &formatProps,
        };
        if (VulkanNative.GetAndroidHardwareBufferPropertiesANDROID(_device, ahb.AhbHandle, &props) != Result.Success)
        {
            _logger.LogTrace("vkGetAndroidHardwareBufferPropertiesANDROID 失败（RGBA AHB 转换）。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }

        // 2) concrete RGBA VkImage：R8G8B8A8Unorm（与 _surfaceVkFormat(Android) 一致），仅 SAMPLED 用法。
        //    无 ExternalFormat pNext：RGBA 是 concrete 格式，不走外部格式（UNDEFINED）范式。
        ExternalMemoryImageCreateInfo extMem = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.AndroidHardwareBufferBitAndroid,
        };
        ImageCreateInfo ci = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _surfaceVkFormat,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            PNext = &extMem,
        };
        Image ahbImage = default;
        if (VulkanNative.CreateImage(_device, &ci, null, out ahbImage) != Result.Success)
        {
            _logger.LogTrace("vkCreateImage（RGBA AHB）失败。");
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }

        DeviceMemory ahbMemory = default;
        ImageView ahbView = default;
        bool memBound = false, viewCreated = false;
        try
        {
            // 3) dedicated 内存导入：Buffer 须存 AHardwareBuffer* 的【值】=(nint*)ahb.AhbHandle。
            var dedicated = new MemoryDedicatedAllocateInfo
            {
                SType = StructureType.MemoryDedicatedAllocateInfo,
                Image = ahbImage,
            };
            var imp = new ImportAndroidHardwareBufferInfoANDROID
            {
                SType = StructureType.ImportAndroidHardwareBufferInfoAndroid,
                Buffer = (nint*)ahb.AhbHandle,
                PNext = &dedicated,
            };
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &imp,
                AllocationSize = props.AllocationSize,
                MemoryTypeIndex = ExternalCompatibleMemoryType(props.MemoryTypeBits),
            };
            if (VulkanNative.AllocateMemory(_device, &ai, null, out ahbMemory) != Result.Success)
            {
                _logger.LogTrace("vkAllocateMemory（RGBA AHB 导入）失败。");
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                return false;
            }
            if (VulkanNative.BindImageMemory(_device, ahbImage, ahbMemory, 0) != Result.Success)
            {
                _logger.LogTrace("vkBindImageMemory（RGBA AHB 导入）失败。");
                VulkanNative.FreeMemory(_device, ahbMemory, null);
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                return false;
            }
            memBound = true;

            // 4) 普通 RGBA 转换管线 + 采样视图（无 YCbCr）。
            _rgbaConverter ??= new VulkanRgbaToRgbaConverter(_device, _physicalDevice, _logger);
            _rgbaConverter.EnsurePipeline(_surfaceVkFormat);
            ahbView = _rgbaConverter.CreateImageView(ahbImage);
            viewCreated = true;

            // 【诊断】一次性打印 RGBA AHB 导入（Adreno 兼容路径锚点）。
            if (!_ahbRgbaDiagLogged)
            {
                _ahbRgbaDiagLogged = true;
                _logger.LogInformation(
                    "[AHB-DIAG] 解码侧 RGBA AHB 导入（绕开 YCbCr）{W}x{H} AllocationSize={Size} memTypeBits=0x{Bits:X} vkFormat={Fmt} externalFormat=0x{Ext:X} ycbcrModel={Model}",
                    w, h, (ulong)props.AllocationSize, props.MemoryTypeBits,
                    formatProps.Format, formatProps.ExternalFormat, formatProps.SuggestedYcbcrModel);
            }

            // 5) AHB 源（普通 RGBA）→ 渲染进内部 _convertImage（Android）/ _sharedImages[0]（非 Android）。
            Image convertTarget = _isAndroid ? _convertImage : _sharedImages[0];
            ImageView convertView = _isAndroid ? _convertView : _sharedImageViews[0];
            _logger.LogTrace("[AHB-DIAG] 进入 Convert（RGBA AHB→_convertImage 普通采样）{W}x{H}", w, h);
            _rgbaConverter.Convert(_commandBuffer, ahbImage, ahbView, (uint)w, (uint)h, convertTarget, convertView);
            _logger.LogTrace("[AHB-DIAG] Convert 记录完成，准备分步提交");

            // 6) Android：分步提交。先提交并等待转换（RGBA AHB 采样），隔离采样是否触发设备级错误。
            if (_isAndroid)
            {
                if (!SubmitAhbStep("RGBA-AHB采样转换", w, h))
                {
                    _rgbaConverter.DestroyImageView(ahbView);
                    viewCreated = false;
                    VulkanNative.DestroyImage(_device, ahbImage, null);
                    VulkanNative.FreeMemory(_device, ahbMemory, null);
                    return false;
                }
                _rgbaConverter.DestroyImageView(ahbView);
                viewCreated = false;
                VulkanNative.DestroyImage(_device, ahbImage, null);
                VulkanNative.FreeMemory(_device, ahbMemory, null);

                // 7) 拷贝：_convertImage(TransferSrcOptimal) → 共享离屏 _sharedImage（仅 TRANSFER_DST）。
                VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
                CommandBufferBeginInfo begin2 = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                if (VulkanNative.BeginCommandBuffer(_commandBuffer, ref begin2) != Result.Success)
                {
                    _logger.LogWarning("[AHB-DIAG] 拷贝 BeginCommandBuffer 失败（RGBA）。");
                    return false;
                }
                CopyToSharedImage(_commandBuffer, ImageLayout.TransferSrcOptimal, w, h);
                if (!SubmitAhbStep("拷贝进共享离屏_sharedImage", w, h))
                    return false;
                _ahbSelfSubmitted = true; // 已自提交，TryWriteFrame 跳过公共提交段
                return true;
            }

            // 非 Android：同命令缓冲内转换完成，公共提交段负责提交。销毁瞬态 AHB 侧资源。
            _rgbaConverter.DestroyImageView(ahbView);
            viewCreated = false;
            VulkanNative.DestroyImage(_device, ahbImage, null);
            VulkanNative.FreeMemory(_device, ahbMemory, null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vulkan 共享表面 RGBA AHB 转换记录失败，交回软帧回退。");
            if (viewCreated) _rgbaConverter?.DestroyImageView(ahbView);
            if (memBound) VulkanNative.FreeMemory(_device, ahbMemory, null);
            if (ahbImage.Handle != 0) VulkanNative.DestroyImage(_device, ahbImage, null);
            VulkanNative.ResetCommandBuffer(_commandBuffer, 0);
            return false;
        }
    }

    /// <summary>
    /// Android AHB 分步提交：把当前记录的 <see cref="_commandBuffer"/> 提交并等待，检测 Mali DEVICE_LOST，
    /// 精确定位错误发生在 AHB YCbCr/RGBA 采样步还是写入共享离屏步。返回是否成功（无 device lost）。
    /// </summary>
    private bool SubmitAhbStep(string stepTag, int w, int h)
    {
        _logger.LogTrace("[AHB-DIAG] {Step} 进入提交（EndCommandBuffer 前）", stepTag);
        Result endR = VulkanNative.EndCommandBuffer(_commandBuffer);
        if (endR != Result.Success)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} EndCommandBuffer 失败：{Result}", stepTag, endR);
            return false;
        }
        // 本机 Roslyn 对「&字段」判定为 CS0212，故栈上取副本再取地址（跨环境安全写法）。
        Fence fence = _frameFence;
        VulkanNative.ResetFences(_device, 1, &fence);
        CommandBuffer cb = _commandBuffer;
        SubmitInfo si = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        Result subR = VulkanNative.QueueSubmit(_queue, 1, &si, (nint)_frameFence.Handle);
        if (subR != Result.Success)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} QueueSubmit 失败：{Result}", stepTag, subR);
            return false;
        }
        Result waitR = VulkanNative.WaitForFences(_device, 1, &fence, 1u, WriteWaitTimeoutNs);
        if (waitR == Result.ErrorDeviceLost)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} 触发 Mali DEVICE_LOST（GROUP_ERROR_FATAL）—— 定位到此步。", stepTag);
            return false;
        }
        if (waitR != Result.Success)
        {
            _logger.LogWarning("[AHB-DIAG] {Step} WaitForFences 失败：{Result}", stepTag, waitR);
            return false;
        }
        _logger.LogTrace("[AHB-DIAG] {Step} 提交成功（无 DEVICE_LOST）{W}x{H}", stepTag, w, h);
        return true;
    }
}
