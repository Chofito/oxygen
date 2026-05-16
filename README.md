# Oxygen - Performance Optimizer

Pure performance mod, zero gameplay changes. Compatible with heavy modpacks (Calamity + Infernum + Wrath of the Gods + Thorium + Consolaria). All settings are `ClientSide`.

---

## Implemented systems

| File | System | Impact type |
|---|---|---|
| `Config/ClientConfig.cs` | All configuration | - |
| `Systems/FrameTimeDiagnosticsSystem.cs` | HUD overlay with per-system timing breakdown | Visual |
| `Systems/SharedBossState.cs` | Shared state cache (BossActive, entity counts) | - |
| `Systems/DustOptimizationSystem.cs` | Off-screen dust cull + density cap | CPU/GPU |
| `Systems/DustParallelismSystem.cs` | Parallelize UpdateDust() via ILHook | CPU multi-core |
| `Systems/GoreCullingSystem.cs` | Remove off-screen gore by timeout | CPU |
| `Systems/LightingOptimizationSystem.cs` | Skip Lighting.AddLight() for off-screen positions | CPU (lighting thread) |
| `Systems/SoundThrottlingSystem.cs` | Limit same sound N times per time window | CPU/audio |
| `Systems/WorldUpdateSystem.cs` | Halve UpdateWorld_Inner rate during bosses (SP only) | CPU |
| `Systems/GCPressureManager.cs` | SustainedLowLatency GC during boss fights | GC pauses |
| `Systems/ProjectileParallelismSystem.cs` | Parallelize visual projectiles (damage=0) via ILHook | CPU multi-core |
| `GlobalHooks/GlobalNPCHooks.cs` | Throttle AI of distant NPCs | CPU |
| `GlobalHooks/GlobalProjectileHooks.cs` | Cull + hide ally projectiles | CPU/GPU |
| `Utilities/OxygenParallel.cs` | ThreadPool parallel-for with exception propagation | - |
| `Utilities/ILMethodBodyCloner.cs` | Clone a method's IL body into an ILCursor | - |
| `Oxygen.cs` | Public ModCall API | - |

---

## Hook architecture

```
Game loop
├── PreUpdatePlayers()        → FrameTimeDiagnosticsSystem (start update timer)
├── PreUpdateDusts()          → DustOptimizationSystem     (cull off-screen dust)
├── Dust.UpdateDust()         → DustParallelismSystem      (ILHook - parallelize loop)
├── PreUpdateGores()          → GoreCullingSystem          (timeout off-screen gore)
├── GlobalNPC.PreAI()         → GlobalNPCHooks             (skip AI for distant NPCs)
├── Main.PreUpdateAllProjectiles() → ProjectileParallelismSystem (ILHook - safe/unsafe split)
├── GlobalProjectile.PreAI()  → GlobalProjectileHooks      (throttle off-screen visual projectiles)
├── GlobalProjectile.PreDraw()→ GlobalProjectileHooks      (cull + hide allies)
├── WorldGen.UpdateWorld()    → WorldUpdateSystem          (ILHook - skip UpdateWorld_Inner)
├── SoundEngine.PlaySound()   → SoundThrottlingSystem      (ILHook - rolling window)
├── Lighting.AddLight()       → LightingOptimizationSystem (ILHook - skip off-screen)
├── PostUpdateNPCs()          → SharedBossState            (update BossActive, ActiveNPCCount)
├── PostUpdateEverything()    → SharedBossState            (update ActiveProjectileCount, ActiveDustCount)
│                             → GCPressureManager          (change GC mode)
│                             → FrameTimeDiagnosticsSystem (stop update timer, start render timers)
├── PostDrawTiles()           → FrameTimeDiagnosticsSystem (stop tile timer)
└── PostDrawInterface()       → FrameTimeDiagnosticsSystem (draw overlay, stop GFX timer)
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

---

## Diagnostics overlay

Enable `ShowDiagnosticsOverlay` in Mod Config to display:

```
FPS: 47  (21.3ms)  Upd×1
Plyr: 1.2ms        ← local player (inventory, buffs, movement)
NPC : 8.4ms        ← all NPC AI
Gore: 0.1ms
Proj: 3.7ms        ← all projectile updates
Itms: 0.2ms
Dust: 4.1ms        ← UpdateDust() (parallelized if active)
Wrld: 0.8ms
?Upd: 5.2ms        ← Calamity/Infernum hooks + tModLoader overhead
ΣUpd: 23.7ms (1×)  ← total update cost this draw frame
Tile: 2.1ms        ← tile draw calls
GFX : 8.3ms        ← NPCs, projectiles, shaders, UI
?Oth: 1.4ms        ← lighting thread sync, SoundEngine, XNA overhead
Dust: 4820/6000
Proj: 145/1000
NPCs: 67/200
D P N G S T L     ← uppercase = active, lowercase = inactive
```

**Feature bar letters:** D=DustThrottle, P=ProjectileHiding, N=NPCThrottling, G=GoreCulling, S=SoundThrottling, T=TownNPCThrottling, L=LightingOpt

**`?Upd`** = time inside the update window not covered by our per-system hooks (Calamity GlobalNPC callbacks, Infernum hooks, inter-system tModLoader overhead).

**`?Oth`** = frame time not covered by update or GFX. Primarily: lighting thread sync, SoundEngine.Update, XNA/FNA housekeeping, and MonoGame catch-up loop overhead when `N > 1`.

**`Upd×N`** = how many Update() calls MonoGame fired per Draw(). If `N > 1`, MonoGame is compensating for lag: real CPU cost per draw frame is `ΣUpd = updateTotal × N`.

---

## Public ModCall API

Other mods can interact via `ModLoader.GetMod("Oxygen").Call(...)`:

```csharp
// Exempt an NPC from throttling (e.g. boss minions where npc.boss = false)
Call("ExemptNPCFromThrottling", npcType);

// Exempt a projectile type from ally hiding
Call("ExemptProjectileFromHiding", projectileType);

// Query whether a feature is active
bool active = (bool)Call("IsFeatureActive", "NPCThrottling");
// Valid keys:
//   DustLOD, ProjectileHiding, ProjectileAI, NPCThrottling, TownNPCThrottling,
//   GoreCulling, SoundThrottling, WorldUpdate, GCManagement,
//   DustParallelism, ProjParallelism, LightingOpt
```
