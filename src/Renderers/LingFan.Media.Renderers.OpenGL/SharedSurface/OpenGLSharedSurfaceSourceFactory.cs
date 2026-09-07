namespace LingFan.Media.Renderers.OpenGL.SharedSurface;

/// <summary>
/// GL 共享表面源工厂（能力自报：Android 平台可用）。注册即插拔——UI 层遍历
/// <see cref="ISharedGpuSurfaceSourceFactory"/> 集合，呈现兼容性由宿主呈现适配器按描述符判别。
/// </summary>
/// <remarks>
/// <para><b>M1 简化</b>：测试纹理尺寸为固定值（与验收样片一致），M2 接解码桥后由桥产物决定尺寸。
/// <see cref="IsAvailable"/> 为轻量平台判定，不触碰原生资源（开箱即用原则）。</para>
/// <para><b>AOT 兼容</b>：sealed 类，public ctor（DI 直接激活），零反射。</para>
/// </remarks>
public sealed class OpenGLSharedSurfaceSourceFactory : ISharedGpuSurfaceSourceFactory
{
    private readonly ILoggerFactory _loggerFactory;

    // M1 测试表面尺寸（与验收样片一致）；M2 起由解码桥产物尺寸决定。
    private const int ProbeWidth = 1080;
    private const int ProbeHeight = 1920;

    /// <summary>初始化工厂。</summary>
    public OpenGLSharedSurfaceSourceFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc/>
    public SharedGpuHandleKind HandleKind => SharedGpuHandleKind.GlTexture;

    /// <inheritdoc/>
    public bool IsAvailable
    {
        get
        {
            // 宿主已声明优先形态且非 GL 纹理 ⇒ 自报不可用（选型决策在宿主，模块只自报）。
            var preferred = SharedGpuSourcePolicy.PreferredKind;
            if (preferred.HasValue && preferred.Value != SharedGpuHandleKind.GlTexture)
                return false;
            return OperatingSystem.IsAndroid();
        }
    }

    /// <inheritdoc/>
    public ISharedGpuSurfaceSource Create(SharedGpuAdapterIdentity? targetAdapter = null)
        => new OpenGLSharedSurfaceSource(ProbeWidth, ProbeHeight, _loggerFactory.CreateLogger<OpenGLSharedSurfaceSource>());
}
