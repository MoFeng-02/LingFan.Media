namespace LingFan.Media.Abstractions;

/// <summary>
/// GPU 设备丢失异常（D3D11 <c>DXGI_ERROR_DEVICE_REMOVED</c>/<c>DEVICE_RESET</c>、
/// Vulkan <c>VK_ERROR_DEVICE_LOST</c> 等）。
/// </summary>
/// <remarks>
/// <para><b>B-DEVLOST</b>：设备丢失由驱动崩溃/重置（TDR）、GPU 移除（外接显卡拔出）、
/// 驱动升级等引起——设备及其全部资源（纹理/缓冲/交换链）永久失效，任何后续调用都会失败。</para>
/// <para><b>语义</b>：渲染器检测到设备丢失时抛出本异常（替代泛化的 <see cref="InvalidOperationException"/>），
/// 使上层能以类型区分「可通过重建设备恢复的故障」与「逻辑错误」。</para>
/// <para><b>恢复路径（V2 现状）</b>：异常沿 Present/Clear 调用链上抛至会话层；调用方应释放当前
/// 播放会话并重建（重新 OpenAsync → Attach）——渲染器工厂会重新创建设备。
/// 自动恢复编排（会话内透明重建设备 + 重挂渲染目标 + 续播）为 V3 范围。</para>
/// <para><b>中立性</b>：仅 BCL 依赖，不携带任何图形 API 具体类型；诊断细节（DeviceRemovedReason 等）
/// 以消息文本承载。AOT 兼容：无序列化构造（NativeAOT 下二进制序列化不受支持）。</para>
/// </remarks>
public sealed class GpuDeviceLostException : Exception
{
    /// <summary>初始化默认消息的实例。</summary>
    public GpuDeviceLostException()
        : base("GPU 设备已丢失，需释放并重建渲染会话。") { }

    /// <summary>以指定消息初始化实例。</summary>
    /// <param name="message">异常消息（应包含底层错误码与操作上下文）。</param>
    public GpuDeviceLostException(string message) : base(message) { }

    /// <summary>以指定消息与内部异常初始化实例。</summary>
    /// <param name="message">异常消息。</param>
    /// <param name="innerException">底层异常。</param>
    public GpuDeviceLostException(string message, Exception innerException)
        : base(message, innerException) { }
}
