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
/// <para><b>调用约定（全文件已统一）</b>：COM vtable 方法一律用
/// <c>[UnmanagedFunctionPointer(CallingConvention.Winapi)]</c>，<b>禁止</b> <c>ThisCall</c>。
/// 依据：Windows SDK 头文件中 COM 方法声明为 <c>STDMETHODCALLTYPE</c>（即 <c>__stdcall</c>），
/// vtable 项的 C 原型形如 <c>HRESULT (STDMETHODCALLTYPE *Foo)(IFoo *This, ...)</c>——
/// <c>This</c> 是普通的<b>栈上首参</b>（这正是 C 语言宏 <c>(This)-&gt;lpVtbl-&gt;Foo(This, ...)</c> 能成立的原因）。
/// 而 .NET 的 <c>CallingConvention.ThisCall</c> 语义是"首参放 ECX 寄存器"（MSVC <c>__thiscall</c>，
/// 用于导出的 C++ 成员函数），与 COM ABI 不符：x86 上会导致 this 进 ECX、其余实参整体错位 + 栈失衡；
/// x64/ARM64 因只有单一 ABI 而侥幸等价，掩盖了问题。<c>Winapi</c> 在 Windows x86 解析为 StdCall、
/// x64 为默认 ABI，两端都正确，且与 WASAPI 侧 <c>WasapiInterop.cs</c> 的既有写法一致。</para>
/// </remarks>

// ── IMFSourceReader（IUnknown 之后：GetStreamSelection=3, SetStreamSelection=4, GetNativeMediaType=5, … ReadSample=10）──
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSourceReader_SetStreamSelection(IntPtr self, uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSourceReader_GetNativeMediaType(IntPtr self, uint dwStreamIndex, int dwMediaTypeIndex, out IntPtr ppMediaType);

/// <summary>IMFSourceReader::GetCurrentMediaType（绝对槽 6 → slotIndex 3）。
/// 此声明原本完全没有 [UnmanagedFunctionPointer] 特性（走默认约定，x64 侥幸可用）。
/// 现已按本文件头的调用约定补为 Winapi。所有 COM vtable 委托必须显式标注，勿再遗漏。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSourceReader_GetCurrentMediaType(IntPtr self, uint dwStreamIndex, out IntPtr ppMediaType);

/// <summary>IMFSourceReader::SetCurrentMediaType（绝对槽 7 → slotIndex 4，已比对 Windows SDK 10.0.26100.0
/// mfreadwrite.h:386 原型）。pdwReserved 为 <c>DWORD*</c> 保留参数，必须传 <see cref="IntPtr.Zero"/>。
/// 为音频流设置部分 PCM 媒体类型可令 SourceReader 自动加载解码器 + 重采样器，直接输出解码后 PCM。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSourceReader_SetCurrentMediaType(IntPtr self, uint dwStreamIndex, IntPtr pdwReserved, IntPtr pMediaType);

/// <summary>IMFSourceReader::SetCurrentPosition（绝对槽 8 → slotIndex 5，槽位表已核验）。
/// guidTimeFormat 传 GUID_NULL 表示 100ns 单位；varPosition 为 PROPVARIANT(VT_I8)。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
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
    internal const ushort VT_UI8 = 21; // MF_PD_DURATION 等 UINT64 属性以 VT_UI8 存储；uhVal 与 hVal 同 8 字节联合，直接读 hVal 即可
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSourceReader_ReadSample(IntPtr self, uint dwStreamIndex, int dwControlFlags,
    out int pdwActualStreamIndex, out int pdwStreamFlags, out long pllTimestamp, out IntPtr ppSample);

/// <summary>IMFSourceReader::GetPresentationAttribute（绝对槽 12 → slotIndex 9，槽位表见本文件头注释）。
/// dwStreamIndex 传 <see cref="MFConstants.MF_SOURCE_READER_MEDIASOURCE"/>(0xFFFFFFFF) 可取 presentation descriptor 属性；
/// 取 <see cref="MFConstants.MF_PD_DURATION"/> 得容器时长（VT_UI8，100ns 单位）。VT_UI8 为标量、无指针成员，输出 PROPVARIANT 无需 PropVariantClear。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSourceReader_GetPresentationAttribute(IntPtr self, uint dwStreamIndex, ref Guid guidAttribute, ref MfPropVariant pvarAttribute);

/// <summary>
/// IMFSourceReaderEx::GetTransformForStream（<b>绝对槽 16 → slotIndex 13</b>）。
/// </summary>
/// <remarks>
/// <para><b>槽位推导</b>（mfreadwrite.h:681-833 <c>IMFSourceReaderExVtbl</c> 实物顺序，逐条核对）：
/// IUnknown 0~2；IMFSourceReader 部分 GetStreamSelection=3, SetStreamSelection=4, GetNativeMediaType=5,
/// GetCurrentMediaType=6, SetCurrentMediaType=7, SetCurrentPosition=8, ReadSample=9, Flush=10,
/// GetServiceForStream=11, GetPresentationAttribute=12；Ex 自有 SetNativeMediaType=13,
/// AddTransformForStream=14, RemoveAllTransformsForStream=15, <b>GetTransformForStream=16</b>。
/// <c>MfVTable.Get</c> 的 slotIndex = 绝对槽 − 3 ⇒ 13。</para>
/// <para><b>用途</b>：枚举 SourceReader 为某条流实际建立的 MFT 链（dwTransformIndex 从 0 递增，
/// 越界返回 <c>MF_E_INVALIDINDEX</c>）。这是唯一能证伪「D3D_MANAGER 设了但没被采纳」的手段。</para>
/// <para><c>ppTransform</c> 为 out 引用，调用方必须 <see cref="Marshal.Release"/> 配对（COM 配对原则）。</para>
/// </remarks>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSourceReaderEx_GetTransformForStream(IntPtr self, uint dwStreamIndex, uint dwTransformIndex,
    out Guid pGuidCategory, out IntPtr ppTransform);

