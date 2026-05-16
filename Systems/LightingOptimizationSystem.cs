using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    /// <summary>
    /// Skips Lighting.AddLight() calls for positions outside the visible viewport.
    ///
    /// Why this helps:
    ///   Every active NPC, projectile, and dust particle that emits light calls
    ///   Lighting.AddLight() once per tick. During a boss fight with 125 NPCs and
    ///   235 projectiles, this produces 300-400 AddLight calls per tick, many for
    ///   entities far off-screen. Each call writes to the lighting engine's work
    ///   queue and triggers a background-thread light propagation computation.
    ///
    ///   Culling off-screen AddLight calls reduces the lighting background thread's
    ///   work queue, allowing it to finish sooner and reducing the CPU sync cost at
    ///   the start of the next frame.
    ///
    /// Safety:
    ///   Light is purely visual. Skipping a light contribution for an off-screen
    ///   entity has no gameplay consequence. A 40-tile margin ensures entities that
    ///   are partially visible, or whose light reaches the viewport edge, are never
    ///   culled.
    ///
    /// Scope:
    ///   Hooks Lighting.AddLight(int tileX, int tileY, float r, float g, float b),
    ///   the primary overload that all other overloads ultimately call.
    ///   Silently skips if the method signature changes between Terraria versions.
    /// </summary>
    public class LightingOptimizationSystem : ModSystem
    {
        private ILHook? _addLightHook;

        // Tile margin around the visible viewport within which lights are still processed.
        // 40 tiles ≈ 640 px - generous enough to cover large NPC sprites and light spread.
        private const int ViewportMarginTiles = 40;

        public override void OnModLoad()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.LightingOptimizationEnabled)
                return;

            try
            {
                MethodInfo? addLightMethod = typeof(Lighting).GetMethod(
                    "AddLight",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int), typeof(int), typeof(float), typeof(float), typeof(float) },
                    null);

                if (addLightMethod == null)
                {
                    Mod.Logger.Warn(
                        "[Oxygen] LightingOpt: Lighting.AddLight(int,int,float,float,float) " +
                        "not found - optimization inactive.");
                    return;
                }

                _addLightHook = new ILHook(addLightMethod, PatchAddLight);
                Mod.Logger.Info("[Oxygen] LightingOpt: off-screen AddLight culling active.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] LightingOpt: hook failed - {ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void OnModUnload()
        {
            _addLightHook?.Dispose();
            _addLightHook = null;
        }

        // ── IL patch ──────────────────────────────────────────────────────────

        /// <summary>
        /// Injects at the very start of Lighting.AddLight(int i, int j, float, float, float):
        ///
        ///   if (IsOutsideViewport(i, j)) return;
        ///
        /// The existing ret at the end of the method is reused as the jump target,
        /// so no new ret instruction is needed and the original logic is untouched.
        /// </summary>
        private static void PatchAddLight(ILContext il)
        {
            var c = new ILCursor(il);

            // Locate the final ret - this becomes our early-exit target.
            c.Index = il.Instrs.Count - 1;
            if (il.Instrs[c.Index].OpCode != OpCodes.Ret)
            {
                // Unexpected structure - don't patch.
                ModContent.GetInstance<LightingOptimizationSystem>()?.Mod.Logger.Warn(
                    "[Oxygen] LightingOpt: unexpected IL structure in AddLight - patch skipped.");
                return;
            }

            var retLabel = c.DefineLabel();
            c.MarkLabel(retLabel);

            // Inject at the very beginning of the method.
            c.Index = 0;

            var isOutsideMethod = typeof(LightingOptimizationSystem).GetMethod(
                nameof(IsOutsideViewport),
                BindingFlags.Static | BindingFlags.NonPublic)!;

            // if (IsOutsideViewport(i, j)) goto ret;
            c.Emit(OpCodes.Ldarg_0);          // push i (tileX)
            c.Emit(OpCodes.Ldarg_1);          // push j (tileY)
            c.Emit(OpCodes.Call, isOutsideMethod);
            c.Emit(OpCodes.Brtrue, retLabel); // if true → jump to existing ret
        }

        // ── Viewport check ────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when the tile at (<paramref name="tileX"/>, <paramref name="tileY"/>)
        /// is outside the visible viewport plus a <see cref="ViewportMarginTiles"/>-tile margin.
        /// Called from the injected IL on every AddLight invocation - must be fast.
        /// </summary>
        private static bool IsOutsideViewport(int tileX, int tileY)
        {
            // Convert screen position to tile coordinates once per call.
            // Main.screenPosition is written by the main thread; reading it here
            // (also main thread) is safe. No lock needed.
            int screenTileX = (int)(Main.screenPosition.X / 16f);
            int screenTileY = (int)(Main.screenPosition.Y / 16f);
            int screenTileW = (Main.screenWidth  / 16) + 1;
            int screenTileH = (Main.screenHeight / 16) + 1;

            return tileX < screenTileX - ViewportMarginTiles
                || tileX > screenTileX + screenTileW + ViewportMarginTiles
                || tileY < screenTileY - ViewportMarginTiles
                || tileY > screenTileY + screenTileH + ViewportMarginTiles;
        }
    }
}
