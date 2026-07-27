namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// MediaFoundation COM 互操作接口定义（最小子集）。
/// </summary>
/// <remarks>
/// <para>仅定义 Demuxer（IMFSourceReader）和 Decoder（IMFTransform）所需的接口。</para>
/// <para>所有接口使用 [ComImport] 属性，AOT 兼容（无反射）。</para>
/// <para>仅 Windows 可用。非 Windows 平台不加载此模块。</para>
/// </remarks>

/// <summary>MF 属性存储接口（IMFAttributes 的子集）。</summary>
[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFCFC726"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    int GetItem(ref Guid key, ref PROPVARIANT value);
    int SetItem(ref Guid key, ref PROPVARIANT value);
    int DeleteItem(ref Guid key);
    int GetUINT32(ref Guid key, out uint value);
    int SetUINT32(ref Guid key, uint value);
    int GetUINT64(ref Guid key, out ulong value);
    int SetUINT64(ref Guid key, ulong value);
    int GetDouble(ref Guid key, out double value);
    int GetGuid(ref Guid key, out Guid value);
    int SetGuid(ref Guid key, ref Guid value);
    // ... 其他方法省略（仅使用上述子集）
}

/// <summary>MF 媒体类型接口（继承 IMFAttributes）。</summary>
[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CA465D11FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    int GetMajorType(out Guid pguidMajorType);
    int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
    int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
}

/// <summary>MF 媒体缓冲区接口。</summary>
[ComImport, Guid("045FA593-8799-42b8-BC8D-4442D00E59A5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    int Unlock();
    int GetCurrentLength(out uint pcbCurrentLength);
    int SetCurrentLength(uint cbCurrentLength);
    int GetMaxLength(out uint pcbMaxLength);
}

/// <summary>MF 2D 媒体缓冲区接口（视频帧用）。</summary>
[ComImport, Guid("7F4275A7-D9C4-4d3c-9D48-4C0B6A4F6DD3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    int Lock2D(out IntPtr ppbScanline0, out int plPitch);
    int Unlock2D();
    int GetScanline0AndPitch(out IntPtr pbScanline0, out int lPitch);
    int SetScanline0AndPitch(IntPtr pbScanline0, int lPitch);
    int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool pfIsContiguous);
    int ContiguousLengthFromIMF2DBuffer(out uint pcbLength);
    int ContiguousCopyTo(IMF2DBuffer pDestBuffer, uint destStride, out uint pcbWritten);
    int ContiguousCopyFrom(IntPtr pSrcBuffer, uint srcStride, uint srcSize);
}

/// <summary>MF 采样接口（包含一个或多个缓冲区和时间戳）。</summary>
[ComImport, Guid("C40F0045-2D8C-46f6-BC3C-48A80D5B4F6C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    int GetSampleFlags(out uint pdwSampleFlags);
    int SetSampleFlags(uint dwSampleFlags);
    int GetSampleTime(out long pllSampleTime);
    int SetSampleTime(long llSampleTime);
    int GetSampleDuration(out long pllSampleDuration);
    int SetSampleDuration(long llSampleDuration);
    int GetBufferCount(out uint pdwBufferCount);
    int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
    int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    int AddBuffer(IMFMediaBuffer pBuffer);
    int RemoveBufferByIndex(uint dwIndex);
    int RemoveAllBuffers();
}

/// <summary>MF 源读取器接口（用于解封装）。</summary>
[ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
    int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    int GetNativeMediaType(uint dwStreamIndex, int dwMediaTypeIndex, out IMFMediaType ppMediaType);
    int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
    int SetCurrentMediaType(uint dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
    int ReadSample(uint dwStreamIndex, int dwControlFlags, uint dwStreamIndex2,
        out int pdwActualStreamIndex, out int pdwStreamFlags, out long pllTimestamp, out IMFSample ppSample);
    int SetStreamSelection2(uint dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    int Flush(uint dwStreamIndex);
    int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppv);
    int GetPresentationAttribute(uint dwStreamIndex, ref Guid guidAttribute, out PROPVARIANT pvarAttribute);
}

/// <summary>MF 转换接口（用于解码）。</summary>
[ComImport, Guid("BF94C121-0B6E-4a71-BBA6-7D9A9B2C5C2E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    int GetStreamLimits(out uint pdwInputMinimum, out uint pdwInputMaximum, out uint pdwOutputMinimum, out uint pdwOutputMaximum);
    int GetStreamCount(out uint pdwInputStreams, out uint pdwOutputStreams);
    int GetStreamIDs(uint dwInputIDArraySize, [Out] uint[] pInputIDs, uint dwOutputIDArraySize, [Out] uint[] pOutputIDs);
    int GetInputStreamInfo(uint dwInputStreamID, out uint pdwFlags);
    int GetOutputStreamInfo(uint dwOutputStreamID, out uint pdwFlags);
    int GetInputAvailableType(uint dwInputStreamID, int dwTypeIndex, out IMFMediaType ppType);
    int GetOutputAvailableType(uint dwOutputStreamID, int dwTypeIndex, out IMFMediaType ppType);
    int SetInputType(uint dwInputStreamID, IMFMediaType pType, int dwFlags);
    int SetOutputType(uint dwOutputStreamID, IMFMediaType pType, int dwFlags);
    int GetInputCurrentType(uint dwInputStreamID, out IMFMediaType ppType);
    int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);
    int GetInputStatus(uint dwInputStreamID, out uint pdwFlags);
    int GetOutputStatus(uint dwOutputStreamID, out uint pdwFlags);
    int SetInputBounds(long llLowerBound, long llUpperBound);
    int ProcessEvent(uint dwInputStreamID, IntPtr pEvent);
    int ProcessMessage(int eMessage, IntPtr ulParam);
    int ProcessInput(uint dwInputStreamID, IMFSample pSample, int dwFlags);
    int ProcessOutput(int dwFlags, uint cOutputBufferCount,
        [In, Out] MFT_OUTPUT_DATA_BUFFER[] pOutputSamples, out int pdwStatus);
}

/// <summary>MFT 输出数据缓冲区结构。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER
{
    public uint dwStreamID;
    public IMFSample? pSample;
    public int dwStatus;
    public IntPtr pEvents;
}

/// <summary>PROPVARIANT 简化结构（用于 IMFAttributes）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr data1;
    public IntPtr data2;
}
