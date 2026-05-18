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
using MethodBody = Mono.Cecil.Cil.MethodBody;
using static Oxygen.Utilities.ThreadUnsafeCallWatchdog;

namespace Oxygen.Systems
{
    /// <summary>
    /// Parallelizes the vanilla Dust.UpdateDust() loop using IL patching.
    ///
    /// Strategy (mirrors Nitrate mod's approach, with major safety improvements):
    ///   1. Capture the original Dust.UpdateDust IL body before modification.
    ///   2. Patch UpdateDust to skip its sequential loop and call RunParallel() instead.
    ///   3. Inject the cloned loop body (with adjustable bounds) into our UpdateDustFiller
    ///      stub via an ILHook, so each worker thread can run an independent slice.
    ///
    /// Safety improvements over Nitrate:
    ///   - Worker exceptions are captured and re-thrown (Nitrate silences them).
    ///   - On any runtime error, falls back to sequential execution for that frame
    ///     then auto-disables, so vanilla behaviour is never lost.
    ///   - Hook installation is wrapped in try/catch; if IL patterns change between
    ///     Terraria versions the feature simply disables itself instead of crashing.
    ///   - Requires ProcessorCount >= 2 before enabling.
    /// </summary>
    public class DustParallelismSystem : ModSystem
    {
        // MethodInfo for our two static stubs - resolved once at class load time.
        private static readonly MethodInfo _fillerInfo =
            typeof(DustParallelismSystem).GetMethod(
                nameof(UpdateDustFiller), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo _runParallelInfo =
            typeof(DustParallelismSystem).GetMethod(
                nameof(RunParallel), BindingFlags.NonPublic | BindingFlags.Static)!;

        // The captured Dust.UpdateDust body, used by PatchUpdateDustFiller.
        // Nulled after the filler is patched so the GC can reclaim it.
        private static MethodBody? _capturedDustBody;

        // ILHooks - all three are held so they can be disposed cleanly on unload.
        private ILHook? _captureHook;   // CaptureBody on Dust.UpdateDust
        private ILHook? _fillerHook;    // PatchFiller on UpdateDustFiller stub
        private ILHook? _patchLoopHook; // PatchLoop  on Dust.UpdateDust

        // Runtime state.
        private static bool _parallelEnabled;  // true while parallel is healthy
        private static Mod? _mod;              // captured at load time for use in static callbacks

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public override void OnModLoad()
        {
            _mod = Mod; // capture before any early return so static callbacks can log

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.DustParallelismEnabled)
                return;

            if (Environment.ProcessorCount < 2)
            {
                Mod.Logger.Info("[Oxygen] DustParallelism: single-core CPU detected, feature skipped.");
                return;
            }

            try
            {
                // Reflect the target method once - avoids any IL./HookGen symbol resolution.
                var updateDustMethod = typeof(Dust).GetMethod(
                    "UpdateDust",
                    BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null)
                    ?? throw new Exception("Could not reflect Dust.UpdateDust() - method not found.");

                // Order matters: capture body first, then patch filler, then patch the loop.
                // ILHook callbacks are invoked eagerly at construction time, so creation
                // order equals execution order.
                _captureHook   = new ILHook(updateDustMethod, CaptureBody);
                _fillerHook    = new ILHook(_fillerInfo, PatchFiller);
                _patchLoopHook = new ILHook(updateDustMethod, PatchLoop);

                _parallelEnabled = true;

                // Install the watchdog AFTER IL hooks so the hook chain is fully built.
                // This defers Lighting.AddLight() calls made by Calamity/Infernum ModDust
                // during worker execution, preventing crashes from thread-unsafe lighting API.
                ThreadUnsafeCallWatchdog.Install(Mod);

                int workers = Math.Max(1, Environment.ProcessorCount - 1);
                Mod.Logger.Info($"[Oxygen] DustParallelism: active with {workers} worker threads.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] DustParallelism: hook installation failed - " +
                    $"falling back to vanilla. ({ex.GetType().Name}: {ex.Message})");
                Unregister();
            }
        }

        public override void OnModUnload() => Unregister();

        private void Unregister()
        {
            // Dispose in reverse registration order for clean hook chain teardown.
            _patchLoopHook?.Dispose();
            _patchLoopHook = null;
            _fillerHook?.Dispose();
            _fillerHook = null;
            _captureHook?.Dispose();
            _captureHook = null;

            // Watchdog hooks must be removed AFTER IL hooks so the chain unwinds cleanly.
            ThreadUnsafeCallWatchdog.Uninstall();
        }

        // ── IL hooks ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Hook 1 - runs first. Stores the unmodified UpdateDust body so the
        /// filler can clone it.
        /// </summary>
        private static void CaptureBody(ILContext il)
        {
            _capturedDustBody = il.Body;
        }

