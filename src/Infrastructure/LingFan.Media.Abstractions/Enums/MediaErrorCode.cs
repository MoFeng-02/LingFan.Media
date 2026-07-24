namespace LingFan.Media.Abstractions;

/// <summary>
/// 媒体错误码。
/// </summary>
public enum MediaErrorCode : int
{
    /// <summary>无错误。</summary>
    None,
    /// <summary>媒体源未找到。</summary>
    SourceNotFound,
    /// <summary>媒体源打开失败。</summary>
    SourceOpenFailed,
    /// <summary>容器格式不支持。</summary>
    FormatNotSupported,
    /// <summary>编解码器不支持。</summary>
    CodecNotSupported,
    /// <summary>解码器错误。</summary>
    DecoderError,
    /// <summary>渲染器错误。</summary>
    RendererError,
    /// <summary>音频输出错误。</summary>
    AudioOutputError,
    /// <summary>网络错误。</summary>
    NetworkError,
    /// <summary>缓冲不足。</summary>
    BufferUnderrun,
    /// <summary>定位失败。</summary>
    SeekFailed,
    /// <summary>内存不足。</summary>
    OutOfMemory,
    /// <summary>GPU 错误。</summary>
    GPUError,
    /// <summary>未知错误。</summary>
    Unknown
}
