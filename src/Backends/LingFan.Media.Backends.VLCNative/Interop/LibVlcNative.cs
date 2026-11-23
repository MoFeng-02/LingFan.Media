namespace LingFan.Media.Backends.VLCNative.Interop;

/// <summary>
/// libvlc 原生 P/Invoke 绑定（Apache-2.0，不依赖第三方托管绑定）。
/// </summary>
/// <remarks>
/// <para>调用约定 = <see cref="CallingConvention.Cdecl"/>：libvlc 是纯 C 导出库，与 COM 接口（如 MF）所需的 Winapi 调用约定不同。</para>
/// <para>AOT：全部 <c>[LibraryImport]</c> static partial 方法，所在类型 partial；句柄用 <c>nint</c> 仿 ffmpeg 路径，不用 SafeHandle（AOT 友好）。</para>
/// <para>回调注册类函数（set_* / media_new_callbacks）的回调参数声明为 <c>nint</c> 函数指针；调用方用
/// <see cref="Marshal.GetFunctionPointerForDelegate"/> 转换后传入，避免源生成器对委托参数的封送歧义，最 AOT 安全。</para>
/// <para>字符串参数（location/path/option）显式 <c>StringMarshalling = Utf8</c>：libvlc 是 C 库，路径用 UTF-8。</para>
/// <para>本绑定仅声明 libvlc 既有同步符号；较新版本才引入的异步符号（如 <c>libvlc_media_player_stop_async</c>）未声明，以兼容既有的 libvlc 运行时。</para>
/// </remarks>
public static partial class LibVlcNative
{
    // 实例 / 版本

    /// <summary>创建 libvlc 实例（引用计数 +1，须配对 <see cref="libvlc_release"/>）；失败返回 Zero。</summary>
    /// <param name="argc">启动参数个数。</param>
    /// <param name="argv">启动参数数组（char**，UTF-8；无参传 Zero）。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_new(int argc, IntPtr argv);

    /// <summary>释放 libvlc 实例（引用计数 -1，归零即销毁；此后该实例句柄不可再用）。</summary>
    /// <param name="p_instance">libvlc 实例句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_release(nint p_instance);

    /// <summary>取得 libvlc 版本字符串（静态常量 char*，只读，勿释放）。</summary>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_get_version();

    /// <summary>取得当前线程最近一次 libvlc 错误消息（char*，可能为 Zero；只读，勿释放）。</summary>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_errmsg();

    /// <summary>释放 libvlc 分配的内存（如 <see cref="libvlc_media_get_meta"/> 返回的字符串）。</summary>
    /// <param name="ptr">libvlc 分配的指针。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_free(IntPtr ptr);

    // Media

