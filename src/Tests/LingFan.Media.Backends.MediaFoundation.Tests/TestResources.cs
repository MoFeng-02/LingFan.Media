using System;
using System.IO;

namespace LingFan.Media.Backends.MediaFoundation.Tests;

/// <summary>
/// 测试资源路径解析（与测试文档规则 §4.2 一致：资源经 CopyToOutputDirectory 落到输出目录 Resources\ 下）。
/// </summary>
internal static class TestResources
{
    public static readonly string BasePath =
        Path.Combine(AppContext.BaseDirectory, "Resources");

    public static string GetVideo(string name) =>
        Path.Combine(BasePath, "Video", name);

    public static string GetAudio(string name) =>
        Path.Combine(BasePath, "Audio", name);

    public static string VideoM1 => GetVideo("m1.mp4");

    /// <summary>crickets_night01.mp3：3 分钟纯夜虫环境音，无视频轨，专门用于「纯音频 → WASAPI 真机出声」验证。</summary>
    public static string AudioCrickets => GetAudio("crickets_night01.mp3");
}
