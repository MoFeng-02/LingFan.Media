using System.Text;
using LingFan.Media.Backends.MediaCodec.Interop;

namespace LingFan.Media.Backends.MediaCodec.Wrappers;

/// <summary>
/// <c>AMediaFormat</c> 的托管包装。持有原生 <c>AMediaFormat*</c> 并负责释放。
/// </summary>
/// <remarks>
/// <para>字符串所有权铁律：<c>AMediaFormat_getString</c> / <c>toString</c> 返回的 <c>const char*</c>
/// 由 format 拥有，包装层一律 <see cref="Marshal.PtrToStringUTF8"/> 立即拷贝，绝不释放原生指针。</para>
/// <para>二进制键（csd-0 等）<c>getBuffer</c> 同样由 format 拥有，立即 <see cref="Marshal.Copy(byte[], int, nint, int)"/> 拷出。</para>
/// <para><b>两种构造语义</b>：</para>
/// <list type="bullet">
/// <item><see cref="AndroidMediaFormat()"/> 新建空 format（<c>AMediaFormat_new</c>），<see cref="Dispose"/> 释放。</item>
/// <item><see cref="AndroidMediaFormat(nint)"/> 接管既存指针（如 <c>AMediaExtractor_getTrackFormat</c> /
/// <c>getOutputFormat</c> 返回，调用方须释放），<see cref="Dispose"/> 同样释放——释放责任随所有权转移。</item>
/// </list>
/// </remarks>
internal sealed class AndroidMediaFormat : IDisposable
{
    private nint _native;

    /// <summary>新建空格式；失败抛 <see cref="OutOfMemoryException"/>。</summary>
    public AndroidMediaFormat()
    {
        _native = MediaNdk.AMediaFormat_new();
        if (_native == nint.Zero)
            throw new OutOfMemoryException("[ANDROID-FMT] AMediaFormat_new 返回 null");
    }

    /// <summary>接管既存原生 format 指针（调用方已完成分配，所有权转移给本实例）。</summary>
    public AndroidMediaFormat(nint existing)
    {
        if (existing == nint.Zero)
            throw new ArgumentNullException(nameof(existing));
        _native = existing;
    }

    /// <summary>原生 <c>AMediaFormat*</c> 句柄（供 <c>AMediaCodec_configure</c> 等直接传入）。</summary>
    public nint NativeHandle => _native;

    /// <summary>读取 int32 键；不存在返回 false。</summary>
    public bool TryGetInt32(string name, out int value)
        => MediaNdk.AMediaFormat_getInt32(_native, name, out value) != 0;

    /// <summary>读取 int64 键；不存在返回 false。</summary>
    public bool TryGetInt64(string name, out long value)
        => MediaNdk.AMediaFormat_getInt64(_native, name, out value) != 0;

    /// <summary>读取字符串键；不存在返回 null（已拷贝，调用方无需释放）。</summary>
    public string? GetString(string name)
    {
        if (MediaNdk.AMediaFormat_getString(_native, name, out nint ptr) == 0 || ptr == nint.Zero)
            return null;
        return Marshal.PtrToStringUTF8(ptr); // 拷贝；format 拥有原指针，绝不释放
    }

    /// <summary>读取二进制键（csd-0 等）；不存在返回 null（已拷贝）。</summary>
    public byte[]? GetBuffer(string name)
    {
        if (MediaNdk.AMediaFormat_getBuffer(_native, name, out nint data, out nuint size) == 0
            || data == nint.Zero || size == 0)
            return null;
        var arr = new byte[size];
        Marshal.Copy(data, arr, 0, (int)size); // 拷贝
        return arr;
    }

    /// <summary>写入字符串键（NDK 内部拷贝，入参可立即回收）。</summary>
    public void SetString(string name, string value)
        => MediaNdk.AMediaFormat_setString(_native, name, value);

    /// <summary>写入 int32 键。</summary>
    public void SetInt32(string name, int value)
        => MediaNdk.AMediaFormat_setInt32(_native, name, value);

    /// <summary>写入 int64 键。</summary>
    public void SetInt64(string name, long value)
        => MediaNdk.AMediaFormat_setInt64(_native, name, value);

    /// <summary>写入二进制键（csd-0 等）。NDK 内部拷贝，入参 buffer 在调用期间须钉住。</summary>
    public unsafe void SetBuffer(string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) return;
        fixed (byte* p = data)
            MediaNdk.AMediaFormat_setBuffer(_native, name, (nint)p, (nuint)data.Length);
    }

    /// <summary>格式可读表示（诊断用，已拷贝）。</summary>
    public string ToDebugString()
    {
        nint ptr = MediaNdk.AMediaFormat_toString(_native);
        return ptr == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_native == nint.Zero) return;
        MediaNdk.AMediaFormat_delete(_native);
        _native = nint.Zero;
    }
}
