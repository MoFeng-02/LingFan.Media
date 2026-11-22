using System;
using System.Runtime.InteropServices;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// 视频处理器生命周期分部：枚举器/处理器的创建与重建（尺寸变化时）。
/// </summary>
public sealed partial class D3D11Nv12ToRgbaConverter
{
    private IntPtr _enumeratorPtr;             // CreateVideoProcessor* 创建（本类 Release）
    private IntPtr _processorPtr;              // CreateVideoProcessor* 创建（本类 Release）
    private int _contentWidth = -1;            // 输入纹理实际宽（含对齐填充）
    private int _contentHeight = -1;           // 输入纹理实际高
    private int _frameWidth = -1;              // 帧宽（输出尺寸）
    private int _frameHeight = -1;             // 帧高（输出尺寸）

    /// <summary>
    /// 确保视频处理器存在（尺寸任一变化即重建）。
    /// </summary>
    /// <param name="texWidth">输入纹理实际宽（含编解码对齐填充，如 1080→1088）。</param>
    /// <param name="texHeight">输入纹理实际高。</param>
    /// <param name="frameWidth">帧宽（输出尺寸，即实际视频内容宽）。</param>
    /// <param name="frameHeight">帧高（输出尺寸）。</param>
    /// <remarks>
    /// 内容描述的 InputWidth/InputHeight <b>必须用纹理实际尺寸</b>（而非帧尺寸）：
    /// D3D11VA 硬解纹理有宏块对齐填充（H.264 16 像素对齐，如 1080→1088），输入视图覆盖整张纹理；
    /// 若内容描述声明 1080 而实际纹理 1088，驱动在 VideoProcessorBlt 校验维度不匹配 → E_INVALIDARG。
    /// 输出侧用帧尺寸，并经源矩形裁掉填充（左上角帧尺寸区域），避免垃圾像素被缩进画面。
    /// </remarks>
    private void EnsureProcessor(int texWidth, int texHeight, int frameWidth, int frameHeight)
    {
        if (_enumeratorPtr != IntPtr.Zero && _contentWidth == texWidth && _contentHeight == texHeight
            && _frameWidth == frameWidth && _frameHeight == frameHeight)
            return;

        if (_processorPtr != IntPtr.Zero)
        {
            D3D11Interop.Release(_processorPtr);
            _processorPtr = IntPtr.Zero;
        }

        if (_enumeratorPtr != IntPtr.Zero)
        {
            D3D11Interop.Release(_enumeratorPtr);
            _enumeratorPtr = IntPtr.Zero;
        }

        _contentWidth = texWidth;
        _contentHeight = texHeight;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;

        var contentDesc = new D3D11VideoProcessorContentDescription
        {
            InputFrameFormat = 0, // D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE
            InputFrameRate = new DxgiRational { Numerator = 60, Denominator = 1 },
            InputWidth = (uint)texWidth,   // 纹理实际宽（含对齐填充）
            InputHeight = (uint)texHeight, // 纹理实际高
            OutputFrameRate = new DxgiRational { Numerator = 60, Denominator = 1 },
            OutputWidth = (uint)frameWidth,
            OutputHeight = (uint)frameHeight,
            Usage = 0, // D3D11_VIDEO_USAGE_PLAYBACK_NORMAL
        };
        _enumeratorPtr = D3D11Interop.CreateVideoProcessorEnumerator(_videoDevicePtr, contentDesc);

        // 处理器按 RateConversionCaps 遍历创建（对齐社区成熟做法）：固定 index 0 在部分设备/驱动上
        // 返回「创建成功但无 NV12→BGRA 转换能力」的处理器 → VideoProcessorBlt 报 E_INVALIDARG。
        // 遍历取第一个创建成功的 index；全失败则抛（回落 CPU 传输）。
        D3D11VideoProcessorCaps caps = D3D11Interop.GetVideoProcessorCaps(_enumeratorPtr);
        if (caps.RateConversionCapsCount == 0)
            throw new InvalidOperationException("视频处理器枚举器无任何 RateConversion 能力（GetVideoProcessorCaps.RateConversionCapsCount=0）");
        for (uint i = 0; i < caps.RateConversionCapsCount; i++)
        {
            try
            {
                _processorPtr = D3D11Interop.CreateVideoProcessor(_videoDevicePtr, _enumeratorPtr, i);
                break;
            }
            catch (COMException)
            {
                _processorPtr = IntPtr.Zero;
            }
        }
        if (_processorPtr == IntPtr.Zero)
            throw new InvalidOperationException(
                $"创建视频处理器失败（遍历 RateConversionCapsCount={caps.RateConversionCapsCount} 全部失败）");

        // Blt 前置必需状态（所有参考实现 VLC/Chromium/MF 均在 Blt 前设置；缺失会使部分驱动在 Blt 报 E_INVALIDARG）：
        // (1) 流 0 帧格式 = 逐行扫描。
        D3D11Interop.VideoProcessorSetStreamFrameFormat(_videoContextPtr, _processorPtr, 0, 0); // D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE
        // (2) 输入流颜色空间：BT.709 + 演播室范围 16-235（NV12 YUV 视频标准）。
        D3D11Interop.VideoProcessorSetStreamColorSpace(_videoContextPtr, _processorPtr, 0,
            new D3D11VideoProcessorColorSpace { Value = 0x0C }); // Matrix=1(BT.709) | Nominal=1(16-235)
        // (3) 输出颜色空间：全范围 RGB（BGRA 渲染目标）。
        D3D11Interop.VideoProcessorSetOutputColorSpace(_videoContextPtr, _processorPtr,
            new D3D11VideoProcessorColorSpace { Value = 0x10 }); // Nominal=2(0-255)
        // (4) 源矩形 = 左上角帧尺寸区域：裁掉宏块对齐填充（如纹理 1088 宽、视频 1080 宽，
        //    右侧 8 列是垃圾填充，不裁会被缩进画面）。纹理无填充时矩形=整张纹理，等效默认值。
        D3D11Interop.VideoProcessorSetStreamSourceRect(_videoContextPtr, _processorPtr, 0, 1,
            new D3D11Rect { Left = 0, Top = 0, Right = frameWidth, Bottom = frameHeight });
    }
}