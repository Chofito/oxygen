# Oxygen - Performance Optimizer

Pure performance mod, zero gameplay changes. All settings are `ClientSide` and independently toggle-able via Mod Config. Compatible with heavy modpacks. Not compatible with Nitrate.

---

## AI Disclosure

Parts of this mod's code and documentation were written with the assistance of Claude (Anthropic). All logic was reviewed, tested, and committed by the author.
Don't expect expert support for this mod and this mod was created for PERSONAL purpose and published in case someone finds it useful.

---

## Implemented systems

| File | System | Impact type |
|---|---|---|
| `Config/ClientConfig.cs` | All configuration (28 options) | |
| `Systems/SharedBossState.cs` | Shared state cache (BossActive, entity counts) | |
| `Systems/FrameTimeDiagnosticsSystem.cs` | HUD overlay with per-system timing breakdown | Visual |
| `Systems/DustOptimizationSystem.cs` | Off-screen dust cull + density cap | CPU/GPU |
| `Systems/DustParallelismSystem.cs` | Parallelize UpdateDust() via ILHook | CPU multi-core |
| `Systems/GoreCullingSystem.cs` | Remove off-screen gore by timeout | CPU |
| `Systems/LightingOptimizationSystem.cs` | Skip Lighting.AddLight() for off-screen positions | CPU (lighting thread) |
| `Systems/SoundThrottlingSystem.cs` | Limit same sound N times per time window | CPU/audio |
| `Systems/WorldUpdateSystem.cs` | Halve UpdateWorld_Inner rate during bosses (SP only) | CPU |
| `Systems/GCPressureManager.cs` | SustainedLowLatency GC during boss fights | GC pauses |
| `Systems/ProjectileParallelismSystem.cs` | Parallelize visual projectiles (damage=0) via ILHook | CPU multi-core |
| `Systems/FasterPylonSystem.cs` | O(pylons) pylon proximity check | CPU |
| `Systems/LaserRulerRenderSystem.cs` | Laser ruler ~200 draw calls vs ~14 000 vanilla | GPU |
| `GlobalHooks/GlobalNPCHooks.cs` | Throttle AI of distant NPCs | CPU |
| `GlobalHooks/GlobalProjectileHooks.cs` | Cull + hide ally projectiles | CPU/GPU |
| `Utilities/OxygenParallel.cs` | ThreadPool parallel-for with exception propagation | |
| `Utilities/ILMethodBodyCloner.cs` | Clone a method's IL body into an ILCursor | |
| `Utilities/ThreadUnsafeCallWatchdog.cs` | Defer Lighting.AddLight() from workers to main thread | |
| `Oxygen.cs` | Public ModCall API | |

---

## Hook architecture

```
Game loop
├── PreUpdatePlayers()             → FrameTimeDiagnosticsSystem   (start update timer)
├── PreUpdateDusts()               → DustOptimizationSystem        (cull off-screen dust)
├── Dust.UpdateDust() [ILHook]     → DustParallelismSystem         (parallelize loop)
│                                  → ThreadUnsafeCallWatchdog      (defer AddLight from workers)
├── PreUpdateGores()               → GoreCullingSystem             (timeout off-screen gore)
├── GlobalNPC.PreAI()              → GlobalNPCHooks                (skip AI for distant NPCs)
├── Main.PreUpdateAllProjectiles() → ProjectileParallelismSystem   (ILHook - safe/unsafe split)
├── GlobalProjectile.PreAI()       → GlobalProjectileHooks         (throttle off-screen visual projectiles)
├── GlobalProjectile.PreDraw()     → GlobalProjectileHooks         (cull + hide allies)
├── WorldGen.UpdateWorld() [ILHook]→ WorldUpdateSystem             (conditional skip UpdateWorld_Inner)
├── SoundEngine.PlaySound() [ILHook]→ SoundThrottlingSystem        (rolling window throttle)
├── Lighting.AddLight() [ILHook]   → LightingOptimizationSystem    (skip off-screen)
│                      [Hook]      → ThreadUnsafeCallWatchdog      (queue if in parallel window)
├── PostUpdateNPCs()               → SharedBossState               (update BossActive, ActiveNPCCount)
├── PostUpdateEverything()         → SharedBossState               (update Proj/Dust counts)
│                                  → GCPressureManager             (change GC mode)
│                                  → FrameTimeDiagnosticsSystem    (stop update timer)
├── PostDrawTiles()                → FrameTimeDiagnosticsSystem    (stop tile timer)
└── PostDrawInterface()            → FrameTimeDiagnosticsSystem    (draw overlay)
```

