# GlobalHooks - Oxygen Mod

Contains the `GlobalNPC` and `GlobalProjectile` classes that intercept per-entity behavior.

---

## GlobalNPCHooks

**File:** `GlobalNPCHooks.cs`
**Main class:** `GlobalNPCHooks : GlobalNPC`
**Companion class:** `GlobalNPCHooksSystem : ModSystem` (handles initialization that `GlobalNPC` cannot do directly)

### What it does - NPC AI throttling
`PreAI(NPC npc)` is called for every active NPC every tick. Returning `false` skips all AI logic for that NPC on that tick. When skipped, only `npc.position += npc.velocity` is applied to maintain basic movement.

### Decision tree

```
PreAI(npc)
│
├── Config disabled?                              → return true  (always run)
├── [MultiplayerClient]                           → return true  (never throttle on client)
│
├── [Town NPC path]
│   ├── TownNPCThrottling disabled?               → return true
│   ├── Near any player (2× threshold)?           → return true
│   └── GameUpdateCount % 3 != 0                  → return false (skip, ~20 Hz)
│
├── [Hostile NPC path]
│   ├── NPCThrottling disabled?                   → return true
│   ├── npc.boss == true?                         → return true  (bosses never throttled)
│   ├── npc.realLife → active boss head?          → return true  (worm/chain segments)
│   ├── npc.type in ExemptNPCs?                   → return true  (ModCall API)
│   ├── SharedBossState.BossActive?               → return true  (boss fight — see note)
│   │
│   ├── [SP/Client] npc.target == myPlayer?       → update LastTargetedLocalPlayer[slot]
│   ├── [SP/Client] targeted within last 60 ticks → return true  (actively chasing player)
│   │
│   ├── distance to nearest player < startDist?  → return true  (safe zone)
│   │
│   ├── Server: skipEvery = 2  (max 50%)
│   │   Client: skipEvery = 2/3/4 by distance bracket
│   │
│   └── GameUpdateCount % skipEvery != 0 → return false (skip - position-only)
│                                        → return true  (run normally)
```

### Automatic exemptions
- **Bosses** (`npc.boss`): never throttled.
- **Town NPCs** (`npc.townNPC`): handled by a separate, more conservative path.
- **Worm/chain segments** (`npc.realLife >= 0` and the head has `npc.boss`): the chain depends on every segment updating each tick. Throttling breaks relative position computation.
- **Boss fights active** (`SharedBossState.BossActive`): all hostile NPCs run full AI. Mods spawn non-boss helper NPCs to drive phase transitions and cinematic intro sequences. These NPCs require tick-perfect AI; throttling them causes intros to stall, freezing the player and breaking the camera.
- **NPCs that targeted the player recently** (last 60 ticks): prevents a chasing NPC from visibly "pausing" its AI.

### Multiplayer
| Context | Behavior |
|---|---|
| Singleplayer | `skipEvery` up to 4; `LastTargeted` tracking active |
| MP Client | Disabled entirely - server is authoritative; throttling causes rubber-banding |
| Dedicated Server | `skipEvery = 2` max; does not use `Main.myPlayer` (invalid index on server) |

### Static state
```csharp
internal static readonly int[] LastTargetedLocalPlayer = new int[201]; // tick when NPC targeted local player
```
Cleared in `OnWorldLoad`.

### Relevant config
| Key | Default | Range | Description |
|---|---|---|---|
| `NPCThrottlingEnabled` | `true` | | Enable/disable hostile NPC throttling |
| `TownNPCThrottlingEnabled` | `false` | | Enable/disable town NPC throttling |
| `NPCThrottleStartDistance` | `80` | 40-300 | Tiles from the player where throttling begins |

---

## GlobalProjectileHooks

**File:** `GlobalProjectileHooks.cs`
**Class:** `GlobalProjectileHooks : GlobalProjectile`

### What it does
Implements two independent optimizations in a single file.

### 1. Projectile AI throttling (`PreAI`)
Skips AI every other tick for visual-only projectiles (`damage=0`, not hostile) that are outside the extended viewport (256 px margin). Targets purely decorative projectiles with no hitbox.

- Position still advances via `velocity` on skipped ticks (applied by vanilla outside of the AI call).
- Only applies to projectiles owned by the local player in multiplayer (owner is AI authority).
- Never skips hostile projectiles.

### 2. Viewport culling + ally hiding (`PreDraw`)
Returns `false` to skip rendering. AI and hitboxes are unaffected.

**Viewport culling** - player-owned projectiles only (256 px margin):
- NPC/boss-owned projectiles are intentionally excluded from culling - they can extend far beyond the viewport and must not flicker out.

**Ally projectile hiding:**

Conditions to hide:
- `HideAllyProjectilesEnabled = true`
- Projectile is NOT ours (`projectile.owner != Main.myPlayer`)
- Owner is a valid, active player
- Owner is not in hostile mode (PvP)
- If `HideAllyProjectilesBossOnly = true`, only hides during boss fights
- Type is not in `Oxygen.ExemptProjectiles` (ModCall API)

Modes:
- `ProjectileOpacityPercent = 0` → `return false` (invisible, maximum performance)
- `ProjectileOpacityPercent > 0` → reduce `lightColor` to that % and `return true` (dimmed)

### Why only draw (not AI)
Hiding ally projectile AI would cause multiplayer desyncs - the server and other clients would continue processing the projectile with a different trajectory. Hiding only the draw is a pure visual gain with no gameplay side effects.

### Relevant config
| Key | Default | Description |
|---|---|---|
| `ProjectileAIThrottlingEnabled` | `true` | Enable off-screen AI throttling |
| `HideAllyProjectilesEnabled` | `true` | Enable ally hiding system |
| `HideAllyProjectilesBossOnly` | `true` | Limit to boss fights |
| `ProjectileOpacityPercent` | `0` | 0 = hide, 1-100 = reduce alpha |
