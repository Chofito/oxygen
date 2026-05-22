# Config - Oxygen Mod

**File:** `ClientConfig.cs`
**Class:** `ClientConfig : ModConfig`
**Scope:** `ConfigScope.ClientSide` - never synced to the server. The server always uses default values.

---

## Full option reference

### Dust

| Property | Type | Default | Range | Description |
|---|---|---|---|---|
| `DustUpdateThrottling` | bool | `true` | | Enables DustOptimizationSystem (viewport cull + density cap) |
| `DustDensityPercent` | int | `80` | 0-100 | % of max particles allowed. 80 = max 4800 active dust |
| `DustCullDistance` | int | `80` | 20-200 | Tiles from the player. Dust farther away is culled when over the density cap |

### Projectiles

| Property | Type | Default | Range | Description |
|---|---|---|---|---|
| `HideAllyProjectilesEnabled` | bool | `true` | | Enables GlobalProjectileHooks (hide/dim ally projectiles) |
| `HideAllyProjectilesBossOnly` | bool | `true` | | Only hide ally projectiles while a boss is active |
| `ProjectileOpacityPercent` | int | `0` | 0-100 | 0 = fully invisible. 1-100 = reduce alpha to that % |
| `ProjectileAIThrottlingEnabled` | bool | `true` | | Skip AI every other tick for off-screen visual-only projectiles (damage=0) |

### NPC Throttling

| Property | Type | Default | Range | Description |
|---|---|---|---|---|
| `NPCThrottlingEnabled` | bool | `true` | | Enables GlobalNPCHooks (skip AI for distant NPCs) |
| `NPCThrottleStartDistance` | int | `80` | 40-300 | Tiles. NPCs beyond this distance begin to be throttled |
| `TownNPCThrottlingEnabled` | bool | `false` | | Throttle off-screen town NPCs (~20 Hz). Near-player NPCs always run full AI |

### Gore

| Property | Type | Default | Range | Description |
|---|---|---|---|---|
| `GoreCullingEnabled` | bool | `true` | | Enables GoreCullingSystem |
| `GoreTimeoutTicks` | int | `300` | 60-600 | Off-screen ticks before removing gore. 300 = 5 seconds |

### Sound

| Property | Type | Default | Range | Description |
|---|---|---|---|---|
| `SoundThrottlingEnabled` | bool | `true` | | Enables SoundThrottlingSystem |
| `MaxSoundInstancesPerWindow` | int | `3` | 1-10 | Max times the same sound can play within the window |
| `SoundWindowTicks` | int | `10` | 5-30 | Time window in ticks. 10 ticks ≈ 0.17 seconds |

### World

| Property | Type | Default | Description |
|---|---|---|---|
| `WorldUpdateThrottlingEnabled` | bool | `true` | Enables WorldUpdateSystem (reduces UpdateWorld_Inner to 30 Hz during bosses) |

### GC Management

| Property | Type | Default | Description |
|---|---|---|---|
| `GCManagementEnabled` | bool | `false` | Enables GCPressureManager. Enable with caution |

Default `false` because `GCLatencyMode.SustainedLowLatency` allows memory to grow during a boss fight. In long sessions with many mods this may approach OOM.

### Lighting

| Property | Type | Default | Description |
|---|---|---|---|
| `LightingOptimizationEnabled` | bool | `true` | Skip Lighting.AddLight() for positions outside the viewport (40-tile margin) |

### Parallelism

| Property | Type | Default | Description |
|---|---|---|---|
| `DustParallelismEnabled` | bool | `true` | Enables DustParallelismSystem (distributes UpdateDust across multiple cores) |
| `ProjectileParallelismEnabled` | bool | `true` | Enables ProjectileParallelismSystem (visual-only projectiles run in parallel) |

Both auto-disable on any runtime error. Require 2 or more logical cores.

### Misc

| Property | Type | Default | Description |
|---|---|---|---|
| `FasterPylonEnabled` | bool | `true` | Enables PylonProximitySystem (O(pylons) proximity check instead of area scan) |
| `NewLaserRulerRenderingEnabled` | bool | `true` | Enables LaserRulerRenderSystem (~200 draw calls vs ~14000 vanilla) |

### Diagnostics

| Property | Type | Default | Description |
|---|---|---|---|
| `ShowDiagnosticsOverlay` | bool | `false` | Show the FPS / entity count / timing overlay |
| `DiagnosticsPosition` | `OverlayPosition` | `TopLeft` | Corner where the overlay appears |

#### Enum `OverlayPosition`
```csharp
public enum OverlayPosition { TopLeft, TopRight, BottomLeft, BottomRight }
```

---

## Design notes

### Why everything is ClientSide
All optimizations operate on local visual data or AI prediction. Changing them on the server would desync clients that do not have the mod. With `ClientSide`, the server never receives or applies these values.

### Reading config from other systems
```csharp
var cfg = ModContent.GetInstance<ClientConfig>();
if (cfg is null || !cfg.SomeOption) return;
```
`GetInstance<T>()` is O(1) - safe to call every frame.

### Extending the config
1. Add a property with `[DefaultValue(...)]`, `[Range(...)]`, `[Label(...)]`, `[Tooltip(...)]`.
2. tModLoader serializes/deserializes it automatically via its Config system.
3. Add the corresponding key in `Oxygen.cs` `Call("IsFeatureActive", key)` if you want to expose it via ModCall.
