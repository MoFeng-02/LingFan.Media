using System.Runtime.InteropServices;

namespace LingFan.Media.Apple.Shared;

/// <summary>
/// AVFoundation / Foundation 托管助手（建 AVAsset / AVAssetReader / NSArray / NSDictionary / NSString 等）。
/// 全部消费 <see cref="AppleRuntime.objc_msgSend"/> 固定签名重载，不引入第二处 Apple 绑定。
/// </summary>
/// <remarks>
/// <para>对象所有权遵循 Cocoa 规则：<c>alloc</c>/<c>init</c> 返回 +1（调用方须 <see cref="Release"/>）；
/// property getter / 工厂方法返回 autoreleased，跨 autorelease 池边界使用前须 <see cref="Retain"/>。</para>
/// <para>所有构造类方法（Create*）均在 autorelease 池内构建，池外仅返回 +1 自有对象，避免无池环境（NativeAOT）逐帧泄漏。</para>
/// </remarks>
public static unsafe class AppleAvFoundation
{
    /// <summary>NSString* → 托管字符串。</summary>
    public static string? AsString(nint ns) => AppleRuntime.GetString(ns);

    /// <summary>Retain（Cocoa +1）。</summary>
    public static nint Retain(nint obj) => AppleRuntime.objc_retain(obj);

    /// <summary>Release（Cocoa -1）。</summary>
    public static void Release(nint obj) => AppleRuntime.objc_release(obj);

