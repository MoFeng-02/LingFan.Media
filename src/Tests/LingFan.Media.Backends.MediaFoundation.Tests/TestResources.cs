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

    public static string VideoM1 => GetVideo("m1.mp4");
}
