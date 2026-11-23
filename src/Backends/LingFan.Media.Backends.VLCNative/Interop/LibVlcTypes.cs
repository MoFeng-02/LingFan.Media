namespace LingFan.Media.Backends.VLCNative.Interop;

/// <summary>
/// libvlc 回调委托与结构体（Apache-2.0）。
/// </summary>
/// <remarks>
/// <para>所有回调委托必须用 <see cref="UnmanagedFunctionPointerAttribute"/> + <see cref="CallingConvention.Cdecl"/>：
/// libvlc 是纯 C ABI，调用约定为 Cdecl，而非 COM 接口（如 MF）所用的 Winapi。</para>
/// <para>回调 ABI 要点（与 libvlc 原生声明对齐）：</para>
/// <list type="bullet">
/// <item>A 音频 <c>format</c> 原生是 <c>char*</c>（按值）→ 声明 <c>IntPtr</c>，<b>绝不可</b> <c>ref IntPtr</c>。</item>
/// <item>B 视频 <c>pitches</c>/<c>lines</c> 原生是数组（每平面一项）→ 声明 <c>IntPtr</c>，自行 <c>Marshal.ReadUInt32</c> 读出。</item>
/// <item>C 视频 <c>cleanup</c> 的 <c>opaque</c> 原生是 <c>void*</c>（按值）→ 声明 <c>IntPtr</c>。</item>
/// </list>
/// <para>结构体只读头部稳定字段，尾部（如 <c>i_multiview</c>）跨版本增减，不声明以免 Pack 错位。</para>
/// </remarks>
public static class LibVlcTypes
{
    // 视频回调

    /// <summary>视频格式协商回调（libvlc_video_format_cb）：写入 chroma/宽高/pitches/lines，返回需要的 picture 缓冲数。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    /// <param name="chroma">可写的 4 字节 chroma FourCC 缓冲。</param>
    /// <param name="width">宽度（uint*，可写）。</param>
    /// <param name="height">高度（uint*，可写）。</param>
    /// <param name="pitches">每平面行字节数数组（uint*，可写）。</param>
    /// <param name="lines">每平面行数数组（uint*，可写）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint VideoFormatCb(IntPtr opaque, IntPtr chroma, IntPtr width, IntPtr height, IntPtr pitches, IntPtr lines);

    /// <summary>视频格式清理回调（libvlc_video_cleanup_cb）：与 format 回调配对，释放协商期分配的资源。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoCleanupCb(IntPtr opaque);

    /// <summary>视频平面锁定回调（libvlc_video_lock_cb）：经 planes（void**）交出各平面指针，返回 picture 标识。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    /// <param name="planes">平面指针输出数组（void**）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr VideoLockCb(IntPtr opaque, IntPtr planes);

    /// <summary>视频平面解锁回调（libvlc_video_unlock_cb）：与 lock 配对，picture 为 lock 的返回值。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    /// <param name="picture">lock 回调返回的 picture 标识。</param>
    /// <param name="planes">lock 时交出的平面指针数组。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoUnlockCb(IntPtr opaque, IntPtr picture, IntPtr planes);

    /// <summary>视频帧呈现回调（libvlc_video_display_cb）：unlock 之后通知该 picture 可消费。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    /// <param name="picture">lock 回调返回的 picture 标识。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoDisplayCb(IntPtr opaque, IntPtr picture);

    // 音频回调

    /// <summary>音频格式协商回调（libvlc_audio_setup_cb）：format 为 char*（按值，见类型 remarks A），rate/channels 为 uint*；返回 0 接受。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    /// <param name="format">4 字节格式 FourCC（char*，按值传递，可写）。</param>
    /// <param name="rate">采样率（uint*，可写）。</param>
    /// <param name="channels">声道数（uint*，可写）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AudioSetupCb(IntPtr opaque, IntPtr format, IntPtr rate, IntPtr channels);

    /// <summary>音频格式清理回调（libvlc_audio_cleanup_cb）：与 setup 配对。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioCleanupCb(IntPtr opaque);

    /// <summary>音频样本投递回调（libvlc_audio_play_cb）：pts 为微秒。</summary>
    /// <param name="data">注册时传入的用户数据。</param>
    /// <param name="samples">样本缓冲（按 setup 协商的格式）。</param>
    /// <param name="count">样本帧数。</param>
    /// <param name="pts">呈现时间戳（微秒）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioPlayCb(IntPtr data, IntPtr samples, uint count, long pts);

    /// <summary>音频暂停回调（libvlc_audio_pause_cb）：pts 为暂停时刻（微秒）。</summary>
    /// <param name="data">注册时传入的用户数据。</param>
    /// <param name="pts">暂停时刻（微秒）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioPauseCb(IntPtr data, long pts);

