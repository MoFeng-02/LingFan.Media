using System.Buffers;

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// 将非托管指针映射为 <see cref="Memory{T}"/>，供 V2-05 零拷贝场景使用
/// （FFmpeg 引用计数 buffer：<c>av_frame_clone</c> / <c>av_packet_clone</c> 共享的原生内存）。
/// </summary>
/// <remarks>
/// <para><b>所有权</b>：本管理器不拥有底层内存，仅提供托管视图。原生内存的生命周期由
/// 引用计数所有者（<c>SafeAVFrameHandle</c> / <c>SafeAVPacketHandle</c>，以中立
/// <see cref="IDisposable"/> 形式传递给 Abstractions 层的帧/包）控制——
/// 帧/包 Dispose 时释放所有者，原生引用计数减一。</para>
/// <para><b>安全约束</b>：所有者释放后不得再访问由本管理器产生的 <see cref="Memory{T}"/>
/// （悬垂指针）。管线契约保证消费方在 Dispose 前完成读取（Present/Submit 均为同步拷贝）。</para>
/// <para><b>异步策略</b>：sync-only（纯内存视图，无 I/O），符合同步/异步双支持基准第 3 条。</para>
/// <para><b>AOT 兼容</b>：基于标准库 <see cref="MemoryManager{T}"/>，无反射、无动态代码生成。</para>
/// </remarks>
internal sealed unsafe class NativeBufferMemoryManager : MemoryManager<byte>
{
    private readonly byte* _ptr;
    private readonly int _length;

    /// <summary>初始化映射非托管指针的 Memory 管理器。</summary>
    /// <param name="ptr">非托管内存起始指针（仅当 <paramref name="length"/> 为 0 时允许为 <see cref="IntPtr.Zero"/>）。</param>
    /// <param name="length">字节长度（非负）。</param>
    public NativeBufferMemoryManager(IntPtr ptr, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (ptr == IntPtr.Zero && length > 0)
            throw new ArgumentException("指针为空但长度大于零", nameof(ptr));
        _ptr = (byte*)ptr;
        _length = length;
    }

    /// <inheritdoc/>
    public override Span<byte> GetSpan() => new(_ptr, _length);

    /// <inheritdoc/>
    /// <remarks>非托管内存天然固定，无需 GCHandle。</remarks>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_ptr + elementIndex, default, this);
    }

    /// <inheritdoc/>
    public override void Unpin()
    {
        // 非托管内存无需解除固定
    }

    /// <inheritdoc/>
    /// <remarks>不拥有内存，无资源可释放。</remarks>
    protected override void Dispose(bool disposing)
    {
    }
}
