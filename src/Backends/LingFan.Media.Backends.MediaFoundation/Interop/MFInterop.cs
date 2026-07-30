using System.Diagnostics.CodeAnalysis;

namespace LingFan.Media.Backends.MediaFoundation.Interop;

/// <summary>
/// MediaFoundation P/Invoke 声明。
/// </summary>
/// <remarks>
/// <para>仅 Windows 可用。非 Windows 平台在运行时由 <see cref="MFBackend"/> 检测并抛出
/// <see cref="PlatformNotSupportedException"/>。</para>
/// <para>AOT 兼容：使用 [LibraryImport]（源生成器），无反射。</para>
/// </remarks>
internal static partial class MFInterop
{
    private const string MfplatDll = "mfplat.dll";
    private const string MfDll = "mf.dll";
    private const string MfreadwriteDll = "mfreadwrite.dll";

    /// <summary>初始化 MediaFoundation 平台。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFStartup(int version, int dwFlags);

    /// <summary>关闭 MediaFoundation 平台。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial void MFShutdown();

    /// <summary>创建空媒体类型。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFCreateMediaType(out IntPtr ppMFType);

    /// <summary>从 URL 创建源读取器。</summary>
    [LibraryImport(MfreadwriteDll)]
    [UnconditionalSuppressMessage("Trimming", "IL2050",
        Justification = "返回 IntPtr（非 [ComImport] 类型），使用原始 vtable P/Invoke 包装。仅 Windows 运行时使用。")]
    internal static partial int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IntPtr pAttributes,
        out IntPtr ppSourceReader);

    /// <summary>创建属性存储。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    /// <summary>创建空 IMFSample（用于向 MFT ProcessInput 喂压缩样本）。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFCreateSample(out IntPtr ppSample);

    /// <summary>创建 IMFSample 依赖的 IMFMediaBuffer（托管内存，可 Lock 拷贝压缩数据）。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    /// <summary>从 CLSID 创建 COM 对象（ole32）。用于实例化 H264/H265 解码 MFT。</summary>
    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid rriid, out IntPtr ppv);

    /// <summary>CLSCTX 标志（CoCreateInstance）。</summary>
    internal const int CLSCTX_ALL = 0x17;

    /// <summary>MFT 注册类型信息（major + subtype），用于 <see cref="MFTEnum"/> 的输入/输出过滤。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MftRegisterTypeInfo
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    /// <summary>枚举注册的解码 MFT（返回 CLSID 数组，CoTaskMemAlloc 分配，调用方须 <see cref="CoTaskMemFree"/>）。</summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFTEnum(
        ref Guid guidCategory,
        uint Flags,
        ref MftRegisterTypeInfo pInputType,
        IntPtr pOutputType,
        IntPtr pAttributes,
        out IntPtr ppclsidMFT,
        out uint pcMFT);

    /// <summary>释放 CoTaskMemAlloc 分配的内存（MFTEnum 返回的 CLSID 数组）。</summary>
    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(IntPtr pv);

    // ⚠️ 勿添加 MFSetAttributeGUID / MFSetAttributeUINT64 / MFGetAttributeUINT64 / MFSet(Get)AttributeSize 的 P/Invoke：
    // 它们是 mfapi.h 的 inline helper，mfplat.dll **没有这些导出**（本机 GetProcAddress 已验证 NOT EXPORTED，2026-07-29），
    // P/Invoke 一到运行时即 EntryPointNotFoundException。属性读写一律走 IMFAttributes vtable：
    // GetUINT64=slotIndex 5、SetGUID=slotIndex 21（均已运行时验证）。

    /// <summary>MF 转换输入状态标志。</summary>
    internal static class MFTInputStatus
    {
        internal const int MFT_INPUT_STATUS_ACCEPT_DATA = 0x00000001;
    }

    /// <summary>MF 转换输出状态标志。</summary>
    internal static class MFTOutputStatus
    {
        internal const int MFT_OUTPUT_STATUS_SAMPLE_READY = 0x00000001;
    }

    /// <summary>MF 转换输出数据缓冲区标志。</summary>
    internal static class MFTOutputDataBuffer
    {
        internal const int MFT_OUTPUT_DATA_BUFFER_INCOMPLETE = 0x01000000;
        internal const int MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE = 0x00000100;
    }

    /// <summary>源读取器控制标志。</summary>
    internal static class SourceReaderControl
    {
        internal const int MF_SOURCE_READER_CONTROLF_DRAIN = 0x00000001;
    }
}
