using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.GPUShare.D3D11;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

// MFVideoDecoder 的 Output 职责段（部分类，由 MFVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeAsync/ProcessOutputOnce/FlushAsync/Reset/Dispose）在主文件；
// IMFTransform 同步 COM 调用，全部在所属线程执行（COM 线程亲和）。
internal sealed partial class MFVideoDecoder
{

    /// <summary>
    /// STREAM_CHANGE 后重新协商输出媒体类型，更新尺寸与输出 buffer 大小。
    /// MS 推荐流程：从 <c>GetOutputAvailableType</c> 枚举<b>新的</b>可用类型（优先 NV12）→ <c>SetOutputType</c>；
    /// 不能把 <c>GetOutputCurrentType</c> 的旧类型原样设回（运行时验证会返回 MF_E_INVALIDMEDIATYPE）。
    /// </summary>
    private bool RenegotiateOutput()
    {
        // RenegotiateOutput 触碰 _transform/_outputTypePtr，须处于 gate 内。
        // 正常仅从 DecodeAsync/FlushAsync（已在 gate 内）调用，此处再包裹一次无副作用（嵌套 Enter/Exit 配对）。
        if (!_transformGate.TryEnter()) return false;
        try
        {
            Guid nv12 = MFConstants.MFVideoFormat_NV12;
            IntPtr newType = SelectOutputType(ref nv12);
            if (newType == IntPtr.Zero)
            {
                _logger.LogWarning("MF 输出流变更后无可用输出类型");
                return false;
            }

            int hr = _setOutputType!(_transform, 0, newType, 0);
            if (hr < 0)
            {
                Marshal.Release(newType);
                _logger.LogWarning("MF 输出流变更后 SetOutputType 失败: 0x{Hr:X8}", hr);
                return false;
            }

            // 应用成功后替换缓存的输出类型引用
            if (_outputTypePtr != IntPtr.Zero)
                Marshal.Release(_outputTypePtr);
            _outputTypePtr = newType;

            TryReadOutputDimensions();
            QueryOutputStreamInfo();
            _logger.LogDebug("MF 输出流重协商完成: {W}x{H}, cbSize={Size}", _width, _height, _outputBufferSize);
            return true;
        }
        finally { _transformGate.Exit(); }
    }

    /// <summary>
    /// 枚举 MFT 可用输出类型（GetOutputAvailableType，slotIndex=11），返回 subtype 匹配 <paramref name="wantedSubtype"/> 的类型
    /// （调用方接管引用）；未匹配则返回首个可用类型；枚举不到返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    private IntPtr SelectOutputType(ref Guid wantedSubtype)
    {
        var getAvail = MfVTable.Get<IMFTransform_GetOutputAvailableType>(_transform, 11); // 绝对 14
        Guid mtSubtype = MFConstants.MF_MT_SUBTYPE;
        IntPtr first = IntPtr.Zero;
        for (uint i = 0; ; i++)
        {
            int hr = getAvail(_transform, 0, i, out IntPtr type);
            if (hr < 0 || type == IntPtr.Zero)
                break;

            // IMFMediaType.GetGUID = slotIndex 7（IMFAttributes 第 8 方法；MFDemuxer.ParseTracks 同槽已验证）
            var getGuid = MfVTable.Get<IMFMediaType_GetGuid>(type, 7);
            if (getGuid(type, ref mtSubtype, out Guid subtype) >= 0 && subtype == wantedSubtype)
            {
                if (first != IntPtr.Zero) Marshal.Release(first);
                return type; // 命中目标 subtype（NV12）
            }

            if (first == IntPtr.Zero) first = type; // 记住首个作回退
            else Marshal.Release(type);
        }
        return first;
    }

    /// <summary>
    /// 探测该解码 MFT 是否声明 <c>MF_SA_D3D11_AWARE</c>（支持 Direct3D 11 视频解码）。
    /// </summary>
    /// <remarks>
    /// 为何必须探测：<c>IMFTransform::ProcessMessage</c> 的契约允许 MFT 对**不认识的消息直接返回 S_OK
    /// 静默忽略**，因此「SET_D3D_MANAGER 返回 S_OK」**不能**证明 DXVA 已启用。MFT 自报的
    /// MF_SA_D3D11_AWARE 属性才是能力层面的权威判据。属性缺失（MF_E_ATTRIBUTENOTFOUND）即视为不支持。
    /// </remarks>
    private bool QueryD3D11Aware()
    {
        var getAttrs = MfVTable.Get<IMFTransform_GetAttributes>(_transform, 5); // 绝对 8
        int hr = getAttrs(_transform, out IntPtr attrs);
        if (hr < 0 || attrs == IntPtr.Zero)
        {
            _logger.LogWarning("[DXVA-DIAG] IMFTransform.GetAttributes 失败：HRESULT=0x{HR:X8}，无法确认 D3D11 能力", hr);
            return false;
        }
        try
        {
            Guid key = MFConstants.MF_SA_D3D11_AWARE;
            hr = MfVTable.Get<IMFAttributes_GetUINT32>(attrs, 4)(attrs, ref key, out uint aware);
            if (hr < 0)
            {
                _logger.LogWarning("[DXVA-DIAG] MFT 未设置 MF_SA_D3D11_AWARE（HRESULT=0x{HR:X8}）→ 不支持 D3D11 硬解", hr);
                return false;
            }
            _logger.LogInformation("[DXVA-DIAG] MF_SA_D3D11_AWARE={Aware}", aware);
            return aware != 0;
        }
        finally
        {
            Marshal.Release(attrs);
        }
    }

