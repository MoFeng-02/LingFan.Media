using System;
using Avalonia;
using Avalonia.Headless;
using LingFan.Media.Abstractions;
using Xunit;

namespace LingFan.Media.Avalonia.Tests;

/// <summary>
/// SkiaVideoPresenter GPU 纹理回退分支测试。
/// 验证收到 IGpuTextureResource 帧时不再抛 NotSupportedException，经中立桥回读为 BGRA32 写入 WriteableBitmap。
/// 全程零 Renderers 引用（依赖倒置严守）。
/// </summary>
public class SkiaVideoPresenterGpuTextureTests
{
    private sealed class HeadlessApp : Application { }

    [Fact]
    public void Present_GpuTextureResource_DispatchesToGpuFallbackAndCreatesBitmap()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
#pragma warning disable xUnit1031 // 故意阻塞：Avalonia Headless session.Dispatch 必须在调用线程同步完成（见 avalonia-headless-tests 技能）
        session.Dispatch(() =>
        {
            // 2x2 纯红 BGRA（B=0,G=0,R=255,A=255）
            var bgra = new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 };
            var gpu = new FakeGpuTextureResource(2, 2, PixelFormat.BGRA32, bgra);
            var frame = new VideoFrame(2, 2, PixelFormat.BGRA32, gpu, TimeSpan.Zero, TimeSpan.Zero, true);

            var presenter = new SkiaVideoPresenter();
            presenter.Present(frame);

            Assert.True(gpu.ReadbackCalled);
            Assert.NotNull(presenter.DebugBitmap);
            Assert.Equal(2, presenter.DebugBitmap!.PixelSize.Width);
            Assert.Equal(2, presenter.DebugBitmap.PixelSize.Height);
        }, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
    }

    private sealed class FakeGpuTextureResource : IGpuTextureResource
    {
        private readonly byte[] _data;
        public FakeGpuTextureResource(int w, int h, PixelFormat fmt, byte[] data)
        {
            Width = w;
            Height = h;
            Format = fmt;
            _data = data;
        }
        public int Width { get; }
        public int Height { get; }
        public PixelFormat Format { get; }
        public IntPtr NativeTextureHandle => IntPtr.Zero;
        public int SubresourceIndex => 0;
        public bool ReadbackCalled { get; private set; }
        public GpuTextureReadback ReadbackToCpu()
        {
            ReadbackCalled = true;
            return new GpuTextureReadback(Width, Height, PixelFormat.BGRA32, _data, Width * 4);
        }
        public void Dispose() { }
    }
}
