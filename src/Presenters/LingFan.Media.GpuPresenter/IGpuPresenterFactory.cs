namespace LingFan.Media.Presenters;

/// <summary>
/// GPU 呈现器工厂。供宿主通过 DI 注册/替换默认 <see cref="IGpuPresenter"/> 实现（如 D3D11GpuPresenter）。
/// </summary>
/// <remarks>
/// <para><b>匹配机制</b>：VideoView 按工厂的 <see cref="PresenterType"/>（Type 对象）匹配已注册工厂，
/// 无需编译期引用具体 GPU 项目（如 LingFan.Media.GpuPresenter.D3D11），符合依赖倒置。</para>
/// <para><b>异步策略</b>：config 分类——纯工厂方法，无 I/O。</para>
/// <para><b>AOT 兼容</b>：实现应为 sealed 类。</para>
/// </remarks>
public interface IGpuPresenterFactory
{
    /// <summary>创建 GPU 呈现器实例。</summary>
    IGpuPresenter Create();

    /// <summary>此工厂创建的 Presenter 类型。供 VideoView 按 RendererType（Type 对象）匹配。</summary>
    Type PresenterType { get; }
}