---

## Multiplayer compatibility

| System | SP | MP Client | Server |
|---|---|---|---|
| Dust / Gore / Lighting (visual) | ✅ | ✅ local | N/A |
| Sound throttling | ✅ | ✅ local | N/A |
| DustParallelism | ✅ | ✅ local | ✅ (sequential on server) |
| ProjectileParallelism | ✅ | ✅ (visual tier only) | ✅ (sequential on server) |
| ProjectileAI throttling | ✅ | ✅ (own projectiles only) | N/A |
| ProjectileCull (PreDraw) | ✅ | ✅ (player-owned only) | N/A |
| NPCThrottling | ✅ | ❌ disabled (prevents desync) | ✅ (max skip=2) |
| TownNPCThrottling | ✅ | ❌ disabled | ✅ (max skip=2) |
| WorldUpdateThrottling | ✅ | ❌ disabled (SP only) | ❌ disabled |
| GCPressure | ✅ | ✅ | ✅ (default off) |
| LightingOpt | ✅ | ✅ local | N/A |
| FasterPylon | ✅ | ✅ | ✅ |
| LaserRuler | ✅ | ✅ | N/A |

---

## Diagnostics overlay

Enable `ShowDiagnosticsOverlay` in Mod Config to display:

```
FPS: 47  (21.3ms)  Upd×1
Plyr: 1.2ms
NPC : 8.4ms
Gore: 0.1ms
Proj: 3.7ms
Itms: 0.2ms
Dust: 4.1ms
Wrld: 0.8ms
?Upd: 5.2ms        ← mod hooks + tModLoader overhead not covered by our timers
ΣUpd: 23.7ms (1×)  ← total update cost this draw frame
Tile: 2.1ms
GFX : 8.3ms
?Oth: 1.4ms        ← lighting thread sync, SoundEngine, XNA overhead
Dust: 4820/6000
Proj: 145/1000
NPCs: 67/200
D P N G S T L     ← uppercase = active, lowercase = inactive
```

**Feature bar:** D=DustThrottle, P=ProjectileHiding, N=NPCThrottling, G=GoreCulling, S=SoundThrottling, T=TownNPCThrottling, L=LightingOpt

**`?Upd`**: time inside the update window not covered by our per-system hooks. Includes other mods' GlobalNPC callbacks, inter-system tModLoader overhead.

**`?Oth`**: frame time not covered by update or GFX. Primarily lighting thread sync, SoundEngine.Update, XNA/FNA housekeeping, and MonoGame catch-up loop overhead when `N > 1`.

**`Upd×N`**: how many Update() calls MonoGame fired per Draw(). If `N > 1`, real CPU cost per draw frame is `ΣUpd = updateTotal × N`.

---

## Public ModCall API

Other mods can interact via `ModLoader.GetMod("Oxygen").Call(...)`:

```csharp
// Exempt an NPC type from AI throttling (e.g. boss minions where npc.boss = false)
Call("ExemptNPCFromThrottling", npcType);

// Exempt a projectile type from ally hiding
Call("ExemptProjectileFromHiding", projectileType);

// Query whether a feature is currently active
bool active = (bool)Call("IsFeatureActive", "NPCThrottling");
// Valid keys:
//   DustLOD, ProjectileHiding, ProjectileAI, NPCThrottling, TownNPCThrottling,
//   GoreCulling, SoundThrottling, WorldUpdate, GCManagement,
//   DustParallelism, ProjParallelism, LightingOpt
```

---

## Thread safety notes

`DustParallelismSystem` runs dust updates on worker threads. To prevent crashes from thread-unsafe API calls made by third-party ModDust classes during worker execution:

- `ThreadUnsafeCallWatchdog` intercepts `Lighting.AddLight()` overloads. While the parallel window is open, calls are queued in a `ConcurrentBag<Action>` and drained on the main thread after all workers complete.
- `SoundThrottlingSystem` protects its dictionary with `lock(_lock)` so sound throttle checks from workers are safe.
- `OxygenParallel` propagates worker exceptions via `AggregateException`. On any error, the parallel path auto-disables for the session and falls back to sequential.
