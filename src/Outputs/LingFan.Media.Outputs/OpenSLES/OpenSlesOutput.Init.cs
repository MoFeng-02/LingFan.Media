using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace LingFan.Media.Outputs.OpenSLES;

// OpenSlesOutput 的初始化职责段（部分类，按职责拆分，编译期等价）。
// OpenSL ES 引擎/输出混音创建与参数化初始化；控制与提交编排在主文件，播放器创建在 Player 段。
internal sealed unsafe partial class OpenSlesOutput
{
    private void InitializeCore()
    {
        // 1. 创建引擎对象
        int ret = slCreateEngine(out _engineObject, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        if (ret != SL_RESULT_SUCCESS || _engineObject == IntPtr.Zero)
            throw new InvalidOperationException($"slCreateEngine 失败，code={ret}。");

        try
        {
            // 2. Realize 引擎
            var realize = GetVTable<SLResult_BoolDelegate>(_engineObject, SLOT_Object_Realize);
            ret = realize(_engineObject, SL_BOOLEAN_FALSE);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"引擎 Realize 失败，code={ret}。");

            // 3. 获取 SLEngineItf
            var getIface = GetVTable<SLResult_IntPtrDelegate>(_engineObject, SLOT_Object_GetInterface);
            ret = getIface(_engineObject, GetIid("SL_IID_ENGINE"), out _engineItf);
            if (ret != SL_RESULT_SUCCESS || _engineItf == IntPtr.Zero)
                throw new InvalidOperationException($"获取 SLEngineItf 失败，code={ret}。");

            // 4. 创建 OutputMix
            var createMix = GetVTable<SLResult_OutObjDelegate>(_engineItf, SLOT_Engine_CreateOutputMix);
            ret = createMix(_engineItf, out _outputMixObject, 0, IntPtr.Zero, IntPtr.Zero);
            if (ret != SL_RESULT_SUCCESS || _outputMixObject == IntPtr.Zero)
                throw new InvalidOperationException($"CreateOutputMix 失败，code={ret}。");

            var realizeMix = GetVTable<SLResult_BoolDelegate>(_outputMixObject, SLOT_Object_Realize);
            ret = realizeMix(_outputMixObject, SL_BOOLEAN_FALSE);
            if (ret != SL_RESULT_SUCCESS)
                throw new InvalidOperationException($"OutputMix Realize 失败，code={ret}。");
        }
        catch
        {
            ReleaseNdkObjects();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engineItf == IntPtr.Zero)
            throw new InvalidOperationException("InitializeAsync 尚未调用，无法 Initialize。");
        if (_initialized)
            throw new InvalidOperationException("OpenSL ES 输出已初始化，请先 Dispose 再重新初始化。");
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0。");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "声道数必须大于 0。");

        _sampleRate = sampleRate;
        _channels = channels;

        _logger.LogInformation("[OPENSLES] 播放器创建: {Rate}Hz/{Ch}ch S16", sampleRate, channels);
        CreatePlayer(sampleRate, channels);
        _initialized = true;
    }
}
