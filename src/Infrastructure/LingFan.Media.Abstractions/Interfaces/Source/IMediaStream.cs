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
    long Seek(long offset, SeekOrigin origin);

    /// <summary>关闭流。</summary>
    void Close();
}
