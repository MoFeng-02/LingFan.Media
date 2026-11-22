using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LingFan.Media.Abstractions;
using LingFan.Media.Backends.MediaFoundation.Concurrency;
using LingFan.Media.Backends.MediaFoundation.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LingFan.Media.GPUShare.D3D11;

namespace LingFan.Media.Backends.MediaFoundation.Decoders;

// MFVideoDecoder 的样本提取职责段（部分类，按职责拆分，编译期等价）。
// IMFSample → VideoFrame（GPU 纹理提取 / CPU 拷贝）与非 DXGI 缓冲诊断；输出协商在 Output 主段。
internal sealed partial class MFVideoDecoder
{
    /// <summary>从输出 sample 提取 NV12 数据并构建 <see cref="VideoFrame"/>（接管并释放 sample 引用）。</summary>
    private unsafe VideoFrame? ExtractFrame(IntPtr sample, MediaPacket sourcePacket)
    {
        try
        {
            // 时间戳优先从输出 sample 读（B 帧重排后输出顺序 ≠ 输入顺序）；失败回退输入 packet
            var ts = sourcePacket.Timestamp;
            var dur = sourcePacket.Duration;
            var getTime = MfVTable.Get<IMFSample_GetSampleTime>(sample, 32);     // IMFAttributes(30) + 2
            var getDur = MfVTable.Get<IMFSample_GetSampleDuration>(sample, 34);  // IMFAttributes(30) + 4
            if (getTime(sample, out long sampleTime) >= 0)
            {
                ts = TimeSpan.FromTicks(sampleTime);
                _lastFramePts = ts;
            }
            else if (_lastFramePts != TimeSpan.Zero)
            {
                // DRAIN/重排帧时间戳缺失：用上一成功帧 PTS + Duration 递增外推，禁用 0
                // （否则末段帧 PTS=0 → delta 巨负 → 必 Drop，正是尾帧丢失诱因之一）。
                ts = _lastFramePts + _lastFrameDur;
                _lastFramePts = ts;
            }
            if (getDur(sample, out long sampleDur) >= 0 && sampleDur > 0)
            {
                dur = TimeSpan.FromTicks(sampleDur);
                _lastFrameDur = dur;
            }

            // DXVA 零拷贝路径：必须用 GetBufferByIndex 取 sample 原始 buffer，再 QI IMFDXGIBuffer。
            // 绝不能改用 ConvertToContiguousBuffer：其契约就是「把 sample 合并并读回到连续系统内存」，
            //    永远返回 CPU buffer、绝不可能承载 DXGI 纹理——用 ConvertToContiguousBuffer 做零拷贝在原理上注定失败。
            // vtable 槽位（与运行时已验证的 ConvertToContiguousBuffer=38 / AddBuffer=39 一致，IMFSample 第 8 方法）：
            //   GetBufferByIndex = 绝对 40 → slotIndex 37
            IntPtr rawBuffer = IntPtr.Zero;
            if (_dxvaActive)
            {
                var getBuffer = MfVTable.Get<IMFSample_GetBufferByIndex>(sample, 37);
                int hrRaw = getBuffer(sample, 0, out rawBuffer);
            if (hrRaw >= 0 && rawBuffer != IntPtr.Zero)
            {
                var gpu = TryExtractGpuTexture(rawBuffer);
                    if (gpu is not null)
                    {
                        System.Threading.Interlocked.Increment(ref _gpuZeroCopyFrames);
                        // rawBuffer 是 IMFMediaBuffer 自身的引用（GetBufferByIndex 已 AddRef），纹理引用由 gpu 持有。
                        InteropTrace.ReleaseComPtr(rawBuffer, "ExtractFrame:dxva-rawBuffer");
                        return new VideoFrame(_width, _height, PixelFormat.NV12, gpu, ts, dur, sourcePacket.KeyFrame);
                    }
                    // 原始 buffer 非 DXGI（或 GetResource 失败）：释放 rawBuffer，回落 ConvertToContiguousBuffer 软解。
                    InteropTrace.ReleaseComPtr(rawBuffer, "ExtractFrame:dxva-rawBuffer-fallback");
                    rawBuffer = IntPtr.Zero;
                }
                else
                {
                    _logger.LogWarning("GetBufferByIndex(0) 失败，HRESULT=0x{HR:X8}，回落 ConvertToContiguousBuffer", hrRaw);
                    rawBuffer = IntPtr.Zero;
                }
            }

            // 软解拷贝路径：输出数据可能分散在多个 buffer，ConvertToContiguousBuffer 合并并读回系统内存。
            // IMFSample 槽位：ConvertToContiguousBuffer = 绝对 41 → slotIndex 38
            // （IMFAttributes 恰 30 方法，IMFSample 第 9 方法；运行时已验证 slot38 返回有效 buffer）
            var toContig = MfVTable.Get<IMFSample_ConvertToContiguousBuffer>(sample, 38);
            int hr = toContig(sample, out IntPtr outBuffer);
            if (hr < 0 || outBuffer == IntPtr.Zero)
                return null;

            try
            {
                var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(outBuffer, 0);
                var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(outBuffer, 1);
                var getLen = MfVTable.Get<IMFMediaBuffer_GetCurrentLength>(outBuffer, 2);

                getLen(outBuffer, out uint currentLength);
                if (currentLength == 0)
                    return null;

                // 确保尺寸已知（首帧 STREAM_CHANGE 后刚协商出）
                if (_width <= 0 || _height <= 0)
                    TryReadOutputDimensions();
                if (_width <= 0 || _height <= 0)
                {
                    _logger.LogWarning("MF 解码输出尺寸未知，跳过该帧。");
                    return null;
                }

                // COM 配对原则（与 MFDemuxer.ExtractPacket 同构）：
                //    IMFMediaBuffer.Unlock 只能与【成功的】Lock 配对，且恰好一次。
                //    下方「Lock → 拷贝 → Unlock」整体置于【嵌套 try】，所有提前 return
                //    （currentLength==0 / 尺寸未知 / Lock 失败）都发生在进入该嵌套 try 之前，
                //    故绝不会触发 Unlock。旧代码把 Unlock 放在外层 finally，导致未 Lock 即 Unlock
                //    （2D/DXGI 临时拷贝实现的 Unlock 会野指针写）→ 滞后至下次 CLR 堆操作才以原生堆损坏暴露。
                // COM 配对原则（与 MFDemuxer.ExtractPacket 同构）：
                //    IMFMediaBuffer.Unlock 只能与【成功的】Lock 配对，且恰好一次。
                //    错误链：经 InteropTrace 记录并（严格模式）校验 Lock/Unlock 配对。
                hr = InteropTrace.LockBuffer(outBuffer, lockDel, out IntPtr pData, out _, out _,
                    "ExtractFrame:IMFMediaBuffer.Lock");
                if (hr < 0)
                {
                    _logger.LogWarning("IMFMediaBuffer.Lock 失败: HRESULT=0x{HR:X8}（未 Unlock，符合配对规范）", hr);
                    return null;
                }
                SoftwareFrameResource resource;
                try
                {
                    var src = new ReadOnlySpan<byte>((void*)pData, (int)currentLength);

                    // 首帧布局诊断：把「显示尺寸 / 编码尺寸 / MF 源 buffer 真实长度」一次性摊开。
                    // 关键判据——currentLength 是否等于 codedW*codedH*3/2：
                    //   相等 → 下方裁剪路径的「源 stride == codedWidth」假定成立；
                    //   不等 → 假定破产，逐行拷贝会渐进错位（斜条纹/花屏）。
                    if (!_loggedLayoutOnce)
                    {
                        _loggedLayoutOnce = true;
                        bool cropPath = !(_codedWidth <= 0 || (_codedWidth == _width && _codedHeight == _height));
                        long codedLen = (long)_codedWidth * _codedHeight * 3 / 2;
                        long compactLen = (long)_width * _height * 3 / 2;
                        int uvRows = _codedHeight * 3 / 2;
                        int derivedStride = uvRows > 0 ? (int)(currentLength / (uint)uvRows) : 0;
                        _logger.LogInformation(
                            "[NV12-LAYOUT] display={W}x{H} coded={CW}x{CH} MF源buffer={Len}B | " +
                            "codedW*codedH*1.5={CodedLen}B compact={CompactLen}B | 按codedH推导源stride={S} | 路径={Path} | stride假定={Verdict}",
                            _width, _height, _codedWidth, _codedHeight, currentLength,
                            codedLen, compactLen, derivedStride,
                            cropPath ? "裁剪逐行" : "整块拷贝",
                            currentLength == codedLen ? "成立" : "破产");

                        // 显示孔径偏移留证：不臆断偏移为 0，摊开实际值 + 原始 blob 供对证。
                        int gapX = _codedWidth - _width;
                        int gapY = _codedHeight - _height;
                        string offVerdict = _apertureOffsetX == 0 && _apertureOffsetY == 0
                            ? (gapX > 0 && gapX % 2 == 0 && _apertureOffsetX == 0
                                ? "偏移=0（裁剪全在右/下边）"
                                : "偏移=0")
                            : $"偏移非零 → 旧代码从(0,0)起裁会整体平移 {_apertureOffsetX}列/{_apertureOffsetY}行";
                        _logger.LogInformation(
                            "[APERTURE] OffsetX={OX} OffsetY={OY} | 编码-显示差 宽={GX} 高={GY} | blob16B={Hex} | {Verdict}",
                            _apertureOffsetX, _apertureOffsetY, gapX, gapY,
                            _apertureBlobHex ?? "(未取到)", offVerdict);

                        // 自给自足的黄金参照：把【裁剪之前】的完整 coded 帧落盘，
                        // 直接肉眼/数值确认真实画面从第几列开始，无需 ffmpeg 等外部解码器对照。
                        MFCodedFrameDump.TryDump(src, _codedWidth, _codedHeight, _width, _height,
                            _apertureOffsetX, _apertureOffsetY, _logger);
                    }

                    if (_codedWidth <= 0 || (_codedWidth == _width && _codedHeight == _height))
                    {
                        // 编码尺寸即显示尺寸（或编码尺寸未知）：整块拷贝
                        resource = new SoftwareFrameResource(_width, _height, PixelFormat.NV12, (int)currentLength);
                        src.CopyTo(resource.Data.Span);
                    }
                    else
                    {
                        // 显示孔径裁剪：NV12 编码布局 = Y[codedW×codedH] + UV[codedW×codedH/2]，
                        // 逐行拷贝到紧凑的 display 布局（stride 假定 = codedWidth，MFT 输出 NV12 无额外行距）。
                        // 修正：起点必须用 MFVideoArea.OffsetX/OffsetY，不得臆断为 0。
                        //    旧注释「H264 裁剪只发生在右/下边」是未经验证的假设——非对称裁剪（如 1920→1906
                        //    左右各去 7）会让 (0,0) 起裁的画面整体平移，左边缘吃进编码填充（宏块边缘扩展）。
                        // UV 列偏移须对齐到偶数：NV12 的 UV 是 U,V 交错对，一对覆盖 2 列 Y。
                        int offX = _apertureOffsetX;
                        int offY = _apertureOffsetY;
                        int uvOffX = offX & ~1;      // 向下取偶
                        int uvOffY = offY / 2;

                        int dstLen = _width * _height * 3 / 2;
                        resource = new SoftwareFrameResource(_width, _height, PixelFormat.NV12, dstLen);
                        var dst = resource.Data.Span;
                        for (int y = 0; y < _height; y++)
                            src.Slice((offY + y) * _codedWidth + offX, _width).CopyTo(dst.Slice(y * _width, _width));
                        int srcUv = _codedWidth * _codedHeight;
                        int dstUv = _width * _height;
                        for (int y = 0; y < _height / 2; y++)
                            src.Slice(srcUv + (uvOffY + y) * _codedWidth + uvOffX, _width)
                               .CopyTo(dst.Slice(dstUv + y * _width, _width));
                    }
                }
                finally
                {
                    InteropTrace.UnlockBuffer(outBuffer, unlockDel, "ExtractFrame:IMFMediaBuffer.Unlock");
                }

                System.Threading.Interlocked.Increment(ref _cpuFallbackFrames);
                return new VideoFrame(_width, _height, PixelFormat.NV12, resource, ts, dur, sourcePacket.KeyFrame);
            }
            finally
            {
                InteropTrace.ReleaseComPtr(outBuffer, "ExtractFrame:outBuffer");
            }
        }
        finally
        {
            InteropTrace.ReleaseComPtr(sample, "ExtractFrame:sample");
        }
    }

