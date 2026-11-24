namespace LingFan.Media.Abstractions;

/// <summary>
/// BT.601 全范围（JFIF）YUV→RGB 转换系数——全仓 CPU 转换路径的唯一常量真源。
/// </summary>
/// <remarks>
/// <para>公式（Y/U/V 为 0..255 整数域，色度中心 128）：
/// R = Y + <see cref="Rv"/>·(V−128)；
/// G = Y − <see cref="Gu"/>·(U−128) − <see cref="Gv"/>·(V−128)；
/// B = Y + <see cref="Bu"/>·(U−128)；结果钳制到 0..255。</para>
/// <para>归一化域（0..1，色度中心 0.5）使用同一组系数。GPU 着色器路径
/// （D3D11 HLSL / GLSL / MSL / SPIR-V）内嵌同一组数值，修改本类时须同步核对各着色器源，
/// 否则 CPU 兜底与 GPU 主路径色彩漂移。</para>
/// <para>本类只覆盖 BT.601-Full 默认矩阵；BT.709/BT.2020 与 limited-range 补偿系数
/// 由消费方按 <see cref="VideoColorInfo"/> 另行选择，不在本类范围。</para>
/// </remarks>
public static class Bt601FullRange
{
    /// <summary>R 通道 V 系数（+1.402）。</summary>
    public const double Rv = 1.402;

    /// <summary>G 通道 U 系数（−0.344136，公式中取负）。</summary>
    public const double Gu = 0.344136;

    /// <summary>G 通道 V 系数（−0.714136，公式中取负）。</summary>
    public const double Gv = 0.714136;

    /// <summary>B 通道 U 系数（+1.772）。</summary>
    public const double Bu = 1.772;

    /// <summary>色度中心（8-bit 整数域，128）。</summary>
    public const int ChromaCenter = 128;
}