    /// <summary>按 MRL 创建媒体（如 file:///… 或自定义 scheme；+1，须配对 <see cref="libvlc_media_release"/>）。</summary>
    /// <param name="p_instance">libvlc 实例句柄。</param>
    /// <param name="psz_mrl">媒体 MRL（UTF-8）。</param>
    [LibraryImport("libvlc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_new_location(nint p_instance, string psz_mrl);

    /// <summary>按文件系统路径创建媒体（+1，须配对 release；路径含特殊字符时建议改用 MRL）。</summary>
    /// <param name="p_instance">libvlc 实例句柄。</param>
    /// <param name="psz_path">本地文件路径（UTF-8）。</param>
    [LibraryImport("libvlc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_new_path(nint p_instance, string psz_path);

    /// <summary>以 imem 自定义源回调创建媒体（+1，须配对 release）。回调参数为函数指针（见类型 remarks）。</summary>
    /// <param name="p_instance">libvlc 实例句柄。</param>
    /// <param name="open_cb">MediaOpenCb 函数指针。</param>
    /// <param name="read_cb">MediaReadCb 函数指针。</param>
    /// <param name="seek_cb">MediaSeekCb 函数指针（可为 Zero 表示不可 seek）。</param>
    /// <param name="close_cb">MediaCloseCb 函数指针。</param>
    /// <param name="opaque">传给各回调的用户数据。</param>
    // imem：open_cb/read_cb/seek_cb/close_cb/opaque 均为函数指针或 opaque（nint）
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_new_callbacks(
        nint p_instance, nint open_cb, nint read_cb, nint seek_cb, nint close_cb, nint opaque);

    /// <summary>为媒体追加选项（如 ":file-caching=300"；须在解析/播放前设置）。</summary>
    /// <param name="p_media">媒体句柄。</param>
    /// <param name="psz_options">选项字符串（UTF-8）。</param>
    [LibraryImport("libvlc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_add_option(nint p_media, string psz_options);

    /// <summary>按标志异步解析媒体（元数据/轨道）；返回 0 已受理、-1 失败。结果经 parsed_status/事件观察。</summary>
    /// <param name="p_media">媒体句柄。</param>
    /// <param name="option">解析标志（Parse* 常量组合）。</param>
    /// <param name="timeout">超时毫秒（-1=不限）。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_media_parse_with_options(nint p_media, uint option, int timeout);

    /// <summary>停止进行中的异步解析。</summary>
    /// <param name="p_media">媒体句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_parse_stop(nint p_media);

    /// <summary>取得解析状态（ParsedStatus* 常量）。</summary>
    /// <param name="p_media">媒体句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_media_get_parsed_status(nint p_media);

    /// <summary>取得媒体时长（毫秒；未解析/未知返回 -1）。</summary>
    /// <param name="p_media">媒体句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long libvlc_media_get_duration(nint p_media);

    /// <summary>取得元数据（Meta* 键；返回 libvlc 分配的 char*，须 <see cref="libvlc_free"/> 释放；可能为 Zero）。</summary>
    /// <param name="p_media">媒体句柄。</param>
    /// <param name="meta_type">元数据键（Meta* 常量）。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_get_meta(nint p_media, uint meta_type);

    /// <summary>取得轨道数组：返回轨道数，pp_tracks 为 libvlc_media_track_t* 数组（须 <see cref="libvlc_media_tracks_release"/> 释放）。</summary>
    /// <param name="p_media">媒体句柄（须已解析）。</param>
    /// <param name="pp_tracks">输出轨道指针数组。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint libvlc_media_tracks_get(nint p_media, out nint pp_tracks);

    /// <summary>释放 <see cref="libvlc_media_tracks_get"/> 取得的轨道数组。</summary>
    /// <param name="pp_tracks">轨道指针数组。</param>
    /// <param name="i_count">tracks_get 返回的轨道数。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_tracks_release(nint pp_tracks, uint i_count);

    /// <summary>取得媒体的事件管理器（借入句柄，随媒体存活；勿释放）。</summary>
    /// <param name="p_media">媒体句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_event_manager(nint p_media);

    /// <summary>释放媒体（引用计数 -1；播放器 set_media 会自行持有，释放本地引用不影响播放）。</summary>
    /// <param name="p_media">媒体句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_release(nint p_media);

    // MediaPlayer

    /// <summary>创建媒体播放器（+1，须配对 <see cref="libvlc_media_player_release"/>）。</summary>
    /// <param name="p_instance">libvlc 实例句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_player_new(nint p_instance);

    /// <summary>释放媒体播放器（引用计数 -1；释放前应先 stop，避免回调线程仍在投递）。</summary>
    /// <param name="p_mediaplayer">播放器句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_release(nint p_mediaplayer);

    /// <summary>为播放器设置媒体（播放器内部自行 retain，调用方仍持有自己的媒体引用）。</summary>
    /// <param name="p_mediaplayer">播放器句柄。</param>
    /// <param name="p_media">媒体句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_set_media(nint p_mediaplayer, nint p_media);

    /// <summary>开始播放（异步启动；返回 0 已受理、-1 失败。实际播放状态经事件观察）。</summary>
    /// <param name="p_mediaplayer">播放器句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_media_player_play(nint p_mediaplayer);

    /// <summary>停止播放（同步阻塞直至内部线程收敛；勿在 libvlc 回调线程内调用，会死锁）。</summary>
    /// <param name="p_mediaplayer">播放器句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_stop(nint p_mediaplayer);

    /// <summary>跳转到指定播放时间（毫秒；异步生效，结果经 TimeChanged/事件观察）。</summary>
    /// <param name="p_mediaplayer">播放器句柄。</param>
    /// <param name="time">目标时间（毫秒）。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_set_time(nint p_mediaplayer, long time);

    /// <summary>取得当前播放时间（毫秒；不可用返回 -1）。</summary>
    /// <param name="p_mediaplayer">播放器句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long libvlc_media_player_get_time(nint p_mediaplayer);

    /// <summary>取得播放器的事件管理器（借入句柄，随播放器存活；勿释放）。</summary>
    /// <param name="p_mediaplayer">播放器句柄。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_player_event_manager(nint p_mediaplayer);

    // 回调注册（参数均为函数指针 nint）

    /// <summary>注册视频格式协商回调（format/cleanup；须先于播放设置，委托须存字段防 GC）。</summary>
    /// <param name="mp">播放器句柄。</param>
    /// <param name="format_cb">VideoFormatCb 函数指针。</param>
    /// <param name="cleanup_cb">VideoCleanupCb 函数指针。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_video_set_format_callbacks(nint mp, nint format_cb, nint cleanup_cb);

    /// <summary>注册视频帧回调（lock/unlock/display + opaque；在 libvlc 解码线程触发，处理须快进快出）。</summary>
    /// <param name="mp">播放器句柄。</param>
    /// <param name="lock_cb">VideoLockCb 函数指针。</param>
    /// <param name="unlock_cb">VideoUnlockCb 函数指针。</param>
    /// <param name="display_cb">VideoDisplayCb 函数指针。</param>
    /// <param name="opaque">传给各回调的用户数据。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_video_set_callbacks(nint mp, nint lock_cb, nint unlock_cb, nint display_cb, nint opaque);

    /// <summary>注册音频格式协商回调（setup/cleanup；须先于播放设置）。</summary>
    /// <param name="mp">播放器句柄。</param>
    /// <param name="setup_cb">AudioSetupCb 函数指针。</param>
    /// <param name="cleanup_cb">AudioCleanupCb 函数指针。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_audio_set_format_callbacks(nint mp, nint setup_cb, nint cleanup_cb);

    /// <summary>注册音频播放回调（play/pause/resume/flush/drain + opaque；在 libvlc 音频线程触发）。</summary>
    /// <param name="mp">播放器句柄。</param>
    /// <param name="play_cb">AudioPlayCb 函数指针。</param>
    /// <param name="pause_cb">AudioPauseCb 函数指针。</param>
    /// <param name="resume_cb">AudioResumeCb 函数指针。</param>
    /// <param name="flush_cb">AudioFlushCb 函数指针。</param>
    /// <param name="drain_cb">AudioDrainCb 函数指针。</param>
    /// <param name="opaque">传给各回调的用户数据。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_audio_set_callbacks(
        nint mp, nint play_cb, nint pause_cb, nint resume_cb, nint flush_cb, nint drain_cb, nint opaque);

    // 事件

    /// <summary>订阅事件（返回 0 成功；回调在 libvlc 内部线程触发，须存委托防 GC）。</summary>
    /// <param name="p_event_manager">事件管理器句柄。</param>
    /// <param name="i_event_type">事件类型（EventMediaPlayer* 等常量）。</param>
    /// <param name="f_callback">EventCb 函数指针。</param>
    /// <param name="user_data">传给回调的用户数据。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_event_attach(nint p_event_manager, int i_event_type, nint f_callback, nint user_data);

    /// <summary>退订事件（参数须与 attach 时完全一致）。</summary>
    /// <param name="p_event_manager">事件管理器句柄。</param>
    /// <param name="i_event_type">事件类型。</param>
    /// <param name="f_callback">attach 时的同一函数指针。</param>
    /// <param name="user_data">attach 时的同一用户数据。</param>
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_event_detach(nint p_event_manager, int i_event_type, nint f_callback, nint user_data);
}