        /// <summary>
        /// Hook 2 (on Dust.UpdateDust) - skips the original sequential loop and
        /// calls <see cref="RunParallel"/> instead.
        ///
        /// Before patch:
        ///   ldc.i4.0 / stloc i / br loopCheck
        ///   loopBody: ... / ldloc i / ldc.i4 maxDust / blt loopBody
        ///   ret
        ///
        /// After patch:
        ///   ldc.i4.0 / stloc i / br skipLabel  ← only change here
        ///   loopBody: ... (dead code)
        ///   blt loopBody
        ///   [skipLabel] call RunParallel        ← injected
        ///   ret
        /// </summary>
        private static void PatchLoop(ILContext il)
        {
            var c = new ILCursor(il);

            try
            {
                var skipLabel = c.DefineLabel();

                // Find the unconditional branch that starts the loop (br loopCheck).
                ILLabel? loopCheckLabel = null;
                if (!c.TryGotoNext(MoveType.Before, x => x.MatchBr(out loopCheckLabel))
                    || loopCheckLabel is null)
                    throw new Exception("Cannot find loop entry branch (Br) in Dust.UpdateDust.");

                // Insert 'br skipLabel' immediately before the original 'br loopCheck'.
                // The original branch becomes dead code.
                c.Emit(OpCodes.Br, skipLabel);

                // Move to the loop condition (Blt at the bottom of the loop).
                c.GotoLabel(loopCheckLabel);
                if (!c.TryGotoNext(MoveType.After, x => x.MatchBlt(out _)))
                    throw new Exception("Cannot find loop condition (Blt) in Dust.UpdateDust.");

                // Inject the call to RunParallel right after the Blt.
                c.Emit(OpCodes.Call, _runParallelInfo);

                // The skip label must point to RunParallel (the instruction we just inserted).
                // GotoPrev repositions us just after the Blt, before the new Call.
                c.GotoPrev(MoveType.After, x => x.MatchBlt(out _));
                c.MarkLabel(skipLabel);
            }
            catch (Exception ex)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] DustParallelism: PatchLoop failed - feature disabled. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// ILHook on <see cref="UpdateDustFiller"/> - replaces the empty stub body
        /// with a clone of the original UpdateDust loop, with parameterised bounds.
        ///
        /// The cloned loop uses parameters:
        ///   arg0 = inclusive start
        ///   arg1 = exclusive end
        /// instead of the vanilla hardcoded 0 / Main.maxDust.
        /// </summary>
        private static void PatchFiller(ILContext il)
        {
            if (_capturedDustBody is null)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    "[Oxygen] DustParallelism: PatchFiller ran before body was captured - " +
                    "feature disabled.");
                return;
            }

            var c = new ILCursor(il);

            try
            {
                ILMethodBodyCloner.CloneBodyToCursor(_capturedDustBody, c);

                // Replace upper bound: ldc.i4 <maxDust> → ldarg.1 (exclusive param)
                if (!c.TryGotoNext(MoveType.After, x => x.MatchLdcI4(Main.maxDust)))
                    throw new Exception("Cannot find Main.maxDust constant in cloned body.");
                c.Emit(OpCodes.Pop);
                c.Emit(OpCodes.Ldarg_1);

                // Find which local holds the loop counter (by searching backwards from here).
                int loopVarIdx = -1;
                c.GotoPrev(x => x.MatchLdloc(out loopVarIdx));
                if (loopVarIdx < 0)
                    throw new Exception("Cannot identify loop counter local variable.");

                // Replace lower bound: ldc.i4.0 → ldarg.0 (inclusive param)
                c.Index = 0;
                if (!c.TryGotoNext(x => x.MatchStloc(loopVarIdx)))
                    throw new Exception("Cannot find loop counter initialisation.");
                c.GotoPrev(MoveType.After, x => x.MatchLdcI4(0));
                c.Emit(OpCodes.Pop);
                c.Emit(OpCodes.Ldarg_0);
            }
            catch (Exception ex)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] DustParallelism: PatchFiller failed - feature disabled. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
            finally
            {
                _capturedDustBody = null; // allow GC
            }
        }

        // ── Stubs / runtime ──────────────────────────────────────────────────────

        /// <summary>
        /// Stub method. Its body is replaced at load time by <see cref="PatchFiller"/>
        /// to contain the original Dust.UpdateDust loop restricted to [inclusive, exclusive).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpdateDustFiller(int inclusive, int exclusive) { }

        /// <summary>
        /// Called from the patched Dust.UpdateDust instead of the sequential loop.
        /// Distributes work across ThreadPool workers. Falls back to sequential on
        /// any error and permanently disables the parallel path for this session.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RunParallel()
        {
            if (!_parallelEnabled)
            {
                // Parallel is disabled - run the cloned loop sequentially so dust
                // still updates normally (the original loop was already skipped).
                UpdateDustFiller(0, Main.maxDust);
                return;
            }

            try
            {
                // Open the watchdog window: any Lighting.AddLight() calls made by
                // Calamity/Infernum ModDust on worker threads will be queued instead
                // of executing immediately (which would cause lighting engine race conditions).
                ThreadUnsafeCallWatchdog.Enable();
                OxygenParallel.For(0, Main.maxDust, UpdateDustFiller);
                // Drain queued AddLight calls on the main thread now that all workers
                // have finished (OxygenParallel.For uses CountdownEvent sync internally).
                ThreadUnsafeCallWatchdog.Disable();
            }
            catch (AggregateException ex)
            {
                // Ensure watchdog is always closed, even on error.
                ThreadUnsafeCallWatchdog.Disable();

                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] DustParallelism: runtime error, auto-disabled for this session. " +
                    $"Inner: {ex.InnerException?.Message ?? ex.Message}");

                // Still need to update dust this frame.
                UpdateDustFiller(0, Main.maxDust);
            }
        }
    }
}
