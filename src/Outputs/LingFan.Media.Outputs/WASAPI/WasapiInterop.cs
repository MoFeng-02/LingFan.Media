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
/// <summary>音频会话分类（IAudioClient2.SetClientProperties 使用，audioclient.h AudioClientCategory）。
/// 媒体播放器应设为 <see cref="BackgroundCapableMedia"/> / <see cref="Movie"/> / <see cref="GameMedia"/>，
/// 使 Windows 不将其当作可挂起的后台会话——否则控制台/隐藏窗口/非前台窗口的会话会在播放数秒后
/// 被系统暂停音频（典型表现：声音 ~10-15s 后中断，视频却继续）。</summary>
public enum AudioClientCategory
{
    /// <summary>未指定分类（与不设等价）。</summary>
    Other = 0,
    /// <summary>仅前台媒体。</summary>
    ForegroundOnlyMedia = 1,
    /// <summary>后台可播放媒体（音乐/视频类应用）。Windows 不会因窗口非前台而挂起该会话。</summary>
    BackgroundCapableMedia = 2,
    /// <summary>通信音频（通话等）。</summary>
    Communications = 3,
    /// <summary>提示音。</summary>
    Alerts = 4,
    /// <summary>音效。</summary>
    SoundEffects = 5,
    /// <summary>游戏语音。</summary>
    GameChat = 6,
    /// <summary>游戏媒体。</summary>
    GameMedia = 7,
    /// <summary>电影/长视频。</summary>
    Movie = 8,
    /// <summary>通用媒体。</summary>
    Media = 9,
    /// <summary>语音。</summary>
    Speech = 10,
    /// <summary>通知。</summary>
    Notification = 11,
    /// <summary>音频处理。</summary>
    AudioProcessing = 12,
}