    /// <summary>
    /// 从 DXVA 输出 buffer 提取 GPU 纹理（零拷贝）。
    /// </summary>
    /// <remarks>
    /// <para>优先经当前激活渲染器生产者把 D3D11 纹理导入为 <see cref="IGpuTextureResource"/>（MF DXVA → GPU 零拷贝上屏）；
    /// 导入不可用 / 失败（扩展缺失、纹理非共享、切片不兼容）→ 回落 <see cref="MfD3D11TextureResource"/>（D3D11 零拷贝）。
    /// 导入成功与否以行为副作用为准，绝不误报「零拷贝已生效」。</para>
    /// <para>依赖倒置：仅经 <see cref="IGpuFrameProducer"/> 抽象，不引用渲染器模块；
    /// COM 引用计数严守 GetResource 配对（成功导入时释放 tex 的 GetResource 引用，共享句柄由渲染器侧消费）。</para>
    /// </remarks>
    private IGpuTextureResource? TryExtractGpuTexture(IntPtr buffer)
    {
        Guid iidDxgi = MFConstants.IID_IMFDXGIBuffer;
        int hr = Marshal.QueryInterface(buffer, in iidDxgi, out IntPtr dxgi);
        if (hr < 0 || dxgi == IntPtr.Zero)
        {
            // 每帧都打 warning 会刷屏，故只深度诊断一次。
            if (!_loggedDxvaDiagOnce)
            {
                _loggedDxvaDiagOnce = true;
                DiagnoseNonDxgiBuffer(buffer, hr);
            }
            return null;
        }
        try
        {
            var getResource = MfVTable.Get<MfDxvaInterop.IMFDXGIBuffer_GetResource>(dxgi, 0);   // 绝对 3
            var getSub = MfVTable.Get<MfDxvaInterop.IMFDXGIBuffer_GetSubresourceIndex>(dxgi, 1); // 绝对 4

            Guid iidTex = MFConstants.IID_ID3D11Texture2D;
            hr = getResource(dxgi, ref iidTex, out IntPtr tex);
            if (hr < 0 || tex == IntPtr.Zero)
            {
                _logger.LogWarning("[DXVA-DIAG] IMFDXGIBuffer.GetResource(ID3D11Texture2D) 失败：HRESULT=0x{HR:X8}", hr);
                return null;
            }
            hr = getSub(dxgi, out uint sub);
            if (hr < 0)
            {
                Marshal.Release(tex); // COM 配对：GetResource 成功已 AddRef 纹理，getSub 失败须释放，否则纹理引用泄漏
                _logger.LogWarning("[DXVA-DIAG] IMFDXGIBuffer.GetSubresourceIndex 失败：HRESULT=0x{HR:X8}", hr);
                return null;
            }

            // GPU 零拷贝：D3D11 纹理 → DXGI 共享 NT 句柄 → 渲染器生产者导入为 GPU 纹理。
            if (_gpuProducer is not null)
            {
                try
                {
                    // 共享句柄导出统一走 GPUShare.D3D11（仓级互操作事实源）：
                    // QI IDXGIResource1（失败抛 COMException → catch 回落，与原语义一致）→ CreateSharedHandle 导出 NT 句柄。
                    IntPtr dxgiRes1 = D3D11Interop.QueryInterface(tex, D3D11Interop.IID_IDXGIResource1);
                    nint sharedHandle;
                    try
                    {
                        sharedHandle = D3D11Interop.CreateSharedHandle(
                            dxgiRes1, D3D11Interop.SharedResourceReadWrite);
                    }
                    finally
                    {
                        D3D11Interop.Release(dxgiRes1);
                    }
                    int arrayLayers = (int)D3D11Interop.GetTexture2DDesc(tex).ArraySize;
                    var source = new GpuFrameImportSource
                    {
                        Kind = GpuFrameImportKind.D3D11SharedHandle,
                        Handle = sharedHandle,
                        Width = _width,
                        Height = _height,
                        Format = PixelFormat.NV12,
                        SubresourceIndex = (int)sub,
                        ArrayLayers = arrayLayers,
                    };
                // TryImport 调用即把共享句柄所有权转移给生产者；无论导入成功或失败，生产者均负责 CloseHandle，
                // 调用方（解码器）不得再关闭，避免与生产者双关（S_OK≠被接受）。
                if (_gpuProducer.TryImport(source, out var gpuTex) && gpuTex is not null)
                {
                    // 导入成功：GPU 纹理已绑定外部内存，原 tex 的 GetResource 引用可回收；句柄所有权已由生产者接管。
                    Marshal.Release(tex);
                    return gpuTex;
                }
            }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DXVA-FRAMEPATH] GPU 零拷贝导入异常，回落 D3D11 资源。");
                }
            }

