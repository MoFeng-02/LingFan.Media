using System.Runtime.InteropServices;
using LingFan.Media.Abstractions;

namespace LingFan.Media.Backends.VLCNative;

/// <summary>
/// 将 <see cref="IMediaStream"/> 适配为 VLC 的 imem access 模块（零 LibVLCSharp）。
/// </summary>
/// <remarks>
/// <para>仅用于无 <see cref="IMediaStream.Location"/> 的内存/透传流；有地址流走 location 直接打开（见 VLCNativeDemuxer）。</para>
/// <para>VLC 3.x 下 imem 受 get/release 指针校验限制为罕见路径，但需保留以覆盖无地址字节流。</para>
/// <para>4 个回调委托存字段防 GC；用 <see cref="GCHandle"/> 把本实例钉为 opaque，回调整形取回。</para>
/// <para>AOT 兼容：sealed 类，无反射。</para>
/// </remarks>
internal sealed class VLCNativeMediaStreamInput : IDisposable
{
    private readonly IMediaStream _stream;
    private readonly GCHandle _handle;
    private bool _disposed;

    // imem 回调委托（存字段防 GC）
    private readonly LibVlcTypes.MediaOpenCb _openCb;
    private readonly LibVlcTypes.MediaReadCb _readCb;
    private readonly LibVlcTypes.MediaSeekCb _seekCb;
    private readonly LibVlcTypes.MediaCloseCb _closeCb;

    /// <summary>
    /// 初始化 <see cref="VLCNativeMediaStreamInput"/> 的新实例。
    /// </summary>
    /// <param name="stream">媒体数据流（必须已建连）。</param>
    public VLCNativeMediaStreamInput(IMediaStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _handle = GCHandle.Alloc(this);
        _openCb = OnOpen;
        _readCb = OnRead;
        _seekCb = OnSeek;
        _closeCb = OnClose;
    }

    /// <summary>
    /// 用本实例的 imem 回调创建 libvlc_media_t（<c>libvlc_media_new_callbacks</c>）。
    /// </summary>
    public nint CreateMedia(nint instance)
    {
        nint opaque = GCHandle.ToIntPtr(_handle);
        return LibVlcNative.libvlc_media_new_callbacks(
            instance,
            Marshal.GetFunctionPointerForDelegate(_openCb),
            Marshal.GetFunctionPointerForDelegate(_readCb),
            Marshal.GetFunctionPointerForDelegate(_seekCb),
            Marshal.GetFunctionPointerForDelegate(_closeCb),
            opaque);
    }

    private int OnOpen(IntPtr opaque, IntPtr datap, IntPtr sizep)
    {
        // 把 GCHandle 透传为 read/seek/close 的 opaque，并写入流长度。
        Marshal.WriteIntPtr(datap, opaque);
        long len = _stream.Length;
        Marshal.WriteInt64(sizep, len < 0 ? 0 : len);
        return 0;
    }

    private nint OnRead(IntPtr opaque, IntPtr buf, nint len)
    {
        if (len <= 0) return 0;
        var self = (VLCNativeMediaStreamInput)GCHandle.FromIntPtr(opaque).Target!;
        int toRead = (int)len; // VLC 块大小有限，安全截断
        byte[] tmp = new byte[toRead];
        int read = self._stream.Read(tmp.AsSpan(0, toRead));
        if (read > 0)
            Marshal.Copy(tmp, 0, buf, read);
        return read;
    }

    private int OnSeek(IntPtr opaque, ulong offset)
    {
        var self = (VLCNativeMediaStreamInput)GCHandle.FromIntPtr(opaque).Target!;
        if (!self._stream.CanSeek) return -1;
        long r = self._stream.Seek((long)offset, SeekOrigin.Begin);
        return r >= 0 ? 0 : -1;
    }

    private void OnClose(IntPtr opaque)
    {
        var self = (VLCNativeMediaStreamInput)GCHandle.FromIntPtr(opaque).Target!;
        self._stream.Close();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle.IsAllocated)
            _handle.Free();
    }
}
