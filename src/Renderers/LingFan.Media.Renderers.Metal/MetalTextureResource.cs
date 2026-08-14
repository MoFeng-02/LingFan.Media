using LingFan.Media.Abstractions;

namespace LingFan.Media.Renderers.Metal;

/// <summary>
/// Metal 纹理帧资源。实现 <see cref="IFrameResource"/>。
/// </summary>
/// <remarks>
/// <para>封装 MTLTexture（macOS / iOS）原生句柄，供未来 GPU 纹理零拷贝路径（C 线增强）消费——
/// 当前 <see cref="MetalRenderer.Present"/> 仅走 <see cref="SoftwareFrameResource"/> 软帧上屏，
/// <see cref="IGpuTextureResource"/> 零拷贝路径暂未启用，故本类型作为合法 <see cref="IFrameResource"/> 实现存在，
/// 不直接参与上屏。</para>
/// <para><b>所有权</b>：若 <paramref name="ownsHandle"/> 为 <see langword="true"/>，<see cref="Dispose"/> 中
/// 经 <see cref="MetalNative.objc_release"/> 释放原生纹理；否则仅解除托管引用（句柄由外部所有者管理）。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
public sealed class MetalTextureResource : IFrameResource
{
    private readonly nint _texture;
    private readonly bool _ownsHandle;
    private bool _disposed;

    /// <summary>初始化 <see cref="MetalTextureResource"/> 的新实例。</summary>
    /// <param name="width">帧宽度（像素）。</param>
    /// <param name="height">帧高度（像素）。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="texture">MTLTexture* 原生句柄。</param>
    /// <param name="ownsHandle">是否拥有句柄所有权（<see langword="true"/> 时 Dispose 释放之）。</param>
    public MetalTextureResource(int width, int height, PixelFormat format, nint texture, bool ownsHandle = false)
    {
        Width = width;
        Height = height;
        Format = format;
        _texture = texture;
        _ownsHandle = ownsHandle;
    }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <summary>原生 MTLTexture 句柄（MTLTexture*）。</summary>
    public nint NativeTextureHandle => _texture;

    /// <inheritdoc />
    public bool IsDisposed => _disposed;

    /// <summary>释放原生纹理资源（仅当拥有所有权时经 objc_release）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsHandle && _texture != nint.Zero)
            MetalNative.objc_release(_texture);
    }
}
