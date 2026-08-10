namespace LingFan.Media.Backends.VLCNative.Interop;

/// <summary>
/// libvlc 3.0.23.1 原生 P/Invoke 绑定（Apache-2.0，零 LibVLCSharp）。
/// </summary>
/// <remarks>
/// <para>🔴 调用约定 = <see cref="CallingConvention.Cdecl"/>：libvlc 是纯 C 导出库，与本项目「COM vtable=Winapi」宪法不冲突（后者仅适用于 COM 接口如 MF）。</para>
/// <para>🔴 AOT：全部 <c>[LibraryImport]</c> static partial 方法，所在类型 partial；句柄用 <c>nint</c> 仿 ffmpeg 路径，不用 SafeHandle（AOT 友好）。</para>
/// <para>回调注册类函数（set_* / media_new_callbacks）的回调参数声明为 <c>nint</c> 函数指针；调用方用
/// <see cref="Marshal.GetFunctionPointerForDelegate"/> 转换后传入，避免源生成器对委托参数的封送歧义，最 AOT 安全。</para>
/// <para>字符串参数（location/path/option）显式 <c>StringMarshalling = Utf8</c>：libvlc 是 C 库，路径用 UTF-8。</para>
/// <para>3.x ABI 锁定：<c>libvlc_media_player_stop_async</c> 等 4.x 符号**不存在**，本绑定不声明。</para>
/// </remarks>
public static partial class LibVlcNative
{
    // ── 实例 / 版本 ──

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_new(int argc, IntPtr argv);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_release(nint p_instance);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_get_version();

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_errmsg();

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_free(IntPtr ptr);

    // ── Media ──

    [LibraryImport("libvlc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_new_location(nint p_instance, string psz_mrl);

    [LibraryImport("libvlc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_new_path(nint p_instance, string psz_path);

    // imem：open_cb/read_cb/seek_cb/close_cb/opaque 均为函数指针或 opaque（nint）
    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_new_callbacks(
        nint p_instance, nint open_cb, nint read_cb, nint seek_cb, nint close_cb, nint opaque);

    [LibraryImport("libvlc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_add_option(nint p_media, string psz_options);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_media_parse_with_options(nint p_media, uint option, int timeout);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_parse_stop(nint p_media);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_media_get_parsed_status(nint p_media);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long libvlc_media_get_duration(nint p_media);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_get_meta(nint p_media, uint meta_type);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint libvlc_media_tracks_get(nint p_media, out nint pp_tracks);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_tracks_release(nint pp_tracks, uint i_count);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_event_manager(nint p_media);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_release(nint p_media);

    // ── MediaPlayer ──

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_player_new(nint p_instance);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_release(nint p_mediaplayer);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_set_media(nint p_mediaplayer, nint p_media);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_media_player_play(nint p_mediaplayer);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_stop(nint p_mediaplayer);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_media_player_set_time(nint p_mediaplayer, long time);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long libvlc_media_player_get_time(nint p_mediaplayer);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint libvlc_media_player_event_manager(nint p_mediaplayer);

    // ── 回调注册（参数均为函数指针 nint）──

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_video_set_format_callbacks(nint mp, nint format_cb, nint cleanup_cb);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_video_set_callbacks(nint mp, nint lock_cb, nint unlock_cb, nint display_cb, nint opaque);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_audio_set_format_callbacks(nint mp, nint setup_cb, nint cleanup_cb);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_audio_set_callbacks(
        nint mp, nint play_cb, nint pause_cb, nint resume_cb, nint flush_cb, nint drain_cb, nint opaque);

    // ── 事件 ──

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int libvlc_event_attach(nint p_event_manager, int i_event_type, nint f_callback, nint user_data);

    [LibraryImport("libvlc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void libvlc_event_detach(nint p_event_manager, int i_event_type, nint f_callback, nint user_data);
}
