using System.Runtime.InteropServices;

namespace LingFan.Media.Backends.MediaCodec.Interop;

/// <summary>
/// NDK Media C API 的 P/Invoke 绑定（<c>libmediandk</c>）。
/// </summary>
/// <remarks>
/// <para><b>AOT 合规</b>：全部走 <c>[LibraryImport]</c>（源生成确定性封送，NativeAOT 唯一合规静态 P/Invoke），
/// 绝不使用 <c>[DllImport]</c>。C# 方法名与 NDK 导出符号逐字一致，故省略 <c>EntryPoint</c>；
/// 任何改名（含加 <c>Raw</c>/<c>Ex</c> 后缀）都必须补显式 <c>EntryPoint</c>，否则运行时抛
/// <c>EntryPointNotFoundException</c>。本类型须为 <c>partial</c>（SYSLIB1050）。</para>
///
/// <para><b>C 类型映射铁律</b>（按 NDK 头逐条核准，非按感觉）：</para>
/// <list type="bullet">
/// <item><c>size_t</c> → <see cref="nuint"/>；<c>ssize_t</c> → <see cref="nint"/>（必须保留负值语义：
/// -1 既是错误也是 <c>TRY_AGAIN_LATER</c>，映射成 <c>nuint</c> 会让失败分支永不命中）。</item>
/// <item><c>off_t</c> → <see cref="nint"/>（Android 上 <c>off_t</c> 为本机 long：LP64 为 8 字节、
/// ILP32 为 4 字节，与指针同宽）。写死 <see cref="long"/> 会在 32 位 ABI 上造成实参整体错位。</item>
/// <item><c>off64_t</c>/<c>int64_t</c> → <see cref="long"/>（显式 64 位，与位宽无关）。</item>
/// <item>C <c>bool</c> → <see cref="byte"/>（1 字节；源生成封送要求 <c>bool</c> 必须显式声明封送方式，
/// 用 <c>byte</c> 直通更确定，调用方以 <c>!= 0</c> 判定）。</item>
/// </list>
///
/// <para><b>字符串所有权铁律</b>：NDK 的 <c>AMediaFormat_getString</c>/<c>AMediaFormat_toString</c>
/// 返回的 <c>const char*</c> <b>由 AMediaFormat 拥有</b>（下次调用或 delete 后失效）。
/// 因此出参一律声明为 <see cref="nint"/> 并在包装层用 <c>Marshal.PtrToStringUTF8</c> 拷贝——
/// 若声明为 <c>out string</c>，源生成封送会在返回后对该指针执行 free，等于释放原生库自有内存 ⇒ 堆破坏。
/// 入参方向的字符串（name/value）安全：NDK 内部会自行拷贝。</para>
///
/// <para><b>回调铁律</b>：<c>AMediaDataSource_set*</c> 接收 C 函数指针。源生成 P/Invoke
/// <b>不支持委托封送</b>，且托管委托还带生命周期/反向 stub 问题；故一律声明为
/// <c>delegate* unmanaged[Cdecl]</c>，实现侧配 <c>[UnmanagedCallersOnly]</c> 静态方法。</para>
///
/// <para><b>API 级别</b>：<c>AMediaExtractor_setDataSource</c>（URL/路径）自 API 21 起可用；
/// <c>AMediaDataSource_*</c> 与 <c>AMediaExtractor_setDataSourceCustom</c> 自 <b>API 28</b> 起可用。
/// 低于 28 的设备上后者的符号不存在，首次调用抛 <c>EntryPointNotFoundException</c>，
/// 由包装层捕获后降级/报错，不得让异常穿出。</para>
/// </remarks>
internal static unsafe partial class MediaNdk
{
    private const string Library = "mediandk";

    // ============================================================
    // AMediaExtractor（media/NdkMediaExtractor.h）
    // ============================================================

    /// <summary>创建解封装器；失败返回 <see cref="nint.Zero"/>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaExtractor_new();

    /// <summary>销毁解封装器（media_status_t）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaExtractor_delete(nint extractor);

    /// <summary>设置数据源为 URL 或本地文件路径（API 21+）。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AMediaExtractor_setDataSource(nint extractor, string location);

    /// <summary>设置数据源为自定义 <c>AMediaDataSource</c>（<b>API 28+</b>）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaExtractor_setDataSourceCustom(nint extractor, nint dataSource);

    /// <summary>轨道数（<c>size_t</c>）。</summary>
    [LibraryImport(Library)]
    public static partial nuint AMediaExtractor_getTrackCount(nint extractor);

    /// <summary>取轨道格式；<b>调用方须以 <c>AMediaFormat_delete</c> 释放</b>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaExtractor_getTrackFormat(nint extractor, nuint idx);

    /// <summary>取容器级格式；<b>调用方须以 <c>AMediaFormat_delete</c> 释放</b>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaExtractor_getFileFormat(nint extractor);

    /// <summary>选中轨道；仅被选中轨道参与后续 readSampleData（media_status_t）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaExtractor_selectTrack(nint extractor, nuint idx);

