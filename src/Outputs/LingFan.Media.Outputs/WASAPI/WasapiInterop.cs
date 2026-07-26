namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI COM 互操作声明。包含 COM 接口、GUID、常量和 P/Invoke。
/// </summary>
/// <remarks>
/// <para><b>AOT 兼容</b>：使用原始 vtable P/Invoke（纯 Marshal 封送，无 [ComImport]/RCW），
/// 在 NativeAOT 下完全兼容。（注：本环境 .NET 10 运行时未提供 GeneratedComInterfaceAttribute，
/// 故采用 vtable 委托方式，而非源生成式 COM。）</para>
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

// ── COM vtable 调用（AOT 兼容：纯 P/Invoke + 委托封送，不使用 [ComImport]/RCW）──
// WASAPI 接口 vtable 布局：IUnknown(0=QueryInterface, 1=AddRef, 2=Release) + 接口方法(3+)
// 每个委托首个参数为 COM 对象指针（this），调用时由 ComVTable.Get 从 vtable 槽位读取函数指针。
// 所有 HRESULT 均 PreserveSig 返回，由调用方用 Marshal.ThrowExceptionForHR 处理（与原始 [ComImport] 行为一致）。

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMMDeviceEnumerator_GetDefaultAudioEndpoint(IntPtr self, int dataFlow, int role, out IntPtr endpoint);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IMMDevice_Activate(IntPtr self, ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClient_Initialize(IntPtr self, int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pWaveFormat, ref Guid pAudioSessionGuid);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClient_GetBufferSize(IntPtr self, out uint pNumBufferFrames);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClient_GetCurrentPadding(IntPtr self, out uint pNumPaddingFrames);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClient_Start(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClient_Stop(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClient_Reset(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClient_GetService(IntPtr self, ref Guid riid, out IntPtr ppv);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioRenderClient_GetBuffer(IntPtr self, uint numFramesRequested, out IntPtr pData);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioRenderClient_ReleaseBuffer(IntPtr self, uint numFramesWritten, int dwFlags);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int ISimpleAudioVolume_SetMasterVolume(IntPtr self, float fLevel, ref Guid EventContext);

[UnmanagedFunctionPointer(CallingConvention.ThisCall)]
internal delegate int IAudioClock_GetPosition(IntPtr self, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

/// <summary>
/// 从 COM 接口指针读取第 (3 + slotIndex) 个 vtable 槽位的函数指针并转为强类型委托。
/// </summary>
internal static class ComVTable
{
    public static TDelegate Get<TDelegate>(IntPtr comPtr, int slotIndex) where TDelegate : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        IntPtr methodPtr = Marshal.ReadIntPtr(vtable, (3 + slotIndex) * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }
}
