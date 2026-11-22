using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Outputs.OpenSLES;

// OpenSlesOutput 的 Player 职责段（部分类，由 OpenSlesOutput.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/Submit/OnBufferCompleted/Pause/Resume/Flush/Dispose）在主文件；
// 本段仅播放器创建/重建/NDK 对象释放/vtable 解析，全部同步原生调用。
internal sealed unsafe partial class OpenSlesOutput
{
    /// <summary>
    /// 创建 OpenSL ES 音频播放器，绑定采样率/声道数（engine / outputmix 复用）。
    /// 帧格式变化时经 <see cref="RecreatePlayer"/> 销毁并按新格式重建。
    /// </summary>
    private void CreatePlayer(int sampleRate, int channels)
    {
        uint slSampleRate = sampleRate switch
        {
            44100 => SL_SAMPLINGRATE_44_1,
            48000 => SL_SAMPLINGRATE_48,
            _ => (uint)(sampleRate * 1000) // milliHz
        };

        var bqLocator = new SLDataLocatorAndroidSimpleBufferQueue
        {
            LocatorType = SL_DATALOCATOR_ANDROIDSIMPLEBUFFERQUEUE,
            NumBuffers = MaxInFlightBuffers
        };
        var pcm = new SLDataFormatPcm
        {
            FormatType = SL_DATAFORMAT_PCM,
            NumChannels = (uint)channels,
            SamplesPerSec = slSampleRate,
            BitsPerSample = SL_PCMSAMPLEFORMAT_FIXED_16,
            ContainerSize = SL_PCMSAMPLEFORMAT_FIXED_16,
            ChannelMask = (channels >= 2) ? (SL_SPEAKER_FRONT_LEFT | SL_SPEAKER_FRONT_RIGHT) : SL_SPEAKER_FRONT_LEFT,
            Endianness = SL_BYTEORDER_LITTLEENDIAN
        };

        GCHandle bqHandle = default, pcmHandle = default, outMixHandle = default, srcHandle = default, snkHandle = default, idsHandle = default, reqHandle = default;
        try
        {
            bqHandle = GCHandle.Alloc(bqLocator, GCHandleType.Pinned);
            pcmHandle = GCHandle.Alloc(pcm, GCHandleType.Pinned);

            var dataSource = new SLDataSource
            {
                PLocator = bqHandle.AddrOfPinnedObject(),
                PFormat = pcmHandle.AddrOfPinnedObject()
            };
            srcHandle = GCHandle.Alloc(dataSource, GCHandleType.Pinned);

            var outMixLocator = new SLDataLocatorOutputMix
            {
                LocatorType = SL_DATALOCATOR_OUTPUTMIX,
                OutputMix = _outputMixObject
            };
            outMixHandle = GCHandle.Alloc(outMixLocator, GCHandleType.Pinned);

            var dataSink = new SLDataSink
            {
                PLocator = outMixHandle.AddrOfPinnedObject(),
                PFormat = IntPtr.Zero
            };
            snkHandle = GCHandle.Alloc(dataSink, GCHandleType.Pinned);

            // 接口请求：BUFFERQUEUE + VOLUME
            IntPtr[] ids = { GetIid("SL_IID_BUFFERQUEUE"), GetIid("SL_IID_VOLUME") };
            uint[] req = { SL_BOOLEAN_TRUE, SL_BOOLEAN_TRUE }; // SLboolean[] = SLuint32[]（4 字节/元素）
            idsHandle = GCHandle.Alloc(ids, GCHandleType.Pinned);
            reqHandle = GCHandle.Alloc(req, GCHandleType.Pinned);

            var createPlayer = GetVTable<SLResult_SourceSinkDelegate>(_engineItf, SLOT_Engine_CreateAudioPlayer);
            int ret = createPlayer(_engineItf, out _playerObject, srcHandle.AddrOfPinnedObject(),
                snkHandle.AddrOfPinnedObject(), (uint)ids.Length, idsHandle.AddrOfPinnedObject(), reqHandle.AddrOfPinnedObject());
            if (ret != SL_RESULT_SUCCESS || _playerObject == IntPtr.Zero)
                throw new InvalidOperationException($"CreateAudioPlayer 失败，code={ret}。");

            var realizePlayer = GetVTable<SLResult_BoolDelegate>(_playerObject, SLOT_Object_Realize);
            ret = realizePlayer(_playerObject, SL_BOOLEAN_FALSE);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"播放器 Realize 失败，code={ret}。");

            // 获取接口：PLAY / BUFFERQUEUE / VOLUME
            var getIface = GetVTable<SLResult_IntPtrDelegate>(_playerObject, SLOT_Object_GetInterface);
            ret = getIface(_playerObject, GetIid("SL_IID_PLAY"), out _playItf);
            if (ret != SL_RESULT_SUCCESS || _playItf == IntPtr.Zero)
                throw new InvalidOperationException($"获取 SLPlayItf 失败，code={ret}。");

            ret = getIface(_playerObject, GetIid("SL_IID_BUFFERQUEUE"), out _bqItf);
            if (ret != SL_RESULT_SUCCESS || _bqItf == IntPtr.Zero)
                throw new InvalidOperationException($"获取 SLAndroidSimpleBufferQueueItf 失败，code={ret}。");

            // VOLUME 可选
            if (getIface(_playerObject, GetIid("SL_IID_VOLUME"), out _volumeItf) != SL_RESULT_SUCCESS)
                _volumeItf = IntPtr.Zero;

            // 注册缓冲队列回调（完成时回收缓冲 + 释放背压信号）
            _thisHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            _bqCallback = OnBufferCompleted;
            var registerCb = GetVTable<SLResult_RegisterCallbackDelegate>(_bqItf, SLOT_BQ_RegisterCallback);
            registerCb(_bqItf, _bqCallback, GCHandle.ToIntPtr(_thisHandle));

            // 缓冲池起始不预分配：Submit 按需从池取/分配，回调回收。

            // 开始播放
            var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
            setState(_playItf, SL_PLAYSTATE_PLAYING);

            ApplyVolume();
        }
        finally
        {
            if (bqHandle.IsAllocated) bqHandle.Free();
            if (pcmHandle.IsAllocated) pcmHandle.Free();
            if (outMixHandle.IsAllocated) outMixHandle.Free();
            if (srcHandle.IsAllocated) srcHandle.Free();
            if (snkHandle.IsAllocated) snkHandle.Free();
            if (idsHandle.IsAllocated) idsHandle.Free();
            if (reqHandle.IsAllocated) reqHandle.Free();
        }
    }

    /// <summary>
    /// 按新格式重建播放器：Submit 发现帧采样率/声道数与当前配置不一致时调用。
    /// 已知事实（HE-AAC v2：码流参数 22050Hz/1ch，SBR+PS 解码后实际输出 44100Hz/2ch）——
    /// MediaCodec 的 configure 初始输出格式不可信，真实格式仅首帧 FORMAT_CHANGED 后可知，
    /// 故解码器/MediaPlayer 侧无从预知，正确做法是输出端运行时自适应。
    /// 仅销毁并重建绑定格式的 player（engine / outputmix 保持）；重建前清空缓冲池
    /// （旧格式缓冲大小不匹配新格式，复用会越界写），并重置背压信号。
    /// </summary>
    private void RecreatePlayer(int sampleRate, int channels)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        lock (_gate)
        {
            // 1. 停止旧播放器（不再消费缓冲 → 不再触发完成回调）
            if (_playItf != IntPtr.Zero)
            {
                try
                {
                    var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
                    setState(_playItf, SL_PLAYSTATE_STOPPED);
                }
                catch { /* 忽略停止错误 */ }
            }

            // 2. 清空缓冲池（旧格式缓冲大小不匹配，复用会越界）并重置背压信号
            while (_inFlightBuffers.TryDequeue(out IntPtr b)) Marshal.FreeHGlobal(b);
            while (_freeBuffers.TryDequeue(out IntPtr b)) Marshal.FreeHGlobal(b);
            _inFlightSamples.Clear(); // 主时钟记账同步（重建丢弃的缓冲未播完，不计消费）
            int deficit = MaxInFlightBuffers - _backpressure.CurrentCount;
            if (deficit > 0) _backpressure.Release(deficit);

            // 3. 销毁旧 player（play/bq/volume 接口随之失效）并释放回调句柄
            _playItf = IntPtr.Zero;
            _bqItf = IntPtr.Zero;
            _volumeItf = IntPtr.Zero;
            DestroyObject(ref _playerObject);
            if (_thisHandle.IsAllocated) _thisHandle.Free();
        }

        int oldRate = _sampleRate;
        int oldChannels = _channels;
        _sampleRate = sampleRate;
        _channels = channels;

        // 4. 按新格式重建（_gate 外执行，避免长锁；Submit 单线程调用，串行安全）
        _logger.LogWarning("[OPENSLES] 帧格式与播放器不符，重建播放器: {OldRate}Hz/{OldCh}ch → {NewRate}Hz/{NewCh}ch",
            oldRate, oldChannels, sampleRate, channels);
        CreatePlayer(sampleRate, channels);
    }

    private static T GetVTable<T>(IntPtr obj, int slot) where T : Delegate
    {
        // OpenSL ES 对象为 void**：*obj 是方法表指针。OpenSL 接口无 IUnknown 前缀，
        // SLOT_* 常量为 0 基绝对槽位 → 统一走 ComVTable.GetAbsolute。
        return ComVTable.GetAbsolute<T>(obj, slot);
    }

    private static IntPtr GetIid(string name)
    {
        if (NativeLibrary.TryGetExport(NativeLibrary.Load("libOpenSLES.so"), name, out IntPtr addr))
            return Marshal.ReadIntPtr(addr);
        throw new PlatformNotSupportedException($"无法解析 OpenSL ES 接口 ID：{name}。");
    }

    private void ReleaseNdkObjects()
    {
        if (_playItf != IntPtr.Zero)
        {
            try
            {
                // 先停止播放（SetPlayState STOPPED），再销毁对象
                var setState = GetVTable<SLResult_UintDelegate>(_playItf, SLOT_Play_SetPlayState);
                setState(_playItf, SL_PLAYSTATE_STOPPED);
            }
            catch { /* 忽略停止错误 */ }
            _playItf = IntPtr.Zero;
        }
        _bqItf = IntPtr.Zero;
        _volumeItf = IntPtr.Zero;

        DestroyObject(ref _playerObject);
        DestroyObject(ref _outputMixObject);
        DestroyObject(ref _engineObject);
        _engineItf = IntPtr.Zero;
    }

    private static void DestroyObject(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        try
        {
            var destroy = GetVTable<Void_SelfDelegate>(obj, SLOT_Object_Destroy); // void Destroy(self)
            destroy(obj);
        }
        catch { /* 忽略释放错误 */ }
        obj = IntPtr.Zero;
    }
}
