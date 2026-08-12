using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan GPU 渲染管线：全屏三角形 + Fragment Shader 完成 YUV→RGB 转换与缩放。
/// </summary>
/// <remarks>
/// <para>职责：替代 <see cref="VulkanRenderer"/> 中软帧 YUV 格式的 CPU 逐像素转换，
/// 将 NV12/NV21/YUV420P/YUV422P/YUV444P 的 Y/U/V 平面上传到 GPU 纹理后由 Shader 采样转换，
/// CPU 仅搬运原始平面数据（1080p NV12 约 3MB），彻底消除 ~100ms 的 CPU 转换瓶颈。</para>
/// <para>着色器源用 GLSL 预编译为 SPIR-V（<c>glslang -V</c>），由构建期 MSBuild 目标
/// 经 <c>generate-shader-bytes.ps1</c> 转成 <c>EmbeddedShaders</c> 类的 <see langword="byte"/>[] 字面量
/// 编译进程序集（见 <c>Shaders.g.cs</c>，位于 obj 中间目录，不进源码树）；
/// NativeAOT 下无运行时编译、无 <see cref="System.Reflection.Assembly"/> 资源访问，trim/AOT 绝对安全。</para>
/// <para>异步策略：全部同步原生调用（无 I/O await），与 <see cref="VulkanRenderer.Present"/> 的 sync-only 分类一致。</para>
/// <para>线程安全：由 <see cref="VulkanRenderer"/> 的 <c>_gate</c> 锁串行化调用，本类不再加锁。</para>
/// </remarks>
internal sealed unsafe class VulkanShaderPipeline : IDisposable
{
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;

    // 着色器模块（预编译 SPIR-V）
    private ShaderModule _vertModule;
    private ShaderModule _fragModule;

    // 管线对象
    private DescriptorSetLayout _descriptorSetLayout;
    private PipelineLayout _pipelineLayout;
    private RenderPass _renderPass;
    private Pipeline _pipeline;
    private Sampler _sampler;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;

    // SwapChain 相关（尺寸/格式变化时需重建）
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;
    private Image[] _swapchainImages = [];
    private ImageView[] _swapchainImageViews = [];
    private Framebuffer[] _framebuffers = [];

    // 离屏（no-airspace）渲染相关：把帧渲染进一块固定、可外部导出的 VkImage（SharedSurfaceSource 持有）。
    // 与 SwapChain 路径共用着色器/描述符/采样器/管线布局，仅 RenderPass/Pipeline/Framebuffer 独立。
    private Format _offscreenFormat;
    private Extent2D _offscreenExtent;
    private ImageView _offscreenImageView;
    private RenderPass _offscreenRenderPass;
    private Pipeline _offscreenPipeline;
    private Framebuffer _offscreenFramebuffer;

    // 离屏路径自有 staging 缓冲（上传软件帧平面数据），与 SwapChain 路径的 staging 解耦。
    private Buffer _stagingBuffer;
    private DeviceMemory _stagingMemory;
    private ulong _stagingBufferSize;
    private void* _stagingMapped;

    // 帧平面纹理缓存（尺寸/格式变化时重建）
    private readonly Image[] _planeImages = new Image[3];
    private readonly DeviceMemory[] _planeMemories = new DeviceMemory[3];
    private readonly ImageView[] _planeViews = new ImageView[3];
    private int _cachedWidth;
    private int _cachedHeight;
    private PixelFormat _cachedFormat = (PixelFormat)(-1);

    private bool _disposed;

    // 推送常量：vec4 srcCrop + int format + int isBgra + int flipY + int pad = 32 字节
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct PushConstants
    {
        [FieldOffset(0)] public fixed float SrcCrop[4];
        [FieldOffset(16)] public int Format;
        [FieldOffset(20)] public int IsBgra;
        [FieldOffset(24)] public int FlipY;
        [FieldOffset(28)] public int Pad;
    }

    private const int FormatDirect = 0;
    private const int FormatPlanar = 1;
    private const int FormatNv12 = 2;
    private const int FormatNv21 = 3;

    public VulkanShaderPipeline(PhysicalDevice physicalDevice, Device device)
    {
        _physicalDevice = physicalDevice;
        _device = device;
    }

    /// <summary>
    /// 当 SwapChain 创建/重建时调用，确保 RenderPass/Framebuffer/ImageView 与当前 SwapChain 匹配。
    /// </summary>
    public void EnsureSwapchainResources(Format format, Extent2D extent, Image[] images)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool formatChanged = _swapchainFormat != format || _renderPass.Handle == 0;
        bool imagesChanged = formatChanged
            || _swapchainImages.Length != images.Length
            || _swapchainExtent.Width != extent.Width
            || _swapchainExtent.Height != extent.Height;

