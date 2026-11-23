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
/// <para>返回值约定：返回 <c>int</c> 的函数均为 OSStatus（<see cref="noErr"/>=0 成功）；返回 <c>nint</c> 的
/// CF/VT 对象引用按 Cocoa 命名规则持有所有权——Create/Copy 返回 +1（调用方负责 CFRelease），Get 返回借入引用（不得释放）。</para>
/// </remarks>
public static unsafe partial class AppleRuntime
{
    /// <summary>CoreMedia 有理数时间（value/timescale = 秒）。</summary>
    /// <remarks>[StructLayout(Sequential)]：value(int64) + timescale(int32) + flags(uint32) + epoch(int64) = 24 字节。</remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CMTime
    {
        /// <summary>分子（与 <see cref="timescale"/> 相除得秒）。</summary>
        public long value;
        /// <summary>分母（每秒刻度数；0 表示无效/未定义时间基）。</summary>
        public int timescale;
        /// <summary>kCMTimeFlags 位标志（Valid=1 等）。</summary>
        public uint flags;
        /// <summary>时间纪元（通常为 0）。</summary>
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

    /// <summary>单个样本的时间信息：3 × CMTime = 72 字节（duration / presentationTimeStamp / decodeTimeStamp）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CMSampleTimingInfo
    {
        /// <summary>样本时长（无效时间表示未知）。</summary>
        public CMTime duration;
        /// <summary>呈现时间戳（PTS）。</summary>
        public CMTime presentationTimeStamp;
        /// <summary>解码时间戳（DTS；无 B 帧重排时为无效时间）。</summary>
        public CMTime decodeTimeStamp;
    }

    /// <summary>时间区间：2 × CMTime = 48 字节（start + duration）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CMTimeRange
    {
        /// <summary>区间起点。</summary>
        public CMTime Start;
        /// <summary>区间时长。</summary>
        public CMTime Duration;
    }

    /// <summary>CoreMedia：<c>CMTimeRangeMake</c>——由起点与时长构造时间区间（24 字节以上结构经隐藏指针返回）。</summary>
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

    /// <summary>CoreGraphics 尺寸：16 字节，2 × double（ARM64 属 HFA，x86_64 走 SSE 寄存器）。</summary>
    /// <remarks>必须以"返回结构"方式声明——绝不可用隐藏指针 sret（CGSize 走寄存器返回，非内存返回）。</remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize
    {
        /// <summary>宽度（点）。</summary>
        public double width;
        /// <summary>高度（点）。</summary>
        public double height;
    }

    /// <summary>经 objc_msgSend 取得 CGSize 按值返回的属性（寄存器返回，非 sret）。</summary>
    [LibraryImport("libobjc", EntryPoint = "objc_msgSend")]
    public static partial CGSize objc_msgSend_CGSize(nint receiver, nint selector);

    /// <summary>VideoToolbox 解码输出回调记录：函数指针 + refcon（各 8 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VTDecompressionOutputCallbackRecord
    {
        /// <summary>VTDecompressionOutputCallback 函数指针（同 nint）。</summary>
        public nint decompressionOutputCallback;
        /// <summary>回调用户数据（void*）。</summary>
        public nint decompressionOutputRefCon;
    }

    /// <summary>CoreVideo 像素格式：32 位 BGRA（FourCharCode 'BGRA'）。</summary>
    public const uint kCVPixelFormatType_32BGRA = 0x42475241u;                 // 'BGRA'
    /// <summary>CoreVideo 像素格式：NV12 双平面 8-bit YCbCr，视频范围（FourCharCode '420v'）。</summary>
    public const uint kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange = 0x34323076u; // '420v' (NV12)
    /// <summary>CoreVideo 像素格式：NV12 双平面 8-bit YCbCr，全范围（FourCharCode '420f'）。</summary>
    public const uint kCVPixelFormatType_420YpCbCr8BiPlanarFullRange = 0x34323066u; // '420f' (NV12)
    /// <summary>CoreVideo 像素格式：I420 三平面 8-bit YCbCr（FourCharCode 'y420'）。</summary>
    public const uint kCVPixelFormatType_420YpCbCr8Planar = 0x79343230u;       // 'y420' (I420)

    /// <summary>VideoToolbox 编解码器类型：H.264/AVC（FourCharCode 'avc1'）。</summary>
    public const uint kCMVideoCodecType_H264 = 0x61766331u; // 'avc1'
    /// <summary>VideoToolbox 编解码器类型：H.265/HEVC（FourCharCode 'hvc1'）。</summary>
    public const uint kCMVideoCodecType_HEVC = 0x68766331u; // 'hvc1'

    /// <summary>OSStatus 成功码（0）。</summary>
    public const int noErr = 0;
    /// <summary>VTDecompressionSessionDecodeFrame 标志：允许异步解压（回调在会话内部线程触发）。</summary>
    public const uint kVTDecodeFrame_EnableAsynchronousDecompression = 0x00000400u;
    /// <summary>CVPixelBufferLockBaseAddress 标志：只读锁定（允许共享读，不可写）。</summary>
    public const int kCVPixelBufferLock_ReadOnly = 0x00000001;

    /// <summary>CoreFoundation：<c>CFAllocatorGetDefault</c>——取得默认分配器（借入引用，不释放）。</summary>
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

    /// <summary>CoreFoundation：<c>CFDictionaryGetValue</c>——按键取值（借入引用；键不存在返回 0）。</summary>
    [LibraryImport("CoreFoundation", EntryPoint = "CFDictionaryGetValue")]
    public static partial nint CFDictionaryGetValue(nint dict, nint key);

    /// <summary>CoreFoundation：<c>CFDictionaryContainsKey</c>——字典是否含键（非 0 = 含）。</summary>
    [LibraryImport("CoreFoundation", EntryPoint = "CFDictionaryContainsKey")]
    public static partial byte CFDictionaryContainsKey(nint dict, nint key);

    /// <summary>CoreFoundation：<c>CFDataGetLength</c>——CFData 字节长度。</summary>
    [LibraryImport("CoreFoundation", EntryPoint = "CFDataGetLength")]
    public static partial nint CFDataGetLength(nint data);

    /// <summary>CoreFoundation：<c>CFDataGetBytePtr</c>——CFData 只读字节指针（生命周期随 CFData）。</summary>
    [LibraryImport("CoreFoundation", EntryPoint = "CFDataGetBytePtr")]
    public static partial nint CFDataGetBytePtr(nint data);

    /// <summary>CoreFoundation：<c>CFBooleanGetValue</c>——CFBoolean 布尔值（非 0 = true）。</summary>
    [LibraryImport("CoreFoundation", EntryPoint = "CFBooleanGetValue")]
    public static partial byte CFBooleanGetValue(nint boolean);

    /// <summary>CoreFoundation：<c>CFArrayGetCount</c>——CFArray 元素个数。</summary>
    [LibraryImport("CoreFoundation", EntryPoint = "CFArrayGetCount")]
    public static partial nint CFArrayGetCount(nint array);

    /// <summary>CoreFoundation：<c>CFArrayGetValueAtIndex</c>——按下标取元素（借入引用；越界为未定义行为）。</summary>
    [LibraryImport("CoreFoundation", EntryPoint = "CFArrayGetValueAtIndex")]
    public static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    /// <summary>CoreMedia：<c>CMSampleBufferGetDataBuffer</c>——取样本数据块（CMBlockBuffer，借入引用；无数据返回 0）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetDataBuffer")]
    public static partial nint CMSampleBufferGetDataBuffer(nint sbuf);

    /// <summary>CoreMedia：<c>CMSampleBufferGetFormatDescription</c>——取格式描述（CMFormatDescription，借入引用）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetFormatDescription")]
    public static partial nint CMSampleBufferGetFormatDescription(nint sbuf);

    /// <summary>CoreMedia：<c>CMSampleBufferGetPresentationTimeStamp</c>——取样本呈现时间戳（PTS）。返回 CMTime（24 字节）由 marshaller 经 sret 处理。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetPresentationTimeStamp")]
    public static partial CMTime CMSampleBufferGetPresentationTimeStamp(nint sbuf);

    /// <summary>CoreMedia：<c>CMSampleBufferGetDuration</c>——取样本总时长（sret 返回 CMTime）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetDuration")]
    public static partial CMTime CMSampleBufferGetDuration(nint sbuf);

    /// <summary>CoreMedia：<c>CMSampleBufferGetNumSamples</c>——取样本个数（视频帧恒为 1）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetNumSamples")]
    public static partial nint CMSampleBufferGetNumSamples(nint sbuf);

    /// <summary>CoreMedia：<c>CMSampleBufferGetTotalSampleSize</c>——取全部样本数据总字节数。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetTotalSampleSize")]
    public static partial nuint CMSampleBufferGetTotalSampleSize(nint sbuf);

    /// <summary>CoreMedia：<c>CMSampleBufferGetSampleAttachmentsArray</c>——取样本附件字典数组（借入引用；含同步/丢帧等标记）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferGetSampleAttachmentsArray")]
    public static partial nint CMSampleBufferGetSampleAttachmentsArray(nint sbuf, byte createIfNecessary);

    /// <summary>CoreMedia：<c>CMFormatDescriptionGetExtension</c>——取格式描述扩展（如色彩元数据；借入引用）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMFormatDescriptionGetExtension")]
    public static partial int CMFormatDescriptionGetExtension(nint formatDescription, nint extensionKey, out nint outExtension);

    /// <summary>CoreMedia：<c>CMFormatDescriptionGetMediaSubType</c>——取媒体子类型 FourCharCode（编解码器标识）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMFormatDescriptionGetMediaSubType")]
    public static partial uint CMFormatDescriptionGetMediaSubType(nint formatDescription);

    /// <summary>CoreMedia：<c>CMVideoFormatDescriptionGetH264ParameterSetAtIndex</c>——按下标取 H.264 参数集（SPS/PPS，借入指针）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionGetH264ParameterSetAtIndex")]
    public static partial int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
        nint formatDescription, nuint parameterSetIndex, out nint parameterSetPointer,
        out nuint parameterSetLength, out nint parameterSetCount);

