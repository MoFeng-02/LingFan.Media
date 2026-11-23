using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

// MFDemuxer 的 Duration 职责段（部分类，由 MFDemuxer.Tracks.cs 再拆，编译期等价）。
// 轨道解析主段在 MFDemuxer.Tracks.cs；本段为容器时长探测，同步 COM 调用。
internal sealed partial class MFDemuxer
{
    /// <summary>
    /// 查询容器总时长（presentation descriptor 的 <see cref="MFConstants.MF_PD_DURATION"/>，UINT64/100ns 单位）。
    /// 通过 <c>IMFSourceReader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION)</c> 取得。
    /// </summary>
    /// <remarks>
    /// <para><b>为何必须显式查询</b>：MF 不会自动为源填充「时长」属性；若把 <see cref="_metadata"/> 硬编码为
    /// <c>TimeSpan.Zero</c>，<c>player.Duration</c> 恒为 0——以 <c>pos &gt;= duration-1</c> 判完成的播放逻辑
    /// 会「几秒假完成」（duration=0 时首轮即满足）。</para>
    /// <para><b>容错</b>：查询失败 / 属性缺失 / 非 VT_UI8 / 值为 0 时回落 <c>TimeSpan.Zero</c>，不抛异常，
    /// 仅损失时长信息，不影响解码播放。MF_PD_DURATION 为 VT_UI8 标量，输出 PROPVARIANT 无需 PropVariantClear。</para>
    /// <para><b>槽位</b>：<c>GetPresentationAttribute</c> = 绝对槽 12 → slotIndex 9（见 <see cref="MFComInterfaces"/> 头注释）。</para>
    /// </remarks>
    private TimeSpan QueryContainerDuration(IntPtr readerPtr)
    {
        try
        {
            var getPresentationAttribute = MfVTable.Get<IMFSourceReader_GetPresentationAttribute>(readerPtr, 9);
            Guid durationKey = MFConstants.MF_PD_DURATION;
            // 用 ref + 预初始化（对齐已验证可用的 SetCurrentPosition 同款封送，避免 out 结构体在该路径不稳）。
            var durVar = new MfPropVariant();
            int hr = getPresentationAttribute(readerPtr, MFConstants.MF_SOURCE_READER_MEDIASOURCE,
                ref durationKey, ref durVar);
            if (hr < 0)
            {
                _logger.LogWarning("GetPresentationAttribute(MF_PD_DURATION) 失败: HRESULT=0x{HR:X8}，时长回落 0", hr);
                return TimeSpan.Zero;
            }
            // MF_PD_DURATION 以 VT_UI8 存储 100ns 单位；MfPropVariant.hVal 与之同一 8 字节联合，直接读。
            if (durVar.vt != MfPropVariant.VT_UI8)
            {
                _logger.LogWarning("MF_PD_DURATION 返回非 VT_UI8(VT=0x{VT:X4})，时长回落 0", durVar.vt);
                return TimeSpan.Zero;
            }
            if (durVar.hVal <= 0)
                return TimeSpan.Zero;
            var duration = TimeSpan.FromTicks(durVar.hVal);
            _logger.LogInformation("MF 容器时长: {Duration}", duration);
            return duration;
        }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "查询 MF 容器时长异常，时长回落 0");
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 排空兜底时长探测：逐流 ReadSample 到 EOS，取所有样本时间戳最大值（100ns）作为容器时长。
        /// 仅在 <see cref="QueryContainerDuration"/> 取不到（MF_PD_DURATION 缺失）时调用，保证 Duration 恒有正确值。
        /// 探测后通过 <see cref="IMFSourceReader_SetCurrentPosition"/> 复位读取位置到开头，不影响后续正常播放。
        /// </summary>
        /// <remarks>
        /// <para>样本时间戳由 ReadSample 的 <c>pllTimestamp</c> 直接给出（100ns，可靠），故排空法推算的时长精确。</para>
        /// <para>COM 配对（失败路径不释放）：ReadSample 失败时 <c>*ppSample</c> 语义不可判定，依「泄漏优于误释放」一律不 Release；
        /// 仅成功路径（hr≥0 且 samplePtr≠0）释放样本，与 <see cref="ExtractPacket"/> 同构。</para>
        /// </remarks>
        private TimeSpan ProbeDurationByDraining(IntPtr readerPtr)
        {
            try
            {
                long maxTicks = 0;
                foreach (int s in _selectedStreamIndices)
                {
                    while (true)
                    {
                        int hr = _readSample!(readerPtr, (uint)s, 0,
                            out _, out int streamFlags, out long timestamp, out IntPtr samplePtr);
                        if (hr < 0)
                            break; // 失败路径不释放 *ppSample（泄漏优于误释放）
                        bool eos = (streamFlags & MFConstants.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                        if (samplePtr != IntPtr.Zero)
                            InteropTrace.ReleaseComPtr(samplePtr, "ProbeDurationByDraining:samplePtr");
                        if (eos)
                            break;
                        if (timestamp > maxTicks)
                            maxTicks = timestamp;
                    }
                }

                // 复位读取位置到开头，避免影响后续播放（与 SeekAsync(0) 同效）。
                Guid timeFormat = Guid.Empty;
                var pos = new MfPropVariant { vt = MfPropVariant.VT_I8, hVal = 0 };
                MfVTable.Get<IMFSourceReader_SetCurrentPosition>(readerPtr, 5)(readerPtr, ref timeFormat, ref pos);

                if (maxTicks <= 0)
                    return TimeSpan.Zero;
                var dur = TimeSpan.FromTicks(maxTicks);
                _logger.LogInformation("MF 排空探测时长: {Duration}", dur);
                return dur;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "排空探测时长异常，时长回落 0");
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 末尾定位探测：把读取位置夹到流末尾，逐流读少量末帧取最大样本时间戳（100ns）作为容器时长。
        /// 用 IMFSourceReader 索引跳转（MP4/MKV 等有索引容器为 O(log n)）替代旧「整段排空到 EOS」，
        /// 将 OpenAsync 内推算时长的同步阻塞降到近乎瞬时，消灭启动白屏/黑屏后的长延迟。
        /// 探测后复位读取位置到开头，不影响后续正常播放。
        /// </summary>
        /// <remarks>
        /// <para>样本时间戳由 ReadSample 的 <c>pllTimestamp</c> 直接给出（100ns，可靠），与旧排空法一致。</para>
        /// <para>COM 配对（失败路径不释放）：ReadSample 失败时 <c>*ppSample</c> 语义不可判定，依「泄漏优于误释放」一律不 Release；
        /// 仅成功路径（hr≥0 且 samplePtr≠0）释放样本，与 <see cref="ProbeDurationByDraining"/> 同构。</para>
        /// <para>读上限 16 帧覆盖 B 帧重排导致的末段 PTS 非单调，取最大 PTS（与旧排空法语义一致）。</para>
        /// </remarks>
        private TimeSpan ProbeDurationByEndSeek(IntPtr readerPtr)
        {
            try
            {
                Guid timeFormat = Guid.Empty;

                // 预热：先对任意已选流读一帧，建立 demuxer 可读状态。
                // 部分 MF 实现要求先 ReadSample 后才能 SetCurrentPosition（否则返回失败码）。
                // 该 sample 直接释放，不影响后续探测。
                if (_selectedStreamIndices.Length > 0)
                {
                    try
                    {
                        _readSample!(readerPtr, (uint)_selectedStreamIndices[0], 0,
                            out _, out _, out long _, out IntPtr warmSample);
                        if (warmSample != IntPtr.Zero)
                            InteropTrace.ReleaseComPtr(warmSample, "ProbeDurationByEndSeek:warmSample");
                    }
                    catch { /* 预热失败不影响主路径 */ }
                }

                // GUID_NULL 时间格式 = 100ns 单位；极大时间戳触发 SourceReader 夹到末样本。
                // 必须检查 hr：seek 失败（如本源返回负码）若静默忽略，
                // 后续从头读少量帧（如 16/30fps 即约 0.5s）会伪装成真实时长。失败时立即返回 Zero，
                // 由 OpenCore 回退 ProbeDurationByDraining 拿正确时长（代价是同步阻塞）。
                var endPos = new MfPropVariant { vt = MfPropVariant.VT_I8, hVal = long.MaxValue };
                int seekHr = MfVTable.Get<IMFSourceReader_SetCurrentPosition>(readerPtr, 5)(readerPtr, ref timeFormat, ref endPos);
                if (seekHr < 0)
                {
                    _logger.LogDebug("末尾定位探测: SetCurrentPosition 失败 HRESULT=0x{HR:X8}，回退整段排空", seekHr);
                    return TimeSpan.Zero;
                }

                long maxTicks = 0;
                foreach (int s in _selectedStreamIndices)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int hr = _readSample!(readerPtr, (uint)s, 0,
                            out _, out int streamFlags, out long timestamp, out IntPtr samplePtr);
                        if (hr < 0)
                            break; // 失败路径不释放 *ppSample（泄漏优于误释放）
                        bool eos = (streamFlags & MFConstants.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                        if (samplePtr != IntPtr.Zero)
                            InteropTrace.ReleaseComPtr(samplePtr, "ProbeDurationByEndSeek:samplePtr");
                        if (timestamp > maxTicks)
                            maxTicks = timestamp;
                        if (eos)
                            break;
                    }
                }

                // 复位读取位置到开头，避免影响后续播放（与 SeekAsync(0) 同效）。
                var resetPos = new MfPropVariant { vt = MfPropVariant.VT_I8, hVal = 0 };
                MfVTable.Get<IMFSourceReader_SetCurrentPosition>(readerPtr, 5)(readerPtr, ref timeFormat, ref resetPos);

                if (maxTicks <= 0)
                {
                    _logger.LogDebug("末尾定位探测: 未取到末帧时间戳，回退整段排空");
                    return TimeSpan.Zero;
                }
                var dur = TimeSpan.FromTicks(maxTicks);
                _logger.LogInformation("MF 末尾定位探测时长: {Duration}", dur);
                return dur;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "末尾定位探测时长异常，回落排空兜底");
                return TimeSpan.Zero;
            }
        }
}
