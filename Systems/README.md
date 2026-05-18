# Systems - Oxygen Mod

Contains the `ModSystem` classes that implement Oxygen's optimizations.
All are client-side; each one self-disables cleanly on load failure.

---

## Index

| File | System | Primary hook | Impact |
|---|---|---|---|
| `DustOptimizationSystem.cs` | Off-screen particle cull | `PreUpdateDusts` | CPU/GPU |
| `DustParallelismSystem.cs` | Parallelize UpdateDust loop | ILHook on `Dust.UpdateDust` | CPU multi-core |
| `ProjectileParallelismSystem.cs` | Parallelize visual projectiles | ILHook on `Main.PreUpdateAllProjectiles` | CPU multi-core |
| `SharedBossState.cs` | Boss state cache + per-tick entity counts | `PostUpdateNPCs` + `PostUpdateEverything` | CPU (eliminates redundant scans) |
| `GoreCullingSystem.cs` | Remove off-screen gore by timeout | `PreUpdateGores` | CPU |
| `SoundThrottlingSystem.cs` | Limit repeated sounds | ILHook on `SoundEngine.PlaySound` | CPU/audio |
| `WorldUpdateSystem.cs` | Reduce UpdateWorld_Inner frequency | ILHook on `WorldGen.UpdateWorld` | CPU |
| `LightingOptimizationSystem.cs` | Skip off-screen AddLight calls | ILHook on `Lighting.AddLight` | CPU (lighting thread) |
| `GCPressureManager.cs` | Reduce GC pauses during boss fights | `PostUpdateEverything` (pure C#) | GC pauses |
| `FasterPylonSystem.cs` | O(pylons) pylon proximity check | Hook on `TeleportPylonsSystem.IsPlayerNearAPylon` | CPU |
| `LaserRulerRenderSystem.cs` | Optimized laser ruler rendering | ILHook on `Main.DrawInterface_3_LaserRuler` | GPU |
| `FrameTimeDiagnosticsSystem.cs` | Diagnostics overlay + per-system profiler | `Pre/PostUpdateX` + `PostDrawInterface` | Visual |

---

## DustOptimizationSystem

**File:** `DustOptimizationSystem.cs`
**Hook:** `PreUpdateDusts()` - runs before the game updates dust particles.

### What it does
Iterates `Main.dust[0..6000]` once per frame and:
1. **Viewport cull** - deactivates dust more than 64 tiles outside the screen.
2. **Density cap** - if the active count exceeds `DustDensityPercent * 6000 / 100`, kills the farthest dust from the player until below the cap.

### Why it works
Off-screen dust consumes CPU time in `UpdateDust` but is never rendered. Removing it before the update phase avoids both costs.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `DustUpdateThrottling` | `true` | Enable/disable the entire system |
| `DustDensityPercent` | `80` | % of max dust allowed (0-100) |
| `DustCullDistance` | `80` | Tiles from the player for the density cull |

### Notes
- Operates directly on `Main.dust` - not thread-safe if mixed with `DustParallelismSystem`. However, `PreUpdateDusts` fires before the parallel loop starts, so there is no data race.
- Only deactivates particles; does not modify opacity or scale.

---

## DustParallelismSystem

**File:** `DustParallelismSystem.cs`
**Hook:** Three `ILHook`s on `Dust.UpdateDust()` (installed in `OnModLoad`).

### Strategy

```
OnModLoad:
  1. CaptureBody    ILHook → saves the original MethodBody of UpdateDust
  2. PatchFiller    ILHook → injects the cloned body into UpdateDustFiller(int, int)
  3. PatchLoop      ILHook → replaces the sequential loop with a call to RunParallel()
  4. ThreadUnsafeCallWatchdog.Install() → defers Lighting.AddLight from workers

Frame N:
  Dust.UpdateDust() → RunParallel()
      → Watchdog.Enable()
      → OxygenParallel.For(0, maxDust, UpdateDustFiller)
          ├── Worker 0: UpdateDustFiller(0,    N/4)
          ├── Worker 1: UpdateDustFiller(N/4,  N/2)
          ├── Worker 2: UpdateDustFiller(N/2,  3N/4)
          └── Main   : UpdateDustFiller(3N/4, N)   ← no dispatch overhead
      → Watchdog.Disable()  ← drains queued Lighting.AddLight on main thread
```

### Safety properties
- Worker exceptions are caught and re-thrown as `AggregateException`. On any runtime error, `_parallelEnabled = false` and the rest of the session runs sequentially.
- If hook installation fails (e.g. tModLoader changed the IL structure), the feature disables without crashing.
- Requires `ProcessorCount >= 2`.
- `ThreadUnsafeCallWatchdog` handles `Lighting.AddLight()` calls that ModDust classes may make from worker threads. See `Utilities/README.md`.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `DustParallelismEnabled` | `true` | Enable/disable the entire system |

---

## GoreCullingSystem

**File:** `GoreCullingSystem.cs`
**Hook:** `PreUpdateGores()` - before the gore update.

### What it does
Maintains an `_offscreenTicks[i]` counter per gore slot. Each frame:
- If the gore is outside the viewport → increment the counter.
- When it reaches `GoreTimeoutTicks` → deactivate the gore (`g.active = false`).
- If the gore returns to screen → reset the counter.

### Why a timeout instead of immediate cull
Gore may be momentarily off-screen when spawned (e.g. an explosion at the viewport edge). A grace period prevents relevant gore from disappearing before the player can see it.

### Relevant config
| Key | Default | Range | Description |
|---|---|---|---|
| `GoreCullingEnabled` | `true` | | Enable/disable |
| `GoreTimeoutTicks` | `300` | 60-600 | Off-screen ticks before removal (300 = 5 s) |

---

## SoundThrottlingSystem

**File:** `SoundThrottlingSystem.cs`
**Hook:** `ILHook` on `SoundEngine.PlaySound(in SoundStyle, Vector2?, SoundUpdateCallback?)`.

### Why ILHook (not `On.`)
The method has two overloads differing only in `SoundStyle` nullability. HookGen may fail to disambiguate them, and the `in` modifier on `SoundStyle` causes additional delegate-signature complications. Explicit reflection pinpoints the exact overload at the IL level.

### Injected IL (at method start)
```
ldarg.0                   ; push &SoundStyle (the `in` parameter)
call get_SoundPath        ; push SoundPath string
call ShouldSuppress       ; push bool
brfalse normalFlow        ; if false → execute sound normally
call GetInvalidSlotId     ; push SlotId.Invalid
ret                       ; return without playing
[normalFlow:]             ; original body continues here
```

### Throttling logic
`Dictionary<string, Queue<uint>>` keyed by `SoundStyle.SoundPath`.
For each sound: evict plays older than the window; if count >= `MaxSoundInstancesPerWindow`, suppress.

### Thread safety
`SoundEngine.PlaySound` can be called from ThreadPool workers during `DustParallelismSystem` execution. All dictionary access is inside `lock(_lock)`, including the periodic cleanup in `PostUpdateEverything`.

### Relevant config
| Key | Default | Range | Description |
|---|---|---|---|
| `SoundThrottlingEnabled` | `true` | | Enable/disable |
| `MaxSoundInstancesPerWindow` | `3` | 1-10 | Max plays of the same sound in the window |
| `SoundWindowTicks` | `10` | 5-30 | Time window in ticks (~0.17 s at default) |

---

## WorldUpdateSystem

**File:** `WorldUpdateSystem.cs`
**Hook:** `ILHook` on `WorldGen.UpdateWorld()`.

### Context: tModLoader's method split
tModLoader patches `WorldGen.UpdateWorld()` to become:
```
SystemLoader.PreUpdateWorld()   ← other mods, always runs
UpdateWorld_Inner()             ← vanilla logic (grass, biome spread, etc.)
SystemLoader.PostUpdateWorld()  ← other mods, always runs
```
Only `UpdateWorld_Inner()` is skipped. Other mods' Pre/Post hooks still run every tick.

### Injected IL
```
; Before the call to UpdateWorld_Inner:
call ShouldSkipInnerUpdate   ; push bool
brtrue skipLabel             ; if true → skip
call UpdateWorld_Inner       ; original call (now conditional)
[skipLabel:]                 ; rejoins here
```

### Skip condition
`ShouldSkipInnerUpdate()` returns `true` only if:
- A boss is active (`npc.boss == true`)
- `Main.GameUpdateCount % 2 != 0` (odd ticks only → 30 Hz)
- `Main.netMode == NetmodeID.SinglePlayer`

Ambient effects (grass growth, biome spread) are imperceptible at 30 Hz.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `WorldUpdateThrottlingEnabled` | `true` | Enable/disable |

---

## LightingOptimizationSystem

**File:** `LightingOptimizationSystem.cs`
**Hook:** `ILHook` on `Lighting.AddLight(int tileX, int tileY, float r, float g, float b)`.

### What it does
Injects an early-return guard at the very start of `AddLight`:
```csharp
if (IsOutsideViewport(tileX, tileY)) return;
```
Uses the existing `ret` at the end of the method as the jump target - no new instructions added.

### Why it helps
During a boss fight with many NPCs and projectiles, hundreds of `AddLight` calls fire per tick, many for entities far off-screen. Each call queues work on the lighting background thread. Culling off-screen calls reduces that queue, letting the thread finish sooner and reducing the CPU sync cost at the start of the next frame.

### Viewport margin
40 tiles (~640 px) - generous enough to cover large NPC sprites and light spread reaching the screen edge.

### Interaction with ThreadUnsafeCallWatchdog
The Watchdog adds a Hook on the same method. When workers call `AddLight`, the Watchdog queues the call. When the queue is drained on the main thread, the call goes through this ILHook's viewport check again - off-screen lights are correctly discarded at drain time.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `LightingOptimizationEnabled` | `true` | Enable/disable |

---

## GCPressureManager

**File:** `GCPressureManager.cs`
**Hook:** `PostUpdateEverything()` (pure C#, no IL patches).

### Two-phase strategy

**During the boss:**
```csharp
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
```
The .NET 8 GC avoids blocking Gen-2 collections and prefers background GC → fewer 50-200 ms stutters.

**120 ticks (~2 s) after the boss dies:**
```csharp
GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
```
Reclaims garbage accumulated during the fight in the background, before it can cause a large pause later.

### Why disabled by default
`SustainedLowLatency` allows memory to grow during the fight without being reclaimed. In very long sessions with high allocation rates this may approach OOM. Monitor Task Manager before enabling.

### Cleanup
The original GC mode is restored in `OnWorldUnload`, `OnModUnload`, and when the feature is disabled mid-session.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `GCManagementEnabled` | `false` | Enable with caution |

---

## FasterPylonSystem

**File:** `FasterPylonSystem.cs`
**Hook:** `Hook` on `TeleportPylonsSystem.IsPlayerNearAPylon(Player)`.

### What it does
Replaces the vanilla pylon proximity check with a direct iteration over `Main.PylonSystem.Pylons`.

Vanilla: scans an area around the player → O(reach_area).
Optimized: iterates the pylon list directly → O(pylons), typically O(1) for any normal world.

### Implementation
For each pylon entry, computes the exact reach bounds using `TileReachCheckSettings.Pylons.GetRanges()` (the same method vanilla uses internally) and returns `true` on first match.

### Notes
- Only active on teleport attempts, not every frame.
- Uses `Hook` with reflection instead of HookGen events for build stability.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `FasterPylonEnabled` | `true` | Enable/disable |

---

## LaserRulerRenderSystem

**File:** `LaserRulerRenderSystem.cs`
**Hook:** `ILHook` on `Main.DrawInterface_3_LaserRuler()` (private instance method, reflection).

### What it does
Replaces the vanilla laser ruler renderer with one that draws ~200 shapes instead of ~14 000 individual tile calls.

Vanilla: iterates every tile in the visible screen, one `SpriteBatch.Draw` per tile → ~14 000 calls at 1080p.
Optimized:
1. One full-screen tinted background.
2. One draw per tile column (vertical grid lines).
3. One draw per tile row (horizontal grid lines).
4. Three draws for the mouse-tile red cross highlight.
Total at 1080p: ~200 draw calls.

### IL strategy
Navigates past the two entrance guards of `DrawInterface_3_LaserRuler` (equipped check, opacity check), then injects a delegate. The delegate draws the optimized ruler and returns `true` to skip the vanilla loop. If it returns `false` (or throws), vanilla code runs as fallback.

### Notes
- Only active when the laser ruler accessory is equipped and visible.
- No interaction with content mods.
- Uses `Main.ReverseGravitySupport` to handle reversed gravity correctly.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `NewLaserRulerRenderingEnabled` | `true` | Enable/disable |

---

## FrameTimeDiagnosticsSystem

**File:** `FrameTimeDiagnosticsSystem.cs`
**Hooks:** Multiple `Pre/PostUpdateX` + `PostDrawTiles` + `PostDrawInterface`.

### Measured windows

| Label | Start hook | Stop hook | What it captures |
|---|---|---|---|
| Plyr | `PreUpdatePlayers` | `PostUpdatePlayers` | Player AI, inventory, buffs |
| NPC | `PreUpdateNPCs` | `PostUpdateNPCs` | All NPC AI |
| Gore | `PreUpdateGores` | `PostUpdateGores` | Gore physics |
| Proj | `PreUpdateProjectiles` | `PostUpdateProjectiles` | All projectile updates |
| Itms | `PreUpdateItems` | `PostUpdateItems` | Ground item physics |
| Dust | `PreUpdateDusts` | `PostUpdateDusts` | Dust update (parallelized if active) |
| Wrld | `PreUpdateWorld` | `PostUpdateWorld` | World tile spread |
| ?Upd | `PreUpdatePlayers` to `PostUpdateEverything` minus known | | Third-party mod hooks, tModLoader overhead |
| ΣUpd | | | `updateTotal × N` - real CPU cost per draw frame |
| Tile | `PostUpdateEverything` | `PostDrawTiles` | Tile geometry draw calls |
| GFX | `PostUpdateEverything` | `PostDrawInterface` | NPCs, projectiles, shaders, UI |
| ?Oth | Full frame minus update minus GFX | | Lighting thread sync, SoundEngine, XNA housekeeping |

### `Upd×N` - MonoGame catch-up loop
When the game lags, MonoGame fires N update calls before one draw call to compensate. `ΣUpd = updateTotal × N` is the real per-frame CPU update cost. If N=7 and updateTotal=19 ms, the actual cost is 133 ms.

### Implementation
- Stopwatches run every frame regardless of overlay visibility (~16 Restart/Stop calls ≈ 1.6 µs/frame).
- Entity counts read from `SharedBossState` (single scan/tick shared across all systems).
- Rendered in `PostDrawInterface` - SpriteBatch is already open by tModLoader.
- Semi-transparent background via `TextureAssets.MagicPixel`.
- Text drawn with `Utils.DrawBorderString` at 0.75× scale.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `ShowDiagnosticsOverlay` | `false` | Enable overlay |
| `DiagnosticsPosition` | `TopLeft` | TopLeft / TopRight / BottomLeft / BottomRight |
