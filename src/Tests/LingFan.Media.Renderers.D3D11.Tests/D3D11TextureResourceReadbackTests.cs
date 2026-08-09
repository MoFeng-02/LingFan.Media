using LingFan.Media.Abstractions;
using LingFan.Media.Renderers.D3D11;
using LingFan.Media.Renderers.D3D11.SafeHandles;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace LingFan.Media.Renderers.D3D11.Tests;

/// <summary>
/// D3D11TextureResource.ReadbackToCpu 单元测试（GPU 纹理回退）。
/// 验证中立 IGpuTextureResource 桥在真实 D3D11 设备上将 GPU 纹理回读为 BGRA32。
/// </summary>
public class D3D11TextureResourceReadbackTests
{
    [Fact]
    public void ReadbackToCpu_BgraTexture_ReturnsBgraPixels()
    {
        var factory = new D3D11RendererFactory(NullLoggerFactory.Instance);
        var device = (ID3D11Device)factory.Context.SharedDevice!;

        const int w = 4, h = 4;
        var desc = new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        var texture = device.CreateTexture2D(desc);

        // 纯红 BGRA (B=0,G=0,R=255,A=255)
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4 + 0] = 0;
            pixels[i * 4 + 1] = 0;
            pixels[i * 4 + 2] = 255;
            pixels[i * 4 + 3] = 255;
        }
        device.ImmediateContext.UpdateSubresource<byte>(pixels, texture, 0u, (uint)(w * 4), 0u, null);

        // SafeHandle 接管原生指针（不 AddRef，Vortice 包装废弃，SafeHandle 为唯一释放者）
        var handle = new SafeD3D11TextureHandle(texture.NativePointer);
        using var resource = new D3D11TextureResource(w, h, PixelFormat.BGRA32, handle, 0);

        using var rb = resource.ReadbackToCpu();
        Assert.Equal(PixelFormat.BGRA32, rb.Format);
        Assert.Equal(w * 4, rb.Stride);
        var data = rb.Data.Span;
        for (int i = 0; i < w * h; i++)
        {
            Assert.Equal(0, data[i * 4 + 0]);     // B
            Assert.Equal(0, data[i * 4 + 1]);     // G
            Assert.Equal(255, data[i * 4 + 2]);   // R
            Assert.Equal(255, data[i * 4 + 3]);   // A
        }
    }

    [Fact]
    public void ReadbackToCpu_RgbaTexture_SwapsRAndB()
    {
        var factory = new D3D11RendererFactory(NullLoggerFactory.Instance);
        var device = (ID3D11Device)factory.Context.SharedDevice!;

        const int w = 2, h = 2;
        var desc = new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        var texture = device.CreateTexture2D(desc);

        // 纯红 RGBA (R=255,G=0,B=0,A=255)
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4 + 0] = 255;
            pixels[i * 4 + 1] = 0;
            pixels[i * 4 + 2] = 0;
            pixels[i * 4 + 3] = 255;
        }
        device.ImmediateContext.UpdateSubresource<byte>(pixels, texture, 0u, (uint)(w * 4), 0u, null);

        var handle = new SafeD3D11TextureHandle(texture.NativePointer);
        using var resource = new D3D11TextureResource(w, h, PixelFormat.RGBA32, handle, 0);

        using var rb = resource.ReadbackToCpu();
        Assert.Equal(PixelFormat.BGRA32, rb.Format);
        var data = rb.Data.Span;
        for (int i = 0; i < w * h; i++)
        {
            // 纯红 RGBA（R=255,B=0）经正确 RGBA→BGRA 转换后，颜色不变（仅字节序变）：
            // BGRA32 字节序 B,G,R,A → B=0, G=0, R=255, A=255
            Assert.Equal(0, data[i * 4 + 0]);     // B
            Assert.Equal(0, data[i * 4 + 1]);     // G
            Assert.Equal(255, data[i * 4 + 2]);   // R
            Assert.Equal(255, data[i * 4 + 3]);   // A
        }
    }
}
