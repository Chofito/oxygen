using System;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;
using Terraria.ID;

namespace Oxygen.Systems
{
    /// <summary>
    /// Reduces ambient world update frequency (grass growth, biome spread, etc.)
    /// during boss fights, halving the update rate without affecting other systems.
    ///
    /// Strategy:
    ///   tModLoader wraps the vanilla world update logic in UpdateWorld_Inner(), called
    ///   from WorldGen.UpdateWorld() between SystemLoader.PreUpdateWorld() and
    ///   SystemLoader.PostUpdateWorld(). We ILHook WorldGen.UpdateWorld() to inject a
    ///   conditional skip around the UpdateWorld_Inner() call only, so:
    ///   - Other mods' PreUpdateWorld / PostUpdateWorld hooks still run every tick.
    ///   - Invasions, events, and server sync are unaffected (they're driven elsewhere).
    ///   - Only ambient tile spread (grass, corruption, etc.) is throttled.
    ///
    /// Throttle behaviour:
    ///   During a boss fight, UpdateWorld_Inner() is called only on even game ticks
    ///   (every 2 frames). This is imperceptible for ambient effects (grass growth,
    ///   biome spread) while saving meaningful CPU time per frame.
    ///
    /// Why ILHook instead of IL.Terraria.WorldGen.UpdateWorld:
    ///   Using ILHook with explicit reflection targets the exact compiled method without
    ///   relying on HookGen name resolution, which can be fragile for tModLoader-split
    ///   methods. This mirrors the pattern used by DustParallelismSystem.
    /// </summary>
    public class WorldUpdateSystem : ModSystem
    {
        private ILHook? _worldHook;
        private static Mod? _mod; // captured for use in static callbacks

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void OnModLoad()
        {
            _mod = Mod; // capture before any early return

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.WorldUpdateThrottlingEnabled)
                return;

            try
            {
                var updateWorldMethod = typeof(WorldGen).GetMethod(
                    "UpdateWorld",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes, // no parameters
                    null)
                    ?? throw new Exception("Could not reflect WorldGen.UpdateWorld() - method not found.");

                _worldHook = new ILHook(updateWorldMethod, HookWorldUpdateIL);

                Mod.Logger.Info("[Oxygen] WorldUpdateThrottling: active.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] WorldUpdateThrottling: hook installation failed - feature disabled. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        public override void OnModUnload()
        {
            _worldHook?.Dispose();
            _worldHook = null;
        }

        // ── IL injection ─────────────────────────────────────────────────────

        /// <summary>
        /// Finds the <c>call UpdateWorld_Inner()</c> instruction in WorldGen.UpdateWorld()
        /// and wraps it with a conditional skip:
        ///
        ///   call ShouldSkipInnerUpdate   ; push bool
        ///   brtrue  skipLabel            ; if true, jump past UpdateWorld_Inner
        ///   call    UpdateWorld_Inner    ; original call (now conditional)
        ///   [skipLabel]                  ; rejoins here on skip
        /// </summary>
        private static void HookWorldUpdateIL(ILContext il)
        {
            var c = new ILCursor(il);

            try
            {
                var shouldSkip = typeof(WorldUpdateSystem).GetMethod(
                    nameof(ShouldSkipInnerUpdate),
                    BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new Exception("ShouldSkipInnerUpdate not found via reflection.");

                // Locate the call to UpdateWorld_Inner - match by method name to avoid
                // accessibility issues (it's private in the tML-patched assembly).
                if (!c.TryGotoNext(MoveType.Before,
                    x => x.MatchCall(out MethodReference mr) && mr.Name == "UpdateWorld_Inner"))
                {
                    _mod?.Logger.Warn(
                        "[Oxygen] WorldUpdateThrottling: UpdateWorld_Inner call not found in " +
                        "WorldGen.UpdateWorld IL - vanilla structure may have changed. Feature inactive.");
                    return;
                }

                var skipLabel = c.DefineLabel();

                // Inject before UpdateWorld_Inner:
                //   call ShouldSkipInnerUpdate()
                //   brtrue skipLabel
                c.Emit(OpCodes.Call, shouldSkip);
                c.Emit(OpCodes.Brtrue, skipLabel);

                // Advance past the UpdateWorld_Inner call and mark skipLabel there.
                c.GotoNext(MoveType.After,
                    x => x.MatchCall(out MethodReference mr) && mr.Name == "UpdateWorld_Inner");
                c.MarkLabel(skipLabel);
            }
            catch (Exception ex)
            {
                _mod?.Logger.Error(
                    $"[Oxygen] WorldUpdateThrottling: HookWorldUpdateIL failed - feature disabled. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        // ── Helper called from injected IL ───────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when the inner world update should be skipped this tick.
        /// During boss fights: skip on odd ticks (runs at 30 Hz instead of 60 Hz).
        /// Outside boss fights: never skip.
        /// </summary>
        private static bool ShouldSkipInnerUpdate()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null || !cfg.WorldUpdateThrottlingEnabled)
                return false;

            // In multiplayer, UpdateWorld_Inner() is authoritative on the server and
            // propagates tile changes to clients via sync packets. If a client skips
            // UpdateWorld_Inner() locally, its tile array diverges from the server's
            // between sync packets - grass and biome spread appear inconsistent.
            // Only throttle in singleplayer where there is no external authority.
            if (Main.netMode != NetmodeID.SinglePlayer)
                return false;

            // SharedBossState.BossActive is computed in PostUpdateNPCs,
            // which runs before WorldGen.UpdateWorld is called each tick.
            if (!SharedBossState.BossActive)
                return false;

            return Main.GameUpdateCount % 2 != 0;
        }
    }
}
