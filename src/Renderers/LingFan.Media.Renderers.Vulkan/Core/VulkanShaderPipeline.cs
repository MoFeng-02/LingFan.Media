using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan;

/// <summary>
/// Vulkan GPU 渲染管线：全屏三角形 + Fragment Shader 完成 YUV→RGB 转换与缩放。
/// </summary>
/// <remarks>
/// <para>职责：替代 <see cref="VulkanRenderer"/> 中软帧 YUV 格式的 CPU 逐像素转换，
/// 将 NV12/NV21/YUV420P/YUV422P/YUV444P 的 Y/U/V 平面上传到 GPU 纹理后由 Shader 采样转换，
/// CPU 仅搬运原始平面数据（1080p NV12 约 3MB），彻底消除 CPU 转换瓶颈。</para>
/// <para>着色器源（唯一真源 <c>Targets/Vulkan/Shaders/</c>）用 GLSL 预编译为 SPIR-V（<c>glslang -V</c>），由构建期 MSBuild 目标
/// （共享 <c>Targets/Vulkan/VulkanShaderBytes.targets</c>，跨平台内联任务）转成 <c>EmbeddedShaders</c> 类的
/// <see langword="byte"/>[] 字面量
/// 编译进程序集（见 <c>Shaders.g.cs</c>，位于 obj 中间目录，不进源码树）；
/// NativeAOT 下无运行时编译、无 <see cref="System.Reflection.Assembly"/> 资源访问，trim/AOT 绝对安全。</para>
/// <para>异步策略：全部同步原生调用（无 I/O await），与 <see cref="VulkanRenderer.Present"/> 的 sync-only 分类一致。</para>
/// <para>线程安全：由 <see cref="VulkanRenderer"/> 的 <c>_gate</c> 锁串行化调用，本类不再加锁。</para>
/// </remarks>
internal sealed unsafe partial class VulkanShaderPipeline : IDisposable
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


    // 着色器加载 / 管线创建
}
