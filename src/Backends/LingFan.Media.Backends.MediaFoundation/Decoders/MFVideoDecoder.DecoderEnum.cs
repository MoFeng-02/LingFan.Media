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

// MFVideoDecoder 的 DecoderEnum 职责段（部分类，由 MFVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeAsync/ProcessOutputOnce/FlushAsync/Reset/Dispose）在主文件；
// IMFTransform 同步 COM 调用，全部在所属线程执行（COM 线程亲和）。
internal sealed partial class MFVideoDecoder
{
    /// <summary>临时诊断：枚举全部已注册视频解码器（不过滤 subtype），打印每个 CLSID，确认 HEVC Store 解码器是否可枚举。</summary>
    private void DumpAllVideoDecoders()
    {
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER;
        int hr = MFInterop.MFTEnumExRaw(
            ref category,
            MFConstants.MFT_ENUM_FLAG_ALL | MFConstants.MFT_ENUM_FLAG_UNTRUSTED_STOREMFT,
            IntPtr.Zero, IntPtr.Zero,
            out IntPtr arr, out uint count);
        _logger.LogWarning("[DECODER-DIAG] DumpAllVideoDecoders MFTEnumEx(ALL|STORE, 不过滤) hr=0x{Hr:X8} count={Count}", hr & 0xFFFFFFFF, count);
        if (hr >= 0 && arr != IntPtr.Zero && count > 0)
        {
            Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
            for (uint i = 0; i < count; i++)
            {
                IntPtr act = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
                if (act == IntPtr.Zero) continue;
                try
                {
                    var getGuid = MfVTable.Get<IMFAttributes_GetGUID>(act, 7);
                    int hr2 = getGuid(act, ref clsidKey, out Guid g);
                    _logger.LogWarning("[DECODER-DIAG]   [{I}] hr2=0x{Hr2:X8} CLSID={G:B}", i, hr2 & 0xFFFFFFFF, g);
                }
                finally { Marshal.Release(act); }
            }
            MFInterop.CoTaskMemFree(arr);
        }
    }

    /// <summary>经 <see cref="MFInterop.MFTEnumEx"/> 找到可实例化为 <c>IMFTransform</c> 的解码 MFT CLSID。</summary>
    /// <remarks>
    /// HEVC 视频扩展（Store 安装）注册为<strong>异步 Store MFT</strong>，旧 <c>MFTEnum</c> 枚举不到，
    /// 必须用 <c>MFTEnumEx</c> 并包含 <c>ASYNCMFT | HARDWARE | UNTRUSTED_STOREMFT</c>。
    /// 运行时验证：H264 → CLSID_MSH264DecoderMFT (62ce7e72-4c71-4d20-b15d-452831a87d9d)。
    /// </remarks>
    /// <summary>实例化解码 MFT，返回已激活的 <c>IMFTransform*</c>（零表示未找到）。
    /// 策略：(1) 已知 stock CLSID + <c>CoCreateInstance</c>（H264 等同步 stock MFT）；
    /// (2) 失败则 <c>MFTEnumEx</c> + <c>IMFActivate::ActivateObject</c>（覆盖 Store/异步 HEVC MFT——
    /// 其 <c>IMFActivate</c> 不设 <c>MFT_TRANSFORM_CLSID_Attribute</c>、亦不可 CoCreateInstance，必须 ActivateObject）。</summary>
    private IntPtr FindDecoderTransform(Guid inputSubtype)
    {
        // 1) 已知 stock CLSID + CoCreateInstance（H264 stock MFT 等同步解码器）
        IntPtr t = CoCreateStockDecoder(inputSubtype);
        if (t != IntPtr.Zero) return t;

        // 2) MFTEnumEx + ActivateObject（Store/异步 HEVC MFT 等不设 CLSID 属性、不可 CoCreateInstance）。
        //    返回 lastActivateHr/lastCount：枚举到候选但激活失败（典型 Store HEVC MFT 拒绝未打包桌面进程）。
        int lastActivateHr = 0;
        uint lastCount = 0;
        t = ActivateViaMFTEnumEx(inputSubtype,
            MFConstants.MFT_ENUM_FLAG_ALL | MFConstants.MFT_ENUM_FLAG_UNTRUSTED_STOREMFT,
            out lastActivateHr, out lastCount);
        if (t != IntPtr.Zero) return t;
        t = ActivateViaMFTEnumEx(inputSubtype, MFConstants.MFT_ENUM_FLAG_ALL, out lastActivateHr, out lastCount);
        if (t != IntPtr.Zero) return t;

        // 枚举到候选但无法在当前进程激活（Store 编解码器扩展需打包应用身份）
        if (lastCount > 0)
        {
            throw new PlatformNotSupportedException(
                $"找到 {_codecName(inputSubtype)} 解码 MFT（共 {lastCount} 个候选）但无法在当前进程激活" +
                $"（ActivateObject hr=0x{lastActivateHr & 0xFFFFFFFF:X8}）。Store 安装的编解码器扩展" +
                "（如 Microsoft HEVC 视频扩展）通常只供打包应用（系统播放器/照片）使用，未打包桌面进程直接激活会被拒绝。" +
                "建议：HEVC 解码改走 ffmpeg 后端；或待本库异步 MFT 驱动 + 打包支持落地后再试。");
        }
        return IntPtr.Zero;
    }

