using System.Runtime.InteropServices;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Java.Nio;
using LingFan.Media.GPUShare.Android; // AndroidHardwareBufferFrameResource（跨 GPU API 中立帧 DTO，消费方向：Backends→GPUShare.Android）
using LingFan.Media.GPUShare.Android.Bridge; // AndroidAhbRgbaBridge（解码侧 GLES/EGL→AHB 桥接，GPUShare.Android android 资产）
using AndroidMediaCodec = Android.Media.MediaCodec;
using PixelFormat = LingFan.Media.Abstractions.PixelFormat;

namespace LingFan.Media.Backends.MediaCodec.Decoders;

// AndroidVideoDecoder 的 FrameExtract 职责段（部分类，由 AndroidVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/Initialize/DecodeAsync/FlushAsync/FeedInput/ReadOutput/DrainOutput/Dispose）在主文件；
// MediaCodec 双路径（Surface/AHB 零拷贝 + ByteBuffer I420）解码。
internal sealed partial class AndroidVideoDecoder
{
    /// <summary>闩取 SurfaceTexture 待处理帧并包装为视频帧（ConvertLatest 跨线程阻塞往返：
    /// GL 线程分配 AHB → updateTexImage 闩帧 → 渲染 → glFinish → 移交 AHB 引用）。
    /// 失败（GL 异常态）返回 null 并保留 _pendingSurfaceTextureFrame 标志待下轮重试；
    /// 成功则清除标志。pts 取当前 _lastPresentationTimeUs——"先闩后排空"调用序保证其
    /// 恒与闩取帧对应（闩取时不可能已有更新帧渲入）。</summary>
    private VideoFrame? TryLatchSurfaceTextureFrame()
    {
        _logger.LogTrace("[ANDROID-AHB-DEC]  ConvertLatest（等待 GL 线程闩帧）");
        nint ahb = _bridge!.ConvertLatest();
        _logger.LogTrace("[ANDROID-AHB-DEC]  ConvertLatest 返回 ahb={Ahb}", ahb);
        if (ahb == nint.Zero)
        {
            _logger.LogWarning("[ANDROID-AHB-DEC] ConvertLatest 返回 0（GL 异常态），保留待闩帧标志待下轮重试。");
            return null;
        }
        _pendingSurfaceTextureFrame = false;

        int w = _bridge.FrameWidth;
        int h = _bridge.FrameHeight;
        TimeSpan pts = _lastPresentationTimeUs >= 0
            ? TimeSpan.FromTicks(_lastPresentationTimeUs * 10) // us → ticks（1 tick = 100ns）
            : TimeSpan.Zero;

        // AHB 引用所有权已由桥接移交本帧资源；帧 Dispose 时经 AHardwareBuffer_release 释放，
        // 渲染侧 Vulkan 导入持有独立引用，无悬挂/双释放。
        var resource = new AndroidHardwareBufferFrameResource(ahb, w, h, PixelFormat.RGBA32);
        if ((_framesProduced % LogInterval) == 0)
            _logger.LogTrace("[ANDROID-AHB-DEC] 产帧 #{Count} {W}x{H} {Fmt} pts={Pts:g}",
                _framesProduced, w, h, PixelFormat.RGBA32, pts);
        _framesProduced++;
        _drainDequeued++;
        return new VideoFrame(w, h, PixelFormat.RGBA32, resource, pts, TimeSpan.Zero, false, _colorInfo,
            _rotationDegrees);
    }

