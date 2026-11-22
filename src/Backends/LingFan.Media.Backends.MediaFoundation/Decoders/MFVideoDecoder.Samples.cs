using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Interop;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

/// <summary>
/// Sample 构建分部：输入压缩 sample（buffer 所有权移交）与调用方分配模式的空输出 sample。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class MFVideoDecoder
{
    /// <summary>创建携带 packet 压缩数据的 IMFSample（buffer 所有权移交 sample）。</summary>
    private unsafe IntPtr CreateSampleWithData(MediaPacket packet)
    {
        int hr = MFInterop.MFCreateSample(out IntPtr sample);
        Marshal.ThrowExceptionForHR(hr);
        InteropTrace.OnAlloc(sample, "CreateSampleWithData:sample");

        IntPtr buffer = IntPtr.Zero;
        try
        {
            hr = MFInterop.MFCreateMemoryBuffer(packet.Data.Length, out buffer);
            Marshal.ThrowExceptionForHR(hr);
            InteropTrace.OnAlloc(buffer, "CreateSampleWithData:buffer");

            // Lock → 拷贝 → Unlock → 标记有效长度（IMFMediaBuffer：Lock=0, Unlock=1, GetCurrentLength=2, SetCurrentLength=3）
            var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(buffer, 0);
            var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(buffer, 1);
            var setLenDel = MfVTable.Get<IMFMediaBuffer_SetCurrentLength>(buffer, 3);

            hr = lockDel(buffer, out IntPtr pBuf, out _, out _);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                packet.Data.Span.CopyTo(new Span<byte>((void*)pBuf, packet.Data.Length));
            }
            finally
            {
                unlockDel(buffer);
            }
            setLenDel(buffer, (uint)packet.Data.Length);

            // buffer 挂到 sample（AddBuffer 内部 AddRef；本地引用在 finally 释放）
            // IMFSample 槽位（mfobjects.idl 顺序，ConvertToContiguousBuffer=38 已运行时锚定）：AddBuffer = slotIndex 39（运行时已验证）
            var addBuf = MfVTable.Get<IMFSample_AddBuffer>(sample, 39);
            hr = addBuf(sample, buffer);
            Marshal.ThrowExceptionForHR(hr);

            // 设置样本时间/时长（100ns 单位）
            var setTime = MfVTable.Get<IMFSample_SetSampleTime>(sample, 33);     // IMFAttributes(30) + 3
            var setDur = MfVTable.Get<IMFSample_SetSampleDuration>(sample, 35);  // IMFAttributes(30) + 5
            setTime(sample, packet.Timestamp.Ticks);   // TimeSpan.Ticks 即 100ns 单位
            setDur(sample, packet.Duration.Ticks);

            return sample;
        }
        catch
        {
            Marshal.Release(sample);
            throw;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.Release(buffer);
        }
    }

    /// <summary>创建空输出 sample（调用方分配模式，按 GetOutputStreamInfo.cbSize）。</summary>
    private IntPtr CreateOutputSample()
    {
        int size = (int)Math.Max(_outputBufferSize, 1);
        int hr = MFInterop.MFCreateSample(out IntPtr sample);
        Marshal.ThrowExceptionForHR(hr);
        InteropTrace.OnAlloc(sample, "CreateOutputSample:sample");

        IntPtr buffer = IntPtr.Zero;
        try
        {
            hr = MFInterop.MFCreateMemoryBuffer(size, out buffer);
            Marshal.ThrowExceptionForHR(hr);
            InteropTrace.OnAlloc(buffer, "CreateOutputSample:buffer");

            var addBuf = MfVTable.Get<IMFSample_AddBuffer>(sample, 39); // AddBuffer = slotIndex 39（运行时已验证）
            hr = addBuf(sample, buffer);
            Marshal.ThrowExceptionForHR(hr);
            return sample;
        }
        catch
        {
            Marshal.Release(sample);
            throw;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.Release(buffer);
        }
    }
}