---
title: "抽象契约层"
generated: true
generator: "scripts/gen-api.mjs"
---
# 抽象契约层

契约层：接口、模型、枚举、事件与异常。零实现，除 BCL 与日志抽象外零外部引用。

## 类型目录

| 类型 | 类别 | 摘要 |
| --- | --- | --- |
| [`AspectRatioMode`](/zh/api/abstractions/enums-aspectratiomode) | enum | 视频宽高比缩放模式。 |
| [`AudioCodec`](/zh/api/abstractions/enums-audiocodec) | enum | 音频编解码器类型。 |
| [`AudioEffectParameter`](/zh/api/abstractions/models-settings-audioeffectparameter) | class | 强类型音频效果参数。 |
| [`AudioFrame`](/zh/api/abstractions/models-frames-audioframe) | class | 音频帧。实现 `IDisposableFrame`。 |
| [`AudioSettings`](/zh/api/abstractions/models-settings-audiosettings) | class | 音频解码与输出设置。 |
| [`AudioTrackInfo`](/zh/api/abstractions/models-tracks-audiotrackinfo) | class | 音频轨道详情信息。 |
| [`BackendDescriptor`](/zh/api/abstractions/interfaces-player-backenddescriptor) | record | 一个已注册后端的只读描述（工厂接口集合，非实例）。 由 `IBackendRegistry` 按 DI 注册顺序聚合，供中间件 / 高级消费方查看与选定后端。 |
| [`BufferProgressEventArgs`](/zh/api/abstractions/events-bufferprogresseventargs) | class | 缓冲进度事件参数。 |
| [`BufferState`](/zh/api/abstractions/enums-bufferstate) | enum | 缓冲状态。 |
| [`ClockSyncSource`](/zh/api/abstractions/enums-clocksyncsource) | enum | 时钟同步源。 |
| [`ColorRange`](/zh/api/abstractions/enums-colorrange) | enum | 视频分量的取值区间（Full 或 Limited），决定 YUV→RGB 转换前是否需要偏移/缩放补偿。 |
| [`ColorStandard`](/zh/api/abstractions/enums-colorstandard) | enum | 视频的色彩原色与亮度矩阵标准（ITU-R BT 系列），用于 YUV→RGB 转换时选择正确的亮度/色差系数。 |
| [`ColorTransfer`](/zh/api/abstractions/enums-colortransfer) | enum | 光电传输特性（OETF/EOTF 曲线），用于标识内容是否为 HDR。 |
| [`ContainerFormat`](/zh/api/abstractions/enums-containerformat) | enum | 容器格式类型。 |
| [`DecodePreferencePolicy`](/zh/api/abstractions/models-settings-decodepreferencepolicy) | class | 解码偏好策略：以"解码成本分"替代纯分辨率阈值，判断内容是否超出软件解码的实时能力， 从而决定应"优先硬件加速"。成本分 = 宽×高×帧率 × 编码复杂度权重 × 位深权重。 |
| [`GPUApiType`](/zh/api/abstractions/enums-gpuapitype) | enum | GPU API 类型。 |
| [`GpuDeviceCapabilities`](/zh/api/abstractions/models-gpu-gpudevicecapabilities) | class | GPU 设备能力（中立模型，跨层共享）。 |
| [`GpuDeviceLostException`](/zh/api/abstractions/exceptions-gpudevicelostexception) | class | GPU 设备丢失异常（D3D11 `DXGI_ERROR_DEVICE_REMOVED`/`DEVICE_RESET`、 Vulkan `VK_ERROR_DEVICE_LOST` 等）。 |
| [`GpuFrameImportKind`](/zh/api/abstractions/interfaces-frames-igpuframeproducer) | enum | 解码原生输出类型，供 `IGpuFrameProducer` 判别如何导入为当前渲染器 GPU-API 纹理。 |
| [`GpuFrameImportSource`](/zh/api/abstractions/interfaces-frames-igpuframeproducer) | struct | 解码原生输出导入描述（中立，零外部引用）。 |
| [`GpuTextureReadback`](/zh/api/abstractions/models-frames-gputexturereadback) | class | GPU 纹理 CPU 回读结果（中立、零外部引用）。 |
| [`IAudioDecoder`](/zh/api/abstractions/interfaces-decoders-iaudiodecoder) | interface | 音频解码器接口。 |
| [`IAudioDecoderFactory`](/zh/api/abstractions/interfaces-decoders-iaudiodecoderfactory) | interface | 音频解码器工厂接口。 |
| [`IAudioEngine`](/zh/api/abstractions/interfaces-outputs-iaudioengine) | interface | 音频引擎（Infrastructure 级长期原生资源）。表示"进程内唯一、跨播放会话共享"的底层音频子系统连接， 与 `IAudioOutput`（Session 级、每次 `OpenAsync` 新建）严格分层。 |
| [`IAudioOutput`](/zh/api/abstractions/interfaces-outputs-iaudiooutput) | interface | 音频输出接口。 |
| [`IAudioOutputFactory`](/zh/api/abstractions/interfaces-outputs-iaudiooutputfactory) | interface | 音频输出工厂接口。 |
| [`IAudioSourceFormatAware`](/zh/api/abstractions/interfaces-decoders-iaudiosourceformataware) | interface | 源音频格式感知接口。可选实现：**直通型**音频解码器（自身不做解码、由解封装层已产出 PCM） 通过此接口从调用方接收真实的 PCM 输出格式。 |
| [`IBackendRegistry`](/zh/api/abstractions/interfaces-player-ibackendregistry) | interface | 只读后端注册表：聚合所有已注册的后端组（按 DI 注册顺序 = 回退优先级）。 |
| [`IBatchAudioSubmit`](/zh/api/abstractions/interfaces-outputs-ibatchaudiosubmit) | interface | 可选音频批量提交能力（性能优化特性接口）。 |
| [`IBufferManager`](/zh/api/abstractions/interfaces-core-ibuffermanager) | interface | 缓冲管理器接口。 |
| [`ICodecRegistry`](/zh/api/abstractions/interfaces-codecs-icodecregistry) | interface | 编解码器注册表契约（跨层共享，零外部引用）。 |
| [`IDisposableFrame`](/zh/api/abstractions/interfaces-frames-idisposableframe) | interface | 帧释放接口。VideoFrame / AudioFrame 实现此接口。 |
| [`IFormatDetector`](/zh/api/abstractions/interfaces-detection-iformatdetector) | interface | 媒体格式探测器契约（依赖倒置点）。 |
| [`IFrameChannel`](/zh/api/abstractions/interfaces-core-iframechannel) | interface | 统一视频帧投递通道：解码产出 → 通道 → 扇出至所有订阅的 Sink。 有头与无头共用此通道；零拷贝是 Sink 能力差异（GPU 纹理帧 vs CPU 帧），而非独立分支。 |
| [`IFramePool`](/zh/api/abstractions/interfaces-frames-iframepool) | interface | 帧对象池接口。供解码器等组件复用帧实例，降低分配与 GC 压力。 |
| [`IFramePoolAware`](/zh/api/abstractions/interfaces-frames-iframepoolaware) | interface | 帧池感知接口。可选实现，支持帧对象池的组件通过此接口接收池引用。 |
| [`IFrameResource`](/zh/api/abstractions/interfaces-frames-iframeresource) | interface | 帧资源接口。统一 CPU 内存帧和 GPU 纹理帧的资源管理。 |
| [`IFrameSink`](/zh/api/abstractions/interfaces-core-iframechannel) | interface | 视频帧消费者（Sink）。所有视频末端——无头计算、Skia 软渲染、D3D11 零拷贝 GPU 呈现——均实现本接口， 经统一 `IFrameChannel` 订阅消费。消除"有头 vs 无头"两条独立路径：它们同饮一条通道， 差异仅在终端动作（上屏 / 喂算法）与能力（能否消费 GPU 纹理帧）。 |
| [`IGpuDeviceContext`](/zh/api/abstractions/interfaces-gpu-igpudevicecontext) | interface | 中立 GPU 设备上下文契约（跨层共享，零外部引用）。 |
| [`IGpuFrameProducer`](/zh/api/abstractions/interfaces-frames-igpuframeproducer) | interface | 中立 GPU 帧生产者桥：由渲染器程序集实现并注册为 Singleton，解码后端仅依赖此抽象。 |
| [`IGpuTextureResource`](/zh/api/abstractions/interfaces-frames-igputextureresource) | interface | 中立 GPU 纹理帧资源契约（跨层共享，零外部引用）。 |
| [`IHeadlessAudioConsumer`](/zh/api/abstractions/interfaces-consumers-iheadlessaudioconsumer) | interface | 无头音频消费契约（后端数据处理）。`AudioFrame` 经此交给下游计算，不播放到设备。 简单场景可直接用 `Action{T}` 订阅 `AudioDataAvailable`，无需本接口； 本接口用于需要结构化生命周期 / 批量汇聚的场景（配合 `LingFan.Media.Consumers.ProcessingAudioSink`）。 |
| [`IHeadlessFrameConsumer`](/zh/api/abstractions/interfaces-consumers-iheadlessframeconsumer) | interface | 无头帧消费契约（后端数据处理）。`VideoFrame` 经此交给下游计算，不渲染、不显示。 简单场景可直接用 `Action{T}` 订阅 `VideoFrameAvailable`，无需本接口； 本接口用于需要结构化生命周期 / 批量汇聚的场景（配合 `LingFan.Media.Consumers.ProcessingFrameSink`）。 |
| [`IMediaClock`](/zh/api/abstractions/interfaces-core-imediaclock) | interface | 媒体时钟接口。音视频同步的核心组件。 |
| [`IMediaComponent`](/zh/api/abstractions/interfaces-core-imediacomponent) | interface | 媒体组件生命周期基接口。 |
| [`IMediaDemuxer`](/zh/api/abstractions/interfaces-demuxer-imediademuxer) | interface | 解封装器接口。将容器（MP4/MKV/...）拆解为独立的轨道数据包。 |
| [`IMediaDemuxerFactory`](/zh/api/abstractions/interfaces-demuxer-imediademuxerfactory) | interface | 解封装器工厂接口。 |
| [`IMediaPlayer`](/zh/api/abstractions/interfaces-player-imediaplayer) | interface | 最高层媒体播放器接口。 |
| [`IMediaPlayerFactory`](/zh/api/abstractions/interfaces-player-imediaplayerfactory) | interface | 播放器工厂接口。 |
| [`IMediaSession`](/zh/api/abstractions/interfaces-player-imediasession) | interface | 播放会话接口。持有打开媒体后的所有信息：轨道、元数据、轨道选择。 |
| [`IMediaSource`](/zh/api/abstractions/interfaces-source-imediasource) | interface | 媒体来源描述。 |
| [`IMediaSourceMetadata`](/zh/api/abstractions/interfaces-source-imediasourcemetadata) | interface | 媒体源元数据接口。 |
| [`IMediaStream`](/zh/api/abstractions/interfaces-source-imediastream) | interface | 媒体数据流接口。 |
| [`IMediaStreamFactory`](/zh/api/abstractions/interfaces-source-imediastreamfactory) | interface | 媒体流工厂接口。 |
| [`IRealtimePacedOutput`](/zh/api/abstractions/interfaces-irealtimepacedoutput) | interface | 可实时节流的音频输出标记接口。无头（无硬件）音频输出实现它， 使 `IMediaPlayer` 在 `Fastest` 下关闭实时节流 （瞬时提交、尽快处理完）。 真实硬件输出（WASAPI 等）由硬件节奏限速，无需实现本接口。 |
| [`IRendererHealth`](/zh/api/abstractions/interfaces-renderers-irendererhealth) | interface | 渲染器运行期健康通知契约。 |
| [`IRenderTarget`](/zh/api/abstractions/interfaces-renderers-irendertarget) | interface | 渲染目标接口。 |
| [`ISharedGpuSurfaceSource`](/zh/api/abstractions/interfaces-gpu-isharedgpusurfacesource) | interface | 共享 GPU 表面源（中立契约）。把一帧视频写入一块**可被宿主 UI 合成器直接导入**的 跨设备共享 GPU 纹理，从而实现「无空域、纯控件级」的 GPU 上屏。 |
| [`ISharedGpuSurfaceSourceFactory`](/zh/api/abstractions/interfaces-gpu-isharedgpusurfacesource) | interface | `ISharedGpuSurfaceSource` 的工厂（中立契约）。经 DI 注册多个实现， 由消费方按「宿主合成器支持的句柄类型」挑选可用者。 |
| [`ISubtitleDecoder`](/zh/api/abstractions/interfaces-decoders-isubtitledecoder) | interface | 字幕解码器接口。 |
| [`ISubtitleDecoderFactory`](/zh/api/abstractions/interfaces-decoders-isubtitledecoderfactory) | interface | 字幕解码器工厂接口。 |
| [`IVaApiExport`](/zh/api/abstractions/interfaces-frames-ivaapiexport) | interface | VA-API 导出抽象（依赖倒置）：解码后端经此把 VA Surface 导出为 dma_buf 描述符， 具体实现由 `LingFan.Media.Platforms` 的 `VaApiInterop` 注册（Linux 专属）， 后端绝不可反向引用平台程序集。 |
| [`IVideoDecoder`](/zh/api/abstractions/interfaces-decoders-ivideodecoder) | interface | 视频解码器接口。 |
| [`IVideoDecoderFactory`](/zh/api/abstractions/interfaces-decoders-ivideodecoderfactory) | interface | 视频解码器工厂接口。 |
| [`IVideoRenderer`](/zh/api/abstractions/interfaces-renderers-ivideorenderer) | interface | 视频渲染器接口。 |
| [`IVideoRendererFactory`](/zh/api/abstractions/interfaces-renderers-ivideorendererfactory) | interface | 视频渲染器工厂接口。 |
| [`LogEventArgs`](/zh/api/abstractions/events-logeventargs) | class | 日志事件参数。 |
| [`MediaBackendUnsupportedException`](/zh/api/abstractions/interfaces-player-mediabackendunsupportedexception) | class | 当所有已注册后端均无法打开给定媒体源时抛出（运行时回退穷尽）。 |
| [`MediaCookie`](/zh/api/abstractions/models-mediacookie) | class | HTTP Cookie 模型，用于网络流媒体的 Cookie 传递。 |
| [`MediaErrorCode`](/zh/api/abstractions/enums-mediaerrorcode) | enum | 媒体错误码。 |
| [`MediaErrorEventArgs`](/zh/api/abstractions/events-mediaerroreventargs) | class | 媒体错误事件参数。 |
| [`MediaFormatProfile`](/zh/api/abstractions/models-mediaformatprofile) | struct | 轻量探测到的「容器格式 + 主视频编码」组合。 描述纯数据，不含任何检测算法。 用于回退调度器的「格式级记忆」：同 (容器, 编码) 组合只经历一次异常驱动回退， 后续同类文件直接命中记忆后端，免去重复回退开销。 |
| [`MediaMetadata`](/zh/api/abstractions/models-mediametadata) | class | 媒体元数据。 |
| [`MediaPacket`](/zh/api/abstractions/models-frames-mediapacket) | class | 解封装后的压缩数据包。实现 `IDisposable`。 |
| [`MediaPlayerOptions`](/zh/api/abstractions/models-settings-mediaplayeroptions) | class | 播放器默认配置选项（契约层共享配置模型）。 |
| [`MediaSourceType`](/zh/api/abstractions/enums-mediasourcetype) | enum | 媒体来源类型。 |
| [`MediaState`](/zh/api/abstractions/enums-mediastate) | enum | 媒体播放状态。 |
| [`MediaStateChangedEventArgs`](/zh/api/abstractions/events-mediastatechangedeventargs) | class | 播放状态变更事件参数。 |
| [`MediaTrack`](/zh/api/abstractions/models-tracks-mediatrack) | class | 媒体轨道信息。 |
| [`PixelFormat`](/zh/api/abstractions/enums-pixelformat) | enum | 像素格式。 |
| [`ProcessingMode`](/zh/api/abstractions/models-processingmode) | enum | 无头 / 服务端处理模式。控制音视频同步与实时节流行为。 |
| [`Rational`](/zh/api/abstractions/models-rational) | struct | 有理数（分子/分母），用于表示精确的帧率、宽高比等。 |
| [`RenderHandleType`](/zh/api/abstractions/enums-renderhandletype) | enum | 渲染句柄类型。 |
| [`RenderTargetType`](/zh/api/abstractions/enums-rendertargettype) | enum | 渲染目标类型。 |
| [`SampleFormat`](/zh/api/abstractions/enums-sampleformat) | enum | 音频采样格式。 |
| [`SharedGpuAdapterIdentity`](/zh/api/abstractions/models-gpu-sharedgpusyncmodel) | class | 目标 GPU 适配器身份（中立值对象）。由消费方（宿主合成器）提供，供生产者选择**同一物理设备**。 |
| [`SharedGpuHandleKind`](/zh/api/abstractions/models-gpu-sharedgpusurfacedescriptor) | enum | 共享 GPU 图像句柄类型（中立枚举，跨 GPU API）。 |
| [`SharedGpuSemaphoreKind`](/zh/api/abstractions/models-gpu-sharedgpusyncmodel) | enum | 共享 GPU 信号量的句柄类型（中立枚举）。 |
| [`SharedGpuSourcePolicy`](/zh/api/abstractions/models-gpu-sharedgpusourcepolicy) | class | 共享表面源选型策略（宿主层设定，模块层只消费）：当同一环境存在多个可用的共享表面源时 （如 Android 上 Vulkan 源与 OpenGL 源并存），由**宿主**按其渲染后端声明优先的句柄形态， 各工厂据此自报 `IsAvailable`。 |
| [`SharedGpuSurfaceFormat`](/zh/api/abstractions/models-gpu-sharedgpusurfacedescriptor) | enum | 共享 GPU 表面的像素格式（中立枚举）。 |
| [`SharedGpuSyncMode`](/zh/api/abstractions/models-gpu-sharedgpusyncmodel) | enum | 共享 GPU 表面的同步模型（中立枚举，跨 GPU API）。 |
| [`SoftwareFrameResource`](/zh/api/abstractions/models-frames-softwareframeresource) | class | CPU 内存帧资源。实现 `IFrameResource`。 |
| [`struct`](/zh/api/abstractions/models-frames-videocolorinfo) | record | 视频帧的色彩空间描述（由解码器从容器/输出格式透传，供渲染端选择正确的 YUV→RGB 转换）。 |
| [`struct`](/zh/api/abstractions/models-gpu-sharedgpusurfacedescriptor) | record | 共享 GPU 表面描述符（中立值对象）。描述一块可被宿主合成器导入的跨设备共享纹理。 |
| [`struct`](/zh/api/abstractions/models-gpu-sharedgpusyncmodel) | record | 共享 GPU 表面的信号量对（中立值对象）。用于 `Semaphores` 模型。 |
| [`SubtitleAlignment`](/zh/api/abstractions/enums-subtitlealignment) | enum | 字幕对齐方式。 |
| [`SubtitleCodec`](/zh/api/abstractions/enums-subtitlecodec) | enum | 字幕编解码器类型。 |
| [`SubtitleFrame`](/zh/api/abstractions/models-frames-subtitleframe) | class | 字幕帧。不实现 `IDisposableFrame`（仅 string + TimeSpan，无原生资源）。 |
| [`SubtitlePosition`](/zh/api/abstractions/enums-subtitleposition) | enum | 字幕显示位置。 |
| [`SubtitleStyle`](/zh/api/abstractions/models-subtitlestyle) | class | 字幕样式配置。 |
| [`TrackChangedEventArgs`](/zh/api/abstractions/events-trackchangedeventargs) | class | 轨道切换事件参数。 |
| [`TrackType`](/zh/api/abstractions/enums-tracktype) | enum | 媒体轨道类型。 |
| [`VaApiDmaBufDescriptor`](/zh/api/abstractions/interfaces-frames-ivaapiexport) | class | VAAPI 表面导出的 dma_buf 描述符（`VADRMPRIMESurfaceDescriptor` 托管载体，零外部引用）。 |
| [`VideoCodec`](/zh/api/abstractions/enums-videocodec) | enum | 视频编解码器类型。 |
| [`VideoFrame`](/zh/api/abstractions/models-frames-videoframe) | class | 视频帧。实现 `IDisposableFrame`，级联释放 `Resource`。 |
| [`VideoSettings`](/zh/api/abstractions/models-settings-videosettings) | class | 视频解码与渲染设置。 |
| [`VideoTrackInfo`](/zh/api/abstractions/models-tracks-videotrackinfo) | class | 视频轨道详情信息。 |
| [`VisualizerType`](/zh/api/abstractions/enums-visualizertype) | enum | 音频可视化类型。 |