    /// <summary>查询输出流信息：MFT 是否自行提供输出 sample 与所需 buffer 大小。</summary>
    private void QueryOutputStreamInfo()
    {
        int hr = _getOutputStreamInfo!(_transform, 0, out MftOutputStreamInfo info);
        if (hr >= 0)
        {
            _mftProvidesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            _outputBufferSize = info.cbSize;
        }
        else
        {
            // 查询失败时保守回退：按 NV12 最大预估（重协商后会再查）
            _mftProvidesSamples = false;
            if (_outputBufferSize == 0 && _width > 0 && _height > 0)
                _outputBufferSize = (uint)(_width * _height * 3 / 2);
        }
    }

    /// <summary>
    /// 从输出媒体类型读取尺寸：FRAME_SIZE（uint64 = (w&lt;&lt;32)|h）为宏块对齐<b>编码尺寸</b>（如 1920x1088）；
    /// <b>显示尺寸</b>（1920x1080）从 MF_MT_MINIMUM_DISPLAY_APERTURE（MFVideoArea blob）取，缺失时回退编码尺寸。
    /// </summary>
    private void TryReadOutputDimensions()
    {
        if (_outputTypePtr == IntPtr.Zero)
            return;
        Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
        // IMFAttributes::GetUINT64 = slotIndex 5（运行时已验证）；mfplat 无 MFGetAttributeUINT64 导出（inline helper）
        int hr = MfVTable.Get<IMFMediaType_GetUINT64>(_outputTypePtr, 5)(_outputTypePtr, ref frameSizeKey, out ulong fs);
        if (hr >= 0 && fs != 0)
        {
            _codedWidth = (int)(fs >> 32);
            _codedHeight = (int)(fs & 0xFFFFFFFF);
            _width = _codedWidth;
            _height = _codedHeight;
        }

        // 显示孔径：MFVideoArea（16 字节）= MFOffset OffsetX(4: short fract + short value) ×2 + SIZE(cx4, cy4)
        // AOT 安全：用 GetAllocatedBlob（slot13，原生自分配 buffer），绕开 GetBlob 向调用方 buffer 大块写入在 AOT 崩溃的路径。
        Guid apertureKey = MFConstants.MF_MT_MINIMUM_DISPLAY_APERTURE;
        var kh = GCHandle.Alloc(apertureKey, GCHandleType.Pinned);
        IntPtr aperturePtr = kh.AddrOfPinnedObject();
        IntPtr blobPtr = IntPtr.Zero;
        try
        {
            var getAllocatedBlob = MfVTable.Get<IMFAttributes_GetAllocatedBlob>(_outputTypePtr, 13);
            if (getAllocatedBlob(_outputTypePtr, aperturePtr, out blobPtr, out uint blobSize) >= 0 && blobSize >= 16 && blobPtr != IntPtr.Zero)
            {
                // 原始字节留证：结构布局若理解有误，只有 hex 能对证（vtable/结构必须照抄头文件，不凭空猜测）
                var raw = new byte[16];
                Marshal.Copy(blobPtr, raw, 0, 16);
                _apertureBlobHex = Convert.ToHexString(raw);

                // MFOffset = { WORD fract; short value; }（fract 在低地址）；实际偏移 = value + fract/65536
                int offXValue = Marshal.ReadInt16(blobPtr, 2);
                ushort offXFract = (ushort)Marshal.ReadInt16(blobPtr, 0);
                int offYValue = Marshal.ReadInt16(blobPtr, 6);
                ushort offYFract = (ushort)Marshal.ReadInt16(blobPtr, 4);

                int cx = Marshal.ReadInt32(blobPtr, 8);
                int cy = Marshal.ReadInt32(blobPtr, 12);
                if (cx > 0 && cy > 0 && cx <= _codedWidth && cy <= _codedHeight)
                {
                    _width = cx;
                    _height = cy;
                    // 偏移必须落在编码帧内，否则视为异常并归零（宁可退回旧行为，也不越界读源 buffer）
                    _apertureOffsetX = offXValue >= 0 && offXValue + cx <= _codedWidth ? offXValue : 0;
                    _apertureOffsetY = offYValue >= 0 && offYValue + cy <= _codedHeight ? offYValue : 0;
                    if (offXFract != 0 || offYFract != 0)
                        _logger.LogWarning(
                            "[APERTURE] 显示孔径偏移含非零小数部分 fractX={FX} fractY={FY}（已按整数部分处理，可能产生半像素误差）",
                            offXFract, offYFract);
                }
            }
        }
        finally
        {
            // GetAllocatedBlob 用 CoTaskMem 分配，须 FreeCoTaskMem（非 FreeHGlobal）。
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
            kh.Free();
        }
    }
}
