# Utilities - Oxygen Mod

Internal utility classes used by Oxygen's systems.  
Both are `internal static` - they are not part of the public API.

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

This is an improvement over **Nitrate mod** (the original reference), which released the `CountdownEvent` silently on exception, making errors invisible.

### Callers
`DustParallelismSystem.RunParallel()` and `ProjectileParallelismSystem.RunParallel()`.

### Notes
- The caller **must** handle `AggregateException`. Both parallelism systems do so by disabling the parallel path and falling back to sequential.
- No cancellation - if a worker blocks indefinitely, `CountdownEvent.Wait()` also blocks. In practice, `UpdateDustFiller` and `UpdateSafePartition` cannot block (no I/O or locks of their own).

---

## ILMethodBodyCloner

**File:** `ILMethodBodyCloner.cs`  
**Based on:** Nitrate mod's `IntermediateLanguageUtil` (MIT License) - simplified.

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

### Difference from Nitrate
Nitrate's version also cloned debug points. Oxygen's version omits them deliberately to simplify the code and reduce failure points.

### Why this helper exists
MonoMod/Cecil does not expose a high-level "clone method body" API. Copying instructions without remapping branch targets produces invalid IL (branches point to instructions from another method that no longer exist in the active module). This helper centralizes that logic correctly.
