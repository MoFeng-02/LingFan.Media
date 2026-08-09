# Async & Sync Discipline

This guide describes the concurrency model. Violating these guidelines causes deadlocks, performance regression, and memory leaks.

## Total strategy

| Situation | Correct choice |
|-----------|----------------|
| Real I/O or blocking (network, file, device, decode, pipeline `await`) | **Real async** — `await`, return `Task` / `ValueTask` |
| Pure in-memory / CPU / GPU / native release | **Sync** — `void` or sync return |
| Interface contract requires `Task` but impl is pure in-memory | Return `Task.CompletedTask` (acceptable, **not** pseudo-async) |

**Absolutely forbidden:** hiding blocking inside `Task` via `.Wait()` / `.Result` / `GetAwaiter().GetResult()` / `Task.Run(() => syncBlock)`.

## Sync/async dual support (prefer async)

- **Direction A — real async I/O → you MUST add an `XxxAsync` signature.** If a method has a real `await` internally but only exposes a sync signature, add the async overload. Do not force callers into `.Wait()` / `.Result`.
- **Direction B — pure in-memory → keep it sync-only, do NOT add async.** COM interop calls, in-memory decoder state switches, pool `Get` / `Return`, `Reset`, config / state queries — adding `async` returning `Task` is pseudo-async; don't.

Resource types follow the built-in template: `Dispose` + `DisposeAsync`, `Initialize` + `InitializeAsync`. The default preferred public method is the async version; the sync version is a fallback for boundaries that cannot `await` (native callbacks, `void` lifecycle overrides).

**Mnemonic:** internal real `await` → give an async signature (preferred); internal pure in-memory → give a sync signature (only). Don't cross the lines.

## Pseudo-async hard definition

Pseudo-async = returns `Task` but the body contains **hidden blocking** (`.Wait()` / `.Result` / `GetResult()` / `Task.Run(() => syncBlock)`), moving the block from the caller thread to a pool thread. The block is unchanged in nature.

> **`Task.CompletedTask` is not pseudo-async.** The mark of pseudo-async is *hidden blocking*, not returning `Task.CompletedTask`. A pure-in-memory method implementing an async interface by returning `Task.CompletedTask` is normal and safe.

## Must stay synchronous (real sync, do not async-ify)

- `IVideoRenderer.Present` / D3D11 GPU upload (pure GPU work)
- `IAudioOutput.Submit` (native COM back-pressure, bounded — a normal mechanism)
- `SafeHandle.Dispose` / FFmpeg native release (`avcodec_close`, etc.)
- `MediaClock` / `Synchronizer` (pure in-memory, use `lock` / `Interlocked`)
- Parameterised `Initialize(int,int)` / `Initialize(codec, settings)`
- `DecodeAsync` returns `ValueTask` (hot path — keep `ValueTask`, don't change to `Task`)

## Pipeline method layering

| Method | Returns | Reason |
|--------|---------|--------|
| `VideoPipeline.Start()` | `void` | set flag + `Task.Run` fire-and-forget, pure in-memory |
| `VideoPipeline.Pause()` / `Flush()` / `Stop()` / `Clear()` | `void` | pure in-memory; `Stop()` only calls `cts.Cancel()`, does **not** wait for the thread |
| `AudioPipeline.StartAsync()` | `Task` | a fresh audio start truly `await`s device buffer preroll (`BeginStreamingAsync`) to fix start-of-playback silence |
| `MediaPipelineHost.Start/Pause/Flush/Stop` | `void` | thin wrapper delegating to pipeline sync methods |
| `IMediaPlayer.OpenAsync` | `Task` | real `await` (demuxer open + buffer start + stream read) |
| `IMediaPlayer.PlayAsync/PauseAsync/StopAsync` | `Task` | pure in-memory → `Task.CompletedTask` |
| `IMediaPlayer.SeekAsync` | `Task` | real `await` (demuxer seek depends on stream seek/read) |
| `IMediaPlayer.DisposeAsync` | `ValueTask` | real `await` (thread join + GPU flush + network close) |

> `Stop()` is a fast signal (`cts.Cancel`); `DisposeAsync()` is the graceful wait (join + release). Responsibility is separated.

## Thread join belongs in DisposeAsync, not Stop

`DisposeAsync` step 1 joins the pipeline thread: `cts.Cancel(); await _pipelineTask.WaitAsync(5s, ct)`. Each of its ~11 steps is wrapped in its own `try/catch`.

## Blocking → real-async replacements

| Old blocking | Real-async replacement |
|-------------|------------------------|
| `_pipelineTask.Wait(5s)` | `cts.Cancel(); await _pipelineTask.WaitAsync(5s, ct)` |
| `channel.Reader.ReadAsync().AsTask().GetAwaiter().GetResult()` | `await channel.Reader.ReadAsync(ct)` |
| sync `void Dequeue()` blocks | `ValueTask<T> DequeueAsync(ct)` wrapping `await ReadAsync`; keep `TryDequeue` sync |
| `Dispose()` calls `DisposeAsync().GetResult()` | sync `Dispose()` does its own sync cleanup; **never** call `DisposeAsync().GetResult()` inside sync `Dispose` |

## Sync Dispose fallback

- Sync `Dispose()` is a safety net (ensures no leak if the caller didn't `await`).
- **Hard rule:** sync `Dispose()` must **never** call `DisposeAsync().GetResult()` / `.Wait()` (pseudo-async).
- Sync `Dispose()` does its own synchronous cleanup (call `pipeline.Stop()` + release native resources directly).
- A few frames may be lost if the GPU queue isn't flushed — but **resources never leak**.
- Preferred path is always `await DisposeAsync()`; sync `Dispose()` is purely a fallback.

## Async lock rules

- Shared state across `await` → `SemaphoreSlim` + `await sem.WaitAsync()`.
- Pure-sync shared state (Clock / Synchronizer) → keep `lock` / `Interlocked`, **don't** switch to `SemaphoreSlim`.
- `Channel<T>` is thread-safe by itself; usually no lock needed.
- **Hard rule:** never `await` inside a `lock`.

## `async void` is absolutely forbidden

Any `void` method (event callback, lifecycle override) must **not** be made `async` — exceptions would be swallowed, the caller cannot `await`, and the process may crash. Use `async Task` / `async ValueTask` instead, and add a separate async method for `void` overrides.

Examples:

- `VideoView.OnDetachedFromVisualTree` is a `void` override → add `public async ValueTask DisposePlayerAsync()` for callers; the `void` override calls sync `Dispose()` as fallback.
- `VideoView.OnPlayerChanged` is a `void` callback → the caller should `await player.OpenAsync()` *before* binding the `Player` property.

## HttpClient must use IHttpClientFactory

Anywhere an `HttpClient` is needed, obtain it via `IHttpClientFactory.CreateClient()` — **never `new HttpClient()`** (except rare SSL-bypass cases that set `PooledConnectionLifetime`). `MediaStreamFactory` is a Singleton holding an `IHttpClientFactory` reference. Network streams use the pooled client. Cookies travel via request headers, not `CookieContainer`, to stay compatible with the shared handler.


