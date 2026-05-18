# Utilities - Oxygen Mod

Internal utility classes used by Oxygen's systems.
Not part of the public API.

---

## OxygenParallel

**File:** `OxygenParallel.cs`

### Purpose
`Parallel.For` from .NET creates `ParallelLoopState` objects per iteration, generating too much GC pressure for a loop running 60 times per second. `OxygenParallel.For` operates at the **partition** level (slices of the range), dispatching one worker per partition to the `ThreadPool`.

### Signature
```csharp
internal static void For(int from, int to, Action<int, int> body)
```
`body` receives `(inclusiveStart, exclusiveEnd)` - the worker processes that entire range.

### Implementation
```
workers = min(ProcessorCount - 1, rangeSize)   ← reserves 1 core for the main thread
partitions = evenly distributed (remainder spread across the first partitions)

for i in 0..workers-1:
    if i == workers-1:
        run body() on the calling thread (avoids an unnecessary QueueUserWorkItem)
    else:
        ThreadPool.QueueUserWorkItem(_ => body(...))

CountdownEvent.Wait()   ← blocks until all workers finish
```

### Exception handling
Each worker captures its exception with `Interlocked.CompareExchange` (only the first is kept). Once all workers finish, if there was an error, an `AggregateException` is thrown.

The callers (`DustParallelismSystem`, `ProjectileParallelismSystem`) catch this exception, disable the parallel path, and fall back to sequential for the rest of the session.

### Notes
- The caller must handle `AggregateException`.
- No cancellation - if a worker blocks indefinitely, `CountdownEvent.Wait()` also blocks. In practice the registered worker bodies (`UpdateDustFiller`, `UpdateSafePartition`) cannot block - they contain no I/O or locks of their own.

---

## ILMethodBodyCloner

**File:** `ILMethodBodyCloner.cs`

### Purpose
Clone the IL body of a method (`Mono.Cecil.Cil.MethodBody`) into a destination `ILCursor`, remapping all internal references (branch targets, exception handlers, local variables).

Used by `DustParallelismSystem.PatchFiller` to inject a parameterized copy of the original `Dust.UpdateDust()` loop into the `UpdateDustFiller(int, int)` stub.

### Signature
```csharp
internal static void CloneBodyToCursor(MethodBody source, ILCursor cursor)
```

### 5-step process
1. **Emit instructions** - copies each instruction with its opcode and operand (not yet remapped).
2. **Preserve offsets** - copies original offsets so `IndexOf`-based lookups work correctly.
3. **Remap branch targets** - instructions pointing to other instructions are redirected to their cloned counterparts.
4. **Exception handlers** - cloned with remapped boundaries; `CatchType` references are re-imported into the destination module.
5. **Local variables** - `VariableDefinition` entries are copied.

### What is intentionally NOT cloned
- `SequencePoints` (debug info) - not needed at runtime and adds fragility.
- `CustomDebugInformations` - same reason.

### Why this helper exists
MonoMod/Cecil does not expose a high-level "clone method body" API. Copying instructions without remapping branch targets produces invalid IL (branches point to instructions from another method that no longer exist in the active module). This helper centralizes that logic correctly.

---

## ThreadUnsafeCallWatchdog

**File:** `ThreadUnsafeCallWatchdog.cs`

### Problem
Some ModDust classes in content mods call `Lighting.AddLight()` inside their `Update()` or `MidUpdate()` overrides. When `DustParallelismSystem` dispatches dust updates to worker threads, those `ModDust.Update()` calls execute `Lighting.AddLight()` from non-main threads, causing race conditions in the lighting engine and intermittent crashes.

### Solution
Intercept both `Lighting.AddLight` overloads via `Hook` with reflection. While the parallel window is open (`Enable()`), each call is queued as an `Action` in a `ConcurrentBag`. When `Disable()` is called (after all workers have completed), the queue is drained sequentially on the main thread.

### API
```csharp
ThreadUnsafeCallWatchdog.Enable();   // open parallel window - calls are queued
OxygenParallel.For(...);             // workers may call AddLight safely
ThreadUnsafeCallWatchdog.Disable();  // close window and drain queue on main thread
```

### Interaction with LightingOptimizationSystem
`LightingOptimizationSystem` adds an ILHook inside `AddLight(int,int,float,float,float)` that skips off-screen calls. The Watchdog adds a Hook that wraps around that modified body. When the queue is drained, calls go through the hook chain again with `_enabled=false`, and the viewport check still runs. Off-screen lights queued by workers are correctly discarded at drain time.

### Thread safety
- `ConcurrentBag<Action>` supports concurrent `Add` from multiple worker threads.
- `_enabled` is `volatile` - worker threads always see the latest value without a lock.
- Drain happens only after `OxygenParallel.For` returns (CountdownEvent sync), so there are no concurrent writers during the drain.

### Lifecycle
- `Install(Mod)` is called from `DustParallelismSystem.OnModLoad()` after IL hooks are registered.
- `Uninstall()` is called from `DustParallelismSystem.Unregister()` after IL hooks are removed.
- If either `AddLight` overload is not found (e.g. future tModLoader version changed the signature), a warning is logged and the Watchdog installs partially - DustParallelism remains active but that overload is unprotected.
