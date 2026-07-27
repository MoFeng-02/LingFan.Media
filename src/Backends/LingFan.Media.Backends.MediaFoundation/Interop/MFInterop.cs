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
internal static class MFInterop
{
    private const string MfplatDll = "mfplat.dll";
    private const string MfDll = "mf.dll";
    private const string MfreadwriteDll = "mfreadwrite.dll";

    /// <summary>初始化 MediaFoundation 平台。</summary>
    [DllImport(MfplatDll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MFStartup(int version, int dwFlags);

    /// <summary>关闭 MediaFoundation 平台。</summary>
    [DllImport(MfplatDll, CallingConvention = CallingConvention.StdCall)]
    internal static extern void MFShutdown();

    /// <summary>创建空媒体类型。</summary>
    [DllImport(MfplatDll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    /// <summary>从 URL 创建源读取器。</summary>
    [DllImport(MfreadwriteDll, CallingConvention = CallingConvention.StdCall)]
    [UnconditionalSuppressMessage("Trimming", "IL2050",
        Justification = "COM 接口使用 [ComImport] 显式定义，不会被裁剪器移除。仅 Windows 运行时使用。")]
    internal static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IntPtr pAttributes,
        out IMFSourceReader ppSourceReader);

    /// <summary>创建属性存储。</summary>
    [DllImport(MfplatDll, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    /// <summary>MF 转换消息类型（子集）。</summary>
    internal static class MFTMessageType
    {
        internal const int MFT_MESSAGE_SET_D3D_MANAGER = unchecked((int)0x00000001);
        internal const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = unchecked((int)0x10000000);
        internal const int MFT_MESSAGE_NOTIFY_END_STREAMING = unchecked((int)0x10000001);
        internal const int MFT_COMMAND_FLUSH = unchecked((int)0x00000000);
        internal const int MFT_MESSAGE_DRAIN = unchecked((int)0x10000002);
        internal const int MFT_MESSAGE_COMMAND_DRAIN = unchecked((int)0x00000000);
    }

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
