using System.Runtime.Versioning;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Demuxer;

// MFDemuxer 的 StreamConfig 职责段（部分类，由 MFDemuxer.Tracks.cs 再拆，编译期等价）。
// 轨道解析主段在 MFDemuxer.Tracks.cs；本段为音视频流输出格式配置，同步 COM 调用。
internal sealed partial class MFDemuxer
{
    /// <summary>
    /// 把指定视频流协商为**解码后 NV12** 输出（SourceReader 自带硬解 + DXGI 出样）。
    /// </summary>
    /// <param name="readerPtr">IMFSourceReader*。</param>
    /// <param name="streamIndex">MF 流索引。</param>
    /// <param name="width">进：原生宽；出：MF 实测采纳的输出宽。</param>
    /// <param name="height">进：原生高；出：MF 实测采纳的输出高。</param>
    /// <param name="stride">回读的行跨度（字节）；缺失时为 0，调用方按紧凑 <paramref name="width"/> 处理。</param>
    /// <returns>协商成功（该流自此输出已解码 NV12）返回 <see langword="true"/>；失败返回 <see langword="false"/> 且不改变原有行为。</returns>
    /// <remarks>
    /// <para>与 <see cref="ConfigureAudioStreamToPcm"/> 完全同构（MF 音频早已是这套「demuxer 内解码 + decoder 直通」模式），
    /// 差别只在目标子类型是 NV12 而非 PCM。走已验证的成熟路子，风险最低。</para>
    /// <para>只设 MAJOR_TYPE + SUBTYPE 的<b>部分类型</b>，其余字段留空由 SourceReader 按源填充——
    /// 显式写死尺寸/帧率反而会让硬件 MFT 拒绝协商（MF_E_INVALIDMEDIATYPE）。</para>
    /// <para>协商失败绝不抛异常：返回 false 后该流继续输出压缩裸流，下游 <c>MFVideoDecoder</c> 自解码
    /// （硬解优先、软解兜底）。</para>
    /// <para>同步（native 分类）：全为 COM 调用，无 I/O。</para>
    /// </remarks>
    private bool TryConfigureVideoStreamToNv12(IntPtr readerPtr, int streamIndex, ref int width, ref int height, out int stride)
    {
        stride = 0;

        // 未选中的流不会被建管线，SetCurrentMediaType 无从协商（与音频同）。SetStreamSelection = 绝对槽 4 → slotIndex 1。
        int hr = MfVTable.Get<IMFSourceReader_SetStreamSelection>(readerPtr, 1)(readerPtr, (uint)streamIndex, true);
        if (hr < 0)
        {
            _logger.LogWarning("[MF-D3D] 视频流 {Index} SetStreamSelection 失败 HRESULT=0x{HR:X8}，跳过 NV12 协商", streamIndex, hr);
            return false;
        }

        if (MFInterop.MFCreateMediaType(out IntPtr nv12Type) < 0 || nv12Type == IntPtr.Zero)
        {
            _logger.LogWarning("[MF-D3D] 视频流 {Index} MFCreateMediaType 失败，跳过 NV12 协商", streamIndex);
            return false;
        }

        try
        {
            var setGuid = MfVTable.Get<IMFAttributes_SetGUID>(nv12Type, 21); // SetGUID = slotIndex 21
            Guid majorKey = MFConstants.MF_MT_MAJOR_TYPE;
            Guid majorVal = MFConstants.MFMediaType_Video;
            Guid subKey = MFConstants.MF_MT_SUBTYPE;
            Guid subVal = MFConstants.MFVideoFormat_NV12;
            if (setGuid(nv12Type, ref majorKey, ref majorVal) < 0 || setGuid(nv12Type, ref subKey, ref subVal) < 0)
            {
                _logger.LogWarning("[MF-D3D] 视频流 {Index} 构造 NV12 媒体类型失败，跳过协商", streamIndex);
                return false;
            }

            // SetCurrentMediaType = 绝对槽 7 → slotIndex 4；第二参数为保留 DWORD*，必须 NULL。
            hr = MfVTable.Get<IMFSourceReader_SetCurrentMediaType>(readerPtr, 4)(readerPtr, (uint)streamIndex, IntPtr.Zero, nv12Type);
            if (hr < 0)
            {
                // 常见于 SourceReader 找不到能吃该编码的解码 MFT（如桌面版 HEVC 未装扩展）。
                // 这不是错误路径——回落压缩裸流后仍可由 MFVideoDecoder / 回退中间件的 FFmpeg 接手。
                _logger.LogWarning("[MF-D3D] 视频流 {Index} SetCurrentMediaType(NV12) 失败 HRESULT=0x{HR:X8} → " +
                    "回落「压缩裸流 + MFVideoDecoder 自解码」路径", streamIndex, hr);
                return false;
            }
        }
        finally
        {
            Marshal.Release(nv12Type);
        }

        // 回读 MF 实际采纳的输出类型：硬件 MFT 常把显示尺寸对齐到编码尺寸（如 1080→1088），
        // 且会填 MF_MT_DEFAULT_STRIDE。这两项直接决定 CPU 回落路径的逐行拷贝是否错位。
        // GetCurrentMediaType = 绝对槽 6 → slotIndex 3。
        hr = MfVTable.Get<IMFSourceReader_GetCurrentMediaType>(readerPtr, 3)(readerPtr, (uint)streamIndex, out IntPtr actualType);
        if (hr < 0 || actualType == IntPtr.Zero)
        {
            // 协商本身已成功，只是回读失败：沿用原生尺寸继续（stride=0 ⇒ 按紧凑处理）
            _logger.LogWarning("[MF-D3D] 视频流 {Index} GetCurrentMediaType 失败 HRESULT=0x{HR:X8}，沿用原生尺寸 {W}x{H}",
                streamIndex, hr, width, height);
            return true;
        }

        try
        {
            var getUINT64 = MfVTable.Get<IMFMediaType_GetUINT64>(actualType, 5);
            var getUINT32 = MfVTable.Get<IMFMediaType_GetUINT32>(actualType, 4);

            Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
            if (getUINT64(actualType, ref frameSizeKey, out ulong frameSize) >= 0 && frameSize != 0)
            {
                int w = (int)(frameSize >> 32);
                int h = (int)(frameSize & 0xFFFFFFFF);
                if (w > 0 && h > 0) { width = w; height = h; }
            }

            Guid strideKey = MFConstants.MF_MT_DEFAULT_STRIDE;
            if (getUINT32(actualType, ref strideKey, out uint rawStride) >= 0)
            {
                // UINT32 里存的是 INT32：负值表示 bottom-up 布局。NV12 下罕见，取绝对值并留证。
                int s = (int)rawStride;
                if (s < 0)
                {
                    _logger.LogWarning("[MF-D3D] 视频流 {Index} DefaultStride={S}（负=bottom-up），按绝对值处理", streamIndex, s);
                    s = -s;
                }
                stride = s;
            }

            _logger.LogInformation(
                "[MF-D3D] 视频流 {Index} 已协商为 NV12 解码输出：{W}x{H} stride={Stride}（0=紧凑）" +
                " → 本 demuxer 进入「解封装+解码一体」模式，MFVideoDecoder 转直通",
                streamIndex, width, height, stride);
            return true;
        }
        finally
        {
            Marshal.Release(actualType);
        }
    }