    /// <summary>取消选中轨道（media_status_t）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaExtractor_unselectTrack(nint extractor, nuint idx);

    /// <summary>
    /// 读取当前采样到 <paramref name="buffer"/>（容量 <paramref name="capacity"/> 字节）。
    /// 返回写入字节数；<c>&lt; 0</c> 表示无更多采样（流结束）。
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint AMediaExtractor_readSampleData(nint extractor, nint buffer, nuint capacity);

    /// <summary>当前采样标志；无更多采样（流尾）时按 AOSP 实现返回 <c>0xFFFFFFFF</c>（即 -1）。
    /// 该值与 SAMPLE_FLAG_* 重叠且不可靠（任何错误都返回 -1），判 EOF 请以
    /// <c>AMediaExtractor_getSampleTrackIndex</c> &lt; 0 或 <c>readSampleData</c> &lt; 0 为准。</summary>
    [LibraryImport(Library)]
    public static partial uint AMediaExtractor_getSampleFlags(nint extractor);

    /// <summary>当前采样所属轨道索引；无更多采样返回 -1。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaExtractor_getSampleTrackIndex(nint extractor);

    /// <summary>当前采样 PTS（微秒）；无更多采样返回 -1。</summary>
    [LibraryImport(Library)]
    public static partial long AMediaExtractor_getSampleTime(nint extractor);

    /// <summary>当前采样字节数（<c>ssize_t</c>，<b>API 28+</b>）；无更多采样返回 -1。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaExtractor_getSampleSize(nint extractor);

    /// <summary>前进到下一采样；返回 0 表示已到流末尾。</summary>
    [LibraryImport(Library)]
    public static partial byte AMediaExtractor_advance(nint extractor);

    /// <summary>定位（media_status_t）；<paramref name="mode"/> 取 AMEDIAEXTRACTOR_SEEK_* 之一。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaExtractor_seekTo(nint extractor, long seekPosUs, int mode);

    // ============================================================
    // AMediaFormat（media/NdkMediaFormat.h）
    // ============================================================

    /// <summary>创建空格式；失败返回 <see cref="nint.Zero"/>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaFormat_new();

    /// <summary>销毁格式（media_status_t）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaFormat_delete(nint format);

    /// <summary>读取 int32 键；返回 0 表示键不存在。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte AMediaFormat_getInt32(nint format, string name, out int outValue);

    /// <summary>读取 int64 键；返回 0 表示键不存在。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte AMediaFormat_getInt64(nint format, string name, out long outValue);

    /// <summary>
    /// 读取字符串键；返回 0 表示键不存在。
    /// <paramref name="outValue"/> 指向 <b>format 拥有</b> 的 UTF-8 缓冲，须立即拷贝且绝不可释放。
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte AMediaFormat_getString(nint format, string name, out nint outValue);

    /// <summary>
    /// 读取二进制键（如 csd-0）；返回 0 表示键不存在。
    /// <paramref name="data"/> 指向 <b>format 拥有</b> 的缓冲，须立即拷贝且绝不可释放。
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte AMediaFormat_getBuffer(nint format, string name, out nint data, out nuint size);

    /// <summary>写入字符串键（NDK 内部拷贝，入参缓冲可立即回收）。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void AMediaFormat_setString(nint format, string name, string value);

    /// <summary>写入 int32 键。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void AMediaFormat_setInt32(nint format, string name, int value);

    /// <summary>写入 int64 键。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void AMediaFormat_setInt64(nint format, string name, long value);

    /// <summary>写入二进制键（NDK 内部拷贝，入参缓冲可立即回收）。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void AMediaFormat_setBuffer(nint format, string name, nint data, nuint size);

    /// <summary>
    /// 格式的可读表示；返回 <b>format 拥有</b> 的 UTF-8 指针（诊断用，须立即拷贝，绝不可释放）。
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint AMediaFormat_toString(nint format);

    // ============================================================
    // AMediaCodec（media/NdkMediaCodec.h）
    // ============================================================

    /// <summary>按 MIME 创建解码器；失败返回 <see cref="nint.Zero"/>。</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint AMediaCodec_createDecoderByType(string mimeType);

    /// <summary>销毁解码器（media_status_t）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaCodec_delete(nint codec);

    /// <summary>
    /// 配置解码器（media_status_t）。<paramref name="surface"/> 为 <see cref="nint.Zero"/> 时走
    /// ByteBuffer（CPU 输出）路径；<paramref name="crypto"/> 恒传 Zero（不支持 DRM）；
    /// <paramref name="flags"/> 解码器恒传 0。
    /// </summary>
    [LibraryImport(Library)]
    public static partial int AMediaCodec_configure(nint codec, nint format, nint surface, nint crypto, uint flags);

    /// <summary>启动解码器（media_status_t）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaCodec_start(nint codec);

    /// <summary>停止解码器（media_status_t）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaCodec_stop(nint codec);

    /// <summary>丢弃全部在途输入/输出（media_status_t）。seek 后必须调用。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaCodec_flush(nint codec);

