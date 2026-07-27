namespace LingFan.Media.Abstractions;

/// <summary>
/// 编解码器注册表契约（跨层共享，零外部引用）。
/// </summary>
/// <remarks>
/// <para>提供容器格式 → 编解码器的静态映射查询，供后端自动选择
/// （<c>MediaOptions.EnableAutoBackendSelection</c>）与能力探测使用。</para>
/// <para>纯内存静态表，全部同步（sync 分类）：无 I/O、无 await，实现返回普通 <see langword="bool"/> /
/// 可空枚举，绝不包装为 <see cref="Task"/>（否则即伪异步）。</para>
/// <para>仅引用跨层枚举 <see cref="ContainerFormat"/> / <see cref="VideoCodec"/> / <see cref="AudioCodec"/>
/// （均为 Abstractions 已有类型），零外部引用，依赖倒置严守。</para>
/// </remarks>
public interface ICodecRegistry
{
    /// <summary>判断指定容器格式是否支持给定视频编解码器。</summary>
    /// <param name="container">容器格式。</param>
    /// <param name="videoCodec">视频编解码器。</param>
    /// <returns>支持则返回 <see langword="true"/>。</returns>
    bool IsCodecSupported(ContainerFormat container, VideoCodec videoCodec);

    /// <summary>判断指定容器格式是否支持给定音频编解码器。</summary>
    /// <param name="container">容器格式。</param>
    /// <param name="audioCodec">音频编解码器。</param>
    /// <returns>支持则返回 <see langword="true"/>。</returns>
    bool IsCodecSupported(ContainerFormat container, AudioCodec audioCodec);

    /// <summary>获取指定容器格式的默认视频编解码器（未知容器返回 <see langword="null"/>）。</summary>
    /// <param name="container">容器格式。</param>
    /// <returns>默认视频编解码器，或 <see langword="null"/>。</returns>
    VideoCodec? GetDefaultVideoCodec(ContainerFormat container);

    /// <summary>获取指定容器格式的默认音频编解码器（未知容器返回 <see langword="null"/>）。</summary>
    /// <param name="container">容器格式。</param>
    /// <returns>默认音频编解码器，或 <see langword="null"/>。</returns>
    AudioCodec? GetDefaultAudioCodec(ContainerFormat container);
}
