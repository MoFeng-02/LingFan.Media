using System.Diagnostics;
using LingFan.Media.GPUShare.Vulkan;

namespace LingFan.Media.Renderers.Vulkan;

// VulkanRenderer 的 Surface 职责段（部分类，由 VulkanRenderer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Attach/Present/Clear/Detach/Dispose）在主文件；全部同步原生调用，_gate 串行化。
internal sealed unsafe partial class VulkanRenderer
{
    private unsafe void CreateSurface(IRenderTarget target)
    {
        SurfaceKHR[] surfArr = new SurfaceKHR[1];
        Result result;

        if (OperatingSystem.IsWindows())
        {
            // VUID-VkWin32SurfaceCreateInfoKHR-hinstance-01307 要求有效 HINSTANCE，
            // 不能默认 0 靠驱动宽容（validation layer 必报错）。GetModuleHandleW(null) = 进程模块句柄。
            if (target.NativeHandle is not IntPtr handle || handle == IntPtr.Zero)
                throw new ArgumentException("渲染目标句柄无效。", nameof(target));
            var info = new Win32SurfaceCreateInfoKHR
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                Hinstance = GetModuleHandleW(0),
                Hwnd = handle,
            };
            result = VulkanNative.CreateWin32SurfaceKHR(_instance, ref info, null, out surfArr[0]);
        }
        else if (OperatingSystem.IsAndroid())
        {
            // NativeHandle 本身就是 ANativeWindow*（IRenderTarget 传来的原生窗口指针）。
            // 绝不能写 &handle——那是「指向栈局部变量的指针」，驱动会把栈地址当 ANativeWindow* 解引用（UB）。
            // 对照 Win32 路径 Hwnd = handle 的直接赋值语义：字段里装的必须是窗口指针值本身。
            if (target.NativeHandle is not IntPtr handle || handle == IntPtr.Zero)
                throw new ArgumentException("渲染目标句柄无效。", nameof(target));
            var info = new AndroidSurfaceCreateInfoKHR
            {
                SType = StructureType.AndroidSurfaceCreateInfoKhr,
                Window = (nint*)handle,
            };
            result = VulkanNative.CreateAndroidSurfaceKHR(_instance, ref info, null, out surfArr[0]);
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            // MoltenVK 路径：NativeHandle 应为宿主提供的 CAMetalLayer*（由 Apple 合成栈 / VideoView 注入）。
            // VK_EXT_metal_surface 须已在实例启用（VulkanRendererFactory.GetPlatformExtensions 已加 Apple 分支）。
            // MetalSurfaceCreateInfoEXT.PLayer 是 IntPtr*（指向 CAMetalLayer*），故取局部副本的地址传入。
            if (target.NativeHandle is not IntPtr layer || layer == IntPtr.Zero)
                throw new ArgumentException("渲染目标句柄无效。", nameof(target));
            var info = new MetalSurfaceCreateInfoEXT
            {
                SType = StructureType.MetalSurfaceCreateInfoExt,
                PLayer = &layer,
            };
            result = VulkanNative.CreateMetalSurfaceEXT(_instance, ref info, null, out surfArr[0]);
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux X11/Wayland Surface 需要 Display* 指针（Xlib 的 Dpy / Wayland 的 wl_display*），
            // 由 IRenderTarget.NativeHandle 以中性句柄类型 X11WindowHandle / WaylandWindowHandle 携带
            // （定义于 LingFan.Media.Renderers.Shared，非契约层）。Silk.NET 的 Xlib/Wayland surface 结构体
            // 中 Dpy / Display / Surface 字段为 IntPtr*（指向原生指针），故取局部副本地址传入，避免栈 UB。
            if (target.NativeHandle is X11WindowHandle x11)
            {
                nint dpy = x11.Display;
                Console.Error.WriteLine(
                    $"[VULKAN-ATTACH] CreateXlibSurfaceKHR：instance=0x{_instance.Handle:X} dpy=0x{dpy:X} window=0x{x11.Window:X}");
                // 字段语义：规范 Display* dpy —— Silk.NET 以 IntPtr* 表示「指针值字段」，
                // 必须把 Display 指针值直填进字段。此前 &dpy 填入栈地址，ICD 解引用栈地址当
                // Display* → XCB 层 NULL 解引用（segfault at 0 in libX11-xcb，三轮复现）。
                var info = new XlibSurfaceCreateInfoKHR
                {
                    SType = StructureType.XlibSurfaceCreateInfoKhr,
                    Dpy = (nint*)dpy,
                    Window = x11.Window,
                };
                result = VulkanNative.CreateXlibSurfaceKHR(_instance, ref info, null, out surfArr[0]);
            }
            else if (target.NativeHandle is WaylandWindowHandle wl)
            {
                // 同 Xlib：wl_display* / wl_surface* 指针值直填（勿取局部变量地址）。
                nint disp = wl.Display;
                nint surf = wl.Surface;
                var info = new WaylandSurfaceCreateInfoKHR
                {
                    SType = StructureType.WaylandSurfaceCreateInfoKhr,
                    Display = (nint*)disp,
                    Surface = (nint*)surf,
                };
                result = VulkanNative.CreateWaylandSurfaceKHR(_instance, ref info, null, out surfArr[0]);
            }
            else
                throw new PlatformNotSupportedException(
                    "Linux Vulkan Surface 需要 X11WindowHandle 或 WaylandWindowHandle（携带 Display* 与 Window/Surface 指针）。" +
                    "请由 X11/Wayland 集成层以中性句柄类型构造 IRenderTarget.NativeHandle。");
        }
        else
        {
            throw new PlatformNotSupportedException("Vulkan Surface 创建不支持当前平台。");
        }

        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan Surface 创建失败: {result}");
        _surface = surfArr[0];
    }

    private void CreateSwapchain(uint width, uint height)
    {
        SurfaceCapabilitiesKHR[] capsArr = new SurfaceCapabilitiesKHR[1];
        // 检查 GetPhysicalDeviceSurfaceCapabilities 返回值
        Result capsResult = VulkanNative.GetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, capsArr);
        if (capsResult != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceCapabilitiesKHR 失败: {capsResult}");
        ref SurfaceCapabilitiesKHR caps = ref capsArr[0];

        uint formatCount = 0;
        // 检查 GetPhysicalDeviceSurfaceFormats 返回值
        Result fmtResult = VulkanNative.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref formatCount, (SurfaceFormatKHR*)null);
        if (fmtResult != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceFormatsKHR 失败: {fmtResult}");
        if (formatCount == 0)
            throw new InvalidOperationException("Surface 无可用格式。");

        var formats = new SurfaceFormatKHR[formatCount];
        // 检查第二次 GetPhysicalDeviceSurfaceFormats 返回值
        Result fmtResult2 = VulkanNative.GetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref formatCount, formats);
        if (fmtResult2 != Result.Success)
            throw new InvalidOperationException($"vkGetPhysicalDeviceSurfaceFormatsKHR (第二次) 失败: {fmtResult2}");

        // 优选 B8G8R8A8Unorm，回退 R8G8B8A8Unorm（部分 Android/移动驱动仅报 RGBA8）。
        // UploadSoftwareFrame 按「源像素格式与 SwapChain 格式 R/B 顺序是否一致」动态决定直拷/交换，
        // 两种格式均能正确渲染。仅当两者都不支持时抛异常，避免颜色错乱。
        SurfaceFormatKHR selectedFormat = default;
        bool formatFound = false;
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Unorm) { selectedFormat = f; formatFound = true; break; }
        }
        if (!formatFound)
        {
            foreach (var f in formats)
            {
                if (f.Format == Format.R8G8B8A8Unorm) { selectedFormat = f; formatFound = true; break; }
            }
        }
        if (!formatFound)
            throw new NotSupportedException(
                $"Surface 既不支持 B8G8R8A8Unorm 也不支持 R8G8B8A8Unorm（可用: {string.Join(", ", formats.Select(f => f.Format))}）。" +
                "Vulkan 渲染器仅支持 8bit BGRA/RGBA SwapChain。");

        _swapchainFormat = selectedFormat.Format;
        // 钳制 Extent 到 Surface 能力范围——CurrentExtent.Width==uint.MaxValue 表示由 SwapChain 决定尺寸
        if (caps.CurrentExtent.Width != uint.MaxValue)
        {
            _swapchainExtent = caps.CurrentExtent;
        }
        else
        {
            _swapchainExtent = new Extent2D(
                System.Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                System.Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }

        // MinImageCount 下限保护——至少双缓冲（某些驱动返回 0 或 1）
        // 上限保护——不超过 MaxImageCount（MaxImageCount=0 表示无限制）
        uint minImageCount = System.Math.Max(2u, caps.MinImageCount);
        if (caps.MaxImageCount > 0 && minImageCount > caps.MaxImageCount)
            minImageCount = caps.MaxImageCount;

        SwapchainCreateInfoKHR swapInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = minImageCount,
            ImageFormat = selectedFormat.Format,
            ImageColorSpace = selectedFormat.ColorSpace,
            ImageExtent = _swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            // 按 Surface 支持的 CompositeAlpha 位选择——Opaque 首选，否则 Inherit/PreMultiplied/PostMultiplied
            // （Wayland/Android 常见仅支持后者，硬写 Opaque 会导致 vkCreateSwapchain 失败）。
            // 补漏：末位不再硬选 PreMultiplied——仅 PostMultiplied 可用的驱动也能命中支持位
            //（规范保证 SupportedCompositeAlpha 至少置位一个，四选一必中）。
            CompositeAlpha = SelectCompositeAlpha(caps.SupportedCompositeAlpha),
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
            OldSwapchain = default,
        };

        SwapchainKHR swap;
        Result result = VulkanNative.CreateSwapchainKHR(_device, ref swapInfo, null, out swap);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSwapchain 失败: {result}");
        _swapchain = swap;

        uint imageCount = 0;
        // 检查 GetSwapchainImages 返回值
        Result imgResult = VulkanNative.GetSwapchainImagesKHR(_device, _swapchain, ref imageCount, (Image*)null);
        if (imgResult != Result.Success)
            throw new InvalidOperationException($"vkGetSwapchainImagesKHR 失败: {imgResult}");
        _swapchainImages = new Image[imageCount];
        // 检查第二次 GetSwapchainImages 返回值
        Result imgResult2 = VulkanNative.GetSwapchainImagesKHR(_device, _swapchain, ref imageCount, _swapchainImages);
        if (imgResult2 != Result.Success)
            throw new InvalidOperationException($"vkGetSwapchainImagesKHR (第二次) 失败: {imgResult2}");
    }

    private static CompositeAlphaFlagsKHR SelectCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        if ((supported & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0) return CompositeAlphaFlagsKHR.OpaqueBitKhr;
        if ((supported & CompositeAlphaFlagsKHR.InheritBitKhr) != 0) return CompositeAlphaFlagsKHR.InheritBitKhr;
        if ((supported & CompositeAlphaFlagsKHR.PreMultipliedBitKhr) != 0) return CompositeAlphaFlagsKHR.PreMultipliedBitKhr;
        if ((supported & CompositeAlphaFlagsKHR.PostMultipliedBitKhr) != 0) return CompositeAlphaFlagsKHR.PostMultipliedBitKhr;
        return CompositeAlphaFlagsKHR.OpaqueBitKhr;
    }

    /// <summary>
    /// SwapChain 过期（ErrorOutOfDateKhr）时就地重建。
    /// 调用方须持有 _gate 锁。DeviceWaitIdle 后销毁旧 SwapChain 并按当前 Surface 尺寸重建；
    /// 两个信号量一并重建——同时消除 QueuePresent 失败后信号量可能的 signaled 残留，
    /// 避免下次 AcquireNextImage 因信号量已信号而校验失败/永久超时。
    /// </summary>
    private void RecreateSwapchain()
    {
        // OutOfDate 重建路径的 DeviceWaitIdle 也可能返回 DeviceLost
        //（如休眠唤醒同时掉驱动）——类型化上抛，避免后续 CreateSwapchain 报泛化错误误导会话层。
        ThrowIfDeviceLost(VulkanNative.DeviceWaitIdle(_device), "vkDeviceWaitIdle/RecreateSwapchain");

        if (_swapchain.Handle != 0)
        { VulkanNative.DestroySwapchainKHR(_device, _swapchain, null); _swapchain = default; }
        _swapchainImages = [];

        if (_imageAvailableSemaphore.Handle != 0)
        { VulkanNative.DestroySemaphore(_device, _imageAvailableSemaphore, null); _imageAvailableSemaphore = default; }
        foreach (var s in _renderFinishedSemaphores)
            if (s.Handle != 0) VulkanNative.DestroySemaphore(_device, s, null);
        _renderFinishedSemaphores = [];

        try
        {
            CreateSwapchain(_targetWidth, _targetHeight);
            _shaderPipeline?.EnsureSwapchainResources(_swapchainFormat, _swapchainExtent, _swapchainImages);
            CreateSemaphores();
        }
        catch
        {
            // 多方位补漏：半途失败（如 CreateSwapchain 成功但 CreateSemaphores OOM）
            // 会留下「_attached=true 但信号量为 VK_NULL_HANDLE」的不一致状态——下次 Present 以空信号量
            // 调用 vkAcquireNextImage 属规范违规（semaphore 与 fence 不得同时为 NULL，UB）。
            // 统一释放全部会话资源并标记未附加：后续 Present 抛清晰的「未附加」异常，会话层重新 Attach。
            ReleaseSessionResources();
            _attached = false;
            throw;
        }
        _logger.LogDebug("SwapChain 已因过期重建：{W}x{H}", _swapchainExtent.Width, _swapchainExtent.Height);
    }
}
