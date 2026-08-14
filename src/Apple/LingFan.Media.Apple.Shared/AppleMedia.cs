using System.Runtime.InteropServices;

namespace LingFan.Media.Apple.Shared;

/// <summary>
/// Apple 原生媒体框架（CoreMedia / CoreVideo / VideoToolbox / AudioToolbox / CoreFoundation）C API 绑定层，
/// 与 <see cref="AppleRuntime"/> 同属全仓唯一 Apple 绑定源。全部 <c>[LibraryImport]</c> 静态解析，NativeAOT 零 IL2xxx。
/// </summary>
/// <remarks>
/// <para>所有 Core* 框架符号经中性库名（CoreMedia / CoreVideo / VideoToolbox / AudioToolbox / CoreFoundation）由
/// <see cref="AppleRuntime.ResolveAppleFramework"/> 重定向到 Apple framework 全路径；非 Apple 运行时返回 <see cref="nint.Zero"/>，fail-fast。</para>
/// <para>ABI 注意：返回 <c>CMTime</c>（24 字节结构）的 C 函数由本层以 <c>CMTime</c> 值返回声明——.NET marshaller 自动处理
/// sret（隐藏指针）约定。经 <c>objc_msgSend</c> 返回的 <c>CMTime</c>（如 <c>[asset duration]</c>）则改用
/// <see cref="AppleRuntime.objc_msgSend_CMTime"/>（隐藏指针作为首参），避免 64 位 Apple ABI 下结构按值返回的歧义。</para>
/// </remarks>
public static unsafe partial class AppleRuntime
{
    // ── CMTime：CoreMedia 有理数时间（value/timescale = 秒）──
    // [StructLayout(Sequential)]：value(int64) + timescale(int32) + flags(uint32) + epoch(int64) = 24 字节。
    [StructLayout(LayoutKind.Sequential)]
    public struct CMTime
    {
        public long value;
        public int timescale;
        public uint flags;
        public long epoch;

        /// <summary>转换为秒（timescale 为 0 时返回 0，避免除零）。</summary>
        public readonly double ToSeconds() => timescale == 0 ? 0d : (double)value / timescale;

        /// <summary>由 .NET <see cref="TimeSpan"/>（100ns tick）构造 CMTime（timescale = 1e7 精确无损）。</summary>
        public static CMTime FromTicks(long ticks)
            => new() { value = ticks, timescale = 10_000_000, flags = 1u /*kCMTimeFlags_Valid*/, epoch = 0 };

        /// <summary>无效时间（kCMTimeInvalid），用作 decode/display 时间戳占位。</summary>
        public static CMTime Invalid()
            => new() { value = 0, timescale = 0, flags = 0x17u /*Valid|ImpliedValue|Indefinite*/, epoch = 0 };
    }

    // CMSampleTimingInfo：3 × CMTime = 72 字节（duration / presentationTimeStamp / decodeTimeStamp）。
    [StructLayout(LayoutKind.Sequential)]
    public struct CMSampleTimingInfo
    {
        public CMTime duration;
        public CMTime presentationTimeStamp;
        public CMTime decodeTimeStamp;
    }

    // CMTimeRange：2 × CMTime = 48 字节（start + duration）。
    [StructLayout(LayoutKind.Sequential)]
    public struct CMTimeRange
    {
        public CMTime Start;
        public CMTime Duration;
    }

    [LibraryImport("CoreMedia", EntryPoint = "CMTimeRangeMake")]
    public static partial void CMTimeRangeMake(out CMTimeRange result, CMTime start, CMTime duration);

    private static CMTime? _kCMTimePositiveInfinity;
    /// <summary>解析 <c>kCMTimePositiveInfinity</c> 全局常量（extern const CMTime，符号即结构体地址）。</summary>
    public static CMTime CMTimePositiveInfinity
    {
        get
        {
            if (_kCMTimePositiveInfinity.HasValue) return _kCMTimePositiveInfinity.Value;
            nint sym = GetGlobalSymbol("CoreMedia", "kCMTimePositiveInfinity");
            var v = sym == nint.Zero ? default : Marshal.PtrToStructure<CMTime>(sym);
            _kCMTimePositiveInfinity = v;
            return v;
        }
    }

    // CGSize：16 字节，2 × double（ARM64 属 HFA，x86_64 走 SSE 寄存器）。
    // 必须以"返回结构"方式声明——绝不可用隐藏指针 sret（CGSize 走寄存器返回，非内存返回）。
    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize
    {
        public double width;
        public double height;
    }

    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial CGSize objc_msgSend_CGSize(nint receiver, nint selector);

