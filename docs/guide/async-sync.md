---
title: Async & Sync Discipline
sources: ["src/Infrastructure/LingFan.Media.Abstractions/Interfaces/Player/IMediaPlayer.cs", "src/Infrastructure/LingFan.Media.Core/MediaPlayer/MediaPlayer.Dispose.cs", "src/Infrastructure/LingFan.Media.Core/Playback/AudioPipeline.cs", "src/Infrastructure/LingFan.Media.Sources/Streams/NetworkMediaStream.cs"]
symbols: ["IMediaPlayer", "IVideoRenderer", "IAudioOutput", "IMediaClock", "VideoFrame", "AudioFrame"]
lang: en
---

# Async & Sync Discipline

Async and sync are not interchangeable here. Mixing them incorrectly causes
deadlocks, performance regressions and leaks. This page is the reader-facing
summary of the rule the code follows; the exact per-method classification lives
in the [API Reference](/api/).

## The total strategy

| Situation | Shape |
|---|---|
| Real I/O or blocking work (network, file, device, demux) | Truly async: `await`, returning `Task` / `ValueTask`. |
| Pure in-memory / CPU / GPU / native release | Synchronous: `void` or a synchronous return. |
| An async contract implemented by pure in-memory work | Returning `Task.CompletedTask` is correct and is **not** pseudo-async. |

Never hide blocking inside a `Task` with `.Wait()`, `.Result` or
`GetAwaiter().GetResult()`.

## Dual support, async preferred

Public APIs support both sync and async where it makes sense, with async as the
default. Two complementary rules:

- **Direction A — real async I/O must expose an async signature.** If a method
  internally awaits real I/O but only exposes a sync signature, add an `XxxAsync`
  counterpart. Do not leave only a sync signature that blocks the caller.
- **Direction B — pure in-memory work stays sync-only.** COM interop calls,
  in-memory state switches, pool get/return/reset and configuration queries do
  not get an async wrapper; that would be pseudo-async.

Resource and initialization types follow a symmetric template: `Dispose` +
`DisposeAsync`, `Initialize` + `InitializeAsync`. The async version is the
default; the sync version serves boundaries that cannot await (native callbacks,
`void` lifecycle overrides).

## What pseudo-async means

Pseudo-async is a method that returns `Task` but hides blocking in its body
(`.Wait()` / `.Result` / `GetResult()`, or `Task.Run(() => /* blocking */)`).
Moving the block to a thread-pool thread does not make it async.

The marker is **hidden blocking**, not the return type. A pure in-memory method
that implements an async interface by returning `Task.CompletedTask` is normal
and safe. (`await Task.Run(...)` is also evaluated case by case and annotated in
source when used.)

## What must stay synchronous

- `IVideoRenderer.Present` and GPU texture uploads (pure GPU work).
- `IAudioOutput.Submit` (native back-pressure is a bounded, normal mechanism).
- `SafeHandle.Dispose` and native release calls.
- `IMediaClock` and the synchronizer (pure in-memory, using `lock` / `Interlocked`).
- Parameter/configuration `Initialize(...)` overloads.
- `DecodeAsync` keeps `ValueTask` on the hot path (do not widen it to `Task`).

## Pipeline method layering

| Layer | Methods | Shape |
|---|---|---|
| Video pipeline | Start / Pause / Flush / Stop / Clear | `void`, in-memory. Start sets a flag and launches the loop; Stop only signals cancellation. |
| Audio pipeline | StartAsync | `Task`, truly awaits device buffer pre-roll so playback does not begin into a silent window. Pause / Flush / Stop / Clear remain sync. |
| Pipeline host | Start / Pause / Flush / Stop | Thin sync delegates (Start follows the audio-first async upgrade). |
| `IMediaPlayer` | OpenAsync / SeekAsync / DisposeAsync | Truly async (I/O, thread join, resource release). |
| `IMediaPlayer` | PlayAsync / PauseAsync / StopAsync | Async contract, pure in-memory body returning `Task.CompletedTask`. |

