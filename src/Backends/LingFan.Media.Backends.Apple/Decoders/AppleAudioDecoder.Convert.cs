using System.Runtime.CompilerServices;
using LingFan.Media.Apple.Shared;

namespace LingFan.Media.Backends.Apple.Decoders;

/// <summary>
/// 转换通路分部：AudioConverterFillComplexBuffer 拉模式解码（输入回调、单包解码与转换器排空）。
/// </summary>
internal sealed unsafe partial class AppleAudioDecoder
{
    // 输出缓冲（固定容量，单包解出的 PCM 远小于此；HE-AAC 多声道亦覆盖）
    private byte[] _outBuffer = Array.Empty<byte>();
    private int _outCapacity;
    private AppleRuntime.AudioBufferList _outList;

    // 输入回调（AudioConverterComplexInputDataProc）相关状态
    private GCHandle _gcHandle;          // refCon → this
    private nint _inputProcPtr;          // 静态回调的函数指针
    private bool _inputHasData;          // 当前包是否仍有未供应的压缩数据
    private nint _inputPtr;              // 当前包数据指针（fixed 块内有效）
    private int _inputLength;
    private AppleRuntime.AudioStreamPacketDescription _pd; // 单包描述（fixed 块内被引用）
    private nint _pdPtr;

    private void FeedAndDecode()
    {
        while (_pendingInput.Count > 0)
        {
            var pkt = _pendingInput.Dequeue();
            try
            {
                DecodeOne(pkt);
            }
            finally
            {
                pkt.Dispose();
            }
        }
    }

    private void DecodeOne(MediaPacket pkt)
    {
        byte[] data = pkt.Data.ToArray();
        int total = data.Length;
        if (total == 0) return;

        // MP3 等未知采样率：首包懒解析 MPEG 帧头回填 Output*（转换器输出率按输入推导，无需重建）
        if (_codecType == AudioCodec.MP3 && _outputSampleRate == 0
            && TryParseMp3FrameHeader(data, out int sr, out int ch))
        {
            _outputSampleRate = sr;
            _outputChannels = ch;
            _bytesPerOutputPacket = 2 * _outputChannels;
        }

        _inputLength = total;
        _inputHasData = true;
        _pd.mStartOffset = 0;
        _pd.mDataByteSize = (uint)total;
        _pd.mVariableFramesInPacket = (uint)_framesPerPacket;

        nint refCon = GCHandle.ToIntPtr(_gcHandle);
        fixed (byte* pIn = data)
        fixed (AppleRuntime.AudioStreamPacketDescription* pPd = &_pd)
        fixed (byte* pOut = _outBuffer)
        fixed (AppleRuntime.AudioBufferList* pList = &_outList)
        {
            _inputPtr = (nint)pIn;
            _pdPtr = (nint)pPd;
            while (true)
            {
                pList->mNumberBuffers = 1;
                pList->mBuffers.mNumberChannels = (uint)_outputChannels;
                pList->mBuffers.mData = (nint)pOut;
                pList->mBuffers.mDataByteSize = (uint)_outCapacity;

                int req = _outCapacity / _bytesPerOutputPacket;
                int outPackets = req;
                _ = AppleRuntime.AudioConverterFillComplexBuffer(
                    _converter, _inputProcPtr, refCon, ref outPackets, (nint)pList, nint.Zero);
                if (outPackets <= 0) break;

                int produced = outPackets * _bytesPerOutputPacket;
                var pcm = new byte[produced];
                new ReadOnlySpan<byte>(pOut, produced).CopyTo(pcm);
                int frameCount = produced / (2 * _outputChannels);
                _pendingFrames.Enqueue(new AudioFrame(
                    pcm, _outputSampleRate, _outputChannels, SampleFormat.S16,
                    pkt.Timestamp, TimeSpan.Zero, frameCount));

                if (!_inputHasData) break; // 当前包已耗尽且输出排空
            }
        }
        _inputHasData = false;
    }

    private void DrainConverter()
    {
        if (_converter == nint.Zero) return;
        _inputHasData = false;
        nint refCon = GCHandle.ToIntPtr(_gcHandle);
        fixed (byte* pOut = _outBuffer)
        fixed (AppleRuntime.AudioBufferList* pList = &_outList)
        {
            while (true)
            {
                pList->mNumberBuffers = 1;
                pList->mBuffers.mNumberChannels = (uint)_outputChannels;
                pList->mBuffers.mData = (nint)pOut;
                pList->mBuffers.mDataByteSize = (uint)_outCapacity;

                int req = _outCapacity / _bytesPerOutputPacket;
                int outPackets = req;
                _ = AppleRuntime.AudioConverterFillComplexBuffer(
                    _converter, _inputProcPtr, refCon, ref outPackets, (nint)pList, nint.Zero);
                if (outPackets <= 0) break;

                int produced = outPackets * _bytesPerOutputPacket;
                var pcm = new byte[produced];
                new ReadOnlySpan<byte>(pOut, produced).CopyTo(pcm);
                int frameCount = produced / (2 * _outputChannels);
                _pendingFrames.Enqueue(new AudioFrame(
                    pcm, _outputSampleRate, _outputChannels, SampleFormat.S16,
                    TimeSpan.Zero, TimeSpan.Zero, frameCount));
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int InputProc(nint converter, uint* ioNumberDataPackets, nint ioData,
        nint* outDataPacketDescription, nint userData)
    {
        var dec = GCHandle.FromIntPtr(userData).Target as AppleAudioDecoder;
        if (dec is null) return AppleRuntime.noErr;

        // 无更多压缩数据：告知转换器停止拉取（其将输出已缓冲 PCM 或返回）
        if (!dec._inputHasData)
        {
            *ioNumberDataPackets = 0;
            return AppleRuntime.noErr;
        }

        var abl = (AppleRuntime.AudioBufferList*)ioData;
        abl->mNumberBuffers = 1;
        abl->mBuffers.mNumberChannels = (uint)dec._outputChannels;
        abl->mBuffers.mData = dec._inputPtr;
        abl->mBuffers.mDataByteSize = (uint)dec._inputLength;

        *ioNumberDataPackets = 1;
        *outDataPacketDescription = dec._pdPtr;
        dec._inputHasData = false; // 当前包已交付，下次拉取返回 0
        return AppleRuntime.noErr;
    }
}