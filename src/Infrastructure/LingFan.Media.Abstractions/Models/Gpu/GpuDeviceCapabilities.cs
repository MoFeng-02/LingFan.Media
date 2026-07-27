namespace LingFan.Media.Abstractions;

/// <summary>
/// GPU 设备能力（中立模型，跨层共享）。
/// </summary>
/// <remarks>
/// <para>由 <see cref="IGpuDeviceContext.GetCapabilities"/> 返回，Avalonia / Outputs 等层可读取设备能力
/// 而无需引用具体渲染器模块（依赖倒置严守）。</para>
/// <para>纯数据不可变模型，AOT 友好：仅含 BCL 类型与 Abstractions 已有枚举，零外部引用。</para>
/// </remarks>
public sealed class GpuDeviceCapabilities
{
    /// <summary>
    /// 初始化 <see cref="GpuDeviceCapabilities"/> 的新实例。
    /// </summary>
    /// <param name="deviceName">GPU 名称。</param>
    /// <param name="dedicatedVideoMemory">专用显存（字节）。</param>
    /// <param name="sharedSystemMemory">共享系统内存（字节）。</param>
    /// <param name="maxTextureSize">最大纹理尺寸（像素）。</param>
    /// <param name="supportsComputeShaders">是否支持计算着色器。</param>
    /// <param name="supportsHardwareDecode">是否支持硬件解码（DXVA / VA-API 等）。</param>
    /// <param name="featureLevel">D3D Feature Level 原始值（如 0xB000 = 11_0）；非 D3D API 填 -1。</param>
    public GpuDeviceCapabilities(
        string deviceName,
        ulong dedicatedVideoMemory,
        ulong sharedSystemMemory,
        int maxTextureSize,
        bool supportsComputeShaders,
        bool supportsHardwareDecode,
        int featureLevel)
    {
        DeviceName = deviceName ?? string.Empty;
        DedicatedVideoMemory = dedicatedVideoMemory;
        SharedSystemMemory = sharedSystemMemory;
        MaxTextureSize = maxTextureSize;
        SupportsComputeShaders = supportsComputeShaders;
        SupportsHardwareDecode = supportsHardwareDecode;
        FeatureLevel = featureLevel;
    }

    /// <summary>GPU 名称。</summary>
    public string DeviceName { get; }

    /// <summary>专用显存（字节）。</summary>
    public ulong DedicatedVideoMemory { get; }

    /// <summary>共享系统内存（字节）。</summary>
    public ulong SharedSystemMemory { get; }

    /// <summary>最大纹理尺寸（像素）。</summary>
    public int MaxTextureSize { get; }

    /// <summary>是否支持计算着色器。</summary>
    public bool SupportsComputeShaders { get; }

    /// <summary>是否支持硬件解码（DXVA / VA-API 等）。</summary>
    public bool SupportsHardwareDecode { get; }

    /// <summary>D3D Feature Level 原始值（如 0xB000 = 11_0）；非 D3D API 填 -1。</summary>
    public int FeatureLevel { get; }
}