`Stop` is an O(1) cancellation signal; it never waits for threads. The thread
join happens in `DisposeAsync`, which cancels the pipeline and awaits its task
with a bounded timeout, then releases resources in guarded steps. This separation
keeps `Stop` non-blocking and disposal graceful.

## Blocking points and their async replacements

| Blocking pattern | Replacement |
|---|---|
| `_pipelineTask.Wait(timeout)` | `cts.Cancel(); await _pipelineTask.WaitAsync(timeout, ct)` |
| `channel.Reader.ReadAsync()...GetResult()` | `await channel.Reader.ReadAsync(ct)` |
| Synchronous blocking `Dequeue()` | `ValueTask<T> DequeueAsync(ct)`; keep `TryDequeue` sync for consumers |
| `Dispose()` calling `DisposeAsync().GetResult()` | A separate synchronous cleanup path in `Dispose()` |

## Synchronous Dispose is a fallback

`Dispose()` exists so resources are not leaked when a caller did not await
disposal. It runs its **own** synchronous cleanup path; it must never call
`DisposeAsync().GetResult()` or `.Wait()`. The recommended path remains
`await DisposeAsync()`; a synchronous dispose may drop a few in-flight GPU frames
but does not leak resources.

## Async locks

- Shared state across an `await` → `SemaphoreSlim` with `await WaitAsync()`.
- Pure synchronous shared state (clock, synchronizer) → keep `lock` / `Interlocked`.
- `Channel<T>` is already thread-safe; usually no extra lock is needed.
- Never `await` inside a `lock`. If a method awaits, it needs an async lock.

## Caller contract: no async void

`async void` is forbidden — exceptions escape unobserved and can crash the
process. `async Task` / `async ValueTask` are fine, but a `void` override cannot
change its signature; add a separate async method instead.

- `VideoView.OnDetachedFromVisualTree` is a `void` override → a separate
  `DisposePlayerAsync()` is provided for callers to await; the override uses the
  synchronous `Dispose()` fallback.
- `VideoView.OnPlayerChanged` is a `void` callback → open the player
  (`await player.OpenAsync()`) **before** assigning the `Player` property.

## HttpClient comes from the factory

Any `HttpClient` is obtained through `IHttpClientFactory.CreateClient()`, not
`new HttpClient()` (except rare custom-handler cases such as SSL bypass, which
must set `PooledConnectionLifetime`). Repeatedly constructing clients exhausts
sockets through `TIME_WAIT` accumulation. Cookies travel as request headers
rather than a `CookieContainer`, so they work with the factory's shared handlers.

## One sanctioned exception

The FFmpeg demuxer's native AVIO read callback has a synchronous `read_packet`
signature, so it bridges to the async stream with `GetResult()` inside the
callback. This runs on an FFmpeg worker thread (not the UI thread), is an
unavoidable native boundary, and is annotated in source — it is not pseudo-async
to be "optimized away".

## Diagnosing stutter and frame stalls

When playback stalls, jitters or rebounds, the first hypothesis is **not** "add
async / `Task.Run`". In this codebase such symptoms have consistently been
synchronous-logic errors, checked in this order:

1. Queue semantics (head vs tail, peek-then-dequeue safety, accidental drops).
2. Clock interpolation (master-clock interpolation, audio device cursor
   quantization, present-threshold spin safety).
3. COM thread affinity (native objects with thread affinity must stay on a single
   dedicated scheduler/thread).
4. Decoder / demuxer contracts (e.g. a transform that rejects input until its
   output is drained).
5. Undue synchronous blocking at an I/O boundary.

Covering a synchronous bug with async introduces cross-thread COM corruption,
shutdown hangs and new scheduling races — worse than the original bug. Async
belongs only at real I/O boundaries; hot paths stay synchronous.
