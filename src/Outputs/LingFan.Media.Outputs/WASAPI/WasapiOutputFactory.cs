using System.Runtime.Versioning;

namespace LingFan.Media.Outputs.Wasapi;

/// <summary>
/// WASAPI 音频输出工厂。Singleton 工厂，每次 Create() 返回新实例（设备句柄独立）。
/// </summary>
/// <remarks>
/// <para>DI 生命周期：Singleton 工厂。WasapiOutput 是 Session 级对象，由工厂 Create() 每次新建。</para>
/// <para>此工厂持有 <see cref="WasapiOptions"/> 配置快照，所有创建的实例共享同一配置。</para>
/// <para>Create() 为同步（sync 分类），手动 new，无 I/O。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiOutputFactory : IAudioOutputFactory
{
    private readonly WasapiOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// 初始化 <see cref="WasapiOutputFactory"/> 的新实例。
    /// </summary>
    /// <param name="options">WASAPI 配置选项。</param>
    /// <param name="loggerFactory">日志工厂。</param>
    public WasapiOutputFactory(WasapiOptions options, ILoggerFactory loggerFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public IAudioOutput Create()
    {
        return new WasapiOutput(_options, _loggerFactory.CreateLogger<WasapiOutput>());
    }
}
