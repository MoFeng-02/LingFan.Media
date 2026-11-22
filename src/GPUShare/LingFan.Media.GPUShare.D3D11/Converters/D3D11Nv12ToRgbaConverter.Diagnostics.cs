using System;

namespace LingFan.Media.GPUShare.D3D11;

/// <summary>
/// 失败诊断分部：首帧失败附带完整对象指针/设备归属诊断，后续帧仅异常本体（防刷屏）。
/// </summary>
public sealed partial class D3D11Nv12ToRgbaConverter
{
    // 失败诊断已打印标志：首帧失败附带完整对象指针/设备归属诊断，后续帧仅异常本体（防刷屏）。
    private bool _failDiagnosed;

    /// <summary>
    /// 构造转换失败异常：首帧失败输出完整诊断（对象指针、VP 格式支持、纹理 DESC、设备归属），
    /// 并捕获 D3D11 调试层消息；后续帧直接返回原异常。
    /// </summary>
    private Exception DiagnoseFailure(
        Exception ex, IntPtr output, IntPtr nv12TexturePtr, int subresourceIndex,
        int width, int height, D3D11Texture2DDesc inputTexDesc)
    {
        if (_failDiagnosed)
            return ex;

        _failDiagnosed = true;
        var failure = new InvalidOperationException(
            $"NV12→RGBA 转换失败: {ex.Message}\n" +
            $"  device=0x{_devicePtr:X} context=0x{_contextPtr:X}\n" +
            $"  videoDevice=0x{_videoDevicePtr:X} videoContext=0x{_videoContextPtr:X}\n" +
            $"  enumerator=0x{_enumeratorPtr:X} processor=0x{_processorPtr:X}\n" +
            $"  output=0x{output:X}（失败路径已释放）\n" +
            $"  VP 格式支持: NV12(103)=0x{D3D11Interop.CheckVideoProcessorFormat(_enumeratorPtr, 103):X} " +
            $"BGRA(87)=0x{D3D11Interop.CheckVideoProcessorFormat(_enumeratorPtr, 87):X}（bit0=输入 bit1=输出）\n" +
            $"  输入纹理(inputTexDesc): {inputTexDesc.Width}x{inputTexDesc.Height} ArraySize={inputTexDesc.ArraySize} Bind=0x{inputTexDesc.BindFlags:X} " +
            $"Fmt=0x{inputTexDesc.Format:X} Misc=0x{inputTexDesc.MiscFlags:X} subresource={subresourceIndex}" +
            $" device=0x{D3D11Interop.GetDeviceChildDevice(nv12TexturePtr):X}\n" +
            $"  帧尺寸(内容描述/输出纹理): {width}x{height}" +
            $"{(inputTexDesc.Width != (uint)width || inputTexDesc.Height != (uint)height ? " 与输入纹理尺寸不一致(编解码对齐填充)" : " (一致)")}\n" +
            $"  设备归属(GetDevice): enumerator→0x{D3D11Interop.GetDeviceChildDevice(_enumeratorPtr):X} " +
            $"processor→0x{D3D11Interop.GetDeviceChildDevice(_processorPtr):X} " +
            $"output→0x{D3D11Interop.GetDeviceChildDevice(output):X}", ex);
        // D3D11 调试层消息捕获：打印驱动/runtime 对 VideoProcessorBlt 失败的精确原因。
        D3D11Interop.DumpDebugMessages(_devicePtr);
        return failure;
    }
}