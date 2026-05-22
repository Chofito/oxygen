# Utilities

Internal utilities used by Oxygen's systems. Not part of the public API.

---

## OxygenParallel

`OxygenParallel.cs`

Persistent worker thread pool for high-frequency parallel loops. Distinct from `Parallel.For` (per-iteration object allocation) and from `ThreadPool.QueueUserWorkItem` patterns (scheduling overhead on every call).

### How it works

`Initialize()` spawns `ProcessorCount - 1` background threads that sleep on a `SemaphoreSlim`. On each `For()` call:

1. Main thread computes partition boundaries and writes `Slice` structs into a pre-allocated array.
2. Main thread releases the semaphore (`N - 1` times), waking the background workers.
3. Background workers read their slice, execute `body(from, to)`, then decrement a shared counter.
4. Main thread runs the last partition inline (no extra dispatch).
5. When the counter reaches zero the last thread sets a `ManualResetEventSlim`; main thread unblocks.

No heap allocation per call. No closures. Scheduling cost is one `SemaphoreSlim.Release` + one `Wait` per call.

Workers set `OxygenWorker.Active = true` before their slice and clear it in the `finally` block, before signalling done. This lets `LightingOptimizationSystem` identify and drop unsafe `AddLight` calls from worker threads.

### Lifecycle

```csharp
OxygenParallel.Initialize();         // called from DustParallelismSystem.OnModLoad
OxygenParallel.For(0, Main.maxDust, UpdateDustFiller);
OxygenParallel.Shutdown();           // called from DustParallelismSystem.Unregister
```

`Shutdown()` signals workers via a null-body sentinel and joins them (2 s timeout). Workers are background threads so the process can exit cleanly even if `Shutdown()` is not called.

### Exception handling

Each worker captures its exception with `Interlocked.CompareExchange` (first write wins). After all workers complete, `For()` rethrows as `AggregateException`. Callers disable the parallel path and fall back to sequential for the rest of the session.

---

## OxygenWorker

`OxygenWorker.cs`

A single `[ThreadStatic] bool Active` flag. Set to `true` by `OxygenParallel`'s worker threads before executing a slice and cleared in the `finally` block.

`LightingOptimizationSystem` reads this flag to drop `Lighting.AddLight` calls that originate from worker threads, which would otherwise race against the lighting engine's internal state.

The main thread never sets this flag, so its own `AddLight` calls always go through normally.

---

## ILMethodBodyCloner

`ILMethodBodyCloner.cs`

Clones a `Mono.Cecil.Cil.MethodBody` into a destination `ILCursor`. Used by `DustParallelismSystem.PatchFiller` to inject a parameterized copy of `Dust.UpdateDust()`'s loop into the `UpdateDustFiller(int, int)` stub.

Five-step process: emit instructions → preserve offsets → remap branch targets → clone exception handlers → clone local variables. Debug info (sequence points, custom debug info) is intentionally not cloned — not needed at runtime.
