using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;
using Oxygen.Utilities;
using Terraria.ID;

namespace Oxygen.Systems
{
    /// <summary>
    /// Parallelizes the vanilla projectile update loop using IL patching.
    ///
    /// Strategy:
    ///   ILHook on Main.PreUpdateAllProjectiles() replaces its sequential
    ///   for-loop with a two-tier update:
    ///
    ///   ┌─ Tier 1 - Parallel (safe tier) ──────────────────────────────────┐
    ///   │  Condition: damage ≤ 0 AND !hostile AND !friendly                │
    ///   │             AND ModProjectile == null (vanilla only)             │
    ///   │                                                                   │
    ///   │  Vanilla-only restriction: mod projectile AI cannot be audited   │
    ///   │  for thread safety. Even damage=0 mod projectiles may call       │
    ///   │  SoundEngine, spawn dust, or read/write player state in ways     │
    ///   │  that race with the main thread. Vanilla projectile AI is stable │
    ///   │  and their update paths are well-understood.                     │
    ///   │                                                                   │
    ///   │  Thread-safe because:                                             │
    ///   │    • damage=0 → NPC damage check is a no-op, no NPC state write  │
    ///   │    • !hostile → no player health modification                     │
    ///   │    • Only writes to own slot (position, rotation, timeLeft)       │
    ///   │    • Tile reads are read-only during update phase                 │
    ///   └───────────────────────────────────────────────────────────────────┘
    ///   ┌─ Tier 2 - Sequential (unsafe tier) ──────────────────────────────┐
    ///   │  Everything else (damage>0, hostile, or friendly).               │
    ///   │  Runs in slot order, identical to vanilla behavior.              │
    ///   └───────────────────────────────────────────────────────────────────┘
    ///
    /// Why simpler than DustParallelismSystem:
    ///   Dust.UpdateDust() loops over Dust values internally - we had to clone
    ///   its IL body into a filler stub to give workers independent ranges.
    ///   Projectile.Update(int) is a public instance method - workers can call
    ///   it directly with their assigned slot index. No ILMethodBodyCloner needed.
    ///
    /// Known benign race conditions in the parallel phase:
    ///   • Dust.NewDust() / Main.dustIndex - two workers may claim the same
    ///     dust slot simultaneously → one particle is lost/overwritten.
    ///     Result: occasional missing aura particle. Invisible in practice.
    ///   • Main.ProjectileUpdateLoopIndex - multiple workers write different
    ///     slot values simultaneously. Code checking == -1 ("outside loop")
    ///     correctly sees a non-(-1) value, satisfying the intent. Code checking
    ///     for a specific slot value (extremely rare) may see a wrong value.
    ///
    /// Safety net: worker exceptions are caught as AggregateException, parallel
    /// is permanently disabled for the session, and the frame completes
    /// sequentially. Vanilla behavior is never permanently lost.
    /// </summary>
    public class ProjectileParallelismSystem : ModSystem
    {
        // ── Static method reference ───────────────────────────────────────────

        private static readonly MethodInfo _runParallelInfo =
            typeof(ProjectileParallelismSystem).GetMethod(
                nameof(RunParallel), BindingFlags.NonPublic | BindingFlags.Static)!;

        // Pre-cache the delegate to avoid per-frame allocation when passing to OxygenParallel.For
        private static readonly Action<int, int> _updateSafeDelegate = UpdateSafePartition;

        // ── Pre-allocated categorization buffers (zero per-frame allocations) ─

        private static readonly int[] _safeSlots   = new int[Main.maxProjectiles];
        private static readonly int[] _unsafeSlots = new int[Main.maxProjectiles];

        // ── ILHook ────────────────────────────────────────────────────────────

        private ILHook? _patchLoopHook;

        // ── Runtime state ─────────────────────────────────────────────────────

        private static bool _parallelEnabled;
        private static Mod? _mod;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void OnModLoad()
        {
            _mod = Mod;

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.ProjectileParallelismEnabled)
                return;

            if (Environment.ProcessorCount < 2)
            {
                Mod.Logger.Info("[Oxygen] ProjectileParallelism: single-core CPU detected, skipped.");
                return;
            }

            try
            {
                // PreUpdateAllProjectiles() is private static in Terraria.Main.
                // tModLoader wraps it with SystemLoader.PreUpdateProjectiles() /
                // PostUpdateProjectiles() but does not rename it.
                var targetMethod = typeof(Main).GetMethod(
                    "PreUpdateAllProjectiles",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, Type.EmptyTypes, null)
                    ?? throw new Exception(
                        "Cannot reflect Main.PreUpdateAllProjectiles() - " +
                        "method not found. tModLoader version may have changed.");

                _patchLoopHook = new ILHook(targetMethod, PatchLoop);
                _parallelEnabled = true;

                int workers = Math.Max(1, Environment.ProcessorCount - 1);
                Mod.Logger.Info($"[Oxygen] ProjectileParallelism: active with {workers} worker threads.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] ProjectileParallelism: hook installation failed - " +
                    $"vanilla fallback. ({ex.GetType().Name}: {ex.Message})");
            }
        }

        public override void OnModUnload()
        {
            _patchLoopHook?.Dispose();
            _patchLoopHook = null;
            _parallelEnabled = false;
        }

        // ── IL patch ──────────────────────────────────────────────────────────

