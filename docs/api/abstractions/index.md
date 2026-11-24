---
title: "Abstractions"
generated: true
generator: "scripts/gen-api.mjs"
---
# Abstractions

The contract layer: interfaces, models, enums, events and exceptions. Zero implementation, zero external references beyond the BCL and logging abstractions.

## Type catalog

| Type | Kind | Summary |
| --- | --- | --- |
| [`AspectRatioMode`](/api/abstractions/enums-aspectratiomode) | enum | Video aspect ratio zoom mode. |
| [`AudioCodec`](/api/abstractions/enums-audiocodec) | enum | Audio codec type. |
| [`AudioEffectParameter`](/api/abstractions/models-settings-audioeffectparameter) | class | Strong type audio effect parameters. |
| [`AudioFrame`](/api/abstractions/models-frames-audioframe) | class | Audio frame. Implement `IDisposableFrame`. |
| [`AudioSettings`](/api/abstractions/models-settings-audiosettings) | class | Audio decoding and output settings. |
| [`AudioTrackInfo`](/api/abstractions/models-tracks-audiotrackinfo) | class | Audio track details. |
| [`BackendDescriptor`](/api/abstractions/interfaces-player-backenddescriptor) | record | A read-only description of a registered backend (factory interface collection, non-instance). Aggregated by `IBackendRegistry` in DI registration order, for middleware/advanced consumers to view and select backends. |
| [`Bt601FullRange`](/api/abstractions/models-frames-bt601fullrange) | class | BT.601 Full Range (JFIF) YUV→RGB Conversion Coefficient — the unique constant true source for the full-chamber CPU conversion path. |
| [`BufferProgressEventArgs`](/api/abstractions/events-bufferprogresseventargs) | class | Buffer progress event parameters. |
| [`BufferState`](/api/abstractions/enums-bufferstate) | enum | Buffer state. |
| [`ClockSyncSource`](/api/abstractions/enums-clocksyncsource) | enum | Clock synchronization source. |
| [`ColorRange`](/api/abstractions/enums-colorrange) | enum | The value range of the video component (Full or Limited) determines whether offset/scaling compensation is needed before YUV → RGB conversion. |
| [`ColorStandard`](/api/abstractions/enums-colorstandard) | enum | The video color primary color and luminance matrix standard (ITU-R BT series), used to select the correct brightness/chromatic coefficient during YUV→RGB conversion. |
| [`ColorTransfer`](/api/abstractions/enums-colortransfer) | enum | Optoelectronic transmission characteristics (OETF/EOTF curves) used to indicate whether content is HDR. |
| [`ContainerFormat`](/api/abstractions/enums-containerformat) | enum | Container format type. |
| [`DecodePreferencePolicy`](/api/abstractions/models-settings-decodepreferencepolicy) | class | Decoding preference strategy: Use the "decoding cost score" instead of the pure resolution threshold to determine whether the content exceeds the software's real-time decoding capability, Thus, it was decided that "hardware acceleration should be prioritized." Cost score = Wide × High × Frame rate × Encoding complexity weight × bit depth weight. |
| [`GPUApiType`](/api/abstractions/enums-gpuapitype) | enum | GPU API type. |
| [`GpuDeviceCapabilities`](/api/abstractions/models-gpu-gpudevicecapabilities) | class | GPU device capabilities (neutral model, cross-layer sharing). |
| [`GpuDeviceLostException`](/api/abstractions/exceptions-gpudevicelostexception) | class | GPU device missing abnormalities (D3D11 `DXGI_ERROR_DEVICE_REMOVED`/`DEVICE_RESET`, Vulkan `VK_ERROR_DEVICE_LOST`, etc.). |
| [`GpuFrameImportKind`](/api/abstractions/interfaces-frames-igpuframeproducer) | enum | Decodes native output types for `IGpuFrameProducer` to determine how to import textures into the current renderer's GPU-API. |
| [`GpuFrameImportSource`](/api/abstractions/interfaces-frames-igpuframeproducer) | struct | Decode native output import description (neutral, zero external references). |
| [`GpuTextureReadback`](/api/abstractions/models-frames-gputexturereadback) | class | GPU texture CPU readback results (neutral, zero external references). |
| [`IAudioDecoder`](/api/abstractions/interfaces-decoders-iaudiodecoder) | interface | Audio decoder interface. |
| [`IAudioDecoderFactory`](/api/abstractions/interfaces-decoders-iaudiodecoderfactory) | interface | Audio decoder factory interface. |
| [`IAudioEngine`](/api/abstractions/interfaces-outputs-iaudioengine) | interface | Audio engine (Infrastructure-level long-term native resource). Represents the "unique in-process, shared across playback sessions" underlying audio subsystem connection, Strictly layered with `IAudioOutput` (Session level, each `OpenAsync` new creation). |
| [`IAudioOutput`](/api/abstractions/interfaces-outputs-iaudiooutput) | interface | Audio output interface. |
| [`IAudioOutputFactory`](/api/abstractions/interfaces-outputs-iaudiooutputfactory) | interface | Audio output factory interface. |
| [`IAudioSourceFormatAware`](/api/abstractions/interfaces-decoders-iaudiosourceformataware) | interface | Source audio format-aware interface. Optional implementation: **Pass-through** audio decoder (does not decode itself, PCM already produced by the depackaging layer) Receive the true PCM output format from the caller through this interface. |
| [`IBackendRegistry`](/api/abstractions/interfaces-player-ibackendregistry) | interface | Read-only backend registry: aggregates all registered backend groups (DI registration order = rollback priority). |
| [`IBatchAudioSubmit`](/api/abstractions/interfaces-outputs-ibatchaudiosubmit) | interface | Optional batch audio submission capability (performance optimization feature interface). |
| [`IBufferManager`](/api/abstractions/interfaces-core-ibuffermanager) | interface | Buffer manager interface. |
| [`ICodecRegistry`](/api/abstractions/interfaces-codecs-icodecregistry) | interface | Codec registry contract (cross-layer sharing, zero external references). |
| [`IDisposableFrame`](/api/abstractions/interfaces-frames-idisposableframe) | interface | Frame release interface. VideoFrame / AudioFrame implements this interface. |
| [`IFormatDetector`](/api/abstractions/interfaces-detection-iformatdetector) | interface | Media format detector contract (dependent inversion). |
| [`IFrameChannel`](/api/abstractions/interfaces-core-iframechannel) | interface | Unified video frame delivery channel: decode output → channels → fan-out to all subscription sinks. Both headed and headless share this passage; zero-copy is a difference in sink capability (GPU texture frames vs. CPU frames), not independent branches. |
| [`IFramePool`](/api/abstractions/interfaces-frames-iframepool) | interface | Frame object pool interface. Allows components such as decoders to reuse frame instances, reducing allocation and GC load. |
| [`IFramePoolAware`](/api/abstractions/interfaces-frames-iframepoolaware) | interface | Frame pool-aware interface. An optional implementation supports components of the frame object pool to receive pool references through this interface. |
| [`IFrameResource`](/api/abstractions/interfaces-frames-iframeresource) | interface | Frame resource interface. Unified resource management of CPU memory frames and GPU texture frames. |
| [`IFrameSink`](/api/abstractions/interfaces-core-iframechannel) | interface | Video frame consumer (Sink). All video endpoints—headless computing, Skia soft rendering, D3D11 zero-copy GPU rendering—all implement this interface, Unified `IFrameChannel` subscription consumption. Eliminate the two independent paths of "with a head versus without a head": they share the same channel, The only difference lies in terminal actions (on-screen / feeding algorithms) and capabilities (whether GPU texture frames can be consumed). |
| [`IGpuDeviceContext`](/api/abstractions/interfaces-gpu-igpudevicecontext) | interface | Neutral GPU device context contract (cross-layer sharing, zero external references). |
| [`IGpuFrameProducer`](/api/abstractions/interfaces-frames-igpuframeproducer) | interface | Neutral GPU Frame Producer Bridge: implemented by the renderer assembly and registered as a Singleton, the decoding backend relies solely on this abstraction. |
| [`IGpuTextureResource`](/api/abstractions/interfaces-frames-igputextureresource) | interface | Neutral GPU texture frame resource contract (shared across layers, zero external references). |
| [`IHeadlessAudioConsumer`](/api/abstractions/interfaces-consumers-iheadlessaudioconsumer) | interface | Headless audio consumption contract (backend data processing). `AudioFrame` After this, the data is handed over to downstream for calculation and not played to the device. Simple scenarios can be directly used with `Action{T}` to subscribe to `AudioDataAvailable`, no need for this interface; This interface is used for scenarios requiring structured lifecycle/batch aggregation (in conjunction with `LingFan.Media.Consumers.ProcessingAudioSink`). |
| [`IHeadlessFrameConsumer`](/api/abstractions/interfaces-consumers-iheadlessframeconsumer) | interface | Headless consumption contract (backend data processing). `VideoFrame` This is handed over to downstream computation, no rendering or display. Simple scenarios can be directly used with `Action{T}` to subscribe to `VideoFrameAvailable`, no need for this interface; This interface is used for scenarios requiring structured lifecycle/batch aggregation (in conjunction with `LingFan.Media.Consumers.ProcessingFrameSink`). |
| [`IMediaClock`](/api/abstractions/interfaces-core-imediaclock) | interface | Media clock interface. Core component of audio and video synchronization. |
| [`IMediaComponent`](/api/abstractions/interfaces-core-imediacomponent) | interface | Media component lifecycle base interface. |
| [`IMediaDemuxer`](/api/abstractions/interfaces-demuxer-imediademuxer) | interface | Unwrapper interface. Put the container (MP4/MKV/... ) is split into independent orbital packets. |
| [`IMediaDemuxerFactory`](/api/abstractions/interfaces-demuxer-imediademuxerfactory) | interface | Decapsulator factory interface. |
| [`IMediaPlayer`](/api/abstractions/interfaces-player-imediaplayer) | interface | Top-level media player interface. |
| [`IMediaPlayerFactory`](/api/abstractions/interfaces-player-imediaplayerfactory) | interface | Player factory interface. |
| [`IMediaSession`](/api/abstractions/interfaces-player-imediasession) | interface | Play session interface. Holds all information after opening media: tracks, metadata, track selection. |
| [`IMediaSource`](/api/abstractions/interfaces-source-imediasource) | interface | Media source description. |
| [`IMediaSourceMetadata`](/api/abstractions/interfaces-source-imediasourcemetadata) | interface | Media source metadata interface. |
| [`IMediaStream`](/api/abstractions/interfaces-source-imediastream) | interface | Media data stream interface. |
| [`IMediaStreamFactory`](/api/abstractions/interfaces-source-imediastreamfactory) | interface | Media stream factory interface. |
| [`IRealtimePacedOutput`](/api/abstractions/interfaces-irealtimepacedoutput) | interface | Audio output marking interface capable of real-time throttling. Headless (hardwareless) audio output is implemented, Disable real-time throttling at `Fastest` `IMediaPlayer` (Submit instantly, process as soon as possible). Real hardware output (such as WASAPI) is throttled by hardware tempo and does not require implementing this interface. |
| [`IRendererHealth`](/api/abstractions/interfaces-renderers-irendererhealth) | interface | Renderer runtime health notification contract. |
| [`IRenderTarget`](/api/abstractions/interfaces-renderers-irendertarget) | interface | Render target interface. |
| [`ISharedGpuSurfaceSource`](/api/abstractions/interfaces-gpu-isharedgpusurfacesource) | interface | Shared GPU surface source (neutral contract). Writing a single video frame into a ** can be directly imported by the host UI compositor GPU textures are shared across devices, enabling "no airspace, pure control-level" GPU onscreen. |
| [`ISharedGpuSurfaceSourceFactory`](/api/abstractions/interfaces-gpu-isharedgpusurfacesource) | interface | `ISharedGpuSurfaceSource` Factory (Neutral Pact). Multiple implementations registered by DI, The consumer selects usable users based on the "host compositor-supported handle types." |
| [`ISubtitleDecoder`](/api/abstractions/interfaces-decoders-isubtitledecoder) | interface | Subtitle decoder interface. |
| [`ISubtitleDecoderFactory`](/api/abstractions/interfaces-decoders-isubtitledecoderfactory) | interface | Subtitle decoder factory interface. |
| [`IVaApiExport`](/api/abstractions/interfaces-frames-ivaapiexport) | interface | VA-API export abstraction (dependency inversion): The decoder backend exports the VA Surface as a dma_buf descriptor through this, Specifically, it is implemented by `LingFan.Media.Platforms`'s `VaApiInterop` registration (Linux exclusive), The backend must never reverse reference the platform assembly. |
| [`IVideoDecoder`](/api/abstractions/interfaces-decoders-ivideodecoder) | interface | Video decoder interface. |
| [`IVideoDecoderFactory`](/api/abstractions/interfaces-decoders-ivideodecoderfactory) | interface | Video decoder factory interface. |
| [`IVideoRenderer`](/api/abstractions/interfaces-renderers-ivideorenderer) | interface | Video renderer interface. |
| [`IVideoRendererFactory`](/api/abstractions/interfaces-renderers-ivideorendererfactory) | interface | Video renderer factory interface. |
| [`LogEventArgs`](/api/abstractions/events-logeventargs) | class | Log event parameters. |
| [`MediaBackendUnsupportedException`](/api/abstractions/interfaces-player-mediabackendunsupportedexception) | class | Thrown when all registered backends cannot open the given media source (runtime rollback exhaustion). |
| [`MediaCookie`](/api/abstractions/models-mediacookie) | class | HTTP Cookie model, used for cookie delivery in web streaming. |
| [`MediaErrorCode`](/api/abstractions/enums-mediaerrorcode) | enum | Media error code. |
| [`MediaErrorEventArgs`](/api/abstractions/events-mediaerroreventargs) | class | Media error event parameters. |
| [`MediaFormatProfile`](/api/abstractions/models-mediaformatprofile) | struct | Lightweight detection of the "container format + master video encoding" combination. Description: pure data, no detection algorithms. "Format-level memory" used for rollback schedulers: the same (container, encoding) combination experiences only one exception drive rollback, Subsequent similar files directly hit the memory backend, eliminating the overhead of repeated rollbacks. |
| [`MediaMetadata`](/api/abstractions/models-mediametadata) | class | Media metadata. |
| [`MediaPacket`](/api/abstractions/models-frames-mediapacket) | class | Compressed data packets after decapsulation. Implement `IDisposable`. |
| [`MediaPlayerOptions`](/api/abstractions/models-settings-mediaplayeroptions) | class | Player default configuration options (contract layer shared configuration model). |
| [`MediaSourceType`](/api/abstractions/enums-mediasourcetype) | enum | Type of media source. |
| [`MediaState`](/api/abstractions/enums-mediastate) | enum | Media playback status. |
| [`MediaStateChangedEventArgs`](/api/abstractions/events-mediastatechangedeventargs) | class | Playback status change event parameters. |
| [`MediaTrack`](/api/abstractions/models-tracks-mediatrack) | class | Media track information. |
| [`PixelFormat`](/api/abstractions/enums-pixelformat) | enum | Pixel format. |
| [`ProcessingMode`](/api/abstractions/models-processingmode) | enum | Headless / server-side processing mode. Control audio and video synchronization with real-time music play. |
| [`Rational`](/api/abstractions/models-rational) | struct | Rational numbers (numerator/denominator), used to represent precise frame rates, aspect ratios, etc. |
| [`RenderHandleType`](/api/abstractions/enums-renderhandletype) | enum | Render handle type. |
| [`RenderTargetType`](/api/abstractions/enums-rendertargettype) | enum | Render target type. |
| [`SampleFormat`](/api/abstractions/enums-sampleformat) | enum | Audio sampling format. |
| [`SharedGpuAdapterIdentity`](/api/abstractions/models-gpu-sharedgpusyncmodel) | class | Target GPU adapter identity (neutral value object). Provided by the consumer (host compositor) for producers to choose **the same physical device**. |
| [`SharedGpuHandleKind`](/api/abstractions/models-gpu-sharedgpusurfacedescriptor) | enum | Shared GPU image handle types (neutral enumeration, across GPU APIs). |
| [`SharedGpuSemaphoreKind`](/api/abstractions/models-gpu-sharedgpusyncmodel) | enum | Handle types sharing GPU semaphore (neutral enumeration). |
| [`SharedGpuSourcePolicy`](/api/abstractions/models-gpu-sharedgpusourcepolicy) | class | Shared surface source selection strategy (host layer setting, module layer consumption only): When multiple available shared surface sources exist in the same environment (For example, on Android, Vulkan sources coexist with OpenGL sources), the **host** declares the preferred handle form according to its rendering backend, Each factory reported `IsAvailable` accordingly. |
| [`SharedGpuSurfaceFormat`](/api/abstractions/models-gpu-sharedgpusurfacedescriptor) | enum | Pixel format sharing the GPU surface (neutral enumeration). |
| [`SharedGpuSyncMode`](/api/abstractions/models-gpu-sharedgpusyncmodel) | enum | Synchronous models sharing GPU surfaces (neutral enumeration, across GPU APIs). |
| [`SoftwareFrameResource`](/api/abstractions/models-frames-softwareframeresource) | class | CPU memory frame resources. Implement `IFrameResource`. |
| [`struct`](/api/abstractions/models-frames-videocolorinfo) | record | Color space description of the video frame (propagated by the decoder from container/output format for rendering to select the correct YUV →RGB conversion). |
| [`struct`](/api/abstractions/models-gpu-sharedgpusurfacedescriptor) | record | Share GPU surface descriptors (neutral value objects). Describes a cross-device shared texture that can be imported by the host compositor. |
| [`struct`](/api/abstractions/models-gpu-sharedgpusyncmodel) | record | Semaphore pairs (neutral value objects) that share the GPU surface. For `Semaphores` models. |
| [`SubtitleAlignment`](/api/abstractions/enums-subtitlealignment) | enum | Subtitle alignment. |
| [`SubtitleCodec`](/api/abstractions/enums-subtitlecodec) | enum | Subtitle codec type. |
| [`SubtitleFrame`](/api/abstractions/models-frames-subtitleframe) | class | Subtitle frame. Do not implement `IDisposableFrame` (string + TimeSpan only, no native resources). |
| [`SubtitlePosition`](/api/abstractions/enums-subtitleposition) | enum | Subtitle display position. |
| [`SubtitleStyle`](/api/abstractions/models-subtitlestyle) | class | Subtitle style configuration. |
| [`TrackChangedEventArgs`](/api/abstractions/events-trackchangedeventargs) | class | Track switch event parameters. |
| [`TrackType`](/api/abstractions/enums-tracktype) | enum | Media track type. |
| [`VaApiDmaBufDescriptor`](/api/abstractions/interfaces-frames-ivaapiexport) | class | dma_buf descriptor exported on the VAAPI surface (`VADRMPRIMESurfaceDescriptor` hosted carrier, zero external references). |
| [`VideoCodec`](/api/abstractions/enums-videocodec) | enum | Video codec type. |
| [`VideoFrame`](/api/abstractions/models-frames-videoframe) | class | Video frame. Achieve `IDisposableFrame`, cascade release `Resource`. |
| [`VideoSettings`](/api/abstractions/models-settings-videosettings) | class | Video decoding and rendering settings. |
| [`VideoTrackInfo`](/api/abstractions/models-tracks-videotrackinfo) | class | Video track details. |
| [`VisualizerType`](/api/abstractions/enums-visualizertype) | enum | Audio visualization type. |