            return new MfD3D11TextureResource(tex, _width, _height, PixelFormat.NV12, (int)sub);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DXVA-DIAG] DXVA 纹理提取异常，回落软解拷贝");
            return null;
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>
    /// DXVA 已激活（PROVIDES_SAMPLES=True）但输出 buffer 不支持 <c>IMFDXGIBuffer</c> 时的一次性深度诊断。
    /// 说明 MFT 把 GPU 纹理读回到了系统内存（"半 DXVA"）：解码走硬解，但零拷贝提取失败。
    /// 本方法摊开 buffer 真实身份（是否仍是 IMFMediaBuffer / IMF2DBuffer / ID3D11Texture2D）与
    /// 输出媒体类型属性（subtype / NominalRange / DefaultStride），用于定位「读回」的成因。
    /// 诊断手段：IID 与 Lock 槽位均取已核验正确的值——IID 曾字节级错误令所有 QI 假阴性，现已订正；
    /// Lock 复用真实 CPU 路径的 vtable 槽位 0/1/2，不依赖 QI，故即使 QI 仍异常也能给出真值。
    /// </summary>
    private void DiagnoseNonDxgiBuffer(IntPtr buffer, int dxgiHr)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[DXVA-DIAG] 输出 buffer 非 IMFDXGIBuffer(QI=0x").Append(dxgiHr.ToString("X8")).Append(") → 深度诊断：");

            // (1) buffer 是否仍是有效 IMFMediaBuffer（用【已订正】的 IID，真值）
            int hrMb = Marshal.QueryInterface(buffer, in MFConstants.IID_IMFMediaBuffer, out IntPtr mb);
            bool isMb = hrMb >= 0;
            sb.Append(" IMFMediaBuffer=").Append(isMb ? "是" : "否");
            if (isMb)
            {
                // (2) 真值校验：直接 Lock（复用 CPU 路径 vtable 槽位 0/1/2）取长度，确认是系统内存 NV12。
                //    若 Lock 成功且长度≈_outputBufferSize(cbSize)，则确认「解码器产出系统内存读回 buffer」。
                try
                {
                    var lockDel = MfVTable.Get<IMFMediaBuffer_Lock>(mb, 0);
                    var unlockDel = MfVTable.Get<IMFMediaBuffer_Unlock>(mb, 1);
                    var getLen = MfVTable.Get<IMFMediaBuffer_GetCurrentLength>(mb, 2);
                    if (getLen(mb, out uint len) >= 0) sb.Append(" len=").Append(len);
                    if (lockDel(mb, out IntPtr _, out uint _, out uint _) >= 0)
                    {
                        sb.Append(" Lock=OK");
                        unlockDel(mb);
                    }
                    else sb.Append(" Lock=FAIL");
                }
                catch (Exception lex) { sb.Append(" Lock=异常(").Append(lex.GetType().Name).Append(")"); }
                Marshal.Release(mb);
            }

            // (3) 是否 DXVA2( D3D9 )表面：微软 H264 解码器在部分配置下内部走 DXVA2 而非 DXGI，
            //    此时 buffer 实现 IMF2DBuffer，零拷贝须走 D3D9 路径而非 IMFDXGIBuffer。
            int hr2d = Marshal.QueryInterface(buffer, in MFConstants.IID_IMF2DBuffer, out IntPtr b2d);
            sb.Append(" | IMF2DBuffer=").Append(hr2d >= 0 ? "是" : "否");
            if (hr2d >= 0) Marshal.Release(b2d);

            // (4) 是否直接包了 ID3D11Texture2D（极少数 MFT 不经 IMFDXGIBuffer 直接包纹理）
            int hrTex = Marshal.QueryInterface(buffer, in MFConstants.IID_ID3D11Texture2D, out IntPtr tex);
            sb.Append(" | ID3D11Texture2D=").Append(hrTex >= 0 ? "是" : "否");
            if (hrTex >= 0) Marshal.Release(tex);

            // (5) 输出媒体类型属性：subtype / NominalRange / DefaultStride
            if (_outputTypePtr != IntPtr.Zero)
            {
                var getGuid = MfVTable.Get<IMFMediaType_GetGuid>(_outputTypePtr, 7);
                Guid subKey = MFConstants.MF_MT_SUBTYPE;
                if (getGuid(_outputTypePtr, ref subKey, out Guid sub) >= 0)
                    sb.Append(" | outSubtype=").Append(sub.ToString("B").Substring(0, 8));
                var getU32 = MfVTable.Get<IMFAttributes_GetUINT32>(_outputTypePtr, 4);
                Guid nrKey = MFConstants.MF_MT_VIDEO_NOMINAL_RANGE;
                int hrNr = getU32(_outputTypePtr, ref nrKey, out uint nr);
                sb.Append(" | NominalRange=").Append(hrNr >= 0 ? nr.ToString() : "缺失");
                Guid strideKey = MFConstants.MF_MT_DEFAULT_STRIDE;
                int hrSt = getU32(_outputTypePtr, ref strideKey, out uint st);
                sb.Append(" | DefaultStride=").Append(hrSt >= 0 ? ((int)st).ToString() : "缺失");
            }

            _logger.LogWarning(sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DXVA-DIAG] 深度诊断自身异常（忽略）");
        }
    }
}
