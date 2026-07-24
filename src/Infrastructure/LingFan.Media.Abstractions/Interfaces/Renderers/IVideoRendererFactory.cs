namespace LingFan.Media.Abstractions;

/// <summary>
/// 视频渲染器工厂接口。
/// </summary>
/// <remarks>
/// <para>Singleton 工厂。GPU 设备上下文（IGpuDeviceContext）由工厂持有</para>
/// <para>（Singleton 共享 GPU Device），但 SwapChain 在 Create() 中独立创建。</para>
/// </remarks>
public interface IVideoRendererFactory
{
    /// <summary>创建新的 IVideoRenderer 实例（共享 GPU Device，独立 SwapChain）。</summary>
    IVideoRenderer Create();
}
