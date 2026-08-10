namespace LingFan.Media.Backends.VLCNative.Interop;

/// <summary>
/// libvlc 回调委托与结构体（Apache-2.0，零 LibVLCSharp）。
/// </summary>
/// <remarks>
/// <para>🔴 所有回调委托必须用 <see cref="UnmanagedFunctionPointerAttribute"/> + <see cref="CallingConvention.Cdecl"/>：
/// libvlc 是纯 C ABI，**不是 Winapi**（后者仅适用于 COM vtable 如 MF）。</para>
/// <para>🔴 §2.3 A/B/C 修正（根治 LibVLCSharp 三处 ABI 不符）：</para>
/// <list type="bullet">
/// <item>A 音频 <c>format</c> 原生是 <c>char*</c>（按值）→ 声明 <c>IntPtr</c>，<b>绝不可</b> <c>ref IntPtr</c>。</item>
/// <item>B 视频 <c>pitches</c>/<c>lines</c> 原生是数组（每平面一项）→ 声明 <c>IntPtr</c>，自行 <c>Marshal.ReadUInt32</c> 读出。</item>
/// <item>C 视频 <c>cleanup</c> 的 <c>opaque</c> 原生是 <c>void*</c>（按值）→ 声明 <c>IntPtr</c>。</item>
/// </list>
/// <para>🔴 结构体只读头部稳定字段，尾部（如 <c>i_multiview</c>）跨 libvlc 版本增减，不声明以免 Pack 错位。</para>
/// </remarks>
public static class LibVlcTypes
{
    // ── 视频回调 ──

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint VideoFormatCb(IntPtr opaque, IntPtr chroma, IntPtr width, IntPtr height, IntPtr pitches, IntPtr lines);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoCleanupCb(IntPtr opaque);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr VideoLockCb(IntPtr opaque, IntPtr planes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoUnlockCb(IntPtr opaque, IntPtr picture, IntPtr planes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoDisplayCb(IntPtr opaque, IntPtr picture);

    // ── 音频回调 ──

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AudioSetupCb(IntPtr opaque, IntPtr format, IntPtr rate, IntPtr channels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioCleanupCb(IntPtr opaque);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioPlayCb(IntPtr data, IntPtr samples, uint count, long pts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioPauseCb(IntPtr data, long pts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioResumeCb(IntPtr data, long pts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioFlushCb(IntPtr data, long pts);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioDrainCb(IntPtr data);

    // ── imem 自定义源回调 ──

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MediaOpenCb(IntPtr opaque, IntPtr datap, IntPtr sizep);

    // 🔴 read_cb 返回 ssize_t → C# 用 nint（非 long/intmax_t）
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint MediaReadCb(IntPtr opaque, IntPtr buf, nint len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MediaSeekCb(IntPtr opaque, ulong offset);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MediaCloseCb(IntPtr opaque);

    // ── 事件回调 ──

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCb(IntPtr args, IntPtr user_data);

    // ── 结构体（只读头部稳定字段）──

    [StructLayout(LayoutKind.Sequential)]
    public struct LibvlcMediaTrackT
    {
        public uint i_codec;
        public uint i_original_fourcc;
        public int i_id;
        public int i_type;        // libvlc_track_type_t
        public int i_profile;
        public int i_level;
        public uint i_bitrate;
        public IntPtr psz_language;
        public IntPtr psz_description;
        public IntPtr union_ptr;  // 联合体存指针（audio/video/subtitle）；🔴 必须位于结构体尾部，与 VLC 3.0 libvlc_media_track_t 一致
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibvlcAudioTrackT
    {
        public uint i_channels;
        public uint i_rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LibvlcVideoTrackT
    {
        public uint i_width;      // 🔴 VLC 顺序：先 width 后 height
        public uint i_height;
        public uint i_sar_num;
        public uint i_sar_den;
        public uint i_frame_rate_num;
        public uint i_frame_rate_den;
        // 尾部 i_orientation/i_projection/pose/i_multiview 跨版本增减 → 不声明
    }

    // ── 常量 ──

    // libvlc_track_type_t（🔴 VLC 3.0 真实枚举值：audio=0 / video=1 / text=2 / unknown=-1。
    // 此前误写为 1/2/3，导致回调式 VLC 后端把视频轨(t=1)误判为音频、音频轨(t=0)漏判，两轨 codec 全落 Unknown。）
    public const int TrackTypeUnknown = -1;
    public const int TrackTypeAudio = 0;
    public const int TrackTypeVideo = 1;
    public const int TrackTypeText = 2;

    // libvlc_media_parse_flag_t
    public const uint ParseLocal = 0x00;
    public const uint ParseNetwork = 0x01;
    public const uint FetchLocal = 0x02;
    public const uint FetchNetwork = 0x04;
    public const uint DoInteract = 0x08;

    // libvlc_media_parsed_status_t
    public const int ParsedStatusNone = 0;
    public const int ParsedStatusPending = 1;
    public const int ParsedStatusSkipped = 2;
    public const int ParsedStatusFailed = 3;
    public const int ParsedStatusDone = 4;
    public const int ParsedStatusTimeout = 5;

    // libvlc_meta_t（常用）
    public const uint MetaTitle = 0;
    public const uint MetaArtist = 1;
    public const uint MetaAlbum = 2;
    public const uint MetaGenre = 3;
    public const uint MetaDate = 4;

    // libvlc_event_e（MediaPlayer 段，3.x 值）
    public const int EventMediaPlayerOpening = 258;
    public const int EventMediaPlayerBuffering = 259;
    public const int EventMediaPlayerPlaying = 260;
    public const int EventMediaPlayerPaused = 261;
    public const int EventMediaPlayerStopped = 262;
    public const int EventMediaPlayerEndReached = 265;
    public const int EventMediaPlayerEncounteredError = 266;
    public const int EventMediaPlayerTimeChanged = 267;
    public const int EventMediaPlayerPositionChanged = 268;
    public const int EventMediaPlayerSeekableChanged = 269;
    public const int EventMediaPlayerLengthChanged = 272;
}