    /// <summary>从 <see cref="Image"/>（YUV_420_888，灵活 YUV420 经 getOutputImage 取得）用托管 CPU 平面提取
    /// 产出标准紧凑帧。全设备通用：plane 顺序（#0=Y、#1=U、#2=V）；Y pixelStride 恒为 1；U/V 的
    /// pixelStride 为 1（planar I420）或 2（semiplanar NV12，U 在偶字节、V 在奇字节，MediaCodec 输出通常如此）。
    /// 可见区由 <see cref="Image.CropRect"/>（= 输出格式 crop-* 矩形）界定，跳过 16 对齐填充；缺失时退回
    /// <c>_visibleWidth/_visibleHeight</c> 字段（由 ReadOutputFormat 解析）。调用方负责关闭 Image 与释放输出缓冲。</summary>
    /// <remarks>官方 AOSP CTS getDataFromImage 范式：frame 尺寸取 crop 宽高（非 image.Width/Height，后者含对齐填充），
    /// 按 (cropTop+row)*rowStride + cropLeft*pixelStride 索引逐行提取；半平面 U 偶 V 奇。
    /// <para><b>性能优化</b>：半平面输入直接产出 <see cref="PixelFormat.NV12"/>，避免先拆成 I420 再被 SkiaVideoPresenter
    /// 重新按 NV12 语义读取的二次拆分/合并；planar 输入仍产出 <see cref="PixelFormat.YUV420P"/>。</para>
    /// 色彩顺序若个别设备为 NV21（U 奇 V 偶）将偏色，此时交换 U/V 即可（本实现默认 NV12）。</remarks>
    private VideoFrame? ExtractI420FromImage(Image image, long infoPtsUs, bool keyFrame)
    {
        var planes = image.GetPlanes();
        if (planes is null || planes.Length < 3) return null;

        // 可见区：优先 image.CropRect（官方权威 per-frame），回退到输出格式解析的 _visible* 字段。
        // 关键：image.Width/Height 是含 16 对齐填充的缓冲尺寸，可见像素只占 crop 矩形内一部分。
        int cl, ct, vw, vh;
        var crop = image.CropRect;
        if (crop is not null && crop.Width() > 0 && crop.Height() > 0)
        {
            cl = crop.Left; ct = crop.Top; vw = crop.Width(); vh = crop.Height();
        }
        else if (_visibleWidth > 0 && _visibleHeight > 0)
        {
            cl = _cropLeft; ct = _cropTop; vw = _visibleWidth; vh = _visibleHeight;
        }
        else
        {
            cl = 0; ct = 0; vw = image.Width; vh = image.Height;
        }
        if (vw <= 0 || vh <= 0) return null;

        var yPlane = planes[0];
        var uPlane = planes[1];
        var vPlane = planes[2];
        var yBuf = yPlane.Buffer;
        var uBuf = uPlane.Buffer;
        var vBuf = vPlane.Buffer;
        if (yBuf is null || uBuf is null || vBuf is null) return null;

        int yRowStride = yPlane.RowStride;
        int yPixelStride = yPlane.PixelStride; // 恒为 1
        int uvRowStride = uPlane.RowStride;
        int uvPixelStride = uPlane.PixelStride; // 1=planar(I420) / 2=semiplanar(NV12)
        int cw = (vw + 1) / 2;
        int ch = (vh + 1) / 2;
        int ySize = vw * vh;

        // 半平面（NV12）且 crop-left/width 均为偶数：直接输出紧凑 NV12（Y + UV 交错），避免拆成 I420 再被 presenter 重拼。
        // crop-left 为奇数或宽度为奇数时，UV 对无法字节对齐，回退到逐像素 I420 拆分（保证正确性）。
        bool fastNv12 = uvPixelStride == 2 && (cl & 1) == 0 && (vw & 1) == 0;
        PixelFormat outFmt = fastNv12 ? PixelFormat.NV12 : PixelFormat.YUV420P;
        int uvRowBytes = fastNv12 ? (vw + 1) & ~1 : cw; // NV12 紧凑行须为偶字节（UV 对），I420 行 = cw
        int uvPlaneBytes = uvRowBytes * ch;
        int totalBytes = fastNv12 ? ySize + uvPlaneBytes : ySize + 2 * cw * ch;

        // 几何变化探测：与上一帧逐项比对，变化即打点（开播前 8 帧无条件打点作基线）
        if (!_geoInit
            || vw != _geoVw || vh != _geoVh || cl != _geoCl || ct != _geoCt
            || yRowStride != _geoYRow || uvRowStride != _geoUvRow || uvPixelStride != _geoUvPix)
        {
            bool first = !_geoInit;
            if (!first) _geoChanges++;
            // 变化/首帧/开播窗口内必打；稳态每 120 帧打一次心跳，证明确实「一直没变」。
            if (first || _framesProduced < 8 || (_framesProduced % 120) == 0 || _geoChanges <= 8)
            {
                _logger.LogInformation(
                    "[ANDROID-VID] 帧几何{Flag} seq={Seq} pts={Pts}ms 可见={VW}x{VH} crop=({CL},{CT}) " +
                    "yRow={YRow} yPix={YPix} uvRow={UvRow} uvPix={UvPix} 快路径={Fast} 累计变化={Changes}",
                    first ? "基线" : "变化", _framesProduced, infoPtsUs / 1000,
                    vw, vh, cl, ct, yRowStride, yPixelStride, uvRowStride, uvPixelStride,
                    fastNv12 ? "NV12" : "I420", _geoChanges);
            }
            _geoVw = vw; _geoVh = vh; _geoCl = cl; _geoCt = ct;
            _geoYRow = yRowStride; _geoUvRow = uvRowStride; _geoUvPix = uvPixelStride;
            _geoInit = true;
        }

        var resource = new SoftwareFrameResource(vw, vh, outFmt, checked(totalBytes));
        Span<byte> dst = resource.Data.Span;

        // 平面统计采样
        // 采样策略：开播前 8 帧全采（对齐「花屏窗口」），之后每 120 帧一采作稳态基线。
        // 判读：若开播期 U/V 均值·范围与稳态显著不同 ⇒ 解码器早期输出本身异常（渲染侧无责）；
        //       若两者一致 ⇒ 解码输出正常，问题在下游（时序/缓冲/上屏）。
        bool diag = _framesProduced < 8 || (_framesProduced % 120) == 0;
        long extractStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Y 平面：整平面拷入 _extractRaw，再逐行按可见区拷贝（Y 与 UV 布局无关，共用）。
        int yCap = yBuf.Capacity();
        if (_extractRaw.Length < yCap) _extractRaw = new byte[yCap];
        yBuf.Rewind();
        yBuf.Get(_extractRaw, 0, yCap);
        long ySum = 0; byte yMin = 255, yMax = 0; int yNonZero = 0;
        int yDst = 0;
        for (int row = 0; row < vh; row++)
        {
            int srcOff = (ct + row) * yRowStride + cl * yPixelStride;
            CopyRow(_extractRaw, srcOff, vw, dst, yDst, yCap);
            if (diag)
                for (int x = 0; x < vw; x++)
                {
                    byte b = dst[yDst + x];
                    ySum += b; if (b < yMin) yMin = b; if (b > yMax) yMax = b; if (b != 0) yNonZero++;
                }
            yDst += vw;
        }

        int chromaRow0 = ct / 2;
        int chromaCol0 = cl / 2;

        // 缓冲上界诊断（仅首帧）：_extractRaw 被 Y/U/V 轮流复用，数组长度 = 三者最大容量。
        // 若 uCap/vCap 远小于 _extractRaw.Length，说明 UV 行按数组长度取上界会读到残留 Y 数据
        // ——这正是画面下半色度错乱（块状破碎/色块错位）的成因。打印三值以便在设备上确认。
        if (diag)
        {
            int uCapDiag = uBuf.Capacity();
            int vCapDiag = vBuf.Capacity();
            int lastUvSrcOff = (chromaRow0 + ch - 1) * uPlane.RowStride + chromaCol0 * 2;
            _logger.LogInformation(
                "[ANDROID-VID] 缓冲上界诊断: _extractRaw.Length={RawLen} yCap={YCap} uCap={UCap} vCap={VCap} " +
                "yRowStride={YRow} uvRowStride={UvRow} 末行UV源偏移={LastOff} 末行需要末字节={LastNeed} " +
                "按数组长度是否越界={OverByArray} 按uCap是否越界={OverByCap}",
                _extractRaw.Length, yCap, uCapDiag, vCapDiag, yRowStride, uvRowStride, lastUvSrcOff,
                lastUvSrcOff + (fastNv12 ? uvRowBytes : cw),
                lastUvSrcOff + (fastNv12 ? uvRowBytes : cw) > _extractRaw.Length,
                lastUvSrcOff + (fastNv12 ? uvRowBytes : cw) > uCapDiag);
        }

        long uSum = 0; byte uMin = 255, uMax = 0; int uNonZero = 0;
        long vSum = 0; byte vMin = 255, vMax = 0; int vNonZero = 0;

        if (fastNv12)
        {
            // 半平面 NV12：plane[1] 已含 UV 交错（U 偶 V 奇），直接按行拷入 destination UV 区。
            // 源行可能有行尾填充（uvRowStride），目标紧凑行 uvRowBytes；copy 后不足处填 128。
            int uCap = uBuf.Capacity();
            if (_extractRaw.Length < uCap) _extractRaw = new byte[uCap];
            uBuf.Rewind();
            uBuf.Get(_extractRaw, 0, uCap);

            int uvDst = ySize;
            for (int cy = 0; cy < ch; cy++)
            {
                int srcOff = (chromaRow0 + cy) * uvRowStride + chromaCol0 * 2;
                int dstOff = uvDst + cy * uvRowBytes;
                // 上界必须是本平面实际装载长度 uCap：_extractRaw 为 Y 平面分配时远大于 UV 容量，
                // 按数组长度计算会让 UV 行越过有效边界读到残留 Y 像素（画面破碎成因）。
                int avail = uCap - srcOff;
                int copy = avail >= uvRowBytes ? uvRowBytes : avail > 0 ? avail : 0;
                // 越界检测：avail < 本行所需 ⇒ 该行尾部会读到残留数据（上一帧的 Y 像素），
                // 画面表现为块状破碎/色块错位。首帧诊断（[ANDROID-VID] 缓冲上界诊断）只在
                // _extractRaw 首次分配时打印、天然不越界，**覆盖不到稳态帧**，故在此逐帧检测。
                if (avail < uvRowBytes && !_uvClampWarned)
                {
                    _uvClampWarned = true;
                    // 注意：此处不用 `_logger?.`——_logger 为非空字段（构造函数已 `?? throw` 保证），
                    // 一旦使用 null 条件运算符，编译器会把后续所有不带 ? 的 _logger 调用判为 CS8604。
                    _logger.LogWarning(
                        "[ANDROID-VID] UV 行读取越界被截断（将读到残留数据→画面破碎）: " +
                        "帧=#{Frame} cy={Cy} srcOff={Off} uCap={Cap} 本行需要={Need} 实际可拷={Copy} " +
                        "uvRowStride={Stride} 可见={VW}x{VH}",
                        _framesProduced, cy, srcOff, uCap, uvRowBytes, copy, uvRowStride, vw, vh);
                }
                if (copy > 0)
                    _extractRaw.AsSpan(srcOff, copy).CopyTo(dst.Slice(dstOff, copy));
                if (copy < uvRowBytes)
                    dst.Slice(dstOff + copy, uvRowBytes - copy).Fill(128);

                if (diag && copy > 0)
                {
                    // 统计：U 在偶字节、V 在奇字节（标准 NV12）。
                    int statLen = copy & ~1;
                    for (int i = 0; i < statLen; i += 2)
                    {
                        byte u = _extractRaw[srcOff + i];
                        byte v = _extractRaw[srcOff + i + 1];
                        uSum += u; if (u < uMin) uMin = u; if (u > uMax) uMax = u; if (u != 0) uNonZero++;
                        vSum += v; if (v < vMin) vMin = v; if (v > vMax) vMax = v; if (v != 0) vNonZero++;
                    }
                }
            }
        }
        else if (uvPixelStride == 1)
        {
            // planar：U/V 各自独立平面，逐行拷贝 cw 字节（须按 uvRowStride 步进，含行填充）。
            int uCap = uBuf.Capacity();
            if (_extractRaw.Length < uCap) _extractRaw = new byte[uCap];
            uBuf.Rewind();
            uBuf.Get(_extractRaw, 0, uCap);

            int uDst = ySize;
            int vDst = ySize + cw * ch;
            for (int cy = 0; cy < ch; cy++)
            {
                int srcRow = (chromaRow0 + cy) * uvRowStride + chromaCol0;
                CopyRow(_extractRaw, srcRow, cw, dst, uDst + cy * cw, uCap);
                if (diag) AccumPlaneStats(dst, uDst + cy * cw, cw, ref uSum, ref uMin, ref uMax, ref uNonZero);
            }
            int vCap = vBuf.Capacity();
            if (_extractRaw.Length < vCap) _extractRaw = new byte[vCap];
            vBuf.Rewind();
            vBuf.Get(_extractRaw, 0, vCap);
            for (int cy = 0; cy < ch; cy++)
            {
                int srcRow = (chromaRow0 + cy) * uvRowStride + chromaCol0;
                CopyRow(_extractRaw, srcRow, cw, dst, vDst + cy * cw, vCap);
                if (diag) AccumPlaneStats(dst, vDst + cy * cw, cw, ref vSum, ref vMin, ref vMax, ref vNonZero);
            }
        }
        else
        {
            // semiplanar 但 crop-left 为奇数：无法字节对齐直接 copy，回退逐像素拆成 I420。
            int uCap = uBuf.Capacity();
            if (_extractRaw.Length < uCap) _extractRaw = new byte[uCap];
            uBuf.Rewind();
            uBuf.Get(_extractRaw, 0, uCap);

            int uDst = ySize;
            int vDst = ySize + cw * ch;
            int elemCap = uCap; // 同 fastNv12：上界取本平面装载长度，非数组长度
            for (int cy = 0; cy < ch; cy++)
            {
                int srcRow = (chromaRow0 + cy) * uvRowStride + chromaCol0 * 2;
                for (int cx = 0; cx < cw; cx++)
                {
                    int s = srcRow + cx * 2;
                    byte u, v;
                    if (s + 1 < elemCap && s >= 0) { u = _extractRaw[s]; v = _extractRaw[s + 1]; }
                    else { u = 128; v = 128; }
                    dst[uDst + cy * cw + cx] = u;
                    dst[vDst + cy * cw + cx] = v;
                    if (diag)
                    {
                        uSum += u; if (u < uMin) uMin = u; if (u > uMax) uMax = u; if (u != 0) uNonZero++;
                        vSum += v; if (v < vMin) vMin = v; if (v > vMax) vMax = v; if (v != 0) vNonZero++;
                    }
                }
            }
        }

        if (diag)
        {
            int yTot = vw * vh, cTot = cw * ch;
            _logger.LogInformation(
                "[ANDROID-VID] 平面统计 seq={Seq} 可见={VW}x{VH} fmt={Fmt} crop=({CL},{CT}) | " +
                "Y 均值={YM:g} 范围[{Ymin},{Ymax}] 非零{Ynz}% | " +
                "U 均值={UM:g} 范围[{Umin},{Umax}] 非零{Unz}% | " +
                "V 均值={VM:g} 范围[{Vmin},{Vmax}] 非零{Vnz}%",
                _framesProduced,
                vw, vh, fastNv12 ? "NV12(semiplanar,U-even,V-odd)" : uvPixelStride == 1 ? "I420(planar)" : "I420(fallback,semiplanar)",
                cl, ct,
                (double)ySum / yTot, yMin, yMax, yNonZero * 100 / yTot,
                (double)uSum / cTot, uMin, uMax, uNonZero * 100 / cTot,
                (double)vSum / cTot, vMin, vMax, vNonZero * 100 / cTot);
            if (_framesProduced == 0)
                _logger.LogInformation(
                    "[ANDROID-VID] 诊断判读: V均值≈128→色度已填充(绿屏应消除); V≈0→解码端未填V(需换软件解码器)。解码器={Name} 硬件={Hw}",
                    _codecName, _hardwareDecoder);
        }

        // 周期性性能诊断：每 60 帧报告一次平均提取耗时，用于定位卡顿瓶颈。
        _extractSamples++;
        _extractTicks += System.Diagnostics.Stopwatch.GetTimestamp() - extractStart;
        if (_framesProduced > 0 && (_framesProduced % LogInterval) == 0)
        {
            double avgMs = System.Diagnostics.Stopwatch.GetElapsedTime(0, _extractTicks).TotalMilliseconds / _extractSamples;
            _logger.LogInformation("[ANDROID-VID] 提取性能: 样本={Samples} 平均={AvgMs:F2}ms/帧 解码器={Name}",
                _extractSamples, avgMs, _codecName);
            _extractSamples = 0;
            _extractTicks = 0;
        }

        // PTS：优先 image.Timestamp（ns）→ ticks；缺失则退回 dequeue 的 presentationTimeUs（us）→ ticks。
        TimeSpan pts = image.Timestamp > 0
            ? TimeSpan.FromTicks(image.Timestamp / 100)
            : infoPtsUs >= 0 ? TimeSpan.FromTicks(infoPtsUs * 10) : TimeSpan.Zero;

        resource.ColorInfo = _colorInfo;

        // 全链路指纹（与 SkiaVideoPresenter 的 [FP-PRESENT] 同口径）：开播前 6 帧全采，之后每 60 帧一采。
        // 同 PTS 下 [FP-DEC].fp 与 [FP-PRESENT].src 必须逐字节相同；不同 ⇒ 帧数据在管线途中被覆写
        // （池化复用 / 缓冲提前归还），正是「开播激流期花屏、稳态正常」的典型特征。
        if (_framesProduced < 6 || (_framesProduced % 60) == 0)
        {
            var d2 = resource.Data.Span;
            int fpYRow = Math.Min(500, Math.Max(0, vh - 1));
            int fpCRow = Math.Min(250, Math.Max(0, vh / 2 - 1));
            var sb = new System.Text.StringBuilder(40);
            for (int i = 0; i < 8; i++)
            {
                int ix = fpYRow * vw + 16 + i;
                sb.Append(ix >= 0 && ix < d2.Length ? d2[ix].ToString("X2") : "--");
            }
            sb.Append('/');
            for (int i = 0; i < 8; i++)
            {
                int ix = vw * vh + fpCRow * vw + 16 + i;
                sb.Append(ix >= 0 && ix < d2.Length ? d2[ix].ToString("X2") : "--");
            }
            _logger.LogInformation("[FP-DEC] seq={Seq} pts={Pts} fmt={Fmt} {W}x{H} fp={Fp}",
                _framesProduced, pts, outFmt, vw, vh, sb.ToString());
        }

        UpdateFrameDelta(resource.Data.Span, vw, vh, _framesProduced, pts);

        return new VideoFrame(vw, vh, outFmt, resource, pts, TimeSpan.Zero, keyFrame, _colorInfo);
    }

