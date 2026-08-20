using System;

namespace LingFan.Media.Backends.FFmpeg.Interop;

/// <summary>
/// FFmpeg 原生绑定门面（自绑定，替代 FFmpeg.AutoGen）。
/// 全部 P/Invoke 使用 <c>[LibraryImport]</c>（AOT 兼容、零反射、零 ComImport），
/// 原生库由 <see cref="FFmpegLibraryLoader"/> 按平台与版本自适应加载。
/// </summary>
internal static partial class FF
{
    // ── 常量（值严格对齐 FFmpeg 头文件，跨主版本稳定）──
    public const int AVFMT_FLAG_CUSTOM_IO = 0x0080;
    public const int AV_DICT_IGNORE_SUFFIX = 2;
    public const int AVSEEK_FLAG_BACKWARD = 1;
    public const int AVSEEK_SIZE = 0x10000;
    public const int AV_PKT_FLAG_KEY = 0x0001;
    public const int AV_FRAME_FLAG_KEY = 2;
    public const int AV_ERROR_MAX_STRING_SIZE = 64;
    public const long AV_NOPTS_VALUE = unchecked((long)0x8000000000000000); // 0x8000000000000000
    public const long AV_TIME_BASE = 1000000;
    public const int AVERROR_EOF = -541478725;

    // 平台 errno EAGAIN：直接取自既有 AutoGen 绑定（ffmpeg.EAGAIN == 11，对应 Windows MSVC 构建的 FFmpeg）。
    // 固定此值可保证 AVERROR(EAGAIN) 判别行为与迁移前完全一致，避免硬解/软解回退时误判"再试"。
    public const int EAGAIN = 11;

    /// <summary>FFmpeg 错误宏 AVERROR(x) = -x（等价于 ffmpeg.AVERROR）。</summary>
    public static int AVERROR(int err) => -err;
}