    // VTDecompressionOutputCallbackRecord：函数指针 + refcon（各 8 字节）。
    [StructLayout(LayoutKind.Sequential)]
    public struct VTDecompressionOutputCallbackRecord
    {
        public nint decompressionOutputCallback; // function pointer（同 nint）
        public nint decompressionOutputRefCon;   // void*
    }

    // 稳定 ABI 常量：CoreVideo 像素格式 FourCharCode（'XXXX' 大端 → 小端读取的 uint 值，跨 Apple 平台恒定）。
    public const uint kCVPixelFormatType_32BGRA = 0x42475241u;                 // 'BGRA'
    public const uint kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange = 0x34323076u; // '420v' (NV12)
    public const uint kCVPixelFormatType_420YpCbCr8BiPlanarFullRange = 0x34323066u; // '420f' (NV12)
    public const uint kCVPixelFormatType_420YpCbCr8Planar = 0x79343230u;       // 'y420' (I420)

    // 编解码器类型 FourCharCode（VideoToolbox CMVideoCodecType）。
    public const uint kCMVideoCodecType_H264 = 0x61766331u; // 'avc1'
    public const uint kCMVideoCodecType_HEVC = 0x68766331u; // 'hvc1'

    // 状态/标志
    public const int noErr = 0;
    public const uint kVTDecodeFrame_EnableAsynchronousDecompression = 0x00000400u;
    public const int kCVPixelBufferLock_ReadOnly = 0x00000001;

    // ── CoreFoundation：分配器 ──

    [LibraryImport("CoreFoundation", EntryPoint = "CFAllocatorGetDefault")]
    public static partial nint CFAllocatorGetDefault();

    private static nint? _kCFAllocatorNull;
    /// <summary>解析 <c>kCFAllocatorNull</c> 全局常量（CFAllocatorRef，表示"不拥有/不释放内存"）。惰性缓存。</summary>
    public static nint kCFAllocatorNull
    {
        get
        {
            if (_kCFAllocatorNull.HasValue) return _kCFAllocatorNull.Value;
            nint sym = GetGlobalSymbol("CoreFoundation", "kCFAllocatorNull");
            _kCFAllocatorNull = sym == nint.Zero ? nint.Zero : Marshal.ReadIntPtr(sym);
            return _kCFAllocatorNull.Value;
        }
    }

    [LibraryImport("CoreFoundation", EntryPoint = "CFDictionaryGetValue")]
    public static partial nint CFDictionaryGetValue(nint dict, nint key);

    [LibraryImport("CoreFoundation", EntryPoint = "CFDictionaryContainsKey")]
    public static partial byte CFDictionaryContainsKey(nint dict, nint key);

    [LibraryImport("CoreFoundation", EntryPoint = "CFDataGetLength")]
    public static partial nint CFDataGetLength(nint data);

    [LibraryImport("CoreFoundation", EntryPoint = "CFDataGetBytePtr")]
    public static partial nint CFDataGetBytePtr(nint data);

    [LibraryImport("CoreFoundation", EntryPoint = "CFBooleanGetValue")]
    public static partial byte CFBooleanGetValue(nint boolean);

    [LibraryImport("CoreFoundation", EntryPoint = "CFArrayGetCount")]
    public static partial nint CFArrayGetCount(nint array);

    [LibraryImport("CoreFoundation", EntryPoint = "CFArrayGetValueAtIndex")]
    public static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    // ── CoreMedia ──

    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetDataBuffer")]
    public static partial nint CMSampleBufferGetDataBuffer(nint sbuf);

    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetFormatDescription")]
    public static partial nint CMSampleBufferGetFormatDescription(nint sbuf);

    // 返回 CMTime（24 字节）由 marshaller 经 sret 处理。
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetPresentationTimeStamp")]
    public static partial CMTime CMSampleBufferGetPresentationTimeStamp(nint sbuf);

    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetDuration")]
    public static partial CMTime CMSampleBufferGetDuration(nint sbuf);

    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetNumSamples")]
    public static partial nint CMSampleBufferGetNumSamples(nint sbuf);

    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetTotalSampleSize")]
    public static partial nuint CMSampleBufferGetTotalSampleSize(nint sbuf);

    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetSampleAttachmentsArray")]
    public static partial nint CMSampleBufferGetSampleAttachmentsArray(nint sbuf, byte createIfNecessary);

    [LibraryImport("CoreMedia", EntryPoint = "CMFormatDescriptionGetExtension")]
    public static partial int CMFormatDescriptionGetExtension(nint formatDescription, nint extensionKey, out nint outExtension);

