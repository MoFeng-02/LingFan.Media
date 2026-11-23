using System.Runtime.CompilerServices;
using LingFan.Media.Abstractions;
using LingFan.Media.Apple.Shared;

namespace LingFan.Media.Backends.Apple.Decoders;

/// <summary>
/// 解码通路分部：压缩包喂入、VTDecompressionSession 同步解码回调与 CVPixelBuffer 软件帧拷贝。
/// </summary>
internal sealed unsafe partial class AppleVideoDecoder
{
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
        // 同步解码：回调在 DecodeFrame 内触发，借实例字段把当前包时间戳带给回调
        _currentPts = pkt.Timestamp;
        _currentDur = pkt.Duration;
        _currentKey = pkt.KeyFrame;

        byte[] data = pkt.Data.ToArray();
        int total = data.Length;
        if (total == 0) return;

        fixed (byte* p = data)
        {
            int st = AppleRuntime.CMBlockBufferCreateWithMemoryBlock(
                AppleRuntime.kCFAllocatorNull, (nint)p, (nuint)total, AppleRuntime.kCFAllocatorNull,
                nint.Zero, 0, (nuint)total, 0, out nint blockBuffer);
            if (st != AppleRuntime.noErr || blockBuffer == nint.Zero) return;

            try
            {
                var timing = new AppleRuntime.CMSampleTimingInfo
                {
                    duration = AppleRuntime.CMTime.FromTicks(pkt.Duration.Ticks),
                    presentationTimeStamp = AppleRuntime.CMTime.FromTicks(pkt.Timestamp.Ticks),
                    decodeTimeStamp = AppleRuntime.CMTime.Invalid(),
                };
                long size = total;
                int st2;
                nint sbuf;
                unsafe
                {
                    var tp = &timing;
                    var sp = &size;
                    st2 = AppleRuntime.CMSampleBufferCreateReady(
                        AppleRuntime.kCFAllocatorNull, blockBuffer, _formatDescription,
                        (nint)1, (nint)1, (nint)tp, (nint)1, (nint)sp, out sbuf);
                }
                if (st2 != AppleRuntime.noErr || sbuf == nint.Zero) return;

                try
                {
                    // 不清算异步/时域标志 → 回调在返回前同步触发
                    _ = AppleRuntime.VTDecompressionSessionDecodeFrame(_session, sbuf, 0, nint.Zero, nint.Zero);
                }
                finally
                {
                    AppleRuntime.CFRelease(sbuf);
                }
            }
            finally
            {
                AppleRuntime.CFRelease(blockBuffer);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDecompressed(nint refCon, nint sourceFrameRefCon, int status, uint infoFlags,
        nint imageBuffer, nint ptsPtr, nint durPtr)
    {
        if (refCon == nint.Zero) return;
        var dec = GCHandle.FromIntPtr(refCon).Target as AppleVideoDecoder;
        dec?.OnFrameDecoded(status, imageBuffer);
    }

    private void OnFrameDecoded(int status, nint imageBuffer)
    {
        // imageBuffer 是 session 借出的引用（回调期间有效），不在此释放；仅同步拷贝后丢弃
        if (status != AppleRuntime.noErr || imageBuffer == nint.Zero) return;
        VideoFrame? frame = CopyToSoftwareFrame(imageBuffer);
        if (frame is not null) _pendingFrames.Enqueue(frame);
    }

    private VideoFrame? CopyToSoftwareFrame(nint imageBuffer)
    {
        uint fmt = AppleRuntime.CVPixelBufferGetPixelFormatType(imageBuffer);
        int w = (int)AppleRuntime.CVPixelBufferGetWidth(imageBuffer);
        int h = (int)AppleRuntime.CVPixelBufferGetHeight(imageBuffer);

        int lockSt = AppleRuntime.CVPixelBufferLockBaseAddress(imageBuffer, 0);
        if (lockSt != AppleRuntime.noErr) return null;
        try
        {
            if (fmt == AppleRuntime.kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
                || fmt == AppleRuntime.kCVPixelFormatType_420YpCbCr8BiPlanarFullRange)
            {
                int yStride = (int)AppleRuntime.CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 0);
                int uvStride = (int)AppleRuntime.CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 1);
                nint yPtr = AppleRuntime.CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 0);
                nint uvPtr = AppleRuntime.CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 1);

                int ySize = w * h;
                int uvSize = w * h / 2;
                var res = new SoftwareFrameResource(w, h, PixelFormat.NV12, ySize + uvSize);
                Span<byte> dst = res.Data.Span;
                for (int r = 0; r < h; r++)
                    new ReadOnlySpan<byte>((void*)(yPtr + r * yStride), w).CopyTo(dst.Slice(r * w, w));
                for (int r = 0; r < h / 2; r++)
                    new ReadOnlySpan<byte>((void*)(uvPtr + r * uvStride), w).CopyTo(dst.Slice(ySize + r * w, w));

                return new VideoFrame(w, h, PixelFormat.NV12, res, _currentPts, _currentDur, _currentKey);
            }

            if (fmt == AppleRuntime.kCVPixelFormatType_32BGRA)
            {
                int stride = (int)AppleRuntime.CVPixelBufferGetBytesPerRow(imageBuffer);
                nint basePtr = AppleRuntime.CVPixelBufferGetBaseAddress(imageBuffer);
                int size = w * h * 4;
                var res = new SoftwareFrameResource(w, h, PixelFormat.BGRA32, size);
                Span<byte> dst = res.Data.Span;
                for (int r = 0; r < h; r++)
                    new ReadOnlySpan<byte>((void*)(basePtr + r * stride), w * 4).CopyTo(dst.Slice(r * w * 4, w * 4));

                return new VideoFrame(w, h, PixelFormat.BGRA32, res, _currentPts, _currentDur, _currentKey);
            }

            _logger.LogWarning("[APPLE-VID] 不支持的 CVPixelBuffer 像素格式 0x{Format:X}，丢弃帧", fmt);
            return null;
        }
        finally
        {
            AppleRuntime.CVPixelBufferUnlockBaseAddress(imageBuffer, 0);
        }
    }
}