    /// <summary>音频恢复回调（libvlc_audio_resume_cb）：pts 为恢复时刻（微秒）。</summary>
    /// <param name="data">注册时传入的用户数据。</param>
    /// <param name="pts">恢复时刻（微秒）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioResumeCb(IntPtr data, long pts);

    /// <summary>音频冲刷回调（libvlc_audio_flush_cb）：seek 等场景丢弃已缓冲样本，pts 为新基准（微秒）。</summary>
    /// <param name="data">注册时传入的用户数据。</param>
    /// <param name="pts">冲刷后的新基准时间戳（微秒）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioFlushCb(IntPtr data, long pts);

    /// <summary>音频排空回调（libvlc_audio_drain_cb）：通知播放完剩余缓冲样本。</summary>
    /// <param name="data">注册时传入的用户数据。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioDrainCb(IntPtr data);

    // imem 自定义源回调

    /// <summary>imem 打开回调（libvlc_media_open_cb）：经 datap 交出 opaque 上下文、sizep 交出总字节数；返回 0 成功。</summary>
    /// <param name="opaque">注册时传入的用户数据。</param>
    /// <param name="datap">上下文指针输出（void**，后续 read/seek/close 收到该值）。</param>
    /// <param name="sizep">媒体总大小输出（uint64*；未知可写 ulong.MaxValue）。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MediaOpenCb(IntPtr opaque, IntPtr datap, IntPtr sizep);

    /// <summary>imem 读取回调（libvlc_media_read_cb）：向 buf 填充至多 len 字节，返回实际读取数（0=EOF，-1=错误）。</summary>
    /// <param name="opaque">open 回调交出的上下文指针。</param>
    /// <param name="buf">目标缓冲。</param>
    /// <param name="len">请求字节数。</param>
    // read_cb 返回 ssize_t → C# 用 nint（非 long/intmax_t）
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint MediaReadCb(IntPtr opaque, IntPtr buf, nint len);

    /// <summary>imem 定位回调（libvlc_media_seek_cb）：移动到绝对偏移 offset；返回 0 成功，-1 失败。</summary>
    /// <param name="opaque">open 回调交出的上下文指针。</param>
    /// <param name="offset">目标绝对字节偏移。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MediaSeekCb(IntPtr opaque, ulong offset);

    /// <summary>imem 关闭回调（libvlc_media_close_cb）：与 open 配对，释放上下文。</summary>
    /// <param name="opaque">open 回调交出的上下文指针。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MediaCloseCb(IntPtr opaque);

    // 事件回调

    /// <summary>libvlc 事件回调（libvlc_callback_t）：在 libvlc 内部线程触发，处理须快进快出、不得阻塞。</summary>
    /// <param name="args">libvlc_event_t* 事件结构指针（头部为 type 字段）。</param>
    /// <param name="user_data">注册时传入的用户数据。</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCb(IntPtr args, IntPtr user_data);

    // 结构体（只读头部稳定字段）

    /// <summary>libvlc_media_track_t 头部稳定字段镜像（尾部联合体以指针承载，见 union_ptr）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LibvlcMediaTrackT
    {
        /// <summary>编解码器 FourCC。</summary>
        public uint i_codec;
        /// <summary>原始 FourCC（容器层面的编码标识）。</summary>
        public uint i_original_fourcc;
        /// <summary>轨道 id（用于 libvlc_media_player 选轨）。</summary>
        public int i_id;
        /// <summary>轨道类型（libvlc_track_type_t，见 TrackType* 常量）。</summary>
        public int i_type;        // libvlc_track_type_t
        /// <summary>编码 profile（codec 相关）。</summary>
        public int i_profile;
        /// <summary>编码 level（codec 相关）。</summary>
        public int i_level;
        /// <summary>码率（bps；0=未知）。</summary>
        public uint i_bitrate;
        /// <summary>语言代码（char*，可为 Null）。</summary>
        public IntPtr psz_language;
        /// <summary>轨道描述（char*，可为 Null）。</summary>
        public IntPtr psz_description;
        /// <summary>轨道类型专属联合体指针（audio/video/subtitle）。</summary>
        public IntPtr union_ptr;  // 联合体存指针（audio/video/subtitle）；必须位于结构体尾部，与 libvlc_media_track_t 布局一致
    }

    /// <summary>libvlc_audio_track_t 头部稳定字段镜像。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LibvlcAudioTrackT
    {
        /// <summary>声道数。</summary>
        public uint i_channels;
        /// <summary>采样率（Hz）。</summary>
        public uint i_rate;
    }

