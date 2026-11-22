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

// MFVideoDecoder 的 Init 职责段（部分类，由 MFVideoDecoder.cs 按职责拆分，编译期等价）。
// 字段与核心编排（ctor/DecodeAsync/ProcessOutputOnce/FlushAsync/Reset/Dispose）在主文件；
// MFT 解码器初始化与输入输出类型设置，同步 COM 调用，COM 线程亲和。
internal sealed partial class MFVideoDecoder
{
    /// <inheritdoc/>
    public void Initialize(VideoCodec codec, VideoSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MediaFoundation 后端仅支持 Windows。");
        if (_initialized)
            throw new InvalidOperationException("MF 视频解码器已初始化，请先 Dispose 再重新初始化。");
        // 关闭不可逆（gate 一旦 BeginClose 便永久关闸，见 NativeCallGate 关闭不变量）。
        // 已关闭实例若重新 Initialize，后续 DecodeAsync 的 TryEnter 恒失败 ⇒ 静默恒返回 null 帧（哑解码器）。
        // 故直接快速失败；Session 级对象按约定为 Transient，请新建实例。
        if (Volatile.Read(ref _closed) != 0)
            throw new InvalidOperationException("该 MF 视频解码器实例已关闭，不可重新初始化；请新建实例。");

        Codec = codec;
        // 修正：原实现 = settings.EnableHardwareAcceleration 原样回显（假回显 bug）。
        // 现先置 false，待下方 DXVA 接入成功后再置 true；失败/无设备则保持 false（软件解码）。
        IsHardwareAccelerated = false;

        // 输入 subtype（H265 系统 MFT 注册的输入 subtype 为 "HEVC"）
        if (codec == VideoCodec.H264) _inputSubtype = MFConstants.MFVideoFormat_H264;
        else if (codec == VideoCodec.H265) _inputSubtype = MFConstants.MFVideoFormat_HEVC;
        else
            throw new NotSupportedException($"MF 视频解码器不支持 {codec}（仅 H264/H265 经系统 MFT）。");

        // 整个原生建立段必须处于 gate 内，与 MFDemuxer.OpenCore 对称。
        // 上面的 _closed 检查只是 TOCTOU 快速失败，**不构成互斥**。缺闸时的竞态：
        // 本线程通过检查后，并发的 Dispose/DisposeAsync 会因 _inFlight==0 立即判定「已排空」→ 执行
        // ReleaseComObjects（含 MFPlatform.Shutdown 使引用计数 −1），而本线程随后仍在
        // CoCreateInstance / SetInputType / SetOutputType / ProcessMessage 上跑原生 COM。后果三重：
        //   (1) 本解码器若是最后一个 MF 消费者，平台被真正 MFShutdown 拆除 ⇒ 原生访问违规 ⇒ 原生堆损坏；
        //   (2) _closed 已置 1，此后任何 Close 都在 Interlocked 处直接返回 ⇒ 本方法建立的 _transform /
        //      _inputTypePtr / _outputTypePtr 永久泄漏，且 MF 引用计数永久失衡（多一次 Startup 无配对）；
        //   (3) gate 恒关 ⇒ DecodeAsync 的 TryEnter 恒 false ⇒ 退化为静默返回 null 帧的「哑解码器」（黑屏无报错）。
        // 入闸后三者全部消失：Close 侧必须等本段 Exit 才可能判定排空。
        if (!_transformGate.TryEnter())
            throw new InvalidOperationException("该 MF 视频解码器实例正在关闭，无法初始化；请新建实例。");

        bool holdsGate = true;
        int hr;
        try
        {
            // MFStartup（进程级引用计数，幂等；成功后须与 ReleaseComObjects 中的 MFShutdown 配对）。
            // 经 MFPlatform 引用计数封装：MF 平台仅在所有消费者（MFBackend 解封装 + 本解码器）全部释放后才真正拆除，
            // 避免某一侧先释放触发 MFShutdown 把正在另一线程 in-flight 的原生 ReadSample/ProcessInput 踩成 AV。
            // 置于 gate 内：Startup 与 ReleaseComObjects 里的 Shutdown 必须被同一把闸串行化。
            MFPlatform.Startup();
            _mfStartupAcquired = true;

            // 解码器发现诊断（仅 LINGFAN_MF_DECODER_DIAG=1 时打印全部候选 CLSID，避免生产日志噪声）
            if (DiagEnabled) DumpAllVideoDecoders();

            // 实例化解码 MFT：优先 CoCreateInstance（已知 stock CLSID，如 H264 同步 stock MFT）；
            // 失败则 MFTEnumEx + IMFActivate::ActivateObject（覆盖 Store/异步 MFT——其 IMFActivate 不设
            // CLSID 属性、亦不可 CoCreateInstance，必须 ActivateObject）。注意：Store 安装的编解码器扩展
            // （如 Microsoft HEVC 视频扩展）在未打包桌面进程中 ActivateObject 常返回 E_INVALIDARG/E_ACCESSDENIED，
            // 此时 FindDecoderTransform 会抛出准确异常并建议改用 ffmpeg 后端。
            _transform = FindDecoderTransform(_inputSubtype);
            if (_transform == IntPtr.Zero)
                throw new PlatformNotSupportedException(
                    $"未找到 {codec} 解码 MFT（系统可能未注册对应解码器）。");
            _logger.LogInformation("[DECODER-ENUM] 已获得 {Codec} 解码 MFT transform={Transform:X}（输入 subtype={Subtype:B}）", codec, _transform, _inputSubtype);

            // 缓存 vtable 委托（slotIndex = 绝对槽 − 3；经 Windows SDK mftransform.h 声明顺序推得，
            // 并已于运行时逐槽验证）。必须在任何 _processMessage 等调用之前缓存。
            // IMFTransform 顺序：GetStreamLimits=0 … GetAttributes=5 … SetInputType=12/SetOutputType=13/
            // ProcessMessage=20/ProcessInput=21/ProcessOutput=22
            _getOutputStreamInfo = MfVTable.Get<IMFTransform_GetOutputStreamInfo>(_transform, 4);   // 绝对 7
            _setInputType = MfVTable.Get<IMFTransform_SetInputType>(_transform, 12);                // 绝对 15
            _setOutputType = MfVTable.Get<IMFTransform_SetOutputType>(_transform, 13);              // 绝对 16
            _processMessage = MfVTable.Get<IMFTransform_ProcessMessage>(_transform, 20);            // 绝对 23
            _processInput = MfVTable.Get<IMFTransform_ProcessInput>(_transform, 21);                // 绝对 24
            _processOutput = MfVTable.Get<IMFTransform_ProcessOutput>(_transform, 22);             // 绝对 25

            // GUID 局部副本（static readonly 字段不可直接作 ref 实参，CS0199）
            Guid mtMajorType = MFConstants.MF_MT_MAJOR_TYPE;
            Guid mtSubtype = MFConstants.MF_MT_SUBTYPE;
            Guid mediaTypeVideo = MFConstants.MFMediaType_Video;
            Guid formatNv12 = MFConstants.MFVideoFormat_NV12;

            // 建输入媒体类型（MajorType=Video, Subtype=H264/HEVC；不写 FRAME_SIZE，MFT 从 SPS 推断）。
            // 属性写入走 IMFAttributes::SetGUID vtable（slotIndex=21，运行时已验证）——
            // mfplat.dll **没有** MFSetAttributeGUID 导出（mfapi.h inline helper），P/Invoke 会 EntryPointNotFound。
            hr = MFInterop.MFCreateMediaType(out _inputTypePtr);
            Marshal.ThrowExceptionForHR(hr);
            var setGuidIn = MfVTable.Get<IMFAttributes_SetGUID>(_inputTypePtr, 21);
            ThrowIfFailed(setGuidIn(_inputTypePtr, ref mtMajorType, ref mediaTypeVideo));
            ThrowIfFailed(setGuidIn(_inputTypePtr, ref mtSubtype, ref _inputSubtype));

            // 应用 out-of-band 编解码器私有配置（H264/H265 的 SPS+PPS / avcC / hvcC）→ MF_MT_MPEG_SEQUENCE_HEADER。
            // MP4 容器内 SPS/PPS 在 avcC 盒，不在每个 sample 内联；缺它解码器永久 NEED_MORE_INPUT。
            // 走 IMFAttributes::SetBlob vtable（slotIndex=23，运行时已验证）；须在 SetInputType 之前写入。
            if (settings.CodecConfiguration.Length > 0)
            {
                Guid seqKey = MFConstants.MF_MT_MPEG_SEQUENCE_HEADER;
                var setBlob = MfVTable.Get<IMFAttributes_SetBlob>(_inputTypePtr, 23);
                var cfg = settings.CodecConfiguration;
                IntPtr h = Marshal.AllocHGlobal(cfg.Length);
                var kh = GCHandle.Alloc(seqKey, GCHandleType.Pinned);
                try
                {
                    Marshal.Copy(cfg.Span.ToArray(), 0, h, cfg.Length);
                    ThrowIfFailed(setBlob(_inputTypePtr, kh.AddrOfPinnedObject(), h, (uint)cfg.Length));
                }
                finally
                {
                    Marshal.FreeHGlobal(h);
                    kh.Free();
                }
            }

            // DXVA 硬件解码零拷贝接入
            // 依赖契约层 IGpuDeviceContext（共享 D3D11 设备），不引用渲染器，严守依赖倒置。
            // 时序原则（对照 SDK 实现）：SET_D3D_MANAGER 必须在 SetInputType **之前**发送
            // （mftransform.h 权威值 0x2，MSDN 原文「必须在 SetInputType / SetOutputType 之前调用」）。MFT 在 SetInputType
            // 内部即查询 DXGI 管理器选 DXVA 配置；若此刻未收到，MFT 锁定软件路径 → 输出恒为系统内存
            // （半 DXVA：硬解激活=True 但 GPU零拷贝=0）；该消息曾被放到「类型协商之后」，现订正回「SetInputType 之前」。
            // 失败一律回退软件解码，绝不抛异常阻断播放。
            _dxvaActive = false;
            if (settings.EnableHardwareAcceleration && _gpuContext is not null && _gpuContext.ApiType == GPUApiType.D3D11)
            {
                try
                {
                    // (1) 能力探测：MF_SA_D3D11_AWARE。
                    // 必须先探测再发消息：MFT 对**不支持的消息按 IMFTransform 约定返回 S_OK 静默忽略**，
                    //    仅凭 ProcessMessage 的 HRESULT 无法区分「真接受」与「忽略」——这正是「硬解激活=True
                    //    却 GPU零拷贝=0」假绿能长期存在的原因。此处把 MFT 自报能力作为第一道判据。
                    if (!QueryD3D11Aware())
                        throw new NotSupportedException("该解码 MFT 未声明 MF_SA_D3D11_AWARE（不支持 Direct3D 11 视频解码）");

                    // (1b) 取设备句柄前先让 MF 自备上下文完成延迟创建（getter 不再隐式创建，契约要求无设备返回零句柄）。
                    IntPtr d3d11Device = MfGpuDeviceContext.ResolveDeviceHandle(_gpuContext);

                    // (2) 多线程保护：解码 MFT 与渲染器分处不同线程共享同一 ID3D11Device，
                    //    未开保护时 D3D11 运行时不做内部同步 ⇒ 竞态/设备移除（MSDN 硬性要求）。
                    if (!MfDxvaInterop.TryEnableMultithreadProtection(d3d11Device))
                        _logger.LogWarning("[DXVA-DIAG] D3D11 设备不支持 ID3D10Multithread，无法开启多线程保护（DXVA 下存在竞态风险）");

                    int dxhr = MfDxvaInterop.MFCreateDXGIDeviceManager(out _dxvaResetToken, out _dxvaManager);
                    Marshal.ThrowExceptionForHR(dxhr);

                    var resetDevice = MfVTable.Get<MfDxvaInterop.IMFDXGIDeviceManager_ResetDevice>(_dxvaManager, 4); // 绝对 7 = ResetDevice（vtable: CloseDeviceHandle=3,GetVideoService=4,LockDevice=5,OpenDeviceHandle=6,ResetDevice=7,TestDevice=8,UnlockDevice=9；MfVTable.Get 读 vtable+(3+slot)，故 slot=4）
                    dxhr = resetDevice(_dxvaManager, d3d11Device, _dxvaResetToken);
                    Marshal.ThrowExceptionForHR(dxhr);

                    // (4) 设备能力真值探测（决定性判据）：共享 D3D11 设备能否为当前编码解码到 NV12 分配 DXGI 表面。
                    // 这是「半 DXVA（GPU 硬解但输出读回系统内存）」的唯一权威判据。若设备不支持
                    //    该编码 DXVA 解码到 NV12，MFT 会**静默**把结果拷贝回系统内存 → 输出 buffer 是普通
                    //    IMFMediaBuffer（QI IMFDXGIBuffer=E_NOINTERFACE）→ 零拷贝永不生效。若不查，会陷入
                    //    「硬解激活=True 却 GPU零拷贝=0」的假绿（与消息号假绿同源）。
                    //    探测失败的处置：不阻断初始化，仅打告警，让后续真实输出行为说话。
                    var dxvaProfile = MfDxvaInterop.DxvaProfileForCodec(codec);
                    if (!MfDxvaInterop.TryProbeDxvaSupport(d3d11Device, dxvaProfile, out bool dxvaCapable))
                        _logger.LogWarning("[DXVA-DIAG] 共享 D3D11 设备不支持 ID3D11VideoDevice（无视频解码能力）→ 零拷贝不可能，将走软解");
                    else if (!dxvaCapable)
                        _logger.LogWarning("[DXVA-DIAG] CheckVideoDecoderFormat(profile→NV12)=不支持 → 设备无法为 {Codec} 分配 DXGI 解码表面，MFT 将静默回落读回系统内存（半 DXVA），零拷贝不成立", codec);
                    else
                        _logger.LogInformation("[DXVA-DIAG] CheckVideoDecoderFormat(profile→NV12)=支持 → 设备具备 {Codec} DXGI 零拷贝解码能力", codec);

                    // (6) 决定性验证：DXGI 管理器是否真正绑定上了设备（解码器经 GetVideoService 取设备）。
                    //    ResetDevice 即便 HRESULT 成功，若 P/Invoke 偏差致绑定未生效，解码器取回空设备 ⇒ 静默读回。
                    string? mgrDiag = MfDxvaInterop.ProbeManagerBoundDevice(_dxvaManager, dxvaProfile);
                    if (mgrDiag != null)
                        _logger.LogInformation("{Diag}", mgrDiag);

                    // (7) 枚举设备真实解码 profile + 逐个验证 NV12，避免 profile 不匹配致 CreateVideoDecoder 失败。
                    string? profDiag = MfDxvaInterop.ProbeDecoderProfiles(d3d11Device);
                    if (profDiag != null)
                        _logger.LogInformation("{Diag}", profDiag);

                    // (5) 适配器身份探针（第二道成因探针）：确认共享设备是否落在 WARP/错误适配器上。
                    // CheckVideoDecoderFormat 仅验格式、不验真实硬件解码引擎；若设备是 WARP，
                    // 格式查询仍可能通过，但解码器在真正解码时无法建立硬件视频解码会话 ⇒ 静默读回系统内存。
                    string? adapterDiag = MfDxvaInterop.ProbeDeviceAdapter(d3d11Device);
                    if (adapterDiag != null)
                        _logger.LogInformation("{Diag}", adapterDiag);

                    // (3) MF DXGI 设备管理器已于上方创建并绑定（MFCreateDXGIDeviceManager + ResetDevice）。
                    //    发送 MFT_MESSAGE_SET_D3D_MANAGER(=0x2) 的时机在下方「SetInputType 之前」（订正，
                    //    非「类型协商之后」）。
                    // 消息号必须是 0x2：SDK mftransform.h 中根本不存在 SET_D3D11_MANAGER；D3D9/D3D11
                    //    共用本消息，只靠 ulParam 接口类型区分。旧代码误写 0x80000013 被 MFT 当未知消息
                    //    返回 S_OK 忽略 = 假激活的成因。
                }
                catch (Exception ex)
                {
                    _dxvaActive = false;
                    IsHardwareAccelerated = false;
                    if (_dxvaManager != IntPtr.Zero) { Marshal.Release(_dxvaManager); _dxvaManager = IntPtr.Zero; }
                    _logger.LogWarning(ex, "MF DXVA 硬解初始化失败，回退软件解码");
                }
            }
            else if (settings.EnableHardwareAcceleration)
            {
                _logger.LogWarning("未提供 D3D11 设备上下文（IGpuDeviceContext），MF 无法启用 DXVA，回退软件解码");
            }

            // DXVA：SET_D3D_MANAGER 必须在 SetInputType 之前发送（SDK 实物权威）
            // mftransform.h：MFT_MESSAGE_SET_D3D_MANAGER = 0x2，MSDN 明文「必须在 SetInputType /
            // SetOutputType 之前调用」。MFT 在 SetInputType 内部即查询 DXGI 设备管理器选择 DXVA 配置；
            // 若此刻尚未收到 manager，MFT 锁定软件路径 → 输出 buffer 恒为系统内存（半 DXVA：
            // 硬解激活=True 但 GPU零拷贝=0）；此前被移到类型协商之后，正是当前「半 DXVA」的成因；
            // 现按 SDK 订正回「SetInputType 之前」。消息号 0x2 已修正，本组合（0x2 + SetInputType 前）此前从未被测。
            if (_dxvaManager != IntPtr.Zero)
            {
                try
                {
                    int setHr = _processMessage!(_transform, MFConstants.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)_dxvaManager);
                    Marshal.ThrowExceptionForHR(setHr);
                    _dxvaActive = true;
                    IsHardwareAccelerated = true;
                    _logger.LogInformation("MF DXVA 硬解已激活（SET_D3D_MANAGER 已于 SetInputType 前发送，共享 D3D11 设备）");
                }
                catch (Exception ex)
                {
                    _dxvaActive = false;
                    IsHardwareAccelerated = false;
                    Marshal.Release(_dxvaManager);
                    _dxvaManager = IntPtr.Zero;
                    _logger.LogWarning(ex, "MF DXVA 硬解激活失败（SET_D3D_MANAGER 被拒），回退软件解码");
                }
            }

            hr = _setInputType!(_transform, 0, _inputTypePtr, 0);
            Marshal.ThrowExceptionForHR(hr);

            // 输出类型协商（MSDN 规范路径）：枚举 MFT 自报的可用输出类型，优先选 NV12；
            // 枚举失败（输入类型未定尺寸时部分 MFT 返回 MF_E_TRANSFORM_TYPE_NOT_SET）则回退手工建最小 NV12 类型。
            IntPtr chosenOutType = SelectOutputType(ref formatNv12);
            if (chosenOutType == IntPtr.Zero)
            {
                hr = MFInterop.MFCreateMediaType(out chosenOutType);
                Marshal.ThrowExceptionForHR(hr);
                var setGuidOut = MfVTable.Get<IMFAttributes_SetGUID>(chosenOutType, 21);
                ThrowIfFailed(setGuidOut(chosenOutType, ref mtMajorType, ref mediaTypeVideo));
                ThrowIfFailed(setGuidOut(chosenOutType, ref mtSubtype, ref formatNv12));
            }
            _outputTypePtr = chosenOutType;

            hr = _setOutputType!(_transform, 0, _outputTypePtr, 0);
            Marshal.ThrowExceptionForHR(hr);

            // 查询输出 sample 分配策略与所需大小
            QueryOutputStreamInfo();

            // DXVA 生效的**第二道也是决定性判据**：类型协商完成后 MFT 是否改报 PROVIDES_SAMPLES。
            // 系统 H264/H265 解码 MFT 在软件路径下由调用方分配输出 sample；一旦 DXVA 真正接管，
            // 它必须自行分配 DXGI 纹理 sample（纹理只能由 MFT 从 D3D11 解码器输出池取），
            // 于是 GetOutputStreamInfo 会置 MFT_OUTPUT_STREAM_PROVIDES_SAMPLES。
            // 若此位为 0 而 _dxvaActive=true，说明 MFT 收下了 manager 却没走 DXVA —— 此时我方会继续用
            // MFCreateMemoryBuffer 造系统内存 sample 交给 MFT 填充，输出永远不可能是 DXGI 表面
            // （每帧 QI(IMFDXGIBuffer) 必得 E_NOINTERFACE）。必须在此显式暴露，绝不让它退化成静默软解。
            if (_dxvaActive)
            {
                _logger.LogInformation(
                    "[DXVA-DIAG] 类型协商后输出流信息：MFT自分配输出sample={Provides}, cbSize={Size} → 零拷贝{Verdict}",
                    _mftProvidesSamples, _outputBufferSize,
                    _mftProvidesSamples ? "前置条件成立" : "前置条件不成立（MFT 未接管输出分配 = DXVA 未真正启用）");
                if (!_mftProvidesSamples)
                    _logger.LogWarning(
                        "[DXVA-DIAG] MFT 接受了 SET_D3D_MANAGER 但仍要求调用方分配输出 sample —— DXVA 未真正启用" +
                        "（可能：设备缺 VIDEO_SUPPORT / 该分辨率无硬件解码配置 / 驱动不支持该 profile）。本次将全程软解。");
            }

            // 通知开始流式处理
            _processMessage!(_transform, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            _processMessage!(_transform, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

            // 读取输出尺寸（若 MFT 已填充）
            TryReadOutputDimensions();

            _initialized = true;
            _logger.LogDebug("MF 视频解码器初始化: {Codec}, 硬解={Hw}, MFT提供输出sample={Provides}, cbSize={Size}",
                codec, IsHardwareAccelerated, _mftProvidesSamples, _outputBufferSize);
        }
        catch
        {
            // 必须**先出闸、再走关闭协议**：CloseNativeSync 内的 WaitDrain 等待在途计数归零，
            // 而此刻唯一的在途计数正是本线程自己 —— 不先 Exit 就会自等满 NativeDrain(5s) 超时 ⇒ 误判「排空失败」
            // ⇒ 把本可安全释放的 _transform 错误地「有意泄漏」并打 Error 日志（直接污染关闭洁净度门控）。
            // 先 Exit 使计数归零后，Close 侧的 drain 立即成功，资源被真正释放。
            _transformGate.Exit();
            holdsGate = false;
            CloseNativeSync();
            throw;
        }
        finally
        {
            // holdsGate 防多余 Exit：多余 Exit 会把他人的在途计数错误减到 0 ⇒ 提前判定排空 ⇒ use-after-free
            //（NativeCallGate 的关闭不变量「危险侧」失配，见该类 Exit 备注）。
            if (holdsGate) _transformGate.Exit();
        }
    }
}
