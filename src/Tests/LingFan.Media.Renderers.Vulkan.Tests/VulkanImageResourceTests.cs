using System.Runtime.InteropServices;

namespace LingFan.Media.Renderers.Vulkan.Tests;

/// <summary>
/// VK-ZERO：<see cref="VulkanImageResource"/> 封装正确性 + <see cref="VulkanRenderer.BlitVulkanImageResource"/>
/// 多平面格式明确抛 <see cref="NotSupportedException"/>（归 Shader 转码）。
/// 本机有 Vulkan（核显/独显），headless 真实驱动验证。
/// </summary>
public unsafe class VulkanImageResourceTests
{
    [Fact]
    [Trait("Category", "RequiresGPU")]
    public void Constructor_WithDefaultLayout_CurrentLayoutIsTransferSrcOptimal()
    {
        using var ctx = new VulkanTestContext();
        CreateTestImage(ctx, 64, 64, out var image, out var memory);
        // VulkanImageResource 拥有 image/memory，Dispose 负责释放（验证完即释放，无泄漏）
        using var res = new VulkanImageResource(ctx.Vk, ctx.Device, image, memory, 64, 64, PixelFormat.BGRA32);
        res.Width.Should().Be(64);
        res.Height.Should().Be(64);
        res.Format.Should().Be(PixelFormat.BGRA32);
        // 默认交付布局为 TransferSrcOptimal（生产者语义）
        res.CurrentLayout.Should().Be(ImageLayout.TransferSrcOptimal);
        res.Image.Should().Be(image);
        res.Memory.Should().Be(memory);
    }

    [Fact]
    [Trait("Category", "RequiresGPU")]
    public void Dispose_DestroysImageAndMemory_Idempotent()
    {
        using var ctx = new VulkanTestContext();
        CreateTestImage(ctx, 32, 32, out var image, out var memory);
        var res = new VulkanImageResource(ctx.Vk, ctx.Device, image, memory, 32, 32, PixelFormat.BGRA32);
        res.Dispose();
        // 二次 Dispose 必须幂等（不抛、不双重释放）
        res.Dispose();
    }

    [Fact]
    [Trait("Category", "RequiresGPU")]
    public void BlitVulkanImageResource_WithNv12_ThrowsNotSupported()
    {
        using var ctx = new VulkanTestContext();
        using var renderer = CreateHeadlessRenderer(ctx);
        // NV12 在格式 switch 立即抛 NotSupportedException，不触碰 image 内容，故 image 可用 default
        using var nv12 = new VulkanImageResource(ctx.Vk, ctx.Device, default, default, 64, 64, PixelFormat.NV12);
        var act = () => renderer.BlitVulkanImageResource(nv12, default);
        act.Should().Throw<NotSupportedException>().WithMessage("*NV12*");
    }

    // ── 供 BlitVulkanImageResourceTests 复用的辅助 ──

    internal static void CreateTestImage(VulkanTestContext ctx, uint w, uint h, out Image image, out DeviceMemory memory)
    {
        var imgInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = (ImageType)1, // VK_IMAGE_TYPE_2D = 1
            Format = Format.B8G8R8A8Unorm,
            Extent = new Extent3D(w, h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        if (ctx.Vk.CreateImage(ctx.Device, &imgInfo, null, out image) != Result.Success)
            throw new InvalidOperationException("vkCreateImage 失败");

        ctx.Vk.GetImageMemoryRequirements(ctx.Device, image, out var memReq);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        if (ctx.Vk.AllocateMemory(ctx.Device, &alloc, null, out memory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory 失败");
        ctx.Vk.BindImageMemory(ctx.Device, image, memory, 0);
    }

    internal static uint FindMemoryType(VulkanTestContext ctx, uint typeFilter, MemoryPropertyFlags props)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                return i;
        return 0;
    }

    internal static VulkanRenderer CreateHeadlessRenderer(VulkanTestContext ctx)
    {
        return new VulkanRenderer(
            ctx.Vk, ctx.Instance, ctx.PhysicalDevice, ctx.Device, ctx.Queue, ctx.QueueFamilyIndex,
            default!, default!, default, default, default, default,
            NullLogger<VulkanRenderer>.Instance);
    }

    internal static CommandBuffer GetRendererCommandBuffer(VulkanRenderer renderer)
    {
        var f = typeof(VulkanRenderer).GetField("_commandBuffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("找不到 _commandBuffer 字段");
        return (CommandBuffer)(f.GetValue(renderer)
            ?? throw new InvalidOperationException("_commandBuffer 为 null"));
    }
}