    [LibraryImport("CoreMedia", EntryPoint = "CMFormatDescriptionGetMediaSubType")]
    public static partial uint CMFormatDescriptionGetMediaSubType(nint formatDescription);

    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionGetH264ParameterSetAtIndex")]
    public static partial int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
        nint formatDescription, nuint parameterSetIndex, out nint parameterSetPointer,
        out nuint parameterSetLength, out nint parameterSetCount);

    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionGetHEVCParameterSetAtIndex")]
    public static partial int CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
        nint formatDescription, nuint parameterSetIndex, out nint parameterSetPointer,
        out nuint parameterSetLength, out nint parameterSetCount);

    [LibraryImport("CoreMedia", EntryPoint = "CMBlockBufferGetDataPointer")]
    public static partial int CMBlockBufferGetDataPointer(
        nint blockBuffer, nuint offset, out nuint lengthAtOffset, out nuint totalLength, out nint dataPointer);

    [LibraryImport("CoreMedia", EntryPoint = "CMBlockBufferCreateWithMemoryBlock")]
    public static partial int CMBlockBufferCreateWithMemoryBlock(
        nint allocator, nint memoryBlock, nuint blockLength, nint blockAllocator,
        nint customBlockSource, nuint offsetToData, nuint dataLength, uint flags, out nint blockBufferOut);

    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferCreateReady")]
    public static partial int CMSampleBufferCreateReady(
        nint allocator, nint blockBuffer, nint formatDescription,
        nint numSamples, nint numSampleTimingEntries, nint sampleTimingArray,
        nint numSampleSizes, nint sampleSizeArray, out nint sbufOut);

    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionCreateFromH264ParameterSets")]
    public static partial int CMVideoFormatDescriptionCreateFromH264ParameterSets(
        nint allocator, nuint parameterSetCount, nint parameterSetPointers, nint parameterSetSizes,
        int nalUnitHeaderLength, out nint formatDescriptionOut);

    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionCreateFromHEVCParameterSets")]
    public static partial int CMVideoFormatDescriptionCreateFromHEVCParameterSets(
        nint allocator, nuint parameterSetCount, nint parameterSetPointers, nint parameterSetSizes,
        int nalUnitHeaderLength, nint extensions, out nint formatDescriptionOut);

    // ── CoreVideo ──

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferLockBaseAddress")]
    public static partial int CVPixelBufferLockBaseAddress(nint pixelBuffer, nuint lockFlags);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferUnlockBaseAddress")]
    public static partial int CVPixelBufferUnlockBaseAddress(nint pixelBuffer, nuint lockFlags);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetWidth")]
    public static partial nuint CVPixelBufferGetWidth(nint pixelBuffer);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetHeight")]
    public static partial nuint CVPixelBufferGetHeight(nint pixelBuffer);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetPixelFormatType")]
    public static partial uint CVPixelBufferGetPixelFormatType(nint pixelBuffer);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetPlaneCount")]
    public static partial nuint CVPixelBufferGetPlaneCount(nint pixelBuffer);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferIsPlanar")]
    public static partial byte CVPixelBufferIsPlanar(nint pixelBuffer);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBaseAddress")]
    public static partial nint CVPixelBufferGetBaseAddress(nint pixelBuffer);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBaseAddressOfPlane")]
    public static partial nint CVPixelBufferGetBaseAddressOfPlane(nint pixelBuffer, nuint planeIndex);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBytesPerRow")]
    public static partial nuint CVPixelBufferGetBytesPerRow(nint pixelBuffer);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBytesPerRowOfPlane")]
    public static partial nuint CVPixelBufferGetBytesPerRowOfPlane(nint pixelBuffer, nuint planeIndex);

    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetIOSurface")]
    public static partial nint CVPixelBufferGetIOSurface(nint pixelBuffer);

    // ── VideoToolbox ──

    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionCreate")]
    public static partial int VTDecompressionSessionCreate(
        nint allocator, nint videoFormatDescription, nint videoDecoderSpecification,
        nint destinationImageBufferAttributes, nint outputCallback, out nint decompressionSessionOut);

    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionDecodeFrame")]
    public static partial int VTDecompressionSessionDecodeFrame(
        nint session, nint sampleBuffer, uint decodeFlags, nint sourceFrameRefCon, nint infoFlagsOut);

    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionWaitForAsynchronousFrames")]
    public static partial int VTDecompressionSessionWaitForAsynchronousFrames(nint session);

    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionInvalidate")]
    public static partial void VTDecompressionSessionInvalidate(nint session);

    // ── AudioToolbox（音频解码器）──

    // AudioStreamBasicDescription：40 字节（mSampleRate f64 + 5×u32 + mReserved u32）。
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    // AudioBuffer：mNumberChannels u32 + mDataByteSize u32 + mData nint。
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        public uint mNumberChannels;
        public uint mDataByteSize;
        public nint mData;
    }

    // AudioBufferList：mNumberBuffers u32 + mBuffers[1]。
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBufferList
    {
        public uint mNumberBuffers;
        public AudioBuffer mBuffers;
    }

    // AudioStreamPacketDescription：VBR 音频（AAC 等）每包描述（mStartOffset SInt64 + mVariableFramesInPacket u32 + mDataByteSize u32 = 16 字节）。
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamPacketDescription
    {
        public long mStartOffset;
        public uint mVariableFramesInPacket;
        public uint mDataByteSize;
    }

    public const uint kAudioFormatLinearPCM = 0x6C70636Du; // 'lpcm'
    public const uint kAudioFormatAAC = 0x61616320u;       // 'aac '
    public const uint kAudioFormatMPEG4AAC = 0x6D346163u;  // 'm4ac' (部分枚举用)
    public const uint kAudioFormatFlagIsFloat = 0x00000001u;
    public const uint kAudioFormatFlagIsSignedInteger = 0x00000002u;
    public const uint kAudioFormatFlagIsPacked = 0x00000008u;
    public const uint kAudioFormatFlagNativeEndian = 0x00000010u;

    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterNew")]
    public static partial int AudioConverterNew(
        in AudioStreamBasicDescription inASBD, in AudioStreamBasicDescription outASBD, out nint converter);

    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterDispose")]
    public static partial int AudioConverterDispose(nint converter);

    // AudioConverterComplexInputDataProc：OSStatus (*)(converter, *ioNumberDataPackets, *ioData(AudioBufferList),
    //   **outDataPacketDescription, userData)。以 delegate* unmanaged 经 nint 传入。
    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterFillComplexBuffer")]
    public static partial int AudioConverterFillComplexBuffer(
        nint converter,
        nint inputProc, // AudioConverterComplexInputDataProc
        nint inputProcUserData,
        ref int ioOutputDataPacketSize,
        nint outBufferList, // AudioBufferList*
        nint outParsingError);

    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterSetProperty")]
    public static partial int AudioConverterSetProperty(
        nint converter, uint propertyID, uint propertySize, nint propertyData);

    // kAudioConverterDecompressionMagicCookie = 'dmcc'（AAC/MP3 解码所需的 AudioSpecificConfig / 私有配置）。
    public const uint kAudioConverterDecompressionMagicCookie = 0x646D6363u;

    // AudioConverterComplexInputDataProc：converter, *ioNumberDataPackets, *ioData(AudioBufferList),
    // **ioDataPacketDescription(AudioStreamPacketDescription), userData。以 delegate* unmanaged 经 nint 传入。
    public const uint kAudioFormatMPEGLayer3 = 0x2E6D7033u; // '.mp3'
}