    /// <summary>由地址（本地文件路径或 http(s) URL）创建 AVURLAsset（返回 +1 自有对象）。</summary>
    public static nint CreateUrlAsset(string path)
    {
        nint pool = AppleRuntime.objc_autoreleasePoolPush();
        try
        {
            nint url;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 网络流：URLWithString:
                url = AppleRuntime.objc_msgSend(
                    AppleRuntime.Class("NSURL"), AppleRuntime.Sel("URLWithString:"), AppleRuntime.MakeNSString(path));
            }
            else
            {
                // 本地文件：fileURLWithPath:
                url = AppleRuntime.objc_msgSend(
                    AppleRuntime.Class("NSURL"), AppleRuntime.Sel("fileURLWithPath:"), AppleRuntime.MakeNSString(path));
            }

            nint asset = AppleRuntime.objc_msgSend(AppleRuntime.Class("AVURLAsset"), AppleRuntime.Sel("alloc"));
            // initWithURL:options: —— options=nil（不强制精确时长，避免额外字典所有权负担）
            asset = AppleRuntime.objc_msgSend(asset, AppleRuntime.Sel("initWithURL:options:"), url, nint.Zero);
            return asset; // +1 自有
        }
        finally
        {
            AppleRuntime.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>取得资产时长（[asset duration]，CMTime 经 sret 取回）。</summary>
    public static AppleRuntime.CMTime GetAssetDuration(nint asset)
    {
        AppleRuntime.objc_msgSend_CMTime(out var t, asset, AppleRuntime.Sel("duration"));
        return t;
    }

    /// <summary>取得资产轨道数组（[asset tracks]，retained 供池外使用）。</summary>
    public static nint GetTracks(nint asset)
    {
        nint arr = AppleRuntime.objc_msgSend(asset, AppleRuntime.Sel("tracks"));
        return arr == nint.Zero ? nint.Zero : AppleRuntime.CFRetain(arr);
    }

    /// <summary>NSArray 元素个数。</summary>
    public static int GetArrayCount(nint nsArray)
        => nsArray == nint.Zero ? 0 : (int)AppleRuntime.objc_msgSend(nsArray, AppleRuntime.Sel("count"));

    /// <summary>NSArray 按索引取元素（autoreleased，须立即使用）。</summary>
    public static nint GetArrayObject(nint nsArray, int index)
        => AppleRuntime.objc_msgSend(nsArray, AppleRuntime.Sel("objectAtIndex:"), (nuint)index);

    /// <summary>AVAssetTrack 的 mediaType（"vide" / "soun" / "sbtl" …）。</summary>
    public static string? GetTrackMediaType(nint track)
    {
        nint mt = AppleRuntime.objc_msgSend(track, AppleRuntime.Sel("mediaType"));
        return AppleRuntime.GetString(mt);
    }

    /// <summary>轨道自然尺寸宽度（[track naturalSize].width）。</summary>
    public static int GetTrackWidth(nint track)
        => (int)AppleRuntime.objc_msgSend_CGSize(track, AppleRuntime.Sel("naturalSize")).width;

    /// <summary>轨道自然尺寸高度。</summary>
    public static int GetTrackHeight(nint track)
        => (int)AppleRuntime.objc_msgSend_CGSize(track, AppleRuntime.Sel("naturalSize")).height;

    /// <summary>创建 AVAssetReader（[[AVAssetReader alloc] initWithAsset:error:]），返回 +1 自有对象。</summary>
    public static nint CreateAssetReader(nint asset, out nint error)
    {
        error = nint.Zero;
        nint reader = AppleRuntime.objc_msgSend(AppleRuntime.Class("AVAssetReader"), AppleRuntime.Sel("alloc"));
        reader = AppleRuntime.objc_msgSend(reader, AppleRuntime.Sel("initWithAsset:error:"), asset, ref error);
        return reader;
    }

    /// <summary>向 AVAssetReader 添加输出（addOutput:）。</summary>
    public static void AssetReaderAddOutput(nint reader, nint output)
        => AppleRuntime.objc_msgSend(reader, AppleRuntime.Sel("addOutput:"), output);

    /// <summary>启动读取（startReading），返回是否成功（BOOL）。</summary>
    public static bool AssetReaderStartReading(nint reader)
        => (byte)AppleRuntime.objc_msgSend(reader, AppleRuntime.Sel("startReading")) != 0;

    /// <summary>读取器状态码（status）。</summary>
    public static int AssetReaderStatus(nint reader)
        => (int)AppleRuntime.objc_msgSend(reader, AppleRuntime.Sel("status"));

    /// <summary>取消读取（cancelReading）。</summary>
    public static void AssetReaderCancelReading(nint reader)
        => AppleRuntime.objc_msgSend(reader, AppleRuntime.Sel("cancelReading"));

    /// <summary>
    /// 创建 AVAssetReaderTrackOutput（[[alloc] initWithTrack:outputSettings:]）。
    /// <paramref name="outputSettings"/> 传 <see cref="nint.Zero"/> 表示 passthrough（不解码，产出压缩包）。
    /// 返回 +1 自有对象。
    /// </summary>
    public static nint CreateTrackOutput(nint track, nint outputSettings)
    {
        nint outp = AppleRuntime.objc_msgSend(AppleRuntime.Class("AVAssetReaderTrackOutput"), AppleRuntime.Sel("alloc"));
        outp = AppleRuntime.objc_msgSend(outp, AppleRuntime.Sel("initWithTrack:outputSettings:"), track, outputSettings);
        return outp;
    }

    /// <summary>从输出取下一个 CMSampleBuffer（copyNextSampleBuffer）。返回 +1 自有对象；流尾返回 <see cref="nint.Zero"/>。</summary>
    public static nint CopyNextSampleBuffer(nint output)
        => AppleRuntime.objc_msgSend(output, AppleRuntime.Sel("copyNextSampleBuffer"));

    /// <summary>
    /// 构建 CVPixelBuffer 属性字典（kCVPixelBufferPixelFormatTypeKey = pixelFormat；可选 IOSurfaceProperties 空字典）。
    /// 返回 +1 自有 NSDictionary；调用方用 <see cref="Release"/> 释放。
    /// </summary>
    public static nint CreatePixelBufferAttributes(uint pixelFormat, bool iosurface)
    {
        nint pool = AppleRuntime.objc_autoreleasePoolPush();
        try
        {
            nint dict = AppleRuntime.objc_msgSend(AppleRuntime.Class("NSMutableDictionary"), AppleRuntime.Sel("alloc"));
            dict = AppleRuntime.objc_msgSend(dict, AppleRuntime.Sel("init"));

            nint keyFmt = AppleRuntime.MakeNSString("PixelFormatType");
            nint valFmt = AppleRuntime.objc_msgSend(
                AppleRuntime.Class("NSNumber"), AppleRuntime.Sel("numberWithUnsignedInt:"), pixelFormat);
            AppleRuntime.objc_msgSend(dict, AppleRuntime.Sel("setObject:forKey:"), valFmt, keyFmt);

            if (iosurface)
            {
                nint keyIo = AppleRuntime.MakeNSString("IOSurfaceProperties");
                nint empty = AppleRuntime.objc_msgSend(AppleRuntime.Class("NSMutableDictionary"), AppleRuntime.Sel("alloc"));
                empty = AppleRuntime.objc_msgSend(empty, AppleRuntime.Sel("init"));
                AppleRuntime.objc_msgSend(dict, AppleRuntime.Sel("setObject:forKey:"), empty, keyIo);
                AppleRuntime.objc_release(empty); // 已被 dict 持有 +1，平衡 alloc/init
            }

            return dict; // +1 自有
        }
        finally
        {
            AppleRuntime.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>extern CFString 常量取实际 CFStringRef（二级指针，须 <see cref="Marshal.ReadIntPtr"/> 解引用）。</summary>
    private static nint GetCoreMediaStringConstant(string symbol)
    {
        nint addr = AppleRuntime.GetGlobalSymbol("CoreMedia", symbol);
        return addr == nint.Zero ? nint.Zero : Marshal.ReadIntPtr(addr);
    }

    /// <summary>取得轨道的编解码器子类型 FourCharCode（如 'avc1' / 'hvc1' / 'aac '），用于映射 <see cref="VideoCodec"/> / <see cref="AudioCodec"/>。</summary>
    public static uint GetTrackCodecSubType(nint track)
    {
        nint fmtDesc = GetFirstTrackFormatDescription(track);
        if (fmtDesc == nint.Zero) return 0;
        uint sub = AppleRuntime.CMFormatDescriptionGetMediaSubType(fmtDesc);
        AppleRuntime.CFRelease(fmtDesc); // GetFirstTrackFormatDescription 返回 +1
        return sub;
    }

    /// <summary>取得轨道首个格式描述（[track formatDescriptions] 首元素），返回 +1（CFRetain）。</summary>
    public static nint GetFirstTrackFormatDescription(nint track)
    {
        nint pool = AppleRuntime.objc_autoreleasePoolPush();
        try
        {
            nint arr = AppleRuntime.objc_msgSend(track, AppleRuntime.Sel("formatDescriptions"));
            if (arr == nint.Zero) return nint.Zero;
            nint retainedArr = AppleRuntime.CFRetain(arr);
            try
            {
                nint count = AppleRuntime.CFArrayGetCount(retainedArr);
                if (count == nint.Zero) return nint.Zero;
                nint desc = AppleRuntime.CFArrayGetValueAtIndex(retainedArr, nint.Zero);
                return desc == nint.Zero ? nint.Zero : AppleRuntime.CFRetain(desc);
            }
            finally
            {
                AppleRuntime.CFRelease(retainedArr);
            }
        }
        finally
        {
            AppleRuntime.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// 提取轨道的编解码器私有配置（标准 avcC / hvcC 字节），供解码器作为 <see cref="VideoSettings.CodecConfiguration"/> / <see cref="AudioSettings.CodecConfiguration"/>。
    /// 路径：CMFormatDescriptionGetExtension(desc, kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms)
    /// → CFDictionary → "avcC"（或 "hvcC"）CFData → 字节。
    /// </summary>
    public static byte[] GetTrackExtraData(nint track)
    {
        nint fmtDesc = GetFirstTrackFormatDescription(track);
        if (fmtDesc == nint.Zero) return Array.Empty<byte>();
        try
        {
            nint atomKey = GetCoreMediaStringConstant("kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms");
            if (atomKey == nint.Zero) return Array.Empty<byte>();

            int status = AppleRuntime.CMFormatDescriptionGetExtension(fmtDesc, atomKey, out nint extDict);
            if (status != 0 || extDict == nint.Zero) return Array.Empty<byte>();

            // 依次尝试 avcC / hvcC
            foreach (string key in new[] { "avcC", "hvcC" })
            {
                nint cfKey = AppleRuntime.MakeNSString(key);
                nint cfData = AppleRuntime.CFDictionaryGetValue(extDict, cfKey);
                AppleRuntime.objc_release(cfKey);
                if (cfData == nint.Zero) continue;

                nint ptr = AppleRuntime.CFDataGetBytePtr(cfData);
                nint len = AppleRuntime.CFDataGetLength(cfData);
                if (ptr == nint.Zero || len <= 0) continue;

                var data = new byte[(int)len];
                Marshal.Copy(ptr, data, 0, (int)len);
                return data;
            }
            return Array.Empty<byte>();
        }
        finally
        {
            AppleRuntime.CFRelease(fmtDesc);
        }
    }

    /// <summary>
    /// 判定压缩样本是否非同步帧（非关键帧）。
    /// 路径：CMSampleBufferGetSampleAttachmentsArray(sbuf, 0) → 首 CFDictionary →
    /// 含 kCMSampleAttachmentKey_NotSync 即非关键帧（与 Mozilla / FFmpeg 判定一致）。
    /// 无附件数组时保守返回 <see langword="true"/>（视为非关键帧）。
    /// </summary>
    public static bool IsNotSyncSample(nint sampleBuffer)
    {
        nint attachments = AppleRuntime.CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, 0);
        if (attachments == nint.Zero) return true;
        nint count = AppleRuntime.CFArrayGetCount(attachments);
        if (count == nint.Zero) return true;

        nint dict = AppleRuntime.CFArrayGetValueAtIndex(attachments, nint.Zero);
        if (dict == nint.Zero) return true;

        nint notSyncKey = GetCoreMediaStringConstant("kCMSampleAttachmentKey_NotSync");
        if (notSyncKey == nint.Zero) return true;

        return AppleRuntime.CFDictionaryContainsKey(dict, notSyncKey) != 0;
    }
}