        if (!formatChanged && !imagesChanged) return;

        ReleaseFramebuffersAndViews();

        if (formatChanged)
        {
            ReleasePipelineAndRenderPass();
            CreateRenderPassAndPipeline(format);
        }

        _swapchainFormat = format;
        _swapchainExtent = extent;
        _swapchainImages = images;
        CreateImageViewsAndFramebuffers();
    }

    /// <summary>
    /// 用 GPU Shader 路径呈现软件帧到 SwapChain 图像。
    /// </summary>
    /// <param name="sw">软件帧资源。</param>
    /// <param name="imageIndex">SwapChain 图像索引。</param>
    /// <param name="dstRect">目标矩形（相对于 SwapChain，单位像素）。</param>
    /// <param name="srcCrop">源裁剪 UV（相对源帧的 [u0,v0,u1,v1]）。</param>
    /// <param name="stagingBuffer">已持久映射的 staging 缓冲。</param>
    /// <param name="stagingMapped">staging 持久映射指针。</param>
    /// <param name="stagingBufferSize">staging 缓冲大小（字节）。</param>
    /// <param name="cmd">正在记录的 Command Buffer。</param>
    /// <param name="swapchainIsBgra">SwapChain 是否为 B8G8R8A8 格式（影响输出 R/B 通道顺序）。</param>
    public void Present(
        SoftwareFrameResource sw,
        uint imageIndex,
        (int X, int Y, int W, int H) dstRect,
        (float U0, float V0, float U1, float V1) srcCrop,
        Buffer stagingBuffer,
        void* stagingMapped,
        ulong stagingBufferSize,
        CommandBuffer cmd,
        bool swapchainIsBgra)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderFrameTo(cmd, _renderPass, _pipeline, _framebuffers[imageIndex], _swapchainExtent,
            sw, dstRect, srcCrop, stagingBuffer, stagingMapped, stagingBufferSize);
    }

    /// <summary>
    /// 用 GPU Shader 路径把软件帧渲染进<b>离屏可导出 VkImage</b>（no-airspace 共享表面源专用）。
    /// 离屏图像固定尺寸 = 帧尺寸，由调用方在 <see cref="EnsureOffscreenResources"/> 中绑定；
    /// 缩放/信箱交给消费方合成层处理，故此处 dstRect 填满整张离屏图像、srcCrop 取全帧。
    /// </summary>
    /// <param name="sw">软件帧资源。</param>
    /// <param name="cmd">正在记录的 Command Buffer。</param>
    /// <param name="dstRect">目标矩形（离屏图像内，单位像素）。</param>
    /// <param name="srcCrop">源裁剪 UV（相对源帧的 [u0,v0,u1,v1]）。</param>
    public void PresentOffscreen(
        SoftwareFrameResource sw,
        CommandBuffer cmd,
        (int X, int Y, int W, int H) dstRect,
        (float U0, float V0, float U1, float V1) srcCrop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_offscreenFramebuffer.Handle == 0)
            throw new InvalidOperationException("离屏渲染目标未初始化（请先调用 EnsureOffscreenResources）。");

        int stagingSize = CalculateStagingSize(sw);
        EnsureStagingBuffer((ulong)stagingSize);

        RenderFrameTo(cmd, _offscreenRenderPass, _offscreenPipeline, _offscreenFramebuffer, _offscreenExtent,
            sw, dstRect, srcCrop, _stagingBuffer, _stagingMapped, _stagingBufferSize);
    }

    /// <summary>
    /// 确保离屏渲染目标就绪：为给定的可导出 VkImage 的 ImageView 创建离屏 RenderPass/Pipeline/Framebuffer。
    /// 格式/尺寸变化时重建（离屏图像固定，仅在源重建共享图像时变化）。
    /// </summary>
    /// <param name="format">离屏图像格式（固定 B8G8R8A8Unorm，与消费方导入格式一致）。</param>
    /// <param name="extent">离屏图像尺寸（=帧尺寸）。</param>
    /// <param name="imageView">离屏可导出 VkImage 的 ImageView（由源创建并持有）。</param>
    public void EnsureOffscreenResources(Format format, Extent2D extent, ImageView imageView)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool formatChanged = _offscreenFormat != format || _offscreenRenderPass.Handle == 0;
        bool extentChanged = formatChanged
            || _offscreenExtent.Width != extent.Width
            || _offscreenExtent.Height != extent.Height;

        if (!formatChanged && !extentChanged && _offscreenImageView.Handle == imageView.Handle) return;

        ReleaseOffscreenFramebuffer();

        if (formatChanged)
        {
            ReleaseOffscreenPipelineAndRenderPass();
            CreateOffscreenRenderPassAndPipeline(format);
        }

        _offscreenFormat = format;
        _offscreenExtent = extent;
        _offscreenImageView = imageView;
        CreateOffscreenFramebuffer();
    }

    /// <summary>渲染帧到指定 RenderPass/Pipeline/Framebuffer（SwapChain 与离屏共用此核心路径）。</summary>
    private void RenderFrameTo(
        CommandBuffer cmd,
        RenderPass renderPass,
        Pipeline pipeline,
        Framebuffer framebuffer,
        Extent2D extent,
        SoftwareFrameResource sw,
        (int X, int Y, int W, int H) dstRect,
        (float U0, float V0, float U1, float V1) srcCrop,
        Buffer stagingBuffer,
        void* stagingMapped,
        ulong stagingBufferSize)
    {
        int w = sw.Width, h = sw.Height;
        EnsurePlaneImages(w, h, sw.Format);
        UploadPlanes(sw, stagingBuffer, stagingMapped, stagingBufferSize, cmd);

        // 转换平面纹理为 ShaderReadOnlyOptimal
        for (int i = 0; i < _planeImages.Length; i++)
        {
            if (_planeImages[i].Handle == 0) continue;
            TransitionImageLayout(cmd, _planeImages[i], ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
        }

        // 更新描述符：绑定当前平面视图
        UpdateDescriptorSet(sw.Format);

        // 开始 RenderPass（清黑底，loadOp=Clear）
        ClearColorValue clearColor = new() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 0 };
        ClearValue clearValue = new() { Color = clearColor };
        RenderPassBeginInfo rpBegin = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D
            {
                Offset = new Offset2D(0, 0),
                Extent = extent,
            },
            ClearValueCount = 1,
            PClearValues = &clearValue,
        };
        VulkanNative.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);

        VulkanNative.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

        Viewport viewport = new()
        {
            X = dstRect.X,
            Y = dstRect.Y,
            Width = dstRect.W,
            Height = dstRect.H,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        VulkanNative.CmdSetViewport(cmd, 0, 1, &viewport);

        Rect2D scissor = new()
        {
            Offset = new Offset2D(dstRect.X, dstRect.Y),
            Extent = new Extent2D((uint)dstRect.W, (uint)dstRect.H),
        };
        VulkanNative.CmdSetScissor(cmd, 0, 1, &scissor);

        int shaderFormat = sw.Format switch
        {
            PixelFormat.BGRA32 or PixelFormat.RGBA32 => FormatDirect,
            PixelFormat.YUV420P or PixelFormat.YUV422P or PixelFormat.YUV444P => FormatPlanar,
            PixelFormat.NV12 => FormatNv12,
            PixelFormat.NV21 => FormatNv21,
            _ => throw new NotSupportedException($"Vulkan Shader 管线不支持像素格式 {sw.Format}。"),
        };

        // Vulkan 片段着色器输出按通道名（R/G/B/A）映射到 attachment 格式分量，
        // 与 D3D11 行为一致，无需手动根据 attachment 是 BGRA/RGBA 交换 R/B。
        // 保留 push_constant 字段供未来需要显式重排的场景，当前恒 0。
        int isBgra = 0;

        // 正高度 viewport 把 NDC y=+1 映射到 framebuffer 底部，导致画面上下颠倒；
        // 在顶点着色器里预翻转纹理 V 坐标，使最终呈现直立。
        int flipY = 1;

        PushConstants pc = new();
        pc.SrcCrop[0] = srcCrop.U0;
        pc.SrcCrop[1] = srcCrop.V0;
        pc.SrcCrop[2] = srcCrop.U1;
        pc.SrcCrop[3] = srcCrop.V1;
        pc.Format = shaderFormat;
        pc.IsBgra = isBgra;
        pc.FlipY = flipY;
        pc.Pad = 0;

        VulkanNative.CmdPushConstants(cmd, _pipelineLayout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0, (uint)sizeof(PushConstants), &pc);

        var descriptorSet = _descriptorSet;
        VulkanNative.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout,
            0, 1, &descriptorSet, 0, null);

        VulkanNative.CmdDraw(cmd, 3, 1, 0, 0);
        VulkanNative.CmdEndRenderPass(cmd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_device.Handle != 0)
        {
            ReleasePlaneImages();
            ReleaseFramebuffersAndViews();
            ReleaseOffscreenFramebuffer();
            ReleaseOffscreenPipelineAndRenderPass();
            ReleasePipelineAndRenderPass();

            // 离屏路径自有 staging 缓冲（与 SwapChain 路径解耦），此处独立释放。
            if (_stagingBuffer.Handle != 0)
            {
                if (_stagingMapped != null)
                {
                    VulkanNative.UnmapMemory(_device, _stagingMemory);
                    _stagingMapped = null;
                }
                VulkanNative.DestroyBuffer(_device, _stagingBuffer, null);
                _stagingBuffer = default;
            }
            if (_stagingMemory.Handle != 0)
            {
                VulkanNative.FreeMemory(_device, _stagingMemory, null);
                _stagingMemory = default;
            }
            _stagingBufferSize = 0;
            // 注意：_offscreenImageView 由共享表面源持有（其底层 VkImage 一并由其释放），此处不销毁。

            if (_sampler.Handle != 0) { VulkanNative.DestroySampler(_device, _sampler, null); _sampler = default; }
            if (_descriptorPool.Handle != 0) { VulkanNative.DestroyDescriptorPool(_device, _descriptorPool, null); _descriptorPool = default; }
            if (_descriptorSetLayout.Handle != 0) { VulkanNative.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null); _descriptorSetLayout = default; }

            if (_vertModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _vertModule, null); _vertModule = default; }
            if (_fragModule.Handle != 0) { VulkanNative.DestroyShaderModule(_device, _fragModule, null); _fragModule = default; }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // 着色器加载 / 管线创建
    // ═════════════════════════════════════════════════════════════════

    private void CreateRenderPassAndPipeline(Format format)
    {
        EnsureShaderModules();
        EnsureDescriptorSetLayout();
        EnsureSampler();
        EnsureDescriptorPoolAndSet();

        // RenderPass：一个 color attachment，load=Clear 清黑底，store=Store，finalLayout=PresentSrc
        AttachmentDescription colorAttachment = new()
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        AttachmentReference colorRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = uint.MaxValue,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        Result result = VulkanNative.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateRenderPass 失败: {result}");

        // PipelineLayout 由 EnsurePipelineLayout() 幂等创建（SwapChain 与离屏路径共用），
        // 故此处仅调用，无重复局部变量声明。
        EnsurePipelineLayout();

        // GraphicsPipeline
        CreateGraphicsPipeline(format, _renderPass, out _pipeline);
    }

    private void EnsurePipelineLayout()
    {
        if (_pipelineLayout.Handle != 0) return;

        PushConstantRange pcRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(PushConstants),
        };

        var descriptorSetLayout = _descriptorSetLayout;
        var pushConstantRange = pcRange;
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange,
        };
        Result result = VulkanNative.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreatePipelineLayout 失败: {result}");
    }

    /// <summary>
    /// 创建离屏 RenderPass/Pipeline：与 SwapChain 同构，仅 attachment 的 finalLayout 为
    /// <see cref="ImageLayout.ColorAttachmentOptimal"/>（渲染器侧写完后交还，消费方导入后自管布局）。
    /// 复用已创建的着色器模块/描述符布局/采样器/描述符集（Ensure* 幂等）。
    /// </summary>
    private void CreateOffscreenRenderPassAndPipeline(Format format)
    {
        EnsureShaderModules();
        EnsureDescriptorSetLayout();
        EnsureSampler();
        EnsureDescriptorPoolAndSet();

        AttachmentDescription colorAttachment = new()
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };

        AttachmentReference colorRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = uint.MaxValue,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        Result result = VulkanNative.CreateRenderPass(_device, &renderPassInfo, null, out _offscreenRenderPass);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateRenderPass（离屏）失败: {result}");

        EnsurePipelineLayout();

        CreateGraphicsPipeline(format, _offscreenRenderPass, out _offscreenPipeline);
    }

    /// <summary>为绑定到 <see cref="_offscreenImageView"/> 的可导出离屏 VkImage 创建离屏 Framebuffer。</summary>
    private void CreateOffscreenFramebuffer()
    {
        var view = _offscreenImageView;
        FramebufferCreateInfo fbInfo = new()
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _offscreenRenderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = _offscreenExtent.Width,
            Height = _offscreenExtent.Height,
            Layers = 1,
        };
        Result result = VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _offscreenFramebuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateFramebuffer（离屏）失败: {result}");
    }

    /// <summary>释放离屏 Framebuffer。注意：<see cref="_offscreenImageView"/> 由共享表面源持有，此处不销毁。</summary>
    private void ReleaseOffscreenFramebuffer()
    {
        if (_device.Handle == 0) return;
        if (_offscreenFramebuffer.Handle != 0)
        {
            VulkanNative.DestroyFramebuffer(_device, _offscreenFramebuffer, null);
            _offscreenFramebuffer = default;
        }
    }

    /// <summary>释放离屏 RenderPass/Pipeline。共享的 <see cref="_pipelineLayout"/> 由 SwapChain 路径的
    /// <see cref="ReleasePipelineAndRenderPass"/> 统一销毁，此处不触碰。</summary>
    private void ReleaseOffscreenPipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        if (_offscreenPipeline.Handle != 0)
        {
            VulkanNative.DestroyPipeline(_device, _offscreenPipeline, null);
            _offscreenPipeline = default;
        }
        if (_offscreenRenderPass.Handle != 0)
        {
            VulkanNative.DestroyRenderPass(_device, _offscreenRenderPass, null);
            _offscreenRenderPass = default;
        }
    }

    /// <summary>计算软帧上传到离屏 staging 缓冲所需的字节数（与 <see cref="UploadPlanes"/> 的平面字节总和一致）。</summary>
    private static int CalculateStagingSize(SoftwareFrameResource sw)
    {
        int w = sw.Width, h = sw.Height;
        return sw.Format switch
        {
            PixelFormat.BGRA32 or PixelFormat.RGBA32 => w * h * 4,
            PixelFormat.NV12 or PixelFormat.NV21 =>
                w * h + (((w + 1) >> 1) * ((h + 1) >> 1) * 2),
            PixelFormat.YUV420P =>
                w * h + 2 * (((w + 1) >> 1) * ((h + 1) >> 1)),
            PixelFormat.YUV422P =>
                w * h + 2 * (((w + 1) >> 1) * h),
            PixelFormat.YUV444P => w * h * 3,
            _ => w * h * 4,
        };
    }

    /// <summary>确保离屏路径自有 staging 缓冲就绪（创建 + 持久映射），与 SwapChain 路径的 staging 解耦。</summary>
    private void EnsureStagingBuffer(ulong requiredSize)
    {
        if (_stagingBuffer.Handle != 0 && _stagingBufferSize >= requiredSize) return;

        if (_stagingBuffer.Handle != 0)
        {
            // realloc：先解持久映射再释放，避免悬空映射
            if (_stagingMapped != null)
            {
                VulkanNative.UnmapMemory(_device, _stagingMemory);
                _stagingMapped = null;
            }
            VulkanNative.DestroyBuffer(_device, _stagingBuffer, null);
            VulkanNative.FreeMemory(_device, _stagingMemory, null);
            _stagingBuffer = default;
            _stagingMemory = default;
        }

        BufferCreateInfo bufInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = requiredSize,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };

        Result result = VulkanNative.CreateBuffer(_device, ref bufInfo, null, out _stagingBuffer);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateBuffer（离屏 staging）失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetBufferMemoryRequirements(_device, _stagingBuffer, &memReq);

        uint memTypeIndex = FindMemoryType(
            memReq.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        MemoryAllocateInfo memInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memTypeIndex,
        };

        result = VulkanNative.AllocateMemory(_device, ref memInfo, null, out _stagingMemory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（离屏 staging）失败: {result}");

        result = VulkanNative.BindBufferMemory(_device, _stagingBuffer, _stagingMemory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindBufferMemory（离屏 staging）失败: {result}");

        // 持久映射整段 staging（HOST_COHERENT，写入经一致性语义自动对 GPU 可见），长期复用。
        void* mapped = null;
        Result mapResult = VulkanNative.MapMemory(_device, _stagingMemory, 0, memReq.Size, 0, &mapped);
        if (mapResult != Result.Success)
            throw new InvalidOperationException($"vkMapMemory（离屏 staging 持久映射）失败: {mapResult}");
        _stagingMapped = mapped;

        // 记录 buffer 创建大小（requiredSize），不能记 memReq.Size（≥ requiredSize，含对齐填充）。
        _stagingBufferSize = requiredSize;
    }

    private void CreateGraphicsPipeline(Format format, RenderPass renderPass, out Pipeline pipeline)
    {
        PipelineShaderStageCreateInfo vertStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _vertModule,
            PName = VulkanNative.StringToPtr("main"),
        };
        PipelineShaderStageCreateInfo fragStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _fragModule,
            PName = VulkanNative.StringToPtr("main"),
        };

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = vertStage;
        stages[1] = fragStage;

        PipelineVertexInputStateCreateInfo vertexInput = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
        };

        PipelineInputAssemblyStateCreateInfo inputAssembly = new()
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };

        PipelineViewportStateCreateInfo viewportState = new()
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };

        PipelineRasterizationStateCreateInfo rasterizer = new()
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.Clockwise,
            LineWidth = 1f,
        };

        PipelineMultisampleStateCreateInfo multisampling = new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        PipelineDepthStencilStateCreateInfo depthStencil = new()
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = false,
            DepthWriteEnable = false,
            StencilTestEnable = false,
        };

        PipelineColorBlendAttachmentState colorBlendAttachment = new()
        {
            BlendEnable = false,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };

        PipelineColorBlendStateCreateInfo colorBlending = new()
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorBlendAttachment,
        };

        var dynamicStates = stackalloc DynamicState[2];
        dynamicStates[0] = DynamicState.Viewport;
        dynamicStates[1] = DynamicState.Scissor;
        PipelineDynamicStateCreateInfo dynamicState = new()
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };

        GraphicsPipelineCreateInfo pipelineInfo = new()
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlending,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = renderPass,
            Subpass = 0,
        };

        Pipeline localPipe;
        Result result = VulkanNative.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, &localPipe);

        // 清理入口点名称内存（VulkanNative 分配的 UTF-8 字符串）
        VulkanNative.FreeStringPtr(vertStage.PName);
        VulkanNative.FreeStringPtr(fragStage.PName);

        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateGraphicsPipelines 失败: {result}");

        pipeline = localPipe;
    }

    private void EnsureShaderModules()
    {
        if (_vertModule.Handle != 0 && _fragModule.Handle != 0) return;

        _vertModule = CreateShaderModule(EmbeddedShaders.yuv_vert);
        _fragModule = CreateShaderModule(EmbeddedShaders.yuv_frag);
    }

    private ShaderModule CreateShaderModule(ReadOnlyMemory<byte> code)
    {
        var span = code.Span;
        ShaderModuleCreateInfo info = new()
        {
            SType = StructureType.ShaderModuleCreateInfo,
            CodeSize = (nuint)span.Length,
        };
        ShaderModule module = default;
        Result result;
        fixed (byte* p = &MemoryMarshal.GetReference(span))
        {
            info.PCode = (uint*)p;
            result = VulkanNative.CreateShaderModule(_device, &info, null, out module);
        }
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateShaderModule 失败: {result}");
        return module;
    }

    private void EnsureDescriptorSetLayout()
    {
        if (_descriptorSetLayout.Handle != 0) return;

        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint i = 0; i < 3; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        Result result = VulkanNative.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _descriptorSetLayout);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorSetLayout 失败: {result}");
    }

    private void EnsureSampler()
    {
        if (_sampler.Handle != 0) return;

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = false,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            MipmapMode = SamplerMipmapMode.Linear,
            MinLod = 0f,
            MaxLod = 1f, // 平面纹理无 mip，只采样 level 0
        };
        Result result = VulkanNative.CreateSampler(_device, &samplerInfo, null, out _sampler);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateSampler 失败: {result}");
    }

    private void EnsureDescriptorPoolAndSet()
    {
        if (_descriptorPool.Handle != 0) return;

        DescriptorPoolSize poolSize = new()
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 3,
        };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Result result = VulkanNative.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateDescriptorPool 失败: {result}");

        var dsl = _descriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl,
        };
        result = VulkanNative.AllocateDescriptorSets(_device, &allocInfo, out _descriptorSet);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateDescriptorSets 失败: {result}");
    }

    // ═════════════════════════════════════════════════════════════════
    // SwapChain ImageView / Framebuffer
    // ═════════════════════════════════════════════════════════════════

    private void CreateImageViewsAndFramebuffers()
    {
        int n = _swapchainImages.Length;
        _swapchainImageViews = new ImageView[n];
        _framebuffers = new Framebuffer[n];

        for (int i = 0; i < n; i++)
        {
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            Result result = VulkanNative.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[i]);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateImageView（SwapChain）失败: {result}");

            var swapchainView = _swapchainImageViews[i];
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &swapchainView,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1,
            };
            result = VulkanNative.CreateFramebuffer(_device, &fbInfo, null, out _framebuffers[i]);
            if (result != Result.Success)
                throw new InvalidOperationException($"vkCreateFramebuffer 失败: {result}");
        }
    }

    private void ReleaseFramebuffersAndViews()
    {
        if (_device.Handle == 0) return;
        foreach (var fb in _framebuffers)
        {
            if (fb.Handle != 0) VulkanNative.DestroyFramebuffer(_device, fb, null);
        }
        _framebuffers = [];
        foreach (var view in _swapchainImageViews)
        {
            if (view.Handle != 0) VulkanNative.DestroyImageView(_device, view, null);
        }
        _swapchainImageViews = [];
    }

    private void ReleasePipelineAndRenderPass()
    {
        if (_device.Handle == 0) return;
        if (_pipeline.Handle != 0) { VulkanNative.DestroyPipeline(_device, _pipeline, null); _pipeline = default; }
        if (_pipelineLayout.Handle != 0) { VulkanNative.DestroyPipelineLayout(_device, _pipelineLayout, null); _pipelineLayout = default; }
        if (_renderPass.Handle != 0) { VulkanNative.DestroyRenderPass(_device, _renderPass, null); _renderPass = default; }
    }

    // ═════════════════════════════════════════════════════════════════
    // 帧平面上传
    // ═════════════════════════════════════════════════════════════════

    private void EnsurePlaneImages(int width, int height, PixelFormat format)
    {
        if (_cachedWidth == width && _cachedHeight == height && _cachedFormat == format) return;

        ReleasePlaneImages();

        switch (format)
        {
            case PixelFormat.BGRA32:
                CreatePlane(0, width, height, Format.B8G8R8A8Unorm);
                break;
            case PixelFormat.RGBA32:
                CreatePlane(0, width, height, Format.R8G8B8A8Unorm);
                break;
            case PixelFormat.YUV420P:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, (width + 1) >> 1, (height + 1) >> 1, Format.R8Unorm);
                CreatePlane(2, (width + 1) >> 1, (height + 1) >> 1, Format.R8Unorm);
                break;
            case PixelFormat.YUV422P:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, (width + 1) >> 1, height, Format.R8Unorm);
                CreatePlane(2, (width + 1) >> 1, height, Format.R8Unorm);
                break;
            case PixelFormat.YUV444P:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, width, height, Format.R8Unorm);
                CreatePlane(2, width, height, Format.R8Unorm);
                break;
            case PixelFormat.NV12:
            case PixelFormat.NV21:
                CreatePlane(0, width, height, Format.R8Unorm);
                CreatePlane(1, (width + 1) >> 1, (height + 1) >> 1, Format.R8G8Unorm);
                break;
            default:
                throw new NotSupportedException($"Vulkan Shader 管线不支持像素格式 {format}。");
        }

        _cachedWidth = width;
        _cachedHeight = height;
        _cachedFormat = format;
    }

    private void CreatePlane(int index, int width, int height, Format format)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        Result result = VulkanNative.CreateImage(_device, &imageInfo, null, out Image image);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImage（平面 {index}）失败: {result}");

        MemoryRequirements memReq;
        VulkanNative.GetImageMemoryRequirements(_device, image, &memReq);
        uint memType = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = memType,
        };
        result = VulkanNative.AllocateMemory(_device, &allocInfo, null, out DeviceMemory memory);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkAllocateMemory（平面 {index}）失败: {result}");

        result = VulkanNative.BindImageMemory(_device, image, memory, 0);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkBindImageMemory（平面 {index}）失败: {result}");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        result = VulkanNative.CreateImageView(_device, &viewInfo, null, out ImageView view);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateImageView（平面 {index}）失败: {result}");

        _planeImages[index] = image;
        _planeMemories[index] = memory;
        _planeViews[index] = view;
    }

    private void ReleasePlaneImages()
    {
        if (_device.Handle == 0) return;
        for (int i = 0; i < _planeImages.Length; i++)
        {
            if (_planeViews[i].Handle != 0) { VulkanNative.DestroyImageView(_device, _planeViews[i], null); _planeViews[i] = default; }
            if (_planeImages[i].Handle != 0) { VulkanNative.DestroyImage(_device, _planeImages[i], null); _planeImages[i] = default; }
            if (_planeMemories[i].Handle != 0) { VulkanNative.FreeMemory(_device, _planeMemories[i], null); _planeMemories[i] = default; }
        }
        _cachedWidth = 0;
        _cachedHeight = 0;
        _cachedFormat = (PixelFormat)(-1);
    }

    private void UploadPlanes(SoftwareFrameResource sw, Buffer stagingBuffer, void* stagingMapped, ulong stagingBufferSize, CommandBuffer cmd)
    {
        int w = sw.Width, h = sw.Height;
        var data = sw.Data.Span;
        Span<byte> staging = new(stagingMapped, (int)stagingBufferSize);

        switch (sw.Format)
        {
            case PixelFormat.BGRA32:
            case PixelFormat.RGBA32:
            {
                int rowBytes = w * 4;
                int srcRowPitch = sw.Stride > 0 ? sw.Stride : rowBytes;
                int dataSize = w * h * 4;
                if ((ulong)dataSize > stagingBufferSize)
                    throw new InvalidOperationException($"staging 缓冲不足：需要 {dataSize}，仅有 {stagingBufferSize}。");

                if (srcRowPitch == rowBytes)
                {
                    data.Slice(0, dataSize).CopyTo(staging.Slice(0, dataSize));
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        data.Slice(y * srcRowPitch, rowBytes).CopyTo(staging.Slice(y * rowBytes, rowBytes));
                }

                TransitionImageLayout(cmd, _planeImages[0], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, 0, _planeImages[0], (uint)w, (uint)h);
                break;
            }

            case PixelFormat.YUV420P:
            case PixelFormat.YUV422P:
            case PixelFormat.YUV444P:
            {
                (int cw, int ch) = sw.Format switch
                {
                    PixelFormat.YUV444P => (w, h),
                    PixelFormat.YUV422P => ((w + 1) >> 1, h),
                    _ => ((w + 1) >> 1, (h + 1) >> 1),
                };
                int ySize = w * h;
                int cSize = cw * ch;
                int total = ySize + 2 * cSize;
                if ((ulong)total > stagingBufferSize)
                    throw new InvalidOperationException($"staging 缓冲不足：需要 {total}，仅有 {stagingBufferSize}。");

                data.Slice(0, ySize).CopyTo(staging.Slice(0, ySize));
                data.Slice(ySize, cSize).CopyTo(staging.Slice(ySize, cSize));
                data.Slice(ySize + cSize, cSize).CopyTo(staging.Slice(ySize + cSize, cSize));

                TransitionImageLayout(cmd, _planeImages[0], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, 0, _planeImages[0], (uint)w, (uint)h);

                TransitionImageLayout(cmd, _planeImages[1], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, (ulong)ySize, _planeImages[1], (uint)cw, (uint)ch);

                TransitionImageLayout(cmd, _planeImages[2], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, (ulong)(ySize + cSize), _planeImages[2], (uint)cw, (uint)ch);
                break;
            }

            case PixelFormat.NV12:
            case PixelFormat.NV21:
            {
                int ySize = w * h;
                int uvW = (w + 1) >> 1;
                int uvH = (h + 1) >> 1;
                int uvSize = uvW * uvH * 2;
                int total = ySize + uvSize;
                if ((ulong)total > stagingBufferSize)
                    throw new InvalidOperationException($"staging 缓冲不足：需要 {total}，仅有 {stagingBufferSize}。");

                data.Slice(0, ySize).CopyTo(staging.Slice(0, ySize));
                data.Slice(ySize, uvSize).CopyTo(staging.Slice(ySize, uvSize));

                TransitionImageLayout(cmd, _planeImages[0], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, 0, _planeImages[0], (uint)w, (uint)h);

                TransitionImageLayout(cmd, _planeImages[1], ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    0, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                CopyBufferToImage(cmd, stagingBuffer, (ulong)ySize, _planeImages[1], (uint)uvW, (uint)uvH);
                break;
            }

            default:
                throw new NotSupportedException($"Vulkan Shader 管线不支持像素格式 {sw.Format}。");
        }
    }

    private void CopyBufferToImage(CommandBuffer cmd, Buffer srcBuffer, ulong offset, Image dstImage, uint width, uint height)
    {
        BufferImageCopy region = new()
        {
            BufferOffset = offset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        VulkanNative.CmdCopyBufferToImage(cmd, srcBuffer, dstImage, ImageLayout.TransferDstOptimal, 1, &region);
    }

    private void UpdateDescriptorSet(PixelFormat format)
    {
        // 绑定规则：
        // - 直接/半平面：binding1/2 复用 binding0 或 binding1 的视图（片段着色器对应分支不会采样它们）。
        // - 平面：binding0=Y, binding1=U, binding2=V。
        // 这样保证描述符始终合法，无需创建 dummy 视图。
        ImageView view0 = _planeViews[0];
        ImageView view1 = _planeViews[1].Handle != 0 ? _planeViews[1] : view0;
        ImageView view2 = _planeViews[2].Handle != 0 ? _planeViews[2] : view1;

        var infos = stackalloc DescriptorImageInfo[3];
        infos[0] = new DescriptorImageInfo { Sampler = _sampler, ImageView = view0, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[1] = new DescriptorImageInfo { Sampler = _sampler, ImageView = view1, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        infos[2] = new DescriptorImageInfo { Sampler = _sampler, ImageView = view2, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

        var writes = stackalloc WriteDescriptorSet[3];
        for (uint i = 0; i < 3; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = i,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &infos[i],
            };
        }
        VulkanNative.UpdateDescriptorSets(_device, 3, writes, 0, null);
    }

    // ═════════════════════════════════════════════════════════════════
    // 辅助
    // ═════════════════════════════════════════════════════════════════

    private void TransitionImageLayout(
        CommandBuffer cmd, Image image,
        ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = ~0u,
            DstQueueFamilyIndex = ~0u,
            Image = image,
            SubresourceRange = range,
        };

        VulkanNative.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
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
        throw new InvalidOperationException("未找到合适的 Vulkan 内存类型。");
    }
}
