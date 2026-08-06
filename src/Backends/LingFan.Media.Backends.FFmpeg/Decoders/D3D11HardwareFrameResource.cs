using System.Runtime.InteropServices;
using LingFan.Media.Backends.FFmpeg.SafeHandles;

namespace LingFan.Media.Backends.FFmpeg.Decoders;

/// <summary>
/// D3D11VA 硬件解码输出的 GPU 纹理帧资源。实现 <see cref="IGpuTextureResource"/>。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：包装 FFmpeg D3D11VA 硬解输出的 <c>ID3D11Texture2D*</c> COM 指针，
/// 供 <c>D3D11Renderer</c> 直接 GPU 拷贝（零拷贝路径）。</para>
/// <para><b>🔴 切片所有权（2026-08-06 §29 根治「画面抽帧 + 跳场景」）</b>：D3D11VA 的
/// <c>AVHWFramesContext</c> 池语义是「少量 <c>ID3D11Texture2D</c> 纹理<b>数组</b>对象 + 每帧占用其中一个
/// array slice」。<c>data[0]</c> 是数组对象、<c>data[1]</c> 是切片索引，而<b>切片的占用权归
/// <c>AVFrame->buf[0]</c></b>（指向池内 buffer），<b>不</b>归纹理对象的 COM 引用计数。
/// 因此仅 <c>Marshal.AddRef(texture)</c> 只能保证「数组对象不被销毁」，
/// <b>完全阻止不了解码器把该切片分配给下一帧</b>。
/// 旧实现在 <c>CreateHardwareFrameFromAVFrame</c> 返回后立即 <c>av_frame_free</c>，切片当场回池 ⇒
/// 渲染线程稍后 <c>CopySubresourceRegion(texture, slice)</c> 拷到的是<b>几帧之后</b>的图像或半写入状态 ⇒
/// 画面「卡住数秒（切片暂未复用）→ 突然跳到别的场景（切片被复用）」+ 撕裂 + 驱动状态错乱崩溃。
/// 修法：持有 <c>av_frame_clone</c> 出的 AVFrame（对 <c>buf[0]</c> 引用计数 +1），
/// <see cref="Dispose"/> 时才释放 ⇒ 切片在渲染完成前绝不回池。这与软解 BGRA 路径
/// （<c>TryCreateZeroCopyResource</c>）和 MediaCodec 表面路径的既有做法一致。</para>
/// <para><b>引用计数</b>：构造时 <c>Marshal.AddRef</c> 纹理对象 + 持有克隆 AVFrame 引用；
/// <see cref="Dispose"/> 时 <c>Marshal.Release</c> + <c>av_frame_free</c>（顺序：先放帧引用再放纹理引用，
/// 保证纹理对象在切片回池期间仍然存活）。</para>
/// <para><b>零拷贝链路</b>：FFmpeg D3D11VA 硬解 → D3D11HardwareFrameResource（切片保活）
/// → D3D11Renderer CopySubresourceRegion → BackBuffer → SwapChain → DirectComposition → Display。</para>
/// <para><b>ReadbackToCpu 限制</b>：硬解纹理不支持 CPU 回读（需要 Vortice 互操作，Backends.FFmpeg 无 Vortice 依赖）。
/// 硬解路径需配合 D3D11 渲染器使用；如需 Skia 渲染，请使用软件解码。</para>
/// <para><b>异步策略</b>：全部同步（sync/native 分类）——COM AddRef/Release 与 IntPtr 操作均为同步，无 I/O await。</para>
/// <para><b>AOT 兼容</b>：sealed 类，无反射，实现中立 <see cref="IGpuTextureResource"/> 契约。</para>
/// </remarks>
internal sealed class D3D11HardwareFrameResource : IGpuTextureResource
{
    private readonly IntPtr _texturePtr;

    /// <summary>
    /// 克隆的 AVFrame，持有 D3D11VA 池内切片的引用计数，保证切片在本资源存活期间不被解码器复用。
    /// </summary>
    private readonly SafeAVFrameHandle? _frameOwner;

    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public PixelFormat Format { get; }

    /// <inheritdoc/>
    public IntPtr NativeTextureHandle => _disposed ? IntPtr.Zero : _texturePtr;

    /// <inheritdoc/>
    public int SubresourceIndex { get; }

    /// <summary>
    /// 初始化 <see cref="D3D11HardwareFrameResource"/> 的新实例。
    /// </summary>
    /// <param name="texturePtr">FFmpeg D3D11VA 输出的 ID3D11Texture2D COM 指针（构造时 AddRef，Dispose 时 Release）。</param>
    /// <param name="width">纹理宽度。</param>
    /// <param name="height">纹理高度。</param>
    /// <param name="format">像素格式（通常为 NV12）。</param>
    /// <param name="subresourceIndex">纹理数组索引（来自 AVFrame->data[1]）。</param>
    /// <param name="frameOwner">
    /// <c>av_frame_clone</c> 出的 AVFrame 句柄，持有池内切片引用计数（<b>必传</b>，否则切片会被解码器提前复用）。
    /// 所有权转移给本实例，<see cref="Dispose"/> 时释放。
    /// </param>
    internal D3D11HardwareFrameResource(
        IntPtr texturePtr,
        int width,
        int height,
        PixelFormat format,
        int subresourceIndex,
        SafeAVFrameHandle frameOwner)
    {
        if (texturePtr == IntPtr.Zero)
        {
            frameOwner?.Dispose();
            throw new ArgumentNullException(nameof(texturePtr));
        }
        if (width <= 0 || height <= 0)
        {
            frameOwner?.Dispose();
            throw new ArgumentOutOfRangeException(nameof(width), "纹理尺寸必须为正数。");
        }
        ArgumentNullException.ThrowIfNull(frameOwner);

        _texturePtr = texturePtr;
        Width = width;
        Height = height;
        Format = format;
        SubresourceIndex = subresourceIndex;
        _frameOwner = frameOwner;

        // AddRef：FFmpeg 持有原始引用（随 AVFrame 释放），我们额外增加引用以独立管理纹理对象生命周期。
        // ⚠️ 这一份引用只保「纹理数组对象」不销毁；切片占用权由 _frameOwner 保障，二者缺一不可。
        Marshal.AddRef(texturePtr);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 硬解纹理不支持 CPU 回读（Backends.FFmpeg 无 Vortice 依赖）。
    /// 硬解路径需配合 D3D11 渲染器使用。
    /// </remarks>
    public GpuTextureReadback ReadbackToCpu()
        => throw new NotSupportedException(
            "D3D11VA 硬件解码纹理不支持 CPU 回读。请使用 D3D11 渲染器直接渲染 GPU 纹理，" +
            "或使用软件解码以获得 CPU 可读帧。");

    /// <summary>
    /// 释放池内切片引用与 COM 纹理引用。
    /// </summary>
    /// <remarks>
    /// 顺序固定：<b>先</b>释放 AVFrame 克隆（切片回池，可被解码器复用）、<b>后</b> Release 纹理对象，
    /// 确保切片归还发生在纹理数组对象仍然存活的窗口内，避免在已销毁对象上操作。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 切片保活引用：归还后解码器方可复用该 slice
        _frameOwner?.Dispose();

        if (_texturePtr != IntPtr.Zero)
        {
            Marshal.Release(_texturePtr);
        }
    }
}
