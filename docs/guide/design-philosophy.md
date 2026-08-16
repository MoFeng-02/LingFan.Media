# Design Philosophy

Every design decision in LingFan.Media traces back to ten principles.

## 1. DI-driven

Everything is wired through `Microsoft.Extensions.DependencyInjection`. You compose a media stack by registering services: `services.AddLingFanMedia().AddFFmpeg().AddD3D11Renderer().AddWasapiOutput()`. No static initialisers, no global state.

## 2. AOT-friendly

The library must publish as a NativeAOT binary with zero trim/analyze warnings.

- **Zero reflection** — no runtime type discovery, no `Dictionary<string, object>` capability maps.
- **`[LibraryImport]`, never `[DllImport]`** — P/Invoke uses source-generated marshalling (`[LibraryImport]`), the only AOT-correct static P/Invoke. `[DllImport]` relies on a runtime reflection-based marshaller and is banned.
- **Sealed types, `ValueTask` hot paths, compile-time-known types.**

### Current limitations

The codebase itself is written to be fully AOT-correct — no reflection, `[LibraryImport]`, sealed types, compile-time-known types. The principal residual friction comes from **third-party GPU interop libraries** (the Vortice / SharpGen-generated Direct3D bindings) that rely on generated, reflection-based marshalling and therefore emit trim/analyze warnings under `PublishAot`.

Today this is *contained, not eliminated*, through:

- `TrimmerRootAssembly` entries that keep those bindings from being aggressively trimmed, and
- targeted `NoWarn` suppressions for the known IL2xxx analyzer IDs they raise.

This keeps the published binary fully functional and AOT-publishable; the warnings are suppressed at the toolchain level rather than removed at the source.

The planned direction is to **gradually replace the reflection-based binding surface with native dynamic interop** — explicit vtable dispatch over raw function pointers (already the model used for all our COM/P/Invoke boundaries). As more of the third-party surface is brought under that model, the root-assembly shims and warning suppressions can be retired, reaching zero trim/analyze warnings without suppression.

Net: the limitation is known and understood, does not block AOT publishing, and is on a clear removal path.

## 3. Backend-replaceable

FFmpeg / VLC / MediaFoundation are interoperable and independently selectable. Your application code never names a backend.

## 4. Not bound to Avalonia

`Core` does not know Avalonia exists. Only the `LingFan.Media.Avalonia` module references UI. The same `IMediaPlayer` drives a headless server and a desktop app.

## 5. GPU zero-copy through `IFrameResource`

A video frame is an `IFrameResource` that may carry CPU or GPU memory. The frame pipeline is agnostic to where pixels live; zero-copy presentation is a Sink capability.

## 6. Headless rendering

A video frame is delivered to the platform compositor as a GPU texture (Windows → DirectComposition, macOS/iOS → CAMetalLayer + CoreAnimation, Android → TextureView + SurfaceFlinger). Each supported platform is implemented correctly once; the user gets every supported platform's headless experience through one `IVideoRenderer` contract. (Linux is excluded from the target surface — see [Backends & Roadmap](./backends).)

## 7. Memory safety

Frame ownership-transfer semantics, `ArrayPool` reuse, `SafeHandle` + explicit `Dispose` layering. A Sink borrows a frame read-only and **must never Dispose it**; the producer owns the lifetime.

## 8. Pipeline sync methods, I/O-boundary real async

`VideoPipeline` / `AudioPipeline` / `SubtitleProcessor` / `MediaPipelineHost` expose `Start` / `Pause` / `Stop` / `Flush` as **synchronous `void`** (pure in-memory work; `Stop` only signals cancellation, it does not join threads). The thread join (5 s timeout) happens in `DisposeAsync`. `IMediaPlayer.PlayAsync` / `PauseAsync` / `StopAsync` return `Task` as an interface contract and, being pure in-memory, return `Task.CompletedTask` — that is **not** pseudo-async. Real `await` is reserved for true I/O boundaries (`OpenAsync`, `SeekAsync`, `ReadPacketAsync`, network connect, `DisposeAsync` join).

## 9. CancellationToken at I/O boundaries only

`CancellationToken` appears at I/O boundaries and session lifetime (`OpenAsync`, `StopAsync`, `SeekAsync`, `ReadPacketAsync`). It is deliberately **absent from hot paths** (`DecodeAsync`, `Present`, `Submit`) where it would add overhead and contention.

## 10. Session isolation

Every `IMediaPlayer` owns an independent session — its own clock, buffer, and pipelines. DI provides system-level factories (Singleton); sessions are Transient and created inside `OpenAsync`.

## Contract-layer evolution

The `Abstractions` layer is **not frozen**. When genuinely needed, method signatures (sync or async) may be *added* — this avoids two failure modes: (a) missing an async signature forces callers into `.Wait()` / `.Result` hard-blocking or into fake-async backends; (b) missing a sync signature forces native-boundary callers to work around `await`.

Two principles govern any addition:

1. **Zero external references.** A new signature's parameters/return types must be BCL types (`IDisposable`, `Memory<byte>`, `Stream`, `CancellationToken`, `Task` / `ValueTask`) or types already defined in `Abstractions`. Never reference a backend / renderer / UI concrete type. *Dependency inversion's real payoff:* keep the contract zero-external-reference and backends / renderers / UI can be swapped freely.
2. **Zero implementation.** `Abstractions` holds only signatures, auto-properties, and pure data models (including `Dispose` releasing its own neutral resources). No business logic, no `new` of concrete implementations.

> Prefer *adding* a signature over changing one. If you must change a signature, audit every implementation and caller.
