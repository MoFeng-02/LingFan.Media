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

    /// <summary>
    /// 增强版 MFT 枚举（MFTEnumEx）：返回 <c>IMFActivate*</c> 数组，可枚举异步/硬件/Store MFT。
    /// HEVC 视频扩展等 Store 安装解码器为异步 MFT，旧 MFTEnum 枚举不到，必须走此 API。
    /// </summary>
    [LibraryImport(MfplatDll)]
    internal static partial int MFTEnumEx(
        ref Guid guidCategory,
        uint Flags,
        ref MftRegisterTypeInfo pInputType,
        IntPtr pOutputType,
        out IntPtr ppMFTActivate,
        out uint pcMFTActivate);

    /// <summary>同 <see cref="MFTEnumEx"/>，但 pInputType/pOutputType 以原生指针传入（可传 <see cref="IntPtr.Zero"/> 表示不过滤）。供诊断枚举全部视频解码器使用。</summary>
    [LibraryImport(MfplatDll, EntryPoint = "MFTEnumEx")]
    internal static partial int MFTEnumExRaw(
        ref Guid guidCategory,
        uint Flags,
        IntPtr pInputType,
        IntPtr pOutputType,
        out IntPtr ppMFTActivate,
        out uint pcMFTActivate);

    /// <summary>释放 CoTaskMemAlloc 分配的内存（MFTEnum / MFTEnumEx 返回的数组）。</summary>
    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(IntPtr pv);

    /// <summary>COM 单元初始化标志（ole32）。MTA=0（多线程单元，无需消息泵）；STA=2（单线程单元，需消息泵）。</summary>
    internal const uint COINIT_MULTITHREADED = 0x0;
    internal const uint COINIT_APARTMENTTHREADED = 0x2;

    /// <summary>
    /// 初始化调用线程的 COM 单元（ole32）。
    /// <para><b>必要性</b>：MFDemuxer 的专用单线程（<see cref="LingFan.Media.Backends.MediaFoundation.Concurrency.SingleThreadTaskScheduler"/>）
    /// 是手动 <c>new Thread</c> 创建，CLR 不会自动 <c>CoInitializeEx</c>。该线程承载 <c>IMFSourceReader</c> 的全部
    /// <b>原始 vtable P/Invoke</b> 调用（ReadSample/OpenCore/SeekAsync）——RCW 才触发 CLR 自动单元初始化，
    /// 裸线程因此处于「无 COM 单元」状态，MF 原生 COM 调用会间歇踩坏原生堆 →
    /// <c>COR_E_EXECUTIONENGINE</c>（原生堆损坏，非确定性、常在若干次成功读后才爆发）。
    /// 显式 MTA 初始化消除该竞态。</para>
    /// </summary>
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    /// <summary>反初始化调用线程的 COM 单元（ole32），须与返回 S_OK/S_FALSE 的 <see cref="CoInitializeEx"/> 配对。</summary>
    /// <remarks>
    /// 副作用远超「本线程注销」：会关闭本线程的 COM 库、对本线程加载过的 in-proc server 逐个
    /// <c>DllCanUnloadNow</c> 卸载，并在本线程是最后一个 MTA 成员时**拆除整个 MTA**。
    /// 故本线程创建的 COM 对象必须在此之前全部 Release（见 MFDemuxer 的 COM 单元不变量），
    /// 且进程级 MTA 由 <see cref="CoIncrementMTAUsage"/> 单独保活（见 <see cref="MFPlatform"/>）。
    /// </remarks>
    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    /// <summary>
    /// 保证进程内存在多线程单元（MTA），且不依赖任何具体线程的 <c>CoInitializeEx</c>（ole32，Windows 8+）。
    /// </summary>
    /// <remarks>
    /// <para><b>必要性（纵深防御）</b>：任一裸线程调用 <see cref="CoUninitialize"/> 时，若它恰是当时
    /// 唯一的 MTA 成员，整个 MTA 会被拆除、其中的 in-proc server 被卸载——殃及**其它组件**仍持有的
    /// COM 对象（<c>MFVideoDecoder</c> 的 <c>IMFTransform</c>、<c>MFBackend</c> 的 MF 平台内部状态）。
    /// 本引擎的 <c>SingleThreadTaskScheduler</c> 正是这样的裸线程，且在纯解封装测试（无解码器参与）中
    /// 极可能成为唯一显式 MTA 成员。</para>
    /// <para><c>CoIncrementMTAUsage</c> 是微软为此提供的官方机制：它把 MTA 的存活与线程解耦，
    /// 无需常驻保活线程，直到配对的 <see cref="CoDecrementMTAUsage"/> 才释放。</para>
    /// </remarks>
    [LibraryImport("ole32.dll")]
    internal static partial int CoIncrementMTAUsage(out IntPtr pCookie);

    /// <summary>释放 <see cref="CoIncrementMTAUsage"/> 取得的 MTA 保活凭据（ole32，Windows 8+）。</summary>
    [LibraryImport("ole32.dll")]
    internal static partial int CoDecrementMTAUsage(IntPtr cookie);

    // 勿添加 MFSetAttributeGUID / MFSetAttributeUINT64 / MFGetAttributeUINT64 / MFSet(Get)AttributeSize 的 P/Invoke：
    // 它们是 mfapi.h 的 inline helper，mfplat.dll **没有这些导出**（运行时 GetProcAddress 已验证 NOT EXPORTED），
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
