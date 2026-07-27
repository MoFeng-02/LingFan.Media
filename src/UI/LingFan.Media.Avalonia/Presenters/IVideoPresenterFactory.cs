namespace LingFan.Media.Avalonia;

/// <summary>
/// 视频呈现器工厂。供宿主通过 DI 注册/替换默认 <see cref="IVideoPresenter"/> 实现
/// （如 SkiaVideoPresenter / 未来的 D3D11 GPU Presenter）。
/// </summary>
/// <remarks>
/// <para><b>异步策略</b>：config 分类——纯工厂方法，无 I/O。</para>
/// <para><b>AOT 兼容</b>：接口，实现应为 sealed 类。</para>
/// </remarks>
public interface IVideoPresenterFactory
{
    /// <summary>创建视频呈现器实例。</summary>
    IVideoPresenter Create();

    /// <summary>
    /// 此工厂创建的 Presenter 类型。供 <see cref="VideoView"/> 按 <c>RendererType</c>（Type 对象）
    /// 匹配已注册的工厂——VideoView 无需编译期引用桥接项目（如 Avalonia.D3D11），仅比较 Type，符合依赖倒置。
    /// </summary>
    Type PresenterType { get; }
}
