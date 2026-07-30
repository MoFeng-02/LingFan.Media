namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// MediaFoundation 原始 vtable P/Invoke 委托与槽位辅助（AOT 兼容，零 [ComImport]/RCW）。
/// </summary>
/// <remarks>
/// <para>参照 WASAPI 的 <c>ComVTable</c> 模式：COM 对象以 <see cref="IntPtr"/> 持有，
/// 通过 <see cref="MfVTable.Get{T}"/> 按绝对 vtable 槽位取函数指针；IUnknown 槽 0=QueryInterface、1=AddRef、2=Release，
/// 接口方法从槽 3 起。释放用 <see cref="Marshal.Release"/>（与 WASAPI 一致，非 RCW 的 ReleaseComObject）。</para>
/// <para>仅声明实际被调用的 vtable 方法，槽位按 Windows SDK 真实顺序排列；未调用的方法（如 IMF2DBuffer、
/// IMFTransform 全量）无需声明——原始 vtable 委托模式按需按槽取函数指针，不要求声明所有槽。</para>
/// <para>仅 Windows 可用。</para>
/// </remarks>

// ── IMFSourceReader（IUnknown 之后：GetStreamSelection=3, SetStreamSelection=4, GetNativeMediaType=5, … ReadSample=10）──
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSourceReader_SetStreamSelection(IntPtr self, uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSourceReader_GetNativeMediaType(IntPtr self, uint dwStreamIndex, int dwMediaTypeIndex, out IntPtr ppMediaType);

internal delegate int IMFSourceReader_GetCurrentMediaType(IntPtr self, uint dwStreamIndex, out IntPtr ppMediaType);

/// <summary>IMFSourceReader::SetCurrentPosition（绝对槽 8 → slotIndex 5，槽位表已审计核验）。
/// guidTimeFormat 传 GUID_NULL 表示 100ns 单位；varPosition 为 PROPVARIANT(VT_I8)。</summary>
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSourceReader_SetCurrentPosition(IntPtr self, ref Guid guidTimeFormat, ref MfPropVariant varPosition);

/// <summary>
/// 最小化 PROPVARIANT（16 字节头 + 8 字节联合，x64 实际 24 字节）。仅用于 VT_I8（hVal）场景，
/// 无指针成员时无需 PropVariantClear。布局与 Windows PROPVARIANT 二进制兼容（AOT 友好，blittable）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MfPropVariant
{
    internal ushort vt;        // VARENUM；VT_I8 = 20
    internal ushort wReserved1;
    internal ushort wReserved2;
    internal ushort wReserved3;
    internal long hVal;        // VT_I8 值（100ns 单位时间戳）

    internal const ushort VT_I8 = 20;
}

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSourceReader_ReadSample(IntPtr self, uint dwStreamIndex, int dwControlFlags,
    out int pdwActualStreamIndex, out int pdwStreamFlags, out long pllTimestamp, out IntPtr ppSample);

// ── IMFMediaType（继承 IMFAttributes 30 方法（slotIndex 0~29，其中 GetUINT32=4, GetUINT64=5, GetGUID=7）；
//     自有方法：GetMajorType=30, IsCompressedFormat=31, IsEqual=32, GetRepresentation=33, FreeRepresentation=34）──
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaType_GetMajorType(IntPtr self, out Guid pguidMajorType);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaType_GetUINT32(IntPtr self, ref Guid guidKey, out uint punValue);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaType_GetUINT64(IntPtr self, ref Guid guidKey, out ulong punValue);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaType_GetGuid(IntPtr self, ref Guid guidKey, out Guid pguidValue);

// ── IMFSample.ConvertToContiguousBuffer（slotIndex=38，运行时已验证；见下方 IMFSample 槽位说明）──
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSample_ConvertToContiguousBuffer(IntPtr self, out IntPtr ppBuffer);

// ── IMFMediaBuffer（IUnknown 之后：Lock=3, Unlock=4, GetCurrentLength=5）──
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaBuffer_Lock(IntPtr self, out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaBuffer_Unlock(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaBuffer_GetCurrentLength(IntPtr self, out uint pcbCurrentLength);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFMediaBuffer_SetCurrentLength(IntPtr self, uint cbCurrentLength);

// ── IMFAttributes（继承 IUnknown；slotIndex 按 mfobjects.idl 声明顺序：GetItem=0, GetItemType=1, CompareItem=2, Compare=3,
//     GetUINT32=4, GetUINT64=5, GetDouble=6, GetGUID=7, GetStringLength=8, GetString=9, GetAllocatedString=10,
//     GetBlobSize=11, GetBlob=12, GetAllocatedBlob=13, GetUnknown=14, SetItem=15, DeleteItem=16, DeleteAllItems=17,
//     SetUINT32=18, SetUINT64=19, SetDouble=20, SetGUID=21, SetString=22, SetBlob=23, SetUnknown=24,
//     LockStore=25, UnlockStore=26, GetCount=27, GetItemByIndex=28, CopyAllItems=29（共 30 方法）。
//     锚点：GetUINT64=5、SetGUID=21 已本机运行时验证（2026-07-29）。⚠️ 早期注释误写 SetUINT64=13/SetGUID=14，勿回退。──
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFAttributes_SetUINT64(IntPtr self, ref Guid guidKey, ulong unValue);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFAttributes_SetGUID(IntPtr self, ref Guid guidKey, ref Guid guidValue);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFAttributes_SetBlob(IntPtr self, ref Guid guidKey, IntPtr pbBuf, uint cbBufSize);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFAttributes_GetBlobSize(IntPtr self, ref Guid guidKey, out uint pcbBlobSize);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFAttributes_GetBlob(IntPtr self, ref Guid guidKey, IntPtr pBuf, uint cbBufSize);

// ── IMFTransform（IUnknown 之后，vtable 顺序见 Windows SDK mftransform.h；下列数字为 MfVTable.Get 的 slotIndex）：
//     GetOutputStreamInfo=4, SetInputType=11, SetOutputType=12, GetOutputCurrentType=14, GetInputStatus=15,
//     ProcessMessage=19, ProcessInput=20, ProcessOutput=21 ──
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint dwFlags;     // MFT_OUTPUT_STREAM_* 标志（0x100 = PROVIDES_SAMPLES）
    public uint cbSize;      // 输出 sample 所需最小字节数
    public uint cbAlignment; // 内存对齐要求
}

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_GetOutputStreamInfo(IntPtr self, uint dwOutputStreamID, out MftOutputStreamInfo pStreamInfo);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_GetOutputAvailableType(IntPtr self, uint dwOutputStreamID, uint dwTypeIndex, out IntPtr ppType);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_SetInputType(IntPtr self, uint dwInputStreamID, IntPtr pInputType, uint dwFlags);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_SetOutputType(IntPtr self, uint dwOutputStreamID, IntPtr pOutputType, uint dwFlags);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_GetInputStatus(IntPtr self, uint dwInputStreamID, out uint pdwFlags);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_ProcessMessage(IntPtr self, int eMessage, nuint ulParam);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_ProcessInput(IntPtr self, uint dwInputStreamID, IntPtr pSample, uint dwFlags);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_ProcessOutput(IntPtr self, uint dwFlags, uint cOutputBufferCount,
    IntPtr pOutputBuffers, out uint pdwStatus);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFTransform_GetOutputCurrentType(IntPtr self, uint dwOutputStreamID, out IntPtr ppType);

// ── IMFSample（继承 IMFAttributes，其 30 个方法占 slotIndex 0~29；IMFSample 自有方法按 mfobjects.idl 声明顺序：
//     GetSampleFlags=30, SetSampleFlags=31, GetSampleTime=32, SetSampleTime=33, GetSampleDuration=34, SetSampleDuration=35,
//     GetBufferCount=36, GetBufferByIndex=37, ConvertToContiguousBuffer=38, AddBuffer=39, RemoveBufferByIndex=40,
//     RemoveAllBuffers=41, GetTotalLength=42, CopyToBuffer=43。
//     锚点：ConvertToContiguousBuffer=38、AddBuffer=39、GetBufferCount=36、Get/SetSampleTime=32/33、Get/SetSampleDuration=34/35
//     均已本机运行时验证（2026-07-29）。⚠️ 早期注释按"GetDuration/GetAttributes/GetStreamID"等臆测顺序推出 AddBuffer=44/
//     ConvertToContiguousBuffer=48，全错——IMFSample 没有那些方法，勿回退。──
[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSample_GetSampleTime(IntPtr self, out long pllTimeStamp);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSample_SetSampleTime(IntPtr self, long llTimeStamp);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSample_GetSampleDuration(IntPtr self, out long pllDuration);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSample_SetSampleDuration(IntPtr self, long llDuration);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSample_GetBufferByIndex(IntPtr self, uint dwBufferIndex, out IntPtr ppBuffer);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMFSample_AddBuffer(IntPtr self, IntPtr pBuffer);

/// <summary>
/// 从 COM 接口指针读取第 (3 + slotIndex) 个 vtable 槽位的函数指针并转为强类型委托。
/// 接口方法槽位从 3（IUnknown 之后）起；IUnknown 的 Release 在槽 2（slotIndex=-1）。
/// </summary>
internal static class MfVTable
{
    public static TDelegate Get<TDelegate>(IntPtr comPtr, int slotIndex) where TDelegate : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        IntPtr methodPtr = Marshal.ReadIntPtr(vtable, (3 + slotIndex) * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }
}
