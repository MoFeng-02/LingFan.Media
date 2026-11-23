using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

// MFDemuxer 的 Diagnostics 职责段（部分类，由 MFDemuxer.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Open/ReadPacket/ExtractPacket/Seek/Close/Dispose）在主文件；
// IMFSourceReader 同步 COM 调用，全部在所属线程执行（COM 线程亲和）。
internal sealed partial class MFDemuxer
{
    /// <summary>
    /// 构造带「D3D 设备管理器 + 允许硬件 MFT」的 SourceReader 创建属性（零拷贝直通前置）。
    /// </summary>
    /// <param name="hardwareRequested">是否成功表达了硬解意图（决定后续是否协商 NV12 直通）。</param>
    /// <returns>IMFAttributes*（调用方用后 <see cref="Marshal.Release"/>）；不可用返回 <see cref="IntPtr.Zero"/>。</returns>
    /// <remarks>
    /// <para>失败绝不抛异常：任一步不成即返回 <see cref="IntPtr.Zero"/>，
    /// <c>MFCreateSourceReaderFromURL</c> 以空属性创建 ⇒ 即「压缩裸流 + MFT 自解码」的默认行为。
    /// 这是「硬解优先、软解兜底」设计原则在打开阶段的落点。</para>
    /// <para><b>刻意不设</b> <c>MF_SOURCE_READER_ENABLE_(ADVANCED_)VIDEO_PROCESSING</c>：
    /// 插入 Video Processor MFT 会把样本落回系统内存，直接毁掉零拷贝。</para>
    /// <para><b>刻意不设</b> <c>MF_SOURCE_READER_PASSTHROUGH_MODE</c>：该模式强制系统内存样本语义，同样破坏零拷贝。</para>
    /// <para>同步（native 分类）：全为 COM 调用，无 I/O。</para>
    /// </remarks>
    private IntPtr TryCreateHardwareReaderAttributes(out bool hardwareRequested)
    {
        hardwareRequested = false;

        var provider = _dxgiManagerProvider;
        if (provider is null)
        {
            _logger.LogDebug("[MF-D3D] 未注入 DXGI 设备管理器提供者 → SourceReader 走默认（软解）路径");
            return IntPtr.Zero;
        }

        // provider 内部已做一次性闸门 + 全部失败告警；此处只判可用性
        IntPtr manager = provider.TryGetManager();
        if (manager == IntPtr.Zero)
            return IntPtr.Zero;

        if (MFInterop.MFCreateAttributes(out IntPtr attrs, 4) < 0 || attrs == IntPtr.Zero)
        {
            _logger.LogWarning("[MF-D3D] MFCreateAttributes 失败 → SourceReader 不挂 D3D 管理器，回落软解");
            return IntPtr.Zero;
        }

        try
        {
            // (1) MF_SOURCE_READER_D3D_MANAGER：IUnknown 属性，必须用 SetUnknown（slotIndex 24，mfobjects.h vtable 实物核验）
            var setUnknown = MfVTable.Get<IMFAttributes_SetUnknown>(attrs, 24);
            Guid d3dKey = MFConstants.MF_SOURCE_READER_D3D_MANAGER;
            int hr = setUnknown(attrs, ref d3dKey, manager);
            if (hr < 0)
            {
                _logger.LogWarning("[MF-D3D] SetUnknown(MF_SOURCE_READER_D3D_MANAGER) 失败 HRESULT=0x{HR:X8} → 回落软解", hr);
                Marshal.Release(attrs);
                return IntPtr.Zero;
            }

            // (2) MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS=1：不开则只用软件 MFT，挂了管理器也拿不到 DXGI 表面
            var setUInt32 = MfVTable.Get<IMFAttributes_SetUINT32>(attrs, 18);
            Guid hwKey = MFConstants.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            hr = setUInt32(attrs, ref hwKey, 1);
            if (hr < 0)
            {
                // 非致命：管理器已挂上，部分实现仍可能给出 DXVA 表面。记录后继续。
                _logger.LogWarning("[MF-D3D] SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS) 失败 HRESULT=0x{HR:X8}，继续尝试", hr);
            }

            // (3) MF_SOURCE_READER_DISABLE_DXVA=0：默认即 0，显式写入表达意图（防某些环境的策略默认值反转）
            Guid disableDxvaKey = MFConstants.MF_SOURCE_READER_DISABLE_DXVA;
            setUInt32(attrs, ref disableDxvaKey, 0);

            hardwareRequested = true;
            _logger.LogInformation("[MF-D3D] SourceReader 创建属性已就绪：D3D_MANAGER + ENABLE_HARDWARE_TRANSFORMS（目标：ReadSample 直出 DXGI 纹理）");
            return attrs;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MF-D3D] 构造 SourceReader 硬解属性异常 → 回落软解");
            Marshal.Release(attrs);
            hardwareRequested = false;
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 取证 SourceReader 为指定流实际建立的 <b>MFT 链</b>（零拷贝失效成因定位）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须做</b>：已把 <c>MF_SOURCE_READER_D3D_MANAGER</c> +
    /// <c>ENABLE_HARDWARE_TRANSFORMS</c> 挂上且全部返回 <c>S_OK</c>，但 ReadSample 出来的样本
    /// QI <c>IMFDXGIBuffer</c> 仍返 <c>E_NOINTERFACE</c>。设计原则「S_OK≠被接受：能力自报+行为副作用双判据」
    /// 要求此时<b>直接查证拓扑</b>而非继续猜。<c>IMFSourceReaderEx::GetTransformForStream</c>
    /// 是唯一能看穿 SourceReader 黑盒的官方接口。</para>
    /// <para><b>三种判决</b>：
    /// (1) 链上出现 <c>MFT_CATEGORY_VIDEO_PROCESSOR</c> ⇒ SourceReader 偷插了 VP 做转换，
    ///    它会把 DXGI 表面拉回系统内存 —— 零拷贝头号杀手，须调整输出类型协商避免触发；
    /// (2) 解码 MFT 的 <c>MF_SA_D3D11_AWARE</c>=0 ⇒ SourceReader 选中的是纯软件 MFT，D3D 管理器根本无处可用；
    /// (3) 解码 MFT aware=1 但 <c>PROVIDES_SAMPLES</c>=0 ⇒ MFT 没进 DXVA 分配模式
    ///    （即收到了 SET_D3D_MANAGER 却拒绝/回落），问题在驱动或 profile 协商。</para>
    /// <para>纯诊断、零副作用：只读属性、不发消息、不改类型。任何一步失败都只记 Debug 后静默返回，
    /// 绝不影响播放（诊断代码永远不该成为故障源）。所有 COM 引用 COM 配对释放。</para>
    /// <para>同步（native 分类）：全为 COM 调用，无 I/O。</para>
    /// </remarks>
    private void DiagnoseStreamTransformChain(IntPtr readerPtr, int streamIndex)
    {
        IntPtr readerEx = IntPtr.Zero;
        try
        {
            int hrQi = Marshal.QueryInterface(readerPtr, in MFConstants.IID_IMFSourceReaderEx, out readerEx);
            if (hrQi < 0 || readerEx == IntPtr.Zero)
            {
                _logger.LogDebug("[MFT-CHAIN] SourceReader 不支持 IMFSourceReaderEx（hr=0x{HR:X8}），跳过链路取证", hrQi);
                return;
            }

            var getTransform = MfVTable.Get<IMFSourceReaderEx_GetTransformForStream>(readerEx, 13); // 绝对槽 16
            int found = 0;
            bool sawVideoProcessor = false;
            bool sawVideoDecoder = false;
            bool decoderIsHardwareMft = false;

            // dwTransformIndex 从 0 递增枚举，越界返回 MF_E_INVALIDINDEX。上限 8 防御异常实现死循环。
            for (uint i = 0; i < 8; i++)
            {
                int hr = getTransform(readerEx, (uint)streamIndex, i, out Guid category, out IntPtr transform);
                if (hr < 0 || transform == IntPtr.Zero)
                    break;

                found++;
                try
                {
                    string categoryName =
                        category == MFConstants.MFT_CATEGORY_VIDEO_DECODER ? "视频解码器"
                        : category == MFConstants.MFT_CATEGORY_VIDEO_PROCESSOR ? "视频处理器(VP)"
                        : category == MFConstants.MFT_CATEGORY_VIDEO_EFFECT ? "视频特效"
                        : $"其他({category:B})";
                    if (category == MFConstants.MFT_CATEGORY_VIDEO_PROCESSOR)
                        sawVideoProcessor = true;

                    // (1) MF_SA_D3D11_AWARE：该 MFT 是否具备 D3D11 视频解码能力（GetAttributes = 绝对槽 8 → slotIndex 5）
                    //    (2) 同时取 MFT 身份（友好名 / HARDWARE_URL / CLSID）——区分「厂商硬件 MFT」与「微软软件 MFT」。
                    string awareText = "属性不可读";
                    string identityText = "身份不可读";
                    if (MfVTable.Get<IMFTransform_GetAttributes>(transform, 5)(transform, out IntPtr mftAttrs) >= 0
                        && mftAttrs != IntPtr.Zero)
                    {
                        try
                        {
                            Guid awareKey = MFConstants.MF_SA_D3D11_AWARE;
                            awareText = MfVTable.Get<IMFAttributes_GetUINT32>(mftAttrs, 4)(mftAttrs, ref awareKey, out uint aware) >= 0
                                ? (aware != 0 ? "D3D11_AWARE=1" : "D3D11_AWARE=0(纯软件)")
                                : "无 D3D11_AWARE 属性(纯软件)";

                            // 官方判据：MFT_ENUM_HARDWARE_URL_Attribute【存在即硬件 MFT】。
                            //    微软内置解码器（如 CMSH264DecoderMFT）同样 D3D11_AWARE=1、也会用 DXVA 硬解，
                            //    但它属于软件 MFT，输出统一落系统内存 —— 这正是「半 DXVA」最常见的成因。
                            string? hwUrl = TryGetAllocatedString(mftAttrs, MFConstants.MFT_ENUM_HARDWARE_URL_Attribute);
                            string? friendly = TryGetAllocatedString(mftAttrs, MFConstants.MFT_FRIENDLY_NAME_Attribute);
                            bool isHardwareMft = !string.IsNullOrEmpty(hwUrl);
                            if (category == MFConstants.MFT_CATEGORY_VIDEO_DECODER)
                            {
                                sawVideoDecoder = true;
                                decoderIsHardwareMft = isHardwareMft;
                            }

                            Guid clsidKey = MFConstants.MFT_TRANSFORM_CLSID_Attribute;
                            string clsidText =
                                MfVTable.Get<IMFAttributes_GetGUID>(mftAttrs, 7)(mftAttrs, ref clsidKey, out Guid mftClsid) >= 0
                                    ? mftClsid.ToString("B")
                                    : "(未暴露)";

                            identityText = $"{(isHardwareMft ? "【厂商硬件MFT】" : "【微软软件MFT】")}名称=\"{friendly ?? "-"}\" CLSID={clsidText}"
                                + (isHardwareMft ? $" HW_URL={hwUrl}" : string.Empty);
                        }
                        finally { Marshal.Release(mftAttrs); }
                    }

                    // (2) PROVIDES_SAMPLES：MFT 是否自分配输出 sample —— DXVA 纹理输出的必要条件
                    //    （GetOutputStreamInfo = 绝对槽 7 → slotIndex 4）
                    string allocText = "输出流信息不可读";
                    if (MfVTable.Get<IMFTransform_GetOutputStreamInfo>(transform, 4)(transform, 0, out MftOutputStreamInfo info) >= 0)
                    {
                        bool providesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                        allocText = providesSamples
                            ? $"MFT自分配输出sample=True(cbSize={info.cbSize})"
                            : $"MFT自分配输出sample=False ⇒ 由SourceReader分配【系统内存】(cbSize={info.cbSize})";
                    }

                    _logger.LogInformation("[MFT-CHAIN] 流 {Index} · MFT[{I}] 分类={Cat} | {Identity} | {Aware} | {Alloc}",
                        streamIndex, i, categoryName, identityText, awareText, allocText);
                }
                finally
                {
                    Marshal.Release(transform);
                }
            }

            if (found == 0)
            {
                _logger.LogWarning("[MFT-CHAIN] 流 {Index} 未枚举到任何 MFT —— SourceReader 可能直通未解码（协商未真正生效）", streamIndex);
            }
            else if (sawVideoProcessor)
            {
                _logger.LogWarning(
                    "[MFT-CHAIN] 流 {Index} 链上存在【视频处理器 VP】—— 零拷贝头号杀手：" +
                    "VP 会把解码器产出的 DXGI 表面拉回系统内存做格式/尺寸转换。" +
                    "成因=输出类型协商触发了转换，须让请求类型与解码器原生输出完全一致。", streamIndex);
            }
            else if (sawVideoDecoder && !decoderIsHardwareMft)
            {
                _logger.LogWarning(
                    "[MFT-CHAIN] 流 {Index} 链上无 VP，但解码器是【微软软件 MFT】（缺 MFT_ENUM_HARDWARE_URL）—— " +
                    "这就是「半 DXVA」的直接成因：软件 MFT 即便 MF_SA_D3D11_AWARE=1、也确实用 DXVA 在 GPU 上完成解码" +
                    "（故 Lock2D 的 pitch 带 GPU 对齐痕迹），但其输出 sample 一律回落系统内存，永远 QI 不到 IMFDXGIBuffer。" +
                    "真零拷贝要求 SourceReader 选中【厂商硬件 MFT】；若当前运行环境该 codec 未注册硬件 MFT，MF 路径无解，应让位 FFmpeg D3D11VA。",
                    streamIndex);
            }
            else
            {
                _logger.LogInformation(
                    "[MFT-CHAIN] 流 {Index} 链上共 {N} 个 MFT，未插入 VP，解码器={Kind} —— 若样本仍非 DXGI，嫌疑转向 SourceReader 封装层读回，" +
                    "可用 --no-fusion 关闭「解封装+解码一体」改走自管 MFT 对照",
                    streamIndex, found, decoderIsHardwareMft ? "厂商硬件MFT" : "非解码器/未知");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 诊断代码绝不能成为故障源
            _logger.LogDebug(ex, "[MFT-CHAIN] 链路取证异常（不影响播放）");
        }
        finally
        {
            if (readerEx != IntPtr.Zero)
                Marshal.Release(readerEx);
        }
    }
}
