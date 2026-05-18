using System;
using System.Collections.Concurrent;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;

namespace Oxygen.Utilities
{
    /// <summary>
    /// Guards against thread-unsafe Terraria API calls during parallel worker execution.
    ///
    /// Problem:
    ///   Calamity and Infernum ModDust classes call Lighting.AddLight() inside their
    ///   Update() / MidUpdate() overrides (confirmed in 11+ Calamity dust types:
    ///   CeaselessDust, AbsorberDust, DiamondDust, FinalFlame, HolyFireDust, LightDust,
    ///   SquareDust, SquashDust, UnstableDust, VoidDust, LavaSplashDust, and more).
    ///
    ///   When DustParallelismSystem dispatches dust updates to worker threads, those
    ///   ModDust.Update() calls execute Lighting.AddLight() from non-main threads,
    ///   causing race conditions in the lighting engine and intermittent crashes.
    ///
    /// Solution:
    ///   Intercept both Lighting.AddLight overloads. While Enabled (the parallel work
    ///   window is open), queue each call as an Action instead of executing it.
    ///   When Disable() is called (back on the main thread, after all workers have
    ///   completed), drain the queue sequentially on the main thread.
    ///
    /// Thread safety:
    ///   - ConcurrentBag supports concurrent Add from multiple worker threads.
    ///   - _enabled is volatile — worker threads always see the latest value.
    ///   - Drain happens only after OxygenParallel.For's CountdownEvent sync,
    ///     so there are no concurrent writers during the foreach/Clear sequence.
    ///
    /// Interaction with LightingOptimizationSystem:
    ///   The ILHook on AddLight(int,int,float,float,float) from LightingOptimizationSystem
    ///   adds an early viewport check inside the method body. Our Hook wraps around that
    ///   modified body. When the queue is drained on the main thread, calling
    ///   Lighting.AddLight() goes through the hook chain again (_enabled is false, so
    ///   our interceptor falls through to orig) and the viewport check still runs —
    ///   off-screen lights queued by workers are correctly discarded at drain time.
    ///
    /// Inspired by Nitrate mod's ThreadUnsafeCallWatchdog (TeamCatalyst/Nitrate).
    /// Key improvements: reflection-based hooks (no HookGen events) and explicit
    /// commentary on interaction with LightingOptimizationSystem.
    /// </summary>
    public static class ThreadUnsafeCallWatchdog
    {
        // ConcurrentBag: O(1) concurrent Add, safe for parallel producers.
        // Drain is sequential on main thread after Enable=false, so no races.
        private static readonly ConcurrentBag<Action> _queue = new();

        // Volatile ensures worker threads always read the freshest value without a lock.
        private static volatile bool _enabled;

        // Held to allow clean disposal on mod unload.
        private static Hook? _hook1; // Lighting.AddLight(int i, int j, int torchId, float lightAmount)
        private static Hook? _hook2; // Lighting.AddLight(int i, int j, float r, float g, float b)

        /// <summary>Whether the parallel work window is currently open.</summary>
        public static bool Enabled => _enabled;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Installs hooks on Lighting.AddLight overloads.
        /// Called from DustParallelismSystem.OnModLoad() only when DustParallelism is active.
        /// </summary>
        internal static void Install(Mod mod)
        {
            try
            {
                // Overload 1: Lighting.AddLight(int i, int j, int torchId, float lightAmount)
                var addLight1 = typeof(Lighting).GetMethod(
                    "AddLight",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int), typeof(int), typeof(int), typeof(float) },
                    null);

                // Overload 2: Lighting.AddLight(int i, int j, float r, float g, float b)
                var addLight2 = typeof(Lighting).GetMethod(
                    "AddLight",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int), typeof(int), typeof(float), typeof(float), typeof(float) },
                    null);

                if (addLight1 != null)
                {
                    _hook1 = new Hook(
                        addLight1,
                        new Action<Action<int, int, int, float>, int, int, int, float>(
                            Intercept_int_int_int_float));
                }
                else
                {
                    mod.Logger.Warn(
                        "[Oxygen] Watchdog: Lighting.AddLight(int,int,int,float) not found — " +
                        "torch-light calls from workers will not be deferred.");
                }

                if (addLight2 != null)
                {
                    _hook2 = new Hook(
                        addLight2,
                        new Action<Action<int, int, float, float, float>, int, int, float, float, float>(
                            Intercept_int_int_float_float_float));
                }
                else
                {
                    mod.Logger.Warn(
                        "[Oxygen] Watchdog: Lighting.AddLight(int,int,float,float,float) not found — " +
                        "RGB-light calls from workers will not be deferred.");
                }

                mod.Logger.Info("[Oxygen] ThreadUnsafeCallWatchdog: installed on Lighting.AddLight.");
            }
            catch (Exception ex)
            {
                mod.Logger.Warn(
                    $"[Oxygen] ThreadUnsafeCallWatchdog: install failed " +
                    $"({ex.GetType().Name}: {ex.Message}). " +
                    "DustParallelism may still crash with Calamity/Infernum.");
            }
        }

        /// <summary>
        /// Removes all hooks. Called from DustParallelismSystem.OnModUnload().
        /// </summary>
        internal static void Uninstall()
        {
            _hook1?.Dispose();
            _hook1 = null;
            _hook2?.Dispose();
            _hook2 = null;
        }

        // ── Control API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Opens the parallel work window. Call immediately BEFORE OxygenParallel.For().
        /// Any Lighting.AddLight() calls made on worker threads will be queued.
        /// </summary>
        public static void Enable()
        {
            _queue.Clear();
            _enabled = true;
        }

        /// <summary>
        /// Closes the parallel work window and drains all queued lighting calls
        /// on the current (main) thread. Call immediately AFTER OxygenParallel.For()
        /// returns, once all workers have been joined by the CountdownEvent.
        /// </summary>
        public static void Disable()
        {
            // Set false BEFORE draining: re-entrant calls during drain skip the queue.
            _enabled = false;

            foreach (var action in _queue)
                action();

            _queue.Clear();
        }

        // ── Interceptors ──────────────────────────────────────────────────────────

        // MonoMod Hook convention: orig is the first parameter, then the original params.
        // When _enabled: queue a closure that captures all parameters and calls Lighting.AddLight
        // directly (which goes through the hook chain again with _enabled=false, then reaches orig).
        // When !_enabled: fall through to orig immediately.

        private static void Intercept_int_int_int_float(
            Action<int, int, int, float> orig,
            int i, int j, int torchId, float lightAmount)
        {
            if (_enabled)
                _queue.Add(() => Lighting.AddLight(i, j, torchId, lightAmount));
            else
                orig(i, j, torchId, lightAmount);
        }

        private static void Intercept_int_int_float_float_float(
            Action<int, int, float, float, float> orig,
            int i, int j, float r, float g, float b)
        {
            if (_enabled)
                _queue.Add(() => Lighting.AddLight(i, j, r, g, b));
            else
                orig(i, j, r, g, b);
        }
    }
}
