namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI COM 互操作声明。包含 COM 接口、GUID、常量和 P/Invoke。
/// </summary>
/// <remarks>
/// <para><b>AOT 兼容</b>：使用 [ComImport] + [InterfaceType(InterfaceIsIUnknown)]，
/// COM 调用存根在编译期生成，不依赖运行时反射。</para>
/// <para><b>不使用 NAudio</b>：NAudio 内部使用反射，不满足 AOT 友好要求。</para>
/// </remarks>
internal static class WasapiInterop
{
    // ── 常量 ──

    /// <summary>COINIT_MULTITHREADED：多线程单元，COM 对象可跨线程访问。</summary>
    public const uint COINIT_MULTITHREADED = 0x0;

    /// <summary>COINIT_APARTMENTTHREADED：单线程单元。</summary>
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    /// <summary>RPC_E_CHANGED_MODE：线程已初始化为不同的 COM 单元模式。</summary>
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary>CLSCTX_ALL：所有类上下文。</summary>
    public const int CLSCTX_ALL = 0x17;

    /// <summary>eRender：渲染（播放）端点。</summary>
    public const int EDataFlow_Render = 0;

    /// <summary>eConsole：控制台用途（默认音频设备）。</summary>
    public const int ERole_Console = 0;

    /// <summary>AUDCLNT_SHAREMODE_SHARED：共享模式。</summary>
    public const int AUDCLNT_SHAREMODE_SHARED = 0;

    /// <summary>AUDCLNT_SHAREMODE_EXCLUSIVE：独占模式。</summary>
    public const int AUDCLNT_SHAREMODE_EXCLUSIVE = 1;

    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT：缓冲区标记为静音。</summary>
    public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    /// <summary>WAVE_FORMAT_PCM：PCM 格式标签。</summary>
    public const ushort WAVE_FORMAT_PCM = 1;

    /// <summary>WAVE_FORMAT_IEEE_FLOAT：IEEE 浮点格式标签。</summary>
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;

    /// <summary>WAVE_FORMAT_EXTENSIBLE：扩展格式标签。</summary>
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    /// <summary>100ns 单位（WASAPI 时间戳基准）。</summary>
    public const long ReftimesPerSec = 10_000_000;

    // ── GUID ──

    public static readonly Guid CLSID_MMDeviceEnumerator =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static readonly Guid IID_IMMDeviceEnumerator =
        new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    public static readonly Guid IID_IAudioClient =
        new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    public static readonly Guid IID_IAudioRenderClient =
        new("F294ACFC-3146-4483-A7BF-ADD077DB4D09");

    public static readonly Guid IID_ISimpleAudioVolume =
        new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    public static readonly Guid IID_IAudioClock =
        new("CD63314F-3FBA-4a1b-812C-EF96358728E7");

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</summary>
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT =
        new("00000003-0000-0010-8000-00aa00389b71");

    // ── P/Invoke ──

    // PreserveSig=true（默认）——必须保留HRESULT返回值，
    // 因为 RPC_E_CHANGED_MODE 是失败HRESULT但需要特殊处理（不抛异常而是跳过CoUninitialize），
    // PreserveSig=false 会让marshaler自动抛COMException，导致RPC_E_CHANGED_MODE分支不可达。
    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);

    [DllImport("ole32.dll")]
    public static extern IntPtr CoTaskMemAlloc(nuint cb);
}

/// <summary>
/// WAVEFORMATEX 结构体，描述 PCM 音频格式。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

/// <summary>
/// WAVEFORMATEXTENSIBLE 结构体，扩展格式（支持多声道和子格式标识）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEXTENSIBLE
{
    public WAVEFORMATEX Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}

// ── COM 接口声明 ──
// vtable 顺序：IUnknown(0=QI, 1=AddRef, 2=Release) + 接口方法(3+)
// 使用 [PreserveSig] 保留 HRESULT，手动用 Marshal.ThrowExceptionForHR 处理

/// <summary>
/// IMMDeviceEnumerator：音频设备枚举器。
/// </summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    // vtable[3]
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr collection);

    // vtable[4]
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
}

/// <summary>
/// IMMDevice：音频端点设备。
/// </summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    // vtable[3]
    [PreserveSig]
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
}

/// <summary>
/// IAudioClient：WASAPI 音频客户端。
/// </summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    // vtable[3]
    [PreserveSig]
    int Initialize(
        int shareMode,
        int streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        IntPtr pWaveFormat,
        Guid pAudioSessionGuid);

    // vtable[4]
    [PreserveSig]
    int GetBufferSize(out uint pNumBufferFrames);

    // vtable[5]
    [PreserveSig]
    int GetStreamLatency(out long phnsLatency);

    // vtable[6]
    [PreserveSig]
    int GetCurrentPadding(out uint pNumPaddingFrames);

    // vtable[7]
    [PreserveSig]
    int IsFormatSupported(
        int shareMode,
        IntPtr pWaveFormat,
        out IntPtr pClosestMatch);

    // vtable[8]
    [PreserveSig]
    int GetMixFormat(out IntPtr pDeviceFormat);

    // vtable[9]
    [PreserveSig]
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    // vtable[10]
    [PreserveSig]
    int Start();

    // vtable[11]
    [PreserveSig]
    int Stop();

    // vtable[12]
    [PreserveSig]
    int Reset();

    // vtable[13]
    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    // vtable[14]
    [PreserveSig]
    int GetService(ref Guid riid, out IntPtr ppv);
}

/// <summary>
/// IAudioRenderClient：音频渲染（播放）缓冲区。
/// </summary>
[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADD077DB4D09")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    // vtable[3]
    [PreserveSig]
    int GetBuffer(uint numFramesRequested, out IntPtr pData);

    // vtable[4]
    [PreserveSig]
    int ReleaseBuffer(uint numFramesWritten, int dwFlags);
}

/// <summary>
/// ISimpleAudioVolume：简单音量控制。
/// </summary>
[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    // vtable[3]
    [PreserveSig]
    int SetMasterVolume(float fLevel, Guid EventContext);

    // vtable[4]
    [PreserveSig]
    int GetMasterVolume(out float pfLevel);

    // vtable[5]
    [PreserveSig]
    int SetMute(int bMute, Guid EventContext);

    // vtable[6]
    [PreserveSig]
    int GetMute(out int pbMute);
}

/// <summary>
/// IAudioClock：音频播放时钟，用于查询已播放位置。
/// </summary>
[ComImport]
[Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClock
{
    // vtable[3]
    [PreserveSig]
    int Start();

    // vtable[4]
    [PreserveSig]
    int Stop();

    // vtable[5]
    [PreserveSig]
    int GetPosition(out ulong pu64DevicePosition, out ulong pu64QPCPosition);

    // vtable[6]
    [PreserveSig]
    int GetCharacteristics(out uint pdwCharacteristics);
}
