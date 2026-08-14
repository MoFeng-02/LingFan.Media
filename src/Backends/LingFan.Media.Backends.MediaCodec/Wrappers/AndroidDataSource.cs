using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LingFan.Media.Backends.MediaCodec.Interop;

namespace LingFan.Media.Backends.MediaCodec.Wrappers;

/// <summary>
/// 桥接 <see cref="IMediaStream"/> → NDK <c>AMediaDataSource</c>（API 28+）。
/// </summary>
/// <remarks>
/// <para>用于没有文件地址/URL 的流（内存流、透传流）。NDK 通过 <c>setReadAt</c> /
/// <c>setGetSize</c> / <c>setClose</c> 接收 C 函数指针，经静态 <c>[UnmanagedCallersOnly]</c> 回调，
/// 用 <see cref="GCHandle"/> 把 <c>userdata</c> 路由回具体的 <see cref="IMediaStream"/>。</para>
/// <para><b>调用约定</b>：回调显式声明 <see cref="CallConvCdecl"/>，与 <c>MediaNdk</c> 中
/// <c>delegate* unmanaged[Cdecl]</c> 的声明严格一致（Android Bionic 默认即 Cdecl，此处显式锚定，避免 ABI 错位）。</para>
/// <para><b>线程安全</b>：NDK 可能从多线程调用 <c>readAt</c>，故每实例持有一把互斥锁串行化对
/// <see cref="IMediaStream"/> 的访问。<see cref="IMediaStream.Read"/> 同步边界，调用可能阻塞（网络流），锁内阻塞是预期行为。</para>
/// <para><b>生命周期</b>：<c>close</c> 回调按 NDK 语义仅用于解除阻塞中的读取，不释放流（流归 demuxer 所有）；
/// GCHandle 与底层 <c>AMediaDataSource</c> 的释放统一在 <see cref="Dispose"/> 中完成。</para>
/// <para><b>仅 Android 可用</b>：本类型只在 Android 运行时被构造；构造函数不强制平台检查（由 demuxer 在
/// <see cref="OperatingSystem.IsAndroid"/> 门控后调用）。</para>
/// </remarks>
internal sealed class AndroidDataSource : IDisposable
{
    // 回调路由状态：经 GCHandle 从原生 userdata 还原。
    private sealed class RouteState
    {
        public IMediaStream Stream = null!;
        public readonly object Gate = new();
        public byte[]? Scratch; // 读缓冲（锁内复用，避免每次分配）
    }

    private readonly nint _native;
    private readonly GCHandle _handle;
    private bool _disposed;

    /// <summary>原生 <c>AMediaDataSource*</c> 句柄（供 <c>AMediaExtractor_setDataSourceCustom</c> 传入）。</summary>
    public nint NativeHandle => _native;

    /// <summary>用指定媒体流构造数据源桥；失败抛 <see cref="OutOfMemoryException"/>。</summary>
    public AndroidDataSource(IMediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _native = MediaNdk.AMediaDataSource_new();
        if (_native == nint.Zero)
            throw new OutOfMemoryException("[ANDROID-DS] AMediaDataSource_new 返回 null");

        var state = new RouteState { Stream = stream };
        _handle = GCHandle.Alloc(state);
        nint userdata = GCHandle.ToIntPtr(_handle);

        // 注册回调：原生侧把 userdata 原样回传，由本类在静态回调中还原 RouteState。
        // 取 [UnmanagedCallersOnly] 静态方法的函数指针必须位于 unsafe 上下文。
        unsafe
        {
            MediaNdk.AMediaDataSource_setUserdata(_native, userdata);
            MediaNdk.AMediaDataSource_setReadAt(_native, &ReadAt);
            MediaNdk.AMediaDataSource_setGetSize(_native, &GetSize);
            MediaNdk.AMediaDataSource_setClose(_native, &Close);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ReadAt(nint userdata, long offset, nint buffer, nuint size)
    {
        if (userdata == nint.Zero || buffer == nint.Zero || size == 0)
            return -1;

        var state = (RouteState)GCHandle.FromIntPtr(userdata).Target!;
        var stream = state.Stream;
        try
        {
            lock (state.Gate)
            {
                if (offset < 0)
                    return -1;
                // 定位到请求偏移（AMediaDataSource 语义：绝对偏移读）
                stream.Seek(offset, SeekOrigin.Begin);

                int capacity = size > int.MaxValue ? int.MaxValue : (int)size;
                if (state.Scratch is null || state.Scratch.Length < capacity)
                    state.Scratch = new byte[capacity];

                // 完整读取：IMediaStream.Read 可能返回短读（尤其网络流），循环补齐直到 size 或 EOF。
                int total = 0;
                while (total < capacity)
                {
                    int r = stream.Read(state.Scratch.AsSpan(total, capacity - total));
                    if (r == 0) break; // EOF
                    total += r;
                }

                if (total > 0)
                    Marshal.Copy(state.Scratch, 0, buffer, total);
                return total; // ssize_t：实际字节数；0=EOF；-1=错误
            }
        }
        catch (Exception)
        {
            // 任何异常转为 NDK 错误码，绝不穿透到原生栈
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetSize(nint userdata)
    {
        var state = (RouteState)GCHandle.FromIntPtr(userdata).Target!;
        long len = state.Stream.Length;
        return len < 0 ? -1 : (nint)len; // 未知大小返回 -1
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Close(nint userdata)
    {
        // NDK 语义：仅通知数据源“即将不再需要”，用于解除阻塞中的 readAt。
        // 底层 IMediaStream 的生命周期归 demuxer，此处不释放。GCHandle 与 _native 在 Dispose 中释放。
        // 预留钩子：若未来需要标记“流已关闭”以快速失败后续读，可在此置位（当前无需）。
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        MediaNdk.AMediaDataSource_delete(_native);
        if (_handle.IsAllocated)
            _handle.Free();
    }
}