    /// <summary>取输入 buffer 指针；失败返回 <see cref="nint.Zero"/>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaCodec_getInputBuffer(nint codec, nuint idx, out nuint outSize);

    /// <summary>取输出 buffer 指针；失败返回 <see cref="nint.Zero"/>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaCodec_getOutputBuffer(nint codec, nuint idx, out nuint outSize);

    /// <summary>
    /// 申领输入 buffer 索引（<c>ssize_t</c>）；返回
    /// <see cref="AndroidMediaConstants.AMEDIACODEC_INFO_TRY_AGAIN_LATER"/> 表示暂无可用。
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint AMediaCodec_dequeueInputBuffer(nint codec, long timeoutUs);

    /// <summary>
    /// 提交输入 buffer（media_status_t）。<paramref name="offset"/> 为 <c>off_t</c>（映射 <see cref="nint"/>），
    /// <paramref name="time"/> 为 PTS 微秒（<c>uint64_t</c>）。
    /// </summary>
    [LibraryImport(Library)]
    public static partial int AMediaCodec_queueInputBuffer(nint codec, nuint idx, nint offset, nuint size,
        ulong time, uint flags);

    /// <summary>
    /// 申领输出 buffer 索引（<c>ssize_t</c>）；负值取 AMEDIACODEC_INFO_* 语义
    /// （TRY_AGAIN_LATER / OUTPUT_FORMAT_CHANGED / OUTPUT_BUFFERS_CHANGED）。
    /// </summary>
    [LibraryImport(Library)]
    public static partial nint AMediaCodec_dequeueOutputBuffer(nint codec, out AMediaCodecBufferInfo info,
        long timeoutUs);

    /// <summary>归还输出 buffer（media_status_t）；<paramref name="render"/> 非 0 时上屏（仅 Surface 模式有效）。</summary>
    [LibraryImport(Library)]
    public static partial int AMediaCodec_releaseOutputBuffer(nint codec, nuint idx, byte render);

    /// <summary>取输出格式；<b>调用方须以 <c>AMediaFormat_delete</c> 释放</b>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaCodec_getOutputFormat(nint codec);

    // ============================================================
    // AMediaDataSource（API 28+，桥接 IMediaStream）
    // ============================================================

    /// <summary>创建自定义数据源；失败返回 <see cref="nint.Zero"/>。</summary>
    [LibraryImport(Library)]
    public static partial nint AMediaDataSource_new();

    /// <summary>销毁自定义数据源。注意：<c>_delete</c> 不会调用 close 回调，两者语义独立。</summary>
    [LibraryImport(Library)]
    public static partial void AMediaDataSource_delete(nint dataSource);

    /// <summary>设置回调首参透传的不透明句柄。</summary>
    [LibraryImport(Library)]
    public static partial void AMediaDataSource_setUserdata(nint dataSource, nint userdata);

    /// <summary>设置 readAt 回调：<c>ssize_t (void* userdata, off64_t offset, void* buffer, size_t size)</c>。</summary>
    [LibraryImport(Library)]
    public static partial void AMediaDataSource_setReadAt(nint dataSource,
        delegate* unmanaged[Cdecl]<nint, long, nint, nuint, nint> callback);

    /// <summary>设置 getSize 回调：<c>ssize_t (void* userdata)</c>，未知大小返回 -1。</summary>
    [LibraryImport(Library)]
    public static partial void AMediaDataSource_setGetSize(nint dataSource,
        delegate* unmanaged[Cdecl]<nint, nint> callback);

    /// <summary>设置 close 回调：<c>void (void* userdata)</c>。</summary>
    [LibraryImport(Library)]
    public static partial void AMediaDataSource_setClose(nint dataSource,
        delegate* unmanaged[Cdecl]<nint, void> callback);

    /// <summary>
    /// 触发 close 回调以解除阻塞中的读取。
    /// AOSP 实现为 <c>mSource-&gt;close(mSource-&gt;userdata)</c>——它<b>只转发回调、不释放对象</b>，
    /// 故仍须另行调用 <c>AMediaDataSource_delete</c>。
    /// </summary>
    [LibraryImport(Library)]
    public static partial void AMediaDataSource_close(nint dataSource);
}

/// <summary>
/// <c>AMediaCodec</c> 输出 buffer 元数据（media/NdkMediaCodec.h）。
/// </summary>
/// <remarks>
/// 布局与 C 结构逐字段对应：<c>int32_t offset; int32_t size; int64_t presentationTimeUs; uint32_t flags;</c>。
/// 字段名保持 C 原名（小驼峰）以便与头文件对读，勿改。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct AMediaCodecBufferInfo
{
    /// <summary>数据在输出 buffer 内的起始偏移（字节）。</summary>
    public int offset;

    /// <summary>有效数据长度（字节）。</summary>
    public int size;

    /// <summary>呈现时间戳（微秒）。</summary>
    public long presentationTimeUs;

    /// <summary>AMEDIACODEC_BUFFER_FLAG_* 组合。</summary>
    public uint flags;
}
