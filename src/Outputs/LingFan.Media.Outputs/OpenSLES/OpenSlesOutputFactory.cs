using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Outputs.OpenSLES;

/// <summary>
/// OpenSL ES 音频输出工厂（Android 真实实现）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。<see cref="OpenSlesOutput"/> 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>Create() 为同步操作，手动 new，无 I/O。</para>
/// <para>平台边界：仅 Android 有效；非 Android 上创建实例本身无副作用，实际调用时抛 <see cref="PlatformNotSupportedException"/>。</para>
/// </remarks>
[SupportedOSPlatform("Android")]
public sealed class OpenSlesOutputFactory : IAudioOutputFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="OpenSlesOutputFactory"/> 的新实例。
    /// </summary>
    /// <param name="loggerFactory">日志器工厂（用于创建 <see cref="OpenSlesOutput"/> 的日志器）。</param>
    public OpenSlesOutputFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public IAudioOutput Create() => new OpenSlesOutput(_loggerFactory.CreateLogger<OpenSlesOutput>());
}