/// <summary>
/// <see cref="AppleRuntime"/> 的 Objective-C 扩展重载（与 <see cref="AppleRuntime"/> 同文件分类，集中放置 CMTime 按值返回的 objc 变体）。
/// </summary>
public static unsafe partial class AppleRuntime
{
    /// <summary>
    /// 经 objc_msgSend 取得 CMTime 按值返回的属性（如 <c>[asset duration]</c>）。
    /// 64 位 Apple ABI（AAPCS64 / System V AMD64）下 ≥16 字节结构按隐藏指针（首参）返回，与此签名一致。
    /// </summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial void objc_msgSend_CMTime(out CMTime result, nint receiver, nint selector);

    /// <summary>setTimeRange: —— CMTimeRange（48 字节，按隐藏指针传参）经 <c>ref</c> 传递。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, ref CMTimeRange range);

    /// <summary>带两个参数的 objc 选择器（如 <c>initWithAsset:error:</c>）：第 3 参为 id，第 4 参为 <c>NSError**</c>（按 <c>ref nint</c> 传递）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector, nint arg1, ref nint arg2);

    /// <summary>NSString* → C# 字符串（UTF8String 选择器返回 const char*，立即读取）。</summary>
    public static string? GetString(nint nsString)
    {
        if (nsString == nint.Zero) return null;
        nint cstr = objc_msgSend(nsString, Sel("UTF8String"));
        return cstr == nint.Zero ? null : Marshal.PtrToStringUTF8(cstr);
    }

    /// <summary>AVAssetReader 状态码（AVAssetReaderStatus 枚举值）。</summary>
    public const int AVAssetReaderStatusUnknown = 0;
    public const int AVAssetReaderStatusReading = 1;
    public const int AVAssetReaderStatusCompleted = 2;
    public const int AVAssetReaderStatusFailed = 3;
    public const int AVAssetReaderStatusCancelled = 4;
}