    /// <summary>经 <c>MFTEnumEx</c> 找到匹配 subtype 的 <c>IMFActivate</c>，调用 <c>ActivateObject</c> 得到 <c>IMFTransform*</c>。
    /// 这是 MFTEnumEx 结果的标准激活方式——Store/异步 MFT 不设 CLSID 属性、不可 CoCreateInstance。</summary>
    private IntPtr ActivateViaMFTEnumEx(Guid inputSubtype, uint flags, out int lastActivateHr, out uint lastCount)
    {
        lastActivateHr = 0;
        lastCount = 0;
        MFInterop.MftRegisterTypeInfo input = new()
        {
            guidMajorType = MFConstants.MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER;
        int hr = MFInterop.MFTEnumEx(
            ref category, flags, ref input,
            IntPtr.Zero, out IntPtr arr, out lastCount);
        if (DiagEnabled)
            Console.Error.WriteLine($"[DECODER-DIAG] ActivateViaMFTEnumEx flags=0x{flags:X} hr=0x{hr & 0xFFFFFFFF:X8} count={lastCount}");
        if (hr < 0 || arr == IntPtr.Zero || lastCount == 0)
            return IntPtr.Zero;

        IntPtr transform = IntPtr.Zero;
        try
        {
            for (uint i = 0; i < lastCount && transform == IntPtr.Zero; i++)
            {
                IntPtr activate = Marshal.ReadIntPtr(arr, (int)(i * IntPtr.Size));
                if (activate == IntPtr.Zero) continue;
                try
                {
                    var activateObject = MfVTable.Get<IMFActivate_ActivateObject>(activate, 28); // 绝对槽 31（IUnknown3+IMFAttributes28+ActivateObject）
                    Guid iid = MFConstants.IID_IMFTransform;
                    int hr2 = activateObject(activate, ref iid, out IntPtr pTransform);
                    if (DiagEnabled)
                        Console.Error.WriteLine($"[DECODER-DIAG]   [{i}] ActivateObject hr=0x{hr2 & 0xFFFFFFFF:X8} pTransform={(hr2 >= 0 ? pTransform.ToString("X") : "-")}");
                    if (hr2 < 0 && lastActivateHr == 0)
                        lastActivateHr = hr2;   // 记录首个激活失败 hr（供异常消息提示）
                    if (hr2 >= 0 && pTransform != IntPtr.Zero)
                    {
                        transform = pTransform;
                        _decoderActivate = activate;   // 保留 activate 上下文（Store 激活依赖它），Dispose 时释放
                        activate = IntPtr.Zero;        // 所有权已转移，本循环不再 Release
                        if (DiagEnabled) LogTransformDiag(transform, i);
                    }
                }
                finally
                {
                    if (activate != IntPtr.Zero) Marshal.Release(activate);
                }
            }
        }
        finally
        {
            MFInterop.CoTaskMemFree(arr);
        }
        return transform;
    }

    /// <summary>已知 stock CLSID + <c>CoCreateInstance</c>（H264 等同步 stock MFT）。返回 <c>IMFTransform*</c> 或零。</summary>
    private IntPtr CoCreateStockDecoder(Guid inputSubtype)
    {
        Guid clsid = EnumDecoderClsidEx(inputSubtype,
            MFConstants.MFT_ENUM_FLAG_ALL | MFConstants.MFT_ENUM_FLAG_UNTRUSTED_STOREMFT);
        if (clsid == Guid.Empty)
            clsid = EnumDecoderClsidEx(inputSubtype, MFConstants.MFT_ENUM_FLAG_ALL);
        // 兜底旧 MFTEnum（旧 API 对 H264 stock MFT 仍有效）
        if (clsid == Guid.Empty)
            clsid = EnumDecoderClsidLegacy(inputSubtype);
        if (clsid == Guid.Empty)
            return IntPtr.Zero;
        Guid iid = MFConstants.IID_IMFTransform;
        int hr = MFInterop.CoCreateInstance(ref clsid, IntPtr.Zero, MFInterop.CLSCTX_ALL, ref iid, out IntPtr t);
        if (hr < 0)
        {
            Console.Error.WriteLine($"[DECODER-DIAG] CoCreateInstance({clsid:B}) hr=0x{hr & 0xFFFFFFFF:X8}");
            return IntPtr.Zero;
        }
        _logger.LogInformation("[DECODER-ENUM] 选中 {Codec} 解码 MFT CLSID={Clsid:B}（CoCreateInstance）", _codecName(inputSubtype), clsid);
        return t;
    }

    /// <summary>把输入 subtype 映射为可读编解码器名（仅日志用）。</summary>
    private static string _codecName(Guid subtype)
    {
        if (subtype == MFConstants.MFVideoFormat_H264) return "H264";
        if (subtype == MFConstants.MFVideoFormat_HEVC || subtype == MFConstants.MFVideoFormat_HEVC_ES) return "HEVC";
        return subtype.ToString("B");
    }

    /// <summary>临时诊断：打印 transform 关键属性（异步标志、MF_SA_D3D11_AWARE、CLSID），确认 HEVC 解码器能力。</summary>
    private void LogTransformDiag(IntPtr transform, uint index)
    {
        var getAttrs = MfVTable.Get<IMFTransform_GetAttributes>(transform, 5);
        int hr = getAttrs(transform, out IntPtr attrs);
        if (hr < 0 || attrs == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] GetAttributes hr=0x{hr & 0xFFFFFFFF:X8}");
            return;
        }
        try
        {
            var getUint = MfVTable.Get<IMFAttributes_GetUINT32>(attrs, 4);
            Guid flagsKey = MFConstants.MF_TRANSFORM_FLAGS_Attribute;
            int hr2 = getUint(attrs, ref flagsKey, out uint flagsVal);
            bool isAsync = (hr2 >= 0) && ((flagsVal & 0x1) != 0);
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] FLAGS hr=0x{hr2 & 0xFFFFFFFF:X8} val=0x{flagsVal:X8} async={isAsync}");
            Guid awareKey = MFConstants.MF_SA_D3D11_AWARE;
            int hr3 = getUint(attrs, ref awareKey, out uint aware);
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] MF_SA_D3D11_AWARE hr=0x{hr3 & 0xFFFFFFFF:X8} val={aware}");
            var getGuid = MfVTable.Get<IMFAttributes_GetGUID>(attrs, 7);
            Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
            int hr4 = getGuid(attrs, ref clsidKey, out Guid g);
            Console.Error.WriteLine($"[DECODER-DIAG]   [{index}] CLSID(from transform) hr=0x{hr4 & 0xFFFFFFFF:X8} {g:B}");
        }
        finally
        {
            Marshal.Release(attrs);
        }
    }

    /// <summary>使用 <c>MFTEnumEx</c> 枚举给定输入 subtype 的解码 MFT，返回首个有效 CLSID（无则 <see cref="Guid.Empty"/>）。</summary>
    private static Guid EnumDecoderClsidEx(Guid inputSubtype, uint flags)
    {
        MFInterop.MftRegisterTypeInfo input = new()
        {
            guidMajorType = MFConstants.MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER; // 静态只读字段不可作 ref 实参（CS0199）
        int hr = MFInterop.MFTEnumEx(
            ref category, flags, ref input,
            IntPtr.Zero, out IntPtr pActivateArray, out uint count);
        Console.Error.WriteLine($"[DECODER-DIAG] EnumDecoderClsidEx flags=0x{flags:X} hr=0x{hr & 0xFFFFFFFF:X8} count={count}");
        if (hr < 0 || pActivateArray == IntPtr.Zero || count == 0)
            return Guid.Empty;

        Guid result = Guid.Empty;
        Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                IntPtr activate = Marshal.ReadIntPtr(pActivateArray, (int)(i * IntPtr.Size));
                if (activate == IntPtr.Zero)
                    continue;
                try
                {
                    if (result == Guid.Empty)
                    {
                        var getGuid = MfVTable.Get<IMFAttributes_GetGUID>(activate, 7); // IMFAttributes::GetGUID
                        int hr2 = getGuid(activate, ref clsidKey, out Guid candidate);
                        if (hr2 >= 0 && candidate != Guid.Empty)
                            result = candidate;
                    }
                }
                finally
                {
                    Marshal.Release(activate);
                }
            }
        }
        finally
        {
            MFInterop.CoTaskMemFree(pActivateArray);
        }
        return result;
    }

    /// <summary>使用旧 <c>MFTEnum</c> 兜底枚举给定输入 subtype 的解码 MFT（仅同步 MFT）。</summary>
    private static Guid EnumDecoderClsidLegacy(Guid inputSubtype)
    {
        MFInterop.MftRegisterTypeInfo input = new()
        {
            guidMajorType = MFConstants.MFMediaType_Video,
            guidSubtype = inputSubtype
        };
        Guid category = MFConstants.MFT_CATEGORY_VIDEO_DECODER;
        int found = MFInterop.MFTEnum(
            ref category, 0, ref input,
            IntPtr.Zero, IntPtr.Zero, out IntPtr pClsidArray, out uint count);
        // HRESULT 语义：S_OK=0 即成功——绝不能写 "<= 0"（会让成功被当成失败，制造"无注册 MFT"假象）
        if (found < 0 || pClsidArray == IntPtr.Zero || count == 0)
            return Guid.Empty;
        try
        {
            // CLSID 数组元素为 16 字节 GUID
            for (uint i = 0; i < count; i++)
            {
                IntPtr p = IntPtr.Add(pClsidArray, (int)(i * 16));
                Guid candidate = Marshal.PtrToStructure<Guid>(p);
                if (candidate != Guid.Empty)
                    return candidate;
            }
            return Guid.Empty;
        }
        finally
        {
            MFInterop.CoTaskMemFree(pClsidArray);
        }
    }
}