        /// <summary>
        /// Skips PreUpdateAllProjectiles()'s sequential for-loop and injects
        /// a call to RunParallel() after it. Identical pattern to
        /// DustParallelismSystem.PatchLoop().
        ///
        /// Before:
        ///   ldc.i4.0 / stloc i / br loopCheck
        ///   loopBody: ... call Projectile.Update ...
        ///   loopCheck: ldloc i / ldc.i4 maxProjectiles / blt loopBody
        ///   [rest of method]
        ///
        /// After:
        ///   ldc.i4.0 / stloc i / br skipLabel      ← rerouted
        ///   loopBody: [dead code, never executed]
        ///   blt loopBody
        ///   [skipLabel] call RunParallel()          ← injected
        ///   [rest of method]
        /// </summary>
        private static void PatchLoop(ILContext il)
        {
            var c = new ILCursor(il);

            try
            {
                var skipLabel = c.DefineLabel();

                // Locate the unconditional branch that enters the loop check.
                ILLabel? loopCheckLabel = null;
                if (!c.TryGotoNext(MoveType.Before, x => x.MatchBr(out loopCheckLabel))
                    || loopCheckLabel is null)
                    throw new Exception(
                        "Cannot find loop entry branch (Br) in PreUpdateAllProjectiles - " +
                        "loop structure may have changed.");

                // Insert 'br skipLabel' before the original 'br loopCheck'.
                c.Emit(OpCodes.Br, skipLabel);

                // Advance to the loop condition (Blt at the bottom of the loop).
                c.GotoLabel(loopCheckLabel);
                if (!c.TryGotoNext(MoveType.After, x => x.MatchBlt(out _)))
                    throw new Exception(
                        "Cannot find loop condition (Blt) in PreUpdateAllProjectiles.");

                // Inject RunParallel() call after the Blt.
                c.Emit(OpCodes.Call, _runParallelInfo);

                // Mark skipLabel at the position of the RunParallel call.
                c.GotoPrev(MoveType.After, x => x.MatchBlt(out _));
                c.MarkLabel(skipLabel);
            }
            catch (Exception ex)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] ProjectileParallelism: PatchLoop failed - feature disabled. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        // ── Runtime ───────────────────────────────────────────────────────────

        /// <summary>
        /// Called from the patched PreUpdateAllProjectiles() instead of the
        /// sequential loop. Categorizes all active projectile slots into safe
        /// (parallel) and unsafe (sequential) tiers, then dispatches accordingly.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RunParallel()
        {
            // ── Dedicated-server guard ────────────────────────────────────────
            // On a server, Projectile.Update() can call NetMessage.SendData() to
            // sync state to connected clients. NetMessage writes to shared packet
            // buffers that are not thread-safe from ThreadPool workers.
            // Run the full vanilla-equivalent sequential loop instead.
            if (Main.netMode == NetmodeID.Server)
            {
                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile p = Main.projectile[i];
                    if (!p.active) continue;
                    Main.ProjectileUpdateLoopIndex = i;
                    p.Update(i);
                }
                return;
            }

            // ── Categorize active slots - O(maxProjectiles=1000), ~1 µs ─────
            int safeCount   = 0;
            int unsafeCount = 0;

            for (int i = 0; i < Main.maxProjectiles; i++)
            {
                Projectile p = Main.projectile[i];
                if (!p.active) continue;

                // Safe tier: vanilla projectiles only (no ModProjectile).
                // Mod projectiles are excluded regardless of damage=0, because their AI
                // may call SoundEngine, access player state, or spawn dust in ways we
                // cannot audit without reading every mod's source. Vanilla projectiles
                // are well-tested and their AI is deterministic and thread-safe.
                if (p.damage <= 0 && !p.hostile && !p.friendly && p.ModProjectile == null)
                    _safeSlots[safeCount++] = i;
                else
                    _unsafeSlots[unsafeCount++] = i;
            }

            // ── Tier 1: safe projectiles ──────────────────────────────────────
            if (!_parallelEnabled)
            {
                // Parallel path disabled - run safe slots sequentially.
                // (We already have the slot list, so no need to re-scan.)
                RunSlotsSequentially(_safeSlots, safeCount);
            }
            else
            {
                try
                {
                    if (safeCount > 0)
                        OxygenParallel.For(0, safeCount, _updateSafeDelegate);
                }
                catch (AggregateException ex)
                {
                    _parallelEnabled = false;
                    _mod?.Logger.Error(
                        $"[Oxygen] ProjectileParallelism: runtime error in worker, " +
                        $"auto-disabled for this session. " +
                        $"Inner: {ex.InnerException?.Message ?? ex.Message}");

                    // Complete the safe slots sequentially for this frame.
                    RunSlotsSequentially(_safeSlots, safeCount);
                }
            }

            // ── Tier 2: unsafe projectiles - always sequential ────────────────
            RunSlotsSequentially(_unsafeSlots, unsafeCount);
        }

        /// <summary>
        /// Processes a partition of the safe-tier slot list.
        /// Called from ThreadPool workers by OxygenParallel.For -
        /// each worker receives an independent [from, to) range of indices
        /// into <see cref="_safeSlots"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpdateSafePartition(int from, int to)
        {
            for (int j = from; j < to; j++)
            {
                int slot = _safeSlots[j];
                // Mirror vanilla behavior: set the loop index before calling Update.
                // This is a data race in the parallel phase (other workers write
                // different values simultaneously) - benign for visual projectiles.
                Main.ProjectileUpdateLoopIndex = slot;
                Projectile p = Main.projectile[slot];
                if (p.active) p.Update(slot);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RunSlotsSequentially(int[] slots, int count)
        {
            for (int j = 0; j < count; j++)
            {
                int slot = slots[j];
                Main.ProjectileUpdateLoopIndex = slot;
                Projectile p = Main.projectile[slot];
                if (p.active) p.Update(slot);
            }
        }
    }
}