internal static partial class WasapiInterop
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

    /// <summary>S_OK：操作成功。</summary>
    public const int S_OK = 0;

    /// <summary>S_FALSE：操作成功但返回额外信息（如 IsFormatSupported 返回最接近格式）。</summary>
    public const int S_FALSE = 1;

    /// <summary>AUDCLNT_STREAMFLAGS_EVENTCALLBACK：事件驱动模式标志。
    /// 设置后 WASAPI 通过事件通知缓冲区可写，替代轮询。</summary>
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;

    /// <summary>
    /// AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM（AudioSessionTypes.h:138 = 0x80000000）。
    /// <para>
    /// 官方语义："A channel matrixer and a sample rate converter are inserted as necessary
    /// to convert between the uncompressed format supplied to IAudioClient::Initialize
    /// and the audio engine mix format."
    /// </para>
    /// <para>
    /// ⚠️ 审计修复（2026-07-31）：仅共享模式有效。不设此标志时，传给 Initialize 的格式
    /// 必须与引擎 mix format 一致，否则要么 AUDCLNT_E_UNSUPPORTED_FORMAT，要么（本项目此前的做法）
    /// 被迫用 mix format 打开设备 —— 而 Submit 侧仍按解码器采样率/声道数写入，导致
    /// 44.1kHz 解码流被 48kHz 设备按 48kHz 播放（音高偏高约 8.8%），或 2ch 数据写进 6ch 缓冲区。
    /// </para>
    /// </summary>
    public const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);

    /// <summary>
    /// AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY（AudioSessionTypes.h:137 = 0x08000000）。
    /// 与 AUTOCONVERTPCM 搭配使用时启用更高质量（但性能开销更高）的采样率转换器。
    /// 官方建议：音频最终给人听时应启用（区别于灌静音 / 电平表等场景）。
    /// </summary>
    public const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    /// <summary>AUDCLNT_E_DEVICE_IN_USE：独占模式下设备已被其他应用占用。</summary>
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);

    /// <summary>AUDCLNT_E_UNSUPPORTED_FORMAT：设备不支持请求的格式。</summary>
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);

    /// <summary>
    /// AUDCLNT_E_INVALID_STREAM_FLAG（audioclient.h:2719 = AUDCLNT_ERR(0x021) = 0x88890021）：
    /// Initialize 的 streamFlags 组合非法（如在独占模式下传 AUTOCONVERTPCM）。
    /// </summary>
    public const int AUDCLNT_E_INVALID_STREAM_FLAG = unchecked((int)0x88890021);

    /// <summary>AUDCLNT_E_NOT_INITIALIZED：IAudioClient 尚未初始化。</summary>
    public const int AUDCLNT_E_NOT_INITIALIZED = unchecked((int)0x88890001);
    /// <summary>AUDCLNT_E_NOT_STOPPED：Start/Reset/Stop 在错误的流状态下调用（如 Running 态调 Start）。</summary>
    public const int AUDCLNT_E_NOT_STOPPED = unchecked((int)0x88890005);

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED：音频设备已被移除或失效。</summary>
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    /// <summary>100ns 单位（WASAPI 时间戳基准）。</summary>
    public const long ReftimesPerSec = 10_000_000;

    // ── GUID ──

    public static readonly Guid CLSID_MMDeviceEnumerator =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static readonly Guid IID_IMMDeviceEnumerator =
        new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    public static readonly Guid IID_IAudioClient =
        new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    // Audioclient.h: MIDL_INTERFACE("726778CD-F60A-4eda-82DE-E47610CD78AA") IAudioClient2 : public IAudioClient
    // 🔴 vtable 绝对槽（逐方法照抄头文件，勿凭记忆推算）：
    //   IUnknown(0..2) + IAudioClient 12 方法(3..14)
    //   + IAudioClient2: IsOffloadCapable(15) / SetClientProperties(16) / GetBufferSizeLimits(17)
    // 即 SetClientProperties 是绝对槽 16（ComVTable slotIndex 13），不是 15。
    // 曾误写为 15 ⇒ 实际调到 IsOffloadCapable（多一个 BOOL* 出参）⇒ 野指针写 ⇒ 0xC0000005。
    // 用于把音频会话分类为媒体类，避免 Windows 对后台/非前台会话施加节流或挂起策略。
    public static readonly Guid IID_IAudioClient2 =
        new("726778CD-F60A-4eda-82DE-E47610CD78AA");

    // Audioclient.h: MIDL_INTERFACE("F294ACFC-3146-4483-A7BF-ADDCA7C260E2") IAudioRenderClient
    // ⚠️ 此 GUID 曾误写为 ...-ADD077DB4D09，导致 GetService 恒返回 E_NOINTERFACE(0x80004002)，
    // 被上层误判为「设备不支持格式」而跳过有头音频测试。改动前务必比对 Windows SDK 头文件原文。
    public static readonly Guid IID_IAudioRenderClient =
        new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    public static readonly Guid IID_ISimpleAudioVolume =
        new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    public static readonly Guid IID_IAudioClock =
        new("CD63314F-3FBA-4a1b-812C-EF96358728E7");

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</summary>
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT =
        new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM：PCM 整数子格式（S16/S32）。</summary>
    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM =
        new("00000001-0000-0010-8000-00aa00389b71");

    // ── P/Invoke ──

    // PreserveSig=true（默认）——必须保留HRESULT返回值，
    // 因为 RPC_E_CHANGED_MODE 是失败HRESULT但需要特殊处理（不抛异常而是跳过CoUninitialize），
    // PreserveSig=false 会让marshaler自动抛COMException，导致RPC_E_CHANGED_MODE分支不可达。
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(IntPtr ptr);
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

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMMDeviceEnumerator_GetDefaultAudioEndpoint(IntPtr self, int dataFlow, int role, out IntPtr endpoint);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IMMDevice_Activate(IntPtr self, ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_Initialize(IntPtr self, int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pWaveFormat, ref Guid pAudioSessionGuid);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_GetBufferSize(IntPtr self, out uint pNumBufferFrames);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_GetStreamLatency(IntPtr self, out long pLatency);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_GetCurrentPadding(IntPtr self, out uint pNumPaddingFrames);

/// <summary>
/// IAudioClient::IsFormatSupported — 查询设备是否支持指定格式。
/// </summary>
/// <param name="self">COM 对象指针。</param>
/// <param name="shareMode">共享/独占模式。</param>
/// <param name="pFormat">请求的格式（WAVEFORMATEX*）。</param>
/// <param name="ppClosestMatch">最接近格式输出指针（共享模式可传非 NULL 接收最接近格式，传 NULL 则不分配；
/// 独占模式必须传 IntPtr.Zero/NULL）。</param>
/// <returns>S_OK=完全支持；S_FALSE=不完全支持但返回最接近格式；AUDCLNT_E_UNSUPPORTED_FORMAT=不支持。</returns>
/// <remarks>
/// 审计修复：参数从 <c>out IntPtr</c> 改为 <c>IntPtr</c>（按值传递）。
/// 原因：1) <c>out</c> 总是传递非 NULL 指针，违反独占模式 ppClosestMatch 必须为 NULL 的 API 约定；
/// 2) 共享模式返回 S_FALSE 时 WASAPI 通过 ppClosestMatch 分配 CoTaskMem 内存，
/// 用 <c>out _</c> 丢弃后无法释放→内存泄漏。改为按值传 IntPtr.Zero 后 WASAPI 不分配内存。
/// </remarks>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_IsFormatSupported(IntPtr self, int shareMode, IntPtr pFormat, IntPtr ppClosestMatch);

/// <summary>
/// IAudioClient::GetMixFormat — 获取音频引擎的混音格式（共享模式设备原生格式）。
/// </summary>
/// <param name="self">COM 对象指针。</param>
/// <param name="pDeviceFormat">返回的 WAVEFORMATEX*（由 CoTaskMemAlloc 分配，调用方负责 CoTaskMemFree）。</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_GetMixFormat(IntPtr self, out IntPtr pDeviceFormat);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_Start(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_Stop(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IUnknown_QueryInterface(IntPtr self, ref Guid iid, out IntPtr ppv);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IUnknown_Release(IntPtr self);

/// <summary>IAudioClient2::SetClientProperties — 设置音频会话分类（audioclient.h AudioClientProperties）。
/// 必须在 IAudioClient::Initialize 之前调用。cbSize 取 Marshal.SizeOf 的本结构非托管大小。</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient2_SetClientProperties(IntPtr self, ref AudioClientProperties pProperties);

/// <summary>IAudioClient2.SetClientProperties 的属性结构（audioclient.h AudioClientProperties）。
/// <para>官方布局（Win8.1+，16 字节，字段顺序不可变）：
/// <c>UINT32 cbSize; BOOL bIsOffload; AUDIO_STREAM_CATEGORY eCategory; AUDCLNT_STREAMOPTIONS Options;</c></para>
/// <para>🔴 2026-08-02 根因修正：此前本结构<b>漏掉了 bIsOffload（BOOL，偏移 4）</b>，导致原生按官方布局解析时
/// 整体错位一格——托管写入的 <c>eCategory=2</c> 被读作 <c>bIsOffload=TRUE</c>（<b>误申请硬件卸载流</b>），
/// 托管写入的 <c>eStreamOptions=0</c> 被读作 <c>eCategory=Other</c>（会话分类实际从未生效）。
/// 普通声卡不支持 offload，且 Win10 起 offload 流必须配合 <c>AUDCLNT_STREAMFLAGS_EVENTCALLBACK</c>，
/// 于是 SetClientProperties 在原生侧触发 0xC0000005。此前三轮「vtable 槽位 / QI 改 BCL / 默认关闭」
/// 均未触及该根因，故崩溃栈三次完全一致。</para>
/// <para>顺序布局，成员全部 blittable（UINT32 / int / int 枚举 / int），AOT 友好。
/// BOOL 用 <see cref="int"/> 表达（0 = FALSE），<b>不得改成 bool</b>——那会引入非 blittable 封送。</para></summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProperties
{
    /// <summary>本结构大小（Marshal.SizeOf = 16）。API 据此判断结构体版本。</summary>
    public uint cbSize;
    /// <summary>BOOL：音频流是否为硬件卸载（offload）流。<b>本库必须为 0（FALSE）</b>——走常规共享模式渲染；
    /// 置 TRUE 会切到 offload 路径，多数声卡不支持且要求事件驱动，导致原生崩溃。</summary>
    public int bIsOffload;
    /// <summary>会话分类（AudioClientCategory）。</summary>
    public AudioClientCategory eCategory;
    /// <summary>流式选项（AUDCLNT_STREAMOPTIONS）；0 = AUDCLNT_STREAMOPTIONS_NONE。Win8.1+ 支持。</summary>
    public int eStreamOptions;
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_Reset(IntPtr self);

/// <summary>
/// IAudioClient::SetEventHandle — 注册事件句柄（事件驱动模式）。
/// Initialize 时设置 AUDCLNT_STREAMFLAGS_EVENTCALLBACK 后必须调用此方法注册事件。
/// </summary>
/// <param name="self">COM 对象指针。</param>
/// <param name="eventHandle">事件句柄（由 CreateEvent 创建，AutoReset）。</param>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_SetEventHandle(IntPtr self, IntPtr eventHandle);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClient_GetService(IntPtr self, ref Guid riid, out IntPtr ppv);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioRenderClient_GetBuffer(IntPtr self, uint numFramesRequested, out IntPtr pData);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioRenderClient_ReleaseBuffer(IntPtr self, uint numFramesWritten, int dwFlags);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int ISimpleAudioVolume_SetMasterVolume(IntPtr self, float fLevel, ref Guid EventContext);

/// <summary>
/// IAudioClock::GetFrequency（audioclient.h:1366，IUnknown 之后相对槽 0）。
/// <para>
/// ⚠️ 审计修复（2026-07-31）：<see cref="IAudioClock_GetPosition"/> 返回的 pu64DevicePosition
/// 单位由设备定义，<b>不是</b>「已播放帧数」。官方换算是
/// <c>秒 = position / frequency</c>，两者单位必须成对使用。
/// 共享模式下 frequency 通常等于 <c>nSamplesPerSec * nBlockAlign</c>（即字节/秒），
/// 此时 position 是字节数——若误除以采样率，结果会偏大 nBlockAlign 倍（F32 立体声即 8 倍）。
/// </para>
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClock_GetFrequency(IntPtr self, out ulong pu64Frequency);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate int IAudioClock_GetPosition(IntPtr self, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

/// <summary>
/// 从 COM 接口指针读取第 (3 + slotIndex) 个 vtable 槽位的函数指针并转为强类型委托。
/// </summary>
internal static class ComVTable
{
    /// <summary>读取第 (3 + slotIndex) 个 vtable 槽位的原始函数指针（绝对槽位 = IUnknown 3 槽 + slotIndex）。</summary>
    public static IntPtr GetMethodPointer(IntPtr comPtr, int slotIndex)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comPtr);
        return Marshal.ReadIntPtr(vtable, (3 + slotIndex) * IntPtr.Size);
    }

    public static TDelegate Get<TDelegate>(IntPtr comPtr, int slotIndex) where TDelegate : Delegate
    {
        IntPtr methodPtr = GetMethodPointer(comPtr, slotIndex);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }
}
