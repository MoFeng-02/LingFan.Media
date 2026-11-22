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

// MFVideoDecoder 的 Output 职责段（部分类，由 MFVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeAsync/ProcessOutputOnce/FlushAsync/Reset/Dispose）在主文件；
// IMFTransform 同步 COM 调用，全部在所属线程执行（COM 线程亲和）。
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

    /// <summary>
    /// STREAM_CHANGE 后重新协商输出媒体类型，更新尺寸与输出 buffer 大小。
    /// MS 推荐流程：从 <c>GetOutputAvailableType</c> 枚举<b>新的</b>可用类型（优先 NV12）→ <c>SetOutputType</c>；
    /// 不能把 <c>GetOutputCurrentType</c> 的旧类型原样设回（运行时验证会返回 MF_E_INVALIDMEDIATYPE）。
    /// </summary>
    private bool RenegotiateOutput()
    {
        // RenegotiateOutput 触碰 _transform/_outputTypePtr，须处于 gate 内。
        // 正常仅从 DecodeAsync/FlushAsync（已在 gate 内）调用，此处再包裹一次无副作用（嵌套 Enter/Exit 配对）。
        if (!_transformGate.TryEnter()) return false;
        try
        {
            Guid nv12 = MFConstants.MFVideoFormat_NV12;
            IntPtr newType = SelectOutputType(ref nv12);
            if (newType == IntPtr.Zero)
            {
                _logger.LogWarning("MF 输出流变更后无可用输出类型");
                return false;
            }

            int hr = _setOutputType!(_transform, 0, newType, 0);
            if (hr < 0)
            {
                Marshal.Release(newType);
                _logger.LogWarning("MF 输出流变更后 SetOutputType 失败: 0x{Hr:X8}", hr);
                return false;
            }

            // 应用成功后替换缓存的输出类型引用
            if (_outputTypePtr != IntPtr.Zero)
                Marshal.Release(_outputTypePtr);
            _outputTypePtr = newType;

            TryReadOutputDimensions();
            QueryOutputStreamInfo();
            _logger.LogDebug("MF 输出流重协商完成: {W}x{H}, cbSize={Size}", _width, _height, _outputBufferSize);
            return true;
        }
        finally { _transformGate.Exit(); }
    }

    /// <summary>
    /// 枚举 MFT 可用输出类型（GetOutputAvailableType，slotIndex=11），返回 subtype 匹配 <paramref name="wantedSubtype"/> 的类型
    /// （调用方接管引用）；未匹配则返回首个可用类型；枚举不到返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    private IntPtr SelectOutputType(ref Guid wantedSubtype)
    {
        var getAvail = MfVTable.Get<IMFTransform_GetOutputAvailableType>(_transform, 11); // 绝对 14
        Guid mtSubtype = MFConstants.MF_MT_SUBTYPE;
        IntPtr first = IntPtr.Zero;
        for (uint i = 0; ; i++)
        {
            int hr = getAvail(_transform, 0, i, out IntPtr type);
            if (hr < 0 || type == IntPtr.Zero)
                break;

            // IMFMediaType.GetGUID = slotIndex 7（IMFAttributes 第 8 方法；MFDemuxer.ParseTracks 同槽已验证）
            var getGuid = MfVTable.Get<IMFMediaType_GetGuid>(type, 7);
            if (getGuid(type, ref mtSubtype, out Guid subtype) >= 0 && subtype == wantedSubtype)
            {
                if (first != IntPtr.Zero) Marshal.Release(first);
                return type; // 命中目标 subtype（NV12）
            }

            if (first == IntPtr.Zero) first = type; // 记住首个作回退
            else Marshal.Release(type);
        }
        return first;
    }

    /// <summary>
    /// 探测该解码 MFT 是否声明 <c>MF_SA_D3D11_AWARE</c>（支持 Direct3D 11 视频解码）。
    /// </summary>
    /// <remarks>
    /// 为何必须探测：<c>IMFTransform::ProcessMessage</c> 的契约允许 MFT 对**不认识的消息直接返回 S_OK
    /// 静默忽略**，因此「SET_D3D_MANAGER 返回 S_OK」**不能**证明 DXVA 已启用。MFT 自报的
    /// MF_SA_D3D11_AWARE 属性才是能力层面的权威判据。属性缺失（MF_E_ATTRIBUTENOTFOUND）即视为不支持。
    /// </remarks>
    private bool QueryD3D11Aware()
    {
        var getAttrs = MfVTable.Get<IMFTransform_GetAttributes>(_transform, 5); // 绝对 8
        int hr = getAttrs(_transform, out IntPtr attrs);
        if (hr < 0 || attrs == IntPtr.Zero)
        {
            _logger.LogWarning("[DXVA-DIAG] IMFTransform.GetAttributes 失败：HRESULT=0x{HR:X8}，无法确认 D3D11 能力", hr);
            return false;
        }
        try
        {
            Guid key = MFConstants.MF_SA_D3D11_AWARE;
            hr = MfVTable.Get<IMFAttributes_GetUINT32>(attrs, 4)(attrs, ref key, out uint aware);
            if (hr < 0)
            {
                _logger.LogWarning("[DXVA-DIAG] MFT 未设置 MF_SA_D3D11_AWARE（HRESULT=0x{HR:X8}）→ 不支持 D3D11 硬解", hr);
                return false;
            }
            _logger.LogInformation("[DXVA-DIAG] MF_SA_D3D11_AWARE={Aware}", aware);
            return aware != 0;
        }
        finally
        {
            Marshal.Release(attrs);
        }
    }

    /// <summary>查询输出流信息：MFT 是否自行提供输出 sample 与所需 buffer 大小。</summary>
    private void QueryOutputStreamInfo()
    {
        int hr = _getOutputStreamInfo!(_transform, 0, out MftOutputStreamInfo info);
        if (hr >= 0)
        {
            _mftProvidesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            _outputBufferSize = info.cbSize;
        }
        else
        {
            // 查询失败时保守回退：按 NV12 最大预估（重协商后会再查）
            _mftProvidesSamples = false;
            if (_outputBufferSize == 0 && _width > 0 && _height > 0)
                _outputBufferSize = (uint)(_width * _height * 3 / 2);
        }
    }

    /// <summary>
    /// 从输出媒体类型读取尺寸：FRAME_SIZE（uint64 = (w&lt;&lt;32)|h）为宏块对齐<b>编码尺寸</b>（如 1920x1088）；
    /// <b>显示尺寸</b>（1920x1080）从 MF_MT_MINIMUM_DISPLAY_APERTURE（MFVideoArea blob）取，缺失时回退编码尺寸。
    /// </summary>
    private void TryReadOutputDimensions()
    {
        if (_outputTypePtr == IntPtr.Zero)
            return;
        Guid frameSizeKey = MFConstants.MF_MT_FRAME_SIZE;
        // IMFAttributes::GetUINT64 = slotIndex 5（运行时已验证）；mfplat 无 MFGetAttributeUINT64 导出（inline helper）
        int hr = MfVTable.Get<IMFMediaType_GetUINT64>(_outputTypePtr, 5)(_outputTypePtr, ref frameSizeKey, out ulong fs);
        if (hr >= 0 && fs != 0)
        {
            _codedWidth = (int)(fs >> 32);
            _codedHeight = (int)(fs & 0xFFFFFFFF);
            _width = _codedWidth;
            _height = _codedHeight;
        }

        // 显示孔径：MFVideoArea（16 字节）= MFOffset OffsetX(4: short fract + short value) ×2 + SIZE(cx4, cy4)
        // AOT 安全：用 GetAllocatedBlob（slot13，原生自分配 buffer），绕开 GetBlob 向调用方 buffer 大块写入在 AOT 崩溃的路径。
        Guid apertureKey = MFConstants.MF_MT_MINIMUM_DISPLAY_APERTURE;
        var kh = GCHandle.Alloc(apertureKey, GCHandleType.Pinned);
        IntPtr aperturePtr = kh.AddrOfPinnedObject();
        IntPtr blobPtr = IntPtr.Zero;
        try
        {
            var getAllocatedBlob = MfVTable.Get<IMFAttributes_GetAllocatedBlob>(_outputTypePtr, 13);
            if (getAllocatedBlob(_outputTypePtr, aperturePtr, out blobPtr, out uint blobSize) >= 0 && blobSize >= 16 && blobPtr != IntPtr.Zero)
            {
                // 原始字节留证：结构布局若理解有误，只有 hex 能对证（vtable/结构必须照抄头文件，不凭空猜测）
                var raw = new byte[16];
                Marshal.Copy(blobPtr, raw, 0, 16);
                _apertureBlobHex = Convert.ToHexString(raw);

                // MFOffset = { WORD fract; short value; }（fract 在低地址）；实际偏移 = value + fract/65536
                int offXValue = Marshal.ReadInt16(blobPtr, 2);
                ushort offXFract = (ushort)Marshal.ReadInt16(blobPtr, 0);
                int offYValue = Marshal.ReadInt16(blobPtr, 6);
                ushort offYFract = (ushort)Marshal.ReadInt16(blobPtr, 4);

                int cx = Marshal.ReadInt32(blobPtr, 8);
                int cy = Marshal.ReadInt32(blobPtr, 12);
                if (cx > 0 && cy > 0 && cx <= _codedWidth && cy <= _codedHeight)
                {
                    _width = cx;
                    _height = cy;
                    // 偏移必须落在编码帧内，否则视为异常并归零（宁可退回旧行为，也不越界读源 buffer）
                    _apertureOffsetX = offXValue >= 0 && offXValue + cx <= _codedWidth ? offXValue : 0;
                    _apertureOffsetY = offYValue >= 0 && offYValue + cy <= _codedHeight ? offYValue : 0;
                    if (offXFract != 0 || offYFract != 0)
                        _logger.LogWarning(
                            "[APERTURE] 显示孔径偏移含非零小数部分 fractX={FX} fractY={FY}（已按整数部分处理，可能产生半像素误差）",
                            offXFract, offYFract);
                }
            }
        }
        finally
        {
            // GetAllocatedBlob 用 CoTaskMem 分配，须 FreeCoTaskMem（非 FreeHGlobal）。
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
            kh.Free();
        }
    }
}
