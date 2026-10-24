namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体数据流接口。
/// </summary>
/// <remarks>
/// <para>Read 和 ReadAsync 均采用 caller-allocated buffer 模式。不接受 null buffer。
/// buffer 大小为 0 时返回 0。</para>
/// <para>优先使用 <see cref="ReadAsync"/>（支持 CancellationToken，不阻塞线程）。
/// 同步 <see cref="Read"/> 仅用于同步边界（如 FormatDetector 探测、FFmpeg AVIO 回调）。</para>
/// <para>网络流必须在 <see cref="Read"/> 之前调用 <see cref="ConnectAsync"/> 建立底层连接——
/// 同步边界（C 函数指针/同步探测）无法 await，故建连必须前置到异步路径。</para>
/// </remarks>
public interface IMediaStream
{

    /// <summary>
    /// 同步读取数据到指定 buffer。
    /// </summary>
    /// <param name="buffer">目标 buffer（Span 切片）。长度为 0 时返回 0。</param>
    /// <returns>实际读取的字节数，0 表示流结束。</returns>
    /// <exception cref="IOException">读取发生 I/O 错误。</exception>
    /// <exception cref="InvalidOperationException">网络流未先调用 <see cref="ConnectAsync"/> 建立连接。</exception>
    /// <remarks>
    /// 同步边界（C 函数指针 / 同步探测），无法 await。
    /// 网络流必须在 <see cref="ConnectAsync"/> 之后调用，否则抛 <see cref="InvalidOperationException"/>。
    /// 已连接后每帧同步读取会阻塞调用线程直到数据到达（FFmpeg 工作线程，非 UI）。仅在同步上下文使用。
    /// </remarks>
    int Read(Span<byte> buffer);

    /// <summary>异步读取数据到指定 buffer。</summary>
    /// <param name="buffer">目标 buffer。不接受空 buffer。</param>
    /// <param name="ct">取消令牌（网络流必须支持，本地文件可忽略）。</param>
    /// <returns>实际读取的字节数，0 表示流结束。</returns>
    /// <remarks>
    /// <para><b>建连语义</b>：<see cref="ReadAsync"/> 允许<b>隐式建连</b>（网络流首次读取时才真正
    /// 连到对端），而同步 <c>Read</c> 必须工作在已建连状态——实现可在 Open 阶段预先建连，
    /// 也可把建连推迟到首次 <see cref="ReadAsync"/>。</para>
    /// <para>建连失败（DNS/连接/握手）须抛出 <see cref="System.IO.IOException"/>，不得返回 0 冒充流结束。</para>
    /// </remarks>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// 异步预建立底层连接（网络流建立 HTTP 连接；文件/透传流为无操作）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>连接建立完成的 Task。</returns>
    /// <remarks>
    /// <b>必须在同步 <see cref="Read"/> 之前调用</b>：FFmpeg AVIO 原生回调与 FormatDetector 同步探测
    /// 均为同步边界，无法 await 建连。网络流若不预建连，<see cref="Read"/> 会抛
    /// <see cref="InvalidOperationException"/>。
    /// </remarks>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>流长度（不可求时为 -1）。</summary>
    long Length { get; }

    /// <summary>当前读取位置。</summary>
    long Position { get; }

    /// <summary>是否可定位。</summary>
    bool CanSeek { get; }

    /// <summary>
    /// 流的可定位地址（文件路径或网络 URL）；无法以地址方式打开时为 null。
    /// </summary>
    /// <remarks>
    /// <para>供需要按地址打开的 backend（如 MediaFoundation 的 <c>MFCreateSourceReaderFromURL</c>）使用。
    /// 文件流返回文件路径，网络流返回 URL；内存/透传流无地址返回 null。</para>
    /// <para>中性契约成员（BCL string），不引用任何具体后端/源类型，遵循依赖倒置。</para>
    /// </remarks>
    string? Location { get; }

    /// <summary>定位到指定偏移。</summary>
    /// <remarks>
    /// <para>不可定位的流（如不可 seek 的直播流）须抛 <see cref="NotSupportedException"/>，
    /// 不得返回 0 或静默忽略。</para>
    /// <para>目标位置越界时抛 <see cref="System.IO.IOException"/>（与读取端错误统一）。</para>
    /// <para>已 <see cref="Close"/> 后调用抛 <see cref="ObjectDisposedException"/>。</para>
    /// </remarks>
    long Seek(long offset, SeekOrigin origin);

    /// <summary>
    /// 按 <see cref="Location"/> 地址打开时建议随附的 HTTP 请求头（默认 User-Agent、
    /// 用户自定义头与 Cookie 的合并结果）；非网络流为 null。
    /// </summary>
    /// <remarks>
    /// <para>供按地址直开的解封装后端（URL 交给后端自身网络栈的场景）在打开地址时透传，
    /// 避免自定义头/Cookie 在地址式打开路径上被静默丢弃（防盗链源必需）。</para>
    /// <para>中性契约成员（BCL 字典类型，零外部引用）；实现须显式提供该属性（不依赖接口默认实现）。</para>
    /// </remarks>
    IReadOnlyDictionary<string, string>? SuggestedOpenHeaders { get; }

    /// <summary>关闭流。</summary>
    /// <remarks>
    /// <para><b>幂等</b>：重复调用不得抛异常（已关闭再关闭是空操作）。</para>
    /// <para>关闭后 <see cref="ReadAsync"/> 与 <see cref="Seek"/> 抛 <see cref="ObjectDisposedException"/>。</para>
    /// <para>关闭须释放底层连接与原生资源，且不等待未完成的读取返回。</para>
    /// </remarks>
    void Close();
}
