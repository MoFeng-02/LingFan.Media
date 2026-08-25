namespace LingFan.Media.Extensions;

/// <summary>
/// 平台后端注册器：共享层（如 net10.0 的 AvaloniaTools）与平台入口（如 net10.0-android）之间的 DI 注入钩子，
/// 用于避免共享层耦合「仅特定平台可用」的后端（例如仅 Android 的 MediaCodec）。
/// </summary>
/// <remarks>
/// <para>用法：平台入口（net10.0-android 应用）在启动前设置 <see cref="PlatformRegistrar"/>，
/// 例如 <c>MediaBuilderPlatformRegistrar.PlatformRegistrar = b => b.AddMediaCodec(...)</c>；
/// 共享层在构建 <see cref="MediaBuilder"/> 时调用 <see cref="ApplyPlatformRegistrar"/> 应用之。</para>
/// <para>这样共享层无需引用「仅平台可用」的后端工程——平台后端由平台入口直接引用并注册，
/// 既不产生跨 TFM 传递解析（避免落到桩实现），也避免两处重复注册冲突。</para>
/// <para>多平台可并存：<see cref="ApplyPlatformRegistrar"/> 通过 <see cref="IServiceCollection"/> 的
/// 集合注册（TryAddEnumerable）让各后端按顺序参与运行时回退，互不覆盖。</para>
/// </remarks>
public static class MediaBuilderPlatformRegistrar
{
    /// <summary>
    /// 平台后端注册器委托。由平台入口（如 Android 应用）在应用启动前设置；可为多个（后设覆盖先设，或由入口自行组合）。
    /// 未设置（null）时 <see cref="ApplyPlatformRegistrar"/> 为无操作。
    /// </summary>
    public static Action<MediaBuilder>? PlatformRegistrar { get; set; }

    /// <summary>
    /// 将平台后端注册器应用到构建器（若有）。
    /// </summary>
    /// <param name="builder">媒体构建器。</param>
    /// <returns>同一构建器，便于链式调用。</returns>
    public static MediaBuilder ApplyPlatformRegistrar(this MediaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        PlatformRegistrar?.Invoke(builder);
        return builder;
    }
}