    /// <summary>
    /// 把指定音频流协商为**解码后 PCM** 输出，并回读 MF 实测采纳的格式。
    /// </summary>
    /// <param name="readerPtr">IMFSourceReader*。</param>
    /// <param name="streamIndex">MF 流索引。</param>
    /// <param name="nativeSampleRate">原生（压缩）媒体类型上的采样率，协商失败时作为回落值。</param>
    /// <param name="nativeChannels">原生声道数，协商失败时作为回落值。</param>
    /// <param name="nativeBits">原生位深，协商失败时作为回落值。</param>
    /// <returns>SourceReader 输出侧实测的 PCM 参数。</returns>
    /// <remarks>
    /// <para>MSDN 推荐做法：只设 MAJOR_TYPE=Audio + SUBTYPE=PCM 的<b>部分类型</b>，其余字段留空，
    /// SourceReader 会自动加载对应解码器（AAC/MP3 Decoder MFT）+ 必要的重采样器，并按源填充剩余字段。</para>
    /// <para>本实现额外显式要求 16bit：下游 <c>AudioFrame</c>/WASAPI 按 S16 切分字节，
    /// 若个别源协商出 32bit 会导致帧数计算错误。若 MFT 拒绝该约束（hr&lt;0），
    /// 剔除 BITS_PER_SAMPLE 后以纯部分类型重试，最大化兼容性。</para>
    /// </remarks>
    private (int SampleRate, int Channels, int BitsPerSample) ConfigureAudioStreamToPcm(
        IntPtr readerPtr, int streamIndex, uint nativeSampleRate, uint nativeChannels, uint nativeBits)
    {
        var fallback = ((int)nativeSampleRate, (int)nativeChannels, (int)nativeBits);

        // 未选中的流不会被 SourceReader 建管线，SetCurrentMediaType 亦无从协商——先行选中（幂等，
        // OpenCore 稍后仍会统一再选一次）。SetStreamSelection = 绝对槽 4 → slotIndex 1。
        int hr = MfVTable.Get<IMFSourceReader_SetStreamSelection>(readerPtr, 1)(readerPtr, (uint)streamIndex, true);
        if (hr < 0)
        {
            _logger.LogWarning("音频流 {Index} SetStreamSelection 失败: HRESULT=0x{HR:X8}，跳过 PCM 协商", streamIndex, hr);
            return fallback;
        }

        if (MFInterop.MFCreateMediaType(out IntPtr pcmType) < 0 || pcmType == IntPtr.Zero)
        {
            _logger.LogWarning("音频流 {Index} MFCreateMediaType 失败，跳过 PCM 协商（将输出压缩裸流）", streamIndex);
            return fallback;
        }

        try
        {
            // SetGUID = slotIndex 21（已运行时验证，见 MFComInterfaces 槽位表）
            var setGuid = MfVTable.Get<IMFAttributes_SetGUID>(pcmType, 21);
            Guid majorKey = MFConstants.MF_MT_MAJOR_TYPE;
            Guid majorVal = MFConstants.MFMediaType_Audio;
            Guid subKey = MFConstants.MF_MT_SUBTYPE;
            Guid subVal = MFConstants.MFAudioFormat_PCM;
            if (setGuid(pcmType, ref majorKey, ref majorVal) < 0 || setGuid(pcmType, ref subKey, ref subVal) < 0)
            {
                _logger.LogWarning("音频流 {Index} 构造 PCM 媒体类型失败，跳过 PCM 协商", streamIndex);
                return fallback;
            }

            Guid bitsKey = MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE;
            MfVTable.Get<IMFAttributes_SetUINT32>(pcmType, 18)(pcmType, ref bitsKey, 16);

            // SetCurrentMediaType = 绝对槽 7 → slotIndex 4（mfreadwrite.h:386 核验）；第二参数为保留 DWORD*，必须 NULL。
            var setCurrent = MfVTable.Get<IMFSourceReader_SetCurrentMediaType>(readerPtr, 4);
            hr = setCurrent(readerPtr, (uint)streamIndex, IntPtr.Zero, pcmType);
            if (hr < 0)
            {
                // 退一步：剔除 16bit 约束（DeleteItem = slotIndex 16），以纯部分类型再试
                MfVTable.Get<IMFAttributes_DeleteItem>(pcmType, 16)(pcmType, ref bitsKey);
                hr = setCurrent(readerPtr, (uint)streamIndex, IntPtr.Zero, pcmType);
            }
            if (hr < 0)
            {
                _logger.LogError("音频流 {Index} SetCurrentMediaType(PCM) 失败: HRESULT=0x{HR:X8}。" +
                    "SourceReader 将继续输出压缩裸流，音频输出会异常。", streamIndex, hr);
                return fallback;
            }
        }
        finally
        {
            Marshal.Release(pcmType);
        }

        // 回读 MF 实际采纳的输出类型（采样率/声道通常沿用源，位深为 16）。
        // GetCurrentMediaType = 绝对槽 6 → slotIndex 3。
        hr = MfVTable.Get<IMFSourceReader_GetCurrentMediaType>(readerPtr, 3)(readerPtr, (uint)streamIndex, out IntPtr actualType);
        if (hr < 0 || actualType == IntPtr.Zero)
        {
            _logger.LogWarning("音频流 {Index} GetCurrentMediaType 失败: HRESULT=0x{HR:X8}，沿用原生参数", streamIndex, hr);
            return fallback;
        }

        try
        {
            var getUINT32 = MfVTable.Get<IMFMediaType_GetUINT32>(actualType, 4);
            Guid rateKey = MFConstants.MF_MT_AUDIO_SAMPLES_PER_SECOND;
            Guid chKey = MFConstants.MF_MT_AUDIO_NUM_CHANNELS;
            Guid bitsKey = MFConstants.MF_MT_AUDIO_BITS_PER_SAMPLE;

            if (getUINT32(actualType, ref rateKey, out uint rate) < 0 || rate == 0)
                rate = nativeSampleRate;
            if (getUINT32(actualType, ref chKey, out uint ch) < 0 || ch == 0)
                ch = nativeChannels;
            if (getUINT32(actualType, ref bitsKey, out uint bits) < 0 || bits == 0)
                bits = 16; // PCM 协商成功但未回填位深：MF 默认 16bit

            _logger.LogInformation("音频流 {Index} 已协商为 PCM 输出: {Rate}Hz {Ch}ch {Bits}bit", streamIndex, rate, ch, bits);
            return ((int)rate, (int)ch, (int)bits);
        }
        finally
        {
            Marshal.Release(actualType);
        }
    }
}