    /// <summary>libvlc_video_track_t 头部稳定字段镜像（尾部跨版本字段不声明，见类型 remarks）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LibvlcVideoTrackT
    {
        /// <summary>视频宽度（像素）。</summary>
        public uint i_width;      // VLC 字段顺序：先 width 后 height
        /// <summary>视频高度（像素）。</summary>
        public uint i_height;
        /// <summary>样本宽高比分子（SAR）。</summary>
        public uint i_sar_num;
        /// <summary>样本宽高比分母（SAR）。</summary>
        public uint i_sar_den;
        /// <summary>帧率分子。</summary>
        public uint i_frame_rate_num;
        /// <summary>帧率分母。</summary>
        public uint i_frame_rate_den;
        // 尾部 i_orientation/i_projection/pose/i_multiview 跨版本增减 → 不声明
    }

    // 常量

    // libvlc_track_type_t：audio=0 / video=1 / text=2 / unknown=-1（回调式后端据此区分轨道）。
    /// <summary>轨道类型：未知（libvlc_track_type_t）。</summary>
    public const int TrackTypeUnknown = -1;
    /// <summary>轨道类型：音频（libvlc_track_type_t）。</summary>
    public const int TrackTypeAudio = 0;
    /// <summary>轨道类型：视频（libvlc_track_type_t）。</summary>
    public const int TrackTypeVideo = 1;
    /// <summary>轨道类型：文本/字幕（libvlc_track_type_t）。</summary>
    public const int TrackTypeText = 2;

    // libvlc_media_parse_flag_t
    /// <summary>解析标志：本地解析（libvlc_media_parse_flag_t）。</summary>
    public const uint ParseLocal = 0x00;
    /// <summary>解析标志：允许网络访问（libvlc_media_parse_flag_t）。</summary>
    public const uint ParseNetwork = 0x01;
    /// <summary>解析标志：抓取本地元数据/封面（libvlc_media_parse_flag_t）。</summary>
    public const uint FetchLocal = 0x02;
    /// <summary>解析标志：抓取网络元数据/封面（libvlc_media_parse_flag_t）。</summary>
    public const uint FetchNetwork = 0x04;
    /// <summary>解析标志：允许交互（登录/对话框等，libvlc_media_parse_flag_t）。</summary>
    public const uint DoInteract = 0x08;

    // libvlc_media_parsed_status_t
    /// <summary>解析状态：未解析（libvlc_media_parsed_status_t）。</summary>
    public const int ParsedStatusNone = 0;
    /// <summary>解析状态：排队/进行中（libvlc_media_parsed_status_t）。</summary>
    public const int ParsedStatusPending = 1;
    /// <summary>解析状态：被跳过（libvlc_media_parsed_status_t）。</summary>
    public const int ParsedStatusSkipped = 2;
    /// <summary>解析状态：失败（libvlc_media_parsed_status_t）。</summary>
    public const int ParsedStatusFailed = 3;
    /// <summary>解析状态：完成（libvlc_media_parsed_status_t）。</summary>
    public const int ParsedStatusDone = 4;
    /// <summary>解析状态：超时（libvlc_media_parsed_status_t）。</summary>
    public const int ParsedStatusTimeout = 5;

    // libvlc_meta_t（常用）
    /// <summary>元数据键：标题（libvlc_meta_t）。</summary>
    public const uint MetaTitle = 0;
    /// <summary>元数据键：艺术家（libvlc_meta_t）。</summary>
    public const uint MetaArtist = 1;
    /// <summary>元数据键：专辑（libvlc_meta_t）。</summary>
    public const uint MetaAlbum = 2;
    /// <summary>元数据键：流派（libvlc_meta_t）。</summary>
    public const uint MetaGenre = 3;
    /// <summary>元数据键：日期（libvlc_meta_t）。</summary>
    public const uint MetaDate = 4;

    // libvlc_event_e（MediaPlayer 段取值）
    /// <summary>事件：媒体打开中（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerOpening = 258;
    /// <summary>事件：缓冲中（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerBuffering = 259;
    /// <summary>事件：开始播放（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerPlaying = 260;
    /// <summary>事件：已暂停（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerPaused = 261;
    /// <summary>事件：已停止（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerStopped = 262;
    /// <summary>事件：到达媒体末尾（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerEndReached = 265;
    /// <summary>事件：遇到错误（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerEncounteredError = 266;
    /// <summary>事件：播放时间变化（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerTimeChanged = 267;
    /// <summary>事件：播放位置变化（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerPositionChanged = 268;
    /// <summary>事件：可 seek 性变化（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerSeekableChanged = 269;
    /// <summary>事件：媒体时长变化（libvlc_event_e，MediaPlayer 段）。</summary>
    public const int EventMediaPlayerLengthChanged = 272;
}
