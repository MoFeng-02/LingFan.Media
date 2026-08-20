namespace LingFan.Media.AvaloniaTools;

/// <summary>
/// 提供「预置到应用内的样例媒体文件」的本地文件系统路径。
/// </summary>
/// <remarks>
/// <para>真机（尤其 Android）经系统文件选择器拿到的是 content URI，不能直接当文件路径喂给后端；
/// 首步验收改为把样例文件（如 sample.mp4）打包进应用、拷到应用私有目录，再用真实路径打开。</para>
/// <para>默认实现 <see cref="BundledSampleProvider"/> 位于共享工程：样例经 <c>AvaloniaResource</c>
/// 嵌入（<c>Assets/sample.mp4</c>），运行时解出到临时文件，所有平台共用，无需平台专属代码。</para>
/// <para>无样例时返回 <c>null</c>（如 Assets 中尚未放入 sample.mp4），调用方据此提示用户。</para>
/// </remarks>
public interface IBundledSampleProvider
{
    /// <summary>返回样例媒体文件的本地路径；不存在/未配置时返回 <c>null</c>。</summary>
    string? GetSamplePath();
}