// ── IMFMediaType（继承 IMFAttributes 30 方法（slotIndex 0~29，其中 GetUINT32=4, GetUINT64=5, GetGUID=7）；
//     自有方法：GetMajorType=30, IsCompressedFormat=31, IsEqual=32, GetRepresentation=33, FreeRepresentation=34）──
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaType_GetMajorType(IntPtr self, out Guid pguidMajorType);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaType_GetUINT32(IntPtr self, ref Guid guidKey, out uint punValue);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaType_GetUINT64(IntPtr self, ref Guid guidKey, out ulong punValue);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaType_GetGuid(IntPtr self, ref Guid guidKey, out Guid pguidValue);

// ── IMFSample.ConvertToContiguousBuffer（slotIndex=38，运行时已验证；见下方 IMFSample 槽位说明）──
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSample_ConvertToContiguousBuffer(IntPtr self, out IntPtr ppBuffer);

// ── IMFMediaBuffer（IUnknown 之后：Lock=3, Unlock=4, GetCurrentLength=5）──
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaBuffer_Lock(IntPtr self, out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaBuffer_Unlock(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaBuffer_GetCurrentLength(IntPtr self, out uint pcbCurrentLength);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFMediaBuffer_SetCurrentLength(IntPtr self, uint cbCurrentLength);

// ── IMF2DBuffer2（mfobjects.h:1667 vtable 顺序：QI/AddRef/Release + Lock2D(3)/Unlock2D(4)/GetScanline0AndPitch(5)/
//     IsContiguousFormat(6)/GetContiguousLength(7)/ContiguousCopyTo(8)/ContiguousCopyFrom(9)/Lock2DSize(10)/Copy2DTo(11)）
//   注：IMF2DBuffer 自身继承 IUnknown（不继承 IMFMediaBuffer！），故 vtable 头 3 槽仍是 IUnknown 自身方法。
//   Lock2D 是 IMF2DBuffer 第一方法（槽 3 → slotIndex 0），Unlock2D 第二（槽 4 → slotIndex 1）。
//   半 DXVA 的治本处理：MS H264 MFT 把帧读回 Direct3DSurface9-backed 2D 内存（实际 pitch 16 对齐），
//   必须 Lock2D 取真值 pitch + scanline0 逐行拷贝，否则按紧凑 stride 拷贝出现横纹错位（AMD 上已验证）。
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMF2DBuffer2_Lock2D(IntPtr self, out IntPtr ppbScanline0, out int plPitch);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMF2DBuffer2_Unlock2D(IntPtr self);

// ── IMFAttributes（继承 IUnknown；slotIndex 按 mfobjects.idl 声明顺序：GetItem=0, GetItemType=1, CompareItem=2, Compare=3,
//     GetUINT32=4, GetUINT64=5, GetDouble=6, GetGUID=7, GetStringLength=8, GetString=9, GetAllocatedString=10,
//     GetBlobSize=11, GetBlob=12, GetAllocatedBlob=13, GetUnknown=14, SetItem=15, DeleteItem=16, DeleteAllItems=17,
//     SetUINT32=18, SetUINT64=19, SetDouble=20, SetGUID=21, SetString=22, SetBlob=23, SetUnknown=24,
//     LockStore=25, UnlockStore=26, GetCount=27, GetItemByIndex=28, CopyAllItems=29（共 30 方法）。
//     锚点：GetUINT64=5、SetGUID=21 已运行时验证。早期注释误写 SetUINT64=13/SetGUID=14，勿回退。──
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_SetUINT32(IntPtr self, ref Guid guidKey, uint unValue);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_SetUINT64(IntPtr self, ref Guid guidKey, ulong unValue);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_SetGUID(IntPtr self, ref Guid guidKey, ref Guid guidValue);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_SetBlob(IntPtr self, IntPtr guidKey, IntPtr pbBuf, uint cbBufSize);

/// <summary>IMFAttributes::SetUnknown（slotIndex 24）。
/// SDK 实物 mfobjects.h：<c>HRESULT SetUnknown(THIS, _In_ REFGUID guidKey, _In_opt_ IUnknown *pUnknown)</c>。
/// 用于把 COM 接口指针存入属性 store —— 本项目用于向 SourceReader 的创建 attributes 挂
/// <c>MF_SOURCE_READER_D3D_MANAGER = IMFDXGIDeviceManager*</c>（SourceReader 零拷贝关键）。
/// 属性 store 会对传入接口 AddRef（内部持有），调用方仍需释放自己那份引用，切勿因「已交给 MF」就不 Release。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_SetUnknown(IntPtr self, ref Guid guidKey, IntPtr pUnknown);

/// <summary>IMFAttributes::DeleteItem（slotIndex 16）。用于协商失败后剔除过度约束的属性再重试。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_DeleteItem(IntPtr self, ref Guid guidKey);

/// <summary>IMFAttributes::GetUINT32（slotIndex 4）。用于探测 MFT 能力属性（如 MF_SA_D3D11_AWARE）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_GetUINT32(IntPtr self, ref Guid guidKey, out uint punValue);

/// <summary>IMFAttributes::GetGUID（slotIndex 7）。用于从 MFTEnumEx 返回的 IMFActivate 读取 MFT_TRANSFORM_CLSID_Attribute。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_GetGUID(IntPtr self, ref Guid guidKey, out Guid pguidValue);

/// <summary>IMFActivate::ActivateObject（slotIndex 28，绝对槽 31；mfobjects.h IMFActivateVtbl：IUnknown(3)+IMFAttributes(28)+ActivateObject）。
/// 经 <c>MFTEnumEx</c> 得到的 <c>IMFActivate</c> 必须调本方法获取 <c>IMFTransform</c>——Store/异步 MFT 的
/// <c>IMFActivate</c> 不设 <c>MFT_TRANSFORM_CLSID_Attribute</c>、亦不可 <c>CoCreateInstance</c>，只能 ActivateObject。
/// （已用 DumpAll 中 GetGUID@slot7 成功反证 IMFAttributes 数法正确 ⇒ ActivateObject 绝对槽 = 3+28 = 31 ⇒ slotIndex 28。）</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFActivate_ActivateObject(IntPtr self, ref Guid riid, out IntPtr ppv);

/// <summary>IMFAttributes::GetAllocatedBlob（slotIndex 13）。AOT 安全版：原生自分配 buffer 并返回指针+长度，
/// 与已工作的 GetBlobSize 同形（ref Guid 改为 IntPtr + 两个 out 参数），彻底绕开 GetBlob 在 AOT 下
/// 「原生向调用方传入 buffer 大块写入（如 42 字节 SEQ_HEADER）静默 AV 退出」的路径。调用方须 Marshal.FreeCoTaskMem 释放。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_GetAllocatedBlob(IntPtr self, IntPtr guidKey, out IntPtr ppBuf, out uint pcbSize);

/// <summary>IMFAttributes::GetAllocatedString（slotIndex 10）。AOT 安全版：原生 CoTaskMemAlloc 出 WCHAR* 并回传指针+字符数，
/// 避免 GetString 由调用方递 buffer（与 GetBlob 同类的 AOT 静默 AV 风险）。调用方须 Marshal.FreeCoTaskMem 释放。
/// 仅用于 MFT 身份取证（FRIENDLY_NAME / HARDWARE_URL），非热路径。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFAttributes_GetAllocatedString(IntPtr self, ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);

// ── IMFTransform（IUnknown 之后，vtable 顺序见 Windows SDK mftransform.h；下列数字为 MfVTable.Get 的 slotIndex）。
//    注意含 GetStreamIDs(2) 与 AddInputStreams(9) 两个方法（早期头注漏数其中之一，致从 SetInputType 起全体 −1，已据 mftransform.h 校正）：
//    GetOutputStreamInfo=4, GetOutputAvailableType=11, SetInputType=12, SetOutputType=13, GetInputCurrentType=14,
//    GetOutputCurrentType=15, GetInputStatus=16, GetOutputStatus=17, SetOutputBounds=18, ProcessEvent=19,
//    ProcessMessage=20, ProcessInput=21, ProcessOutput=22（GetAttributes=5 不可漏数）──
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint dwFlags;     // MFT_OUTPUT_STREAM_* 标志（0x100 = PROVIDES_SAMPLES）
    public uint cbSize;      // 输出 sample 所需最小字节数
    public uint cbAlignment; // 内存对齐要求
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_GetOutputStreamInfo(IntPtr self, uint dwOutputStreamID, out MftOutputStreamInfo pStreamInfo);

/// <summary>IMFTransform::GetAttributes（slotIndex 5，绝对槽 8）。取 MFT 全局属性存储（调用方须 Release）。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_GetAttributes(IntPtr self, out IntPtr ppAttributes);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_GetOutputAvailableType(IntPtr self, uint dwOutputStreamID, uint dwTypeIndex, out IntPtr ppType);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_SetInputType(IntPtr self, uint dwInputStreamID, IntPtr pInputType, uint dwFlags);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_SetOutputType(IntPtr self, uint dwOutputStreamID, IntPtr pOutputType, uint dwFlags);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_GetInputStatus(IntPtr self, uint dwInputStreamID, out uint pdwFlags);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_ProcessMessage(IntPtr self, int eMessage, nuint ulParam);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_ProcessInput(IntPtr self, uint dwInputStreamID, IntPtr pSample, uint dwFlags);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_ProcessOutput(IntPtr self, uint dwFlags, uint cOutputBufferCount,
    IntPtr pOutputBuffers, out uint pdwStatus);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFTransform_GetOutputCurrentType(IntPtr self, uint dwOutputStreamID, out IntPtr ppType);

// ── IMFSample（继承 IMFAttributes，其 30 个方法占 slotIndex 0~29；IMFSample 自有方法按 mfobjects.idl 声明顺序：
//     GetSampleFlags=30, SetSampleFlags=31, GetSampleTime=32, SetSampleTime=33, GetSampleDuration=34, SetSampleDuration=35,
//     GetBufferCount=36, GetBufferByIndex=37, ConvertToContiguousBuffer=38, AddBuffer=39, RemoveBufferByIndex=40,
//     RemoveAllBuffers=41, GetTotalLength=42, CopyToBuffer=43。
//     锚点：ConvertToContiguousBuffer=38、AddBuffer=39、GetBufferCount=36、Get/SetSampleTime=32/33、Get/SetSampleDuration=34/35
//     均已运行时验证。早期注释按"GetDuration/GetAttributes/GetStreamID"等错误顺序推出 AddBuffer=44/
//     ConvertToContiguousBuffer=48，全错——IMFSample 没有那些方法，勿回退。──
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSample_GetSampleTime(IntPtr self, out long pllTimeStamp);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSample_SetSampleTime(IntPtr self, long llTimeStamp);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSample_GetSampleDuration(IntPtr self, out long pllDuration);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSample_SetSampleDuration(IntPtr self, long llDuration);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSample_GetBufferByIndex(IntPtr self, uint dwBufferIndex, out IntPtr ppBuffer);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMFSample_AddBuffer(IntPtr self, IntPtr pBuffer);

/// <summary>
/// 从 COM 接口指针读取第 (3 + slotIndex) 个 vtable 槽位的函数指针并转为强类型委托。
/// 接口方法槽位从 3（IUnknown 之后）起；IUnknown 的 Release 在槽 2（slotIndex=-1）。
/// </summary>
internal static class MfVTable
{
    public static TDelegate Get<TDelegate>(IntPtr comPtr, int slotIndex) where TDelegate : Delegate
    {
        // 错误链：记录每次 vtable 取入口；严格模式下若 comPtr 已释放则立刻指认 UAF。
        InteropTrace.OnVTableGet(comPtr, typeof(TDelegate).Name);
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        IntPtr methodPtr = Marshal.ReadIntPtr(vtable, (3 + slotIndex) * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }
}
