using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>AVIO 读取回调（Cdecl，对齐 avio.h 的 avio_read_callback）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int AVIOReadFunc(void* opaque, byte* buf, int buf_size);

/// <summary>AVIO 写入回调（Cdecl）。本后端仅用于只读 AVIO，通常不传（传 null）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int AVIOWriteFunc(void* opaque, byte* buf, int buf_size);

/// <summary>AVIO 定位回调（Cdecl，返回 int64_t）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate long AVIOSeekFunc(void* opaque, long offset, int whence);