    /// <summary>计算本帧与上一帧的亮度采样平均绝对差（MAD），识别参考帧损坏导致的坏帧。</summary>
    private void UpdateFrameDelta(ReadOnlySpan<byte> data, int w, int h, int seq, TimeSpan pts)
    {
        Span<byte> cur = stackalloc byte[DeltaSampleLen];
        for (int r = 0; r < DeltaRows; r++)
        {
            int row = (h * (r * 2 + 1)) / (DeltaRows * 2);   // 纵向均匀
            for (int c = 0; c < DeltaCols; c++)
            {
                int src = row * w + (w * c) / DeltaCols;      // 横向均匀
                int dst = (r * DeltaCols + c) * DeltaBytesPerBlock;
                for (int i = 0; i < DeltaBytesPerBlock; i++)
                {
                    int pos = src + i;
                    cur[dst + i] = (pos >= 0 && pos < data.Length) ? data[pos] : (byte)0;
                }
            }
        }

        if (_hasPrevSample)
        {
            long sum = 0;
            int badBlocks = 0;
            for (int b = 0; b < DeltaRows * DeltaCols; b++)
            {
                int off = b * DeltaBytesPerBlock;
                int diff = 0;
                for (int i = 0; i < DeltaBytesPerBlock; i++)
                    diff += Math.Abs(cur[off + i] - _prevYSample[off + i]);
                sum += diff;
                if (diff / (double)DeltaBytesPerBlock >= DeltaBlockBadThreshold)
                    badBlocks++;
            }
            double mad = sum / (double)DeltaSampleLen;

            // 判据：整体突变 OR 局部坏块占比 > 5% —— 后者专抓「只有部分宏块损坏」的坏帧。
            bool spike = (_madBase > 1.0 && mad > _madBase * 3.0)
                         || badBlocks * 20 >= DeltaRows * DeltaCols;
            if (spike)
                _spikeFrames++;
            _madBase = _madBase > 0 ? (_madBase * 0.9 + mad * 0.1) : mad;

            if (spike || seq < 240 || (seq % 60) == 0)
                _logger.LogInformation(
                    "[FRAME-DELTA] seq={Seq} pts={Pts} mad={Mad:F1} 基线={Base:F1} 坏块={Bad}/{Total}{Spike} 累计突变={Spikes}",
                    seq, pts, mad, _madBase, badBlocks, DeltaRows * DeltaCols,
                    spike ? " ← 突变（疑似坏帧）" : string.Empty, _spikeFrames);
        }

        cur.CopyTo(_prevYSample);
        _hasPrevSample = true;
    }

