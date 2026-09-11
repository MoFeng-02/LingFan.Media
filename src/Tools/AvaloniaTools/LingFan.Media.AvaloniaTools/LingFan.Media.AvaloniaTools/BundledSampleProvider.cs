using Avalonia.Platform;

namespace LingFan.Media.AvaloniaTools;

/// <summary>
/// 内置样例提供者（跨平台通用，与桌面/移动平台无关）。
/// </summary>
/// <remarks>
/// <para>样例视频经 <c>AvaloniaResource</c> 嵌入共享程序集（<c>Assets/sample.mp4</c>，
/// 见 csproj 的 <c>&lt;AvaloniaResource Include="Assets\**" /&gt;</c>），
/// 运行时经 <see cref="AssetLoader"/> 以 <c>avares://</c> 流解出到临时文件，
/// 返回真实文件路径供媒体源打开。所有平台共用同一实现，无需平台专属代码。</para>
/// <para>未放入样例文件时（<c>Assets/sample.mp4</c> 缺失）返回 <c>null</c>，由调用方提示。</para>
/// </remarks>
public sealed class BundledSampleProvider : IBundledSampleProvider
{
    private const string SampleUri = "avares://LingFan.Media.AvaloniaTools/Assets/sample.mp4";

    /// <inheritdoc />
    public string? GetSamplePath()
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri(SampleUri));
            // 清理上一会话残留：每次播放写一个 GUID 新文件，旧文件不删会在 TempPath 累积
            //（每份 ≈ 数十 MB，反复重播持续吃存储）。播放串行（旧播放器先 Dispose），无并发占用。
            string tempDir = Path.GetTempPath();
            foreach (string stale in Directory.GetFiles(tempDir, "LingFanSample_*.mp4"))
            {
                try { File.Delete(stale); } catch { /* 被占用/已删除：忽略，不影响本次播放 */ }
            }

            string path = Path.Combine(tempDir, $"LingFanSample_{Guid.NewGuid():N}.mp4");
            using (var file = File.Create(path))
            {
                stream.CopyTo(file);
            }
            return path;
        }
        catch
        {
            // 样例文件缺失（AssetLoader 打不开 avares 资源）或落盘失败：返回 null，不崩溃。
            return null;
        }
    }
}