    /// <summary>CoreMedia：<c>CMVideoFormatDescriptionGetHEVCParameterSetAtIndex</c>——按下标取 HEVC 参数集（VPS/SPS/PPS，借入指针）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionGetHEVCParameterSetAtIndex")]
    public static partial int CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
        nint formatDescription, nuint parameterSetIndex, out nint parameterSetPointer,
        out nuint parameterSetLength, out nint parameterSetCount);

    /// <summary>CoreMedia：<c>CMBlockBufferGetDataPointer</c>——取数据块指针（可能仅覆盖 offset 起的连续段，须对照 lengthAtOffset/totalLength）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMBlockBufferGetDataPointer")]
    public static partial int CMBlockBufferGetDataPointer(
        nint blockBuffer, nuint offset, out nuint lengthAtOffset, out nuint totalLength, out nint dataPointer);

    /// <summary>CoreMedia：<c>CMBlockBufferCreateWithMemoryBlock</c>——以外部内存创建数据块（+1；memoryBlock 所有权由 flags/kCFAllocatorNull 约定）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMBlockBufferCreateWithMemoryBlock")]
    public static partial int CMBlockBufferCreateWithMemoryBlock(
        nint allocator, nint memoryBlock, nuint blockLength, nint blockAllocator,
        nint customBlockSource, nuint offsetToData, nuint dataLength, uint flags, out nint blockBufferOut);

    /// <summary>CoreMedia：<c>CMSampleBufferCreateReady</c>——由数据块/格式/时间信息创建就绪样本缓冲（+1）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMSampleBufferCreateReady")]
    public static partial int CMSampleBufferCreateReady(
        nint allocator, nint blockBuffer, nint formatDescription,
        nint numSamples, nint numSampleTimingEntries, nint sampleTimingArray,
        nint numSampleSizes, nint sampleSizeArray, out nint sbufOut);

    /// <summary>CoreMedia：<c>CMVideoFormatDescriptionCreateFromH264ParameterSets</c>——由 SPS/PPS 创建 H.264 格式描述（+1）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionCreateFromH264ParameterSets")]
    public static partial int CMVideoFormatDescriptionCreateFromH264ParameterSets(
        nint allocator, nuint parameterSetCount, nint parameterSetPointers, nint parameterSetSizes,
        int nalUnitHeaderLength, out nint formatDescriptionOut);

    /// <summary>CoreMedia：<c>CMVideoFormatDescriptionCreateFromHEVCParameterSets</c>——由 VPS/SPS/PPS 创建 HEVC 格式描述（+1）。</summary>
    [LibraryImport("CoreMedia", EntryPoint = "CMVideoFormatDescriptionCreateFromHEVCParameterSets")]
    public static partial int CMVideoFormatDescriptionCreateFromHEVCParameterSets(
        nint allocator, nuint parameterSetCount, nint parameterSetPointers, nint parameterSetSizes,
        int nalUnitHeaderLength, nint extensions, out nint formatDescriptionOut);

    /// <summary>CoreVideo：<c>CVPixelBufferLockBaseAddress</c>——锁定像素缓冲基址（CPU 访问前必须；与 Unlock 配对）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferLockBaseAddress")]
    public static partial int CVPixelBufferLockBaseAddress(nint pixelBuffer, nuint lockFlags);

    /// <summary>CoreVideo：<c>CVPixelBufferUnlockBaseAddress</c>——解锁像素缓冲基址（lockFlags 须与 Lock 一致）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferUnlockBaseAddress")]
    public static partial int CVPixelBufferUnlockBaseAddress(nint pixelBuffer, nuint lockFlags);

    /// <summary>CoreVideo：<c>CVPixelBufferGetWidth</c>——像素缓冲宽度（像素）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetWidth")]
    public static partial nuint CVPixelBufferGetWidth(nint pixelBuffer);

    /// <summary>CoreVideo：<c>CVPixelBufferGetHeight</c>——像素缓冲高度（像素）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetHeight")]
    public static partial nuint CVPixelBufferGetHeight(nint pixelBuffer);

    /// <summary>CoreVideo：<c>CVPixelBufferGetPixelFormatType</c>——像素格式 FourCharCode（kCVPixelFormatType_*）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetPixelFormatType")]
    public static partial uint CVPixelBufferGetPixelFormatType(nint pixelBuffer);

    /// <summary>CoreVideo：<c>CVPixelBufferGetPlaneCount</c>——平面数（非平面格式返回 0）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetPlaneCount")]
    public static partial nuint CVPixelBufferGetPlaneCount(nint pixelBuffer);

    /// <summary>CoreVideo：<c>CVPixelBufferIsPlanar</c>——是否平面布局（非 0 = 是）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferIsPlanar")]
    public static partial byte CVPixelBufferIsPlanar(nint pixelBuffer);

    /// <summary>CoreVideo：<c>CVPixelBufferGetBaseAddress</c>——非平面缓冲基址（须先 Lock；平面格式用 OfPlane 变体）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBaseAddress")]
    public static partial nint CVPixelBufferGetBaseAddress(nint pixelBuffer);

    /// <summary>CoreVideo：<c>CVPixelBufferGetBaseAddressOfPlane</c>——指定平面基址（须先 Lock）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBaseAddressOfPlane")]
    public static partial nint CVPixelBufferGetBaseAddressOfPlane(nint pixelBuffer, nuint planeIndex);

    /// <summary>CoreVideo：<c>CVPixelBufferGetBytesPerRow</c>——非平面缓冲每行字节数（含对齐填充）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBytesPerRow")]
    public static partial nuint CVPixelBufferGetBytesPerRow(nint pixelBuffer);

    /// <summary>CoreVideo：<c>CVPixelBufferGetBytesPerRowOfPlane</c>——指定平面每行字节数（含对齐填充）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetBytesPerRowOfPlane")]
    public static partial nuint CVPixelBufferGetBytesPerRowOfPlane(nint pixelBuffer, nuint planeIndex);

    /// <summary>CoreVideo：<c>CVPixelBufferGetIOSurface</c>——取关联 IOSurface（借入引用；无则 0，GPU 互操作入口）。</summary>
    [LibraryImport("CoreVideo", EntryPoint = "CVPixelBufferGetIOSurface")]
    public static partial nint CVPixelBufferGetIOSurface(nint pixelBuffer);

    /// <summary>VideoToolbox：<c>VTDecompressionSessionCreate</c>——创建硬解会话（+1；outputCallback 指向回调记录，生命周期须覆盖会话）。</summary>
    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionCreate")]
    public static partial int VTDecompressionSessionCreate(
        nint allocator, nint videoFormatDescription, nint videoDecoderSpecification,
        nint destinationImageBufferAttributes, nint outputCallback, out nint decompressionSessionOut);

    /// <summary>VideoToolbox：<c>VTDecompressionSessionDecodeFrame</c>——送入一帧压缩样本（异步标志下输出经回调返回）。</summary>
    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionDecodeFrame")]
    public static partial int VTDecompressionSessionDecodeFrame(
        nint session, nint sampleBuffer, uint decodeFlags, nint sourceFrameRefCon, nint infoFlagsOut);

    /// <summary>VideoToolbox：<c>VTDecompressionSessionWaitForAsynchronousFrames</c>——阻塞等待全部异步帧回调完成（排空/Flush 用）。</summary>
    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionWaitForAsynchronousFrames")]
    public static partial int VTDecompressionSessionWaitForAsynchronousFrames(nint session);

    /// <summary>VideoToolbox：<c>VTDecompressionSessionInvalidate</c>——使会话失效（停止回调；随后仍须 CFRelease 平衡创建引用）。</summary>
    [LibraryImport("VideoToolbox", EntryPoint = "VTDecompressionSessionInvalidate")]
    public static partial void VTDecompressionSessionInvalidate(nint session);

    /// <summary>AudioToolbox 音频流基本描述（ASBD）：40 字节（mSampleRate f64 + 5×u32 + mReserved u32）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        /// <summary>采样率（Hz）。</summary>
        public double mSampleRate;
        /// <summary>格式 ID（kAudioFormat* FourCharCode）。</summary>
        public uint mFormatID;
        /// <summary>格式标志（kAudioFormatFlag* 位组合）。</summary>
        public uint mFormatFlags;
        /// <summary>每包字节数（CBR  PCM 等于 mBytesPerFrame）。</summary>
        public uint mBytesPerPacket;
        /// <summary>每帧（样本帧）数/包（PCM 恒 1）。</summary>
        public uint mFramesPerPacket;
        /// <summary>每样本帧字节数（声道数 × 每样本字节）。</summary>
        public uint mBytesPerFrame;
        /// <summary>每帧声道数。</summary>
        public uint mChannelsPerFrame;
        /// <summary>每声道位数。</summary>
        public uint mBitsPerChannel;
        /// <summary>保留（置 0）。</summary>
        public uint mReserved;
    }

    /// <summary>AudioToolbox 音频缓冲：mNumberChannels u32 + mDataByteSize u32 + mData nint。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBuffer
    {
        /// <summary>本缓冲声道数（交错时为总声道数）。</summary>
        public uint mNumberChannels;
        /// <summary>数据字节数。</summary>
        public uint mDataByteSize;
        /// <summary>PCM 数据指针。</summary>
        public nint mData;
    }

    /// <summary>AudioToolbox 音频缓冲列表：mNumberBuffers u32 + mBuffers[1]（变长数组首元素，多缓冲时按原生布局追加）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBufferList
    {
        /// <summary>缓冲个数。</summary>
        public uint mNumberBuffers;
        /// <summary>首个缓冲（交错 PCM 单缓冲即全部）。</summary>
        public AudioBuffer mBuffers;
    }

    /// <summary>VBR 音频（AAC 等）每包描述：mStartOffset SInt64 + mVariableFramesInPacket u32 + mDataByteSize u32 = 16 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamPacketDescription
    {
        /// <summary>包数据在输入缓冲中的起始偏移。</summary>
        public long mStartOffset;
        /// <summary>包内帧数（0 表示按输出格式默认）。</summary>
        public uint mVariableFramesInPacket;
        /// <summary>包字节数。</summary>
        public uint mDataByteSize;
    }

    /// <summary>音频格式：线性 PCM（FourCharCode 'lpcm'）。</summary>
    public const uint kAudioFormatLinearPCM = 0x6C70636Du; // 'lpcm'
    /// <summary>音频格式：AAC（FourCharCode 'aac '）。</summary>
    public const uint kAudioFormatAAC = 0x61616320u;       // 'aac '
    /// <summary>音频格式：MPEG-4 AAC（FourCharCode 'm4ac'，部分枚举用）。</summary>
    public const uint kAudioFormatMPEG4AAC = 0x6D346163u;  // 'm4ac' (部分枚举用)
    /// <summary>ASBD 标志：样本为浮点。</summary>
    public const uint kAudioFormatFlagIsFloat = 0x00000001u;
    /// <summary>ASBD 标志：样本为有符号整数。</summary>
    public const uint kAudioFormatFlagIsSignedInteger = 0x00000002u;
    /// <summary>ASBD 标志：样本紧凑 packed（无高位填充）。</summary>
    public const uint kAudioFormatFlagIsPacked = 0x00000008u;
    /// <summary>ASBD 标志：本机字节序。</summary>
    public const uint kAudioFormatFlagNativeEndian = 0x00000010u;

    /// <summary>AudioToolbox：<c>AudioConverterNew</c>——按输入/输出 ASBD 创建音频转换器（+1，Dispose 配对）。</summary>
    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterNew")]
    public static partial int AudioConverterNew(
        in AudioStreamBasicDescription inASBD, in AudioStreamBasicDescription outASBD, out nint converter);

    /// <summary>AudioToolbox：<c>AudioConverterDispose</c>——销毁音频转换器（与 New 配对）。</summary>
    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterDispose")]
    public static partial int AudioConverterDispose(nint converter);

    /// <summary>AudioToolbox：<c>AudioConverterFillComplexBuffer</c>——拉取转换后 PCM（inputProc 为 AudioConverterComplexInputDataProc：OSStatus (*)(converter, *ioNumberDataPackets, *ioData(AudioBufferList), **outDataPacketDescription, userData)，以 delegate* unmanaged 经 nint 传入）。</summary>
    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterFillComplexBuffer")]
    public static partial int AudioConverterFillComplexBuffer(
        nint converter,
        nint inputProc, // AudioConverterComplexInputDataProc
        nint inputProcUserData,
        ref int ioOutputDataPacketSize,
        nint outBufferList, // AudioBufferList*
        nint outParsingError);

    /// <summary>AudioToolbox：<c>AudioConverterSetProperty</c>——设置转换器属性（如 magic cookie）。</summary>
    [LibraryImport("AudioToolbox", EntryPoint = "AudioConverterSetProperty")]
    public static partial int AudioConverterSetProperty(
        nint converter, uint propertyID, uint propertySize, nint propertyData);

    /// <summary>转换器属性：kAudioConverterDecompressionMagicCookie = 'dmcc'（AAC/MP3 解码所需的 AudioSpecificConfig / 私有配置）。</summary>
    public const uint kAudioConverterDecompressionMagicCookie = 0x646D6363u;

    /// <summary>音频格式：MPEG Layer 3（FourCharCode '.mp3'）。</summary>
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

    /// <summary>AVAssetReader 状态：未知（尚未开始）。</summary>
    public const int AVAssetReaderStatusUnknown = 0;
    /// <summary>AVAssetReader 状态：读取中。</summary>
    public const int AVAssetReaderStatusReading = 1;
    /// <summary>AVAssetReader 状态：读取完成（全部样本已产出）。</summary>
    public const int AVAssetReaderStatusCompleted = 2;
    /// <summary>AVAssetReader 状态：失败（错误经 NSError 取得）。</summary>
    public const int AVAssetReaderStatusFailed = 3;
    /// <summary>AVAssetReader 状态：已取消。</summary>
    public const int AVAssetReaderStatusCancelled = 4;
}