    /// <summary>按源有效长度拷贝一行（越界时只拷可用部分，不整行丢弃，避免对齐填充误差导致整行变 0）。</summary>
    /// <param name="src">源缓冲（被 Y/U/V 三个平面轮流复用，故 <c>src.Length</c> 不等于本平面有效长度）。</param>
    /// <param name="srcOff">本行在源缓冲内的字节偏移。</param>
    /// <param name="count">期望拷贝字节数。</param>
    /// <param name="dst">目标紧凑平面。</param>
    /// <param name="dstOff">目标偏移。</param>
    /// <param name="srcValid">本平面实际装载的有效字节数（= plane.Buffer.Capacity()）。
    /// <b>必须</b>用它而非 <c>src.Length</c> 做上界：源缓冲为 Y 平面分配时远大于 UV 容量，
    /// 若按 <c>src.Length</c> 计算可用量，UV 行会越过有效数据边界读到残留的 Y 像素，
    /// 导致画面下半色度错乱（大面积块状破碎、色块错位、拖影）。</param>
    private static void CopyRow(byte[] src, int srcOff, int count, Span<byte> dst, int dstOff, int srcValid)
    {
        if (srcOff < 0 || count <= 0) return;
        int limit = Math.Min(src.Length, srcValid);
        int n = Math.Min(count, limit - srcOff);
        if (n <= 0) return;
        new ReadOnlySpan<byte>(src, srcOff, n).CopyTo(dst.Slice(dstOff, n));
    }

    /// <summary>累计一行的平面统计（均值/范围/非零占比），供首帧诊断使用。</summary>
    private static void AccumPlaneStats(Span<byte> data, int off, int count,
        ref long sum, ref byte min, ref byte max, ref int nonZero)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = data[off + i];
            sum += b; if (b < min) min = b; if (b > max) max = b; if (b != 0) nonZero++;
        }
    }
}
