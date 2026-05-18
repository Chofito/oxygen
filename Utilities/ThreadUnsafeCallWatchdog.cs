using System;
using System.Reflection;
using System.Threading;
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
    ///   window is open), store each call's arguments as a ValueTuple in a typed
    ///   ConcurrentBag — zero heap allocation per call, no closures.
    ///   When Disable() is called (back on the main thread, after all workers have
    ///   completed), drain both queues sequentially on the main thread.
    ///
    /// Thread safety:
    ///   - Pre-allocated arrays + Interlocked.Increment: O(1) Add, zero allocation.
    ///   - _enabled is volatile — worker threads always see the latest value.
    ///   - Drain happens only after OxygenParallel.For's CountdownEvent sync, so
    ///     all worker writes are complete and visible before the main thread reads them
    ///     (CountdownEvent.Wait() establishes the required happens-before relationship).
    ///   - _buf*Count is reset on the main thread in Enable(), before any workers start,
    ///     so there are no concurrent writers during the reset.
    ///
    /// Interaction with LightingOptimizationSystem:
    ///   The ILHook on AddLight(int,int,float,float,float) from LightingOptimizationSystem
    ///   adds an early viewport check inside the method body. Our Hook wraps around that
    ///   modified body. When the queue is drained on the main thread, calling
    ///   Lighting.AddLight() goes through the hook chain again (_enabled is false, so
    ///   our interceptor falls through to orig) and the viewport check still runs —
    ///   off-screen lights queued by workers are correctly discarded at drain time.
    ///
    /// Uses reflection-based hooks (no HookGen dependency) and documents
    /// the interaction with LightingOptimizationSystem explicitly.
    /// </summary>
    public static class ThreadUnsafeCallWatchdog
    {
        // Pre-allocated ring buffers — one per Lighting.AddLight overload.
        // Each worker atomically claims an index with Interlocked.Increment and writes its
        // struct directly. Zero heap allocation per call; no lock; O(1) Add.
        // MaxQueueSize = 16384: upper bound for any realistic boss fight
        // (6000 max dust × 2 overloads leaves room to spare).
        private const int MaxQueueSize = 16384;

        private static readonly (int i, int j, int torchId, float lightAmount)[] _buf1 =
            new (int, int, int, float)[MaxQueueSize];
        private static readonly (int i, int j, float r, float g, float b)[] _buf2 =
            new (int, int, float, float, float)[MaxQueueSize];

        // Atomic counters. Each worker does Interlocked.Increment to claim a unique slot.
        // Reset to 0 in Enable() before any workers start — safe because Enable() runs
        // on the main thread, before OxygenParallel.For dispatches workers.
        private static int _buf1Count;
        private static int _buf2Count;

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
            // Reset counters before workers start. The subsequent volatile write
            // (_enabled = true) provides a release fence that makes the resets
            // visible to any worker that does an acquire read of _enabled.
            _buf1Count = 0;
            _buf2Count = 0;
            _enabled = true;
        }

        /// <summary>
        /// Closes the parallel work window and drains all queued lighting calls
        /// on the current (main) thread. Call immediately AFTER OxygenParallel.For()
        /// returns, once all workers have been joined by the CountdownEvent.
        /// </summary>
        public static void Disable()
        {
            // Set false BEFORE draining: re-entrant calls during drain fall through to orig.
            _enabled = false;

            // CountdownEvent.Wait() (inside OxygenParallel.For) established a happens-before
            // relationship, so all worker writes to _buf1/_buf2/_buf*Count are visible here.
            int count1 = _buf1Count;
            for (int k = 0; k < count1; k++)
            {
                var (i, j, torchId, lightAmount) = _buf1[k];
                Lighting.AddLight(i, j, torchId, lightAmount);
            }

            int count2 = _buf2Count;
            for (int k = 0; k < count2; k++)
            {
                var (i, j, r, g, b) = _buf2[k];
                Lighting.AddLight(i, j, r, g, b);
            }
        }

        // ── Interceptors ──────────────────────────────────────────────────────────

        // MonoMod Hook convention: orig is the first parameter, then the original params.
        // When _enabled: atomically claim a slot in the pre-allocated buffer and write
        //   the struct (zero allocation, no lock, O(1)).
        // When !_enabled: fall through to orig immediately (main thread or drain path).
        // Drain calls Lighting.AddLight() directly with _enabled=false, which falls through.

        private static void Intercept_int_int_int_float(
            Action<int, int, int, float> orig,
            int i, int j, int torchId, float lightAmount)
        {
            if (_enabled)
            {
                int idx = Interlocked.Increment(ref _buf1Count) - 1;
                if ((uint)idx < MaxQueueSize) // unsigned comparison handles idx < 0 edge case
                    _buf1[idx] = (i, j, torchId, lightAmount);
                // else: buffer full — discard silently (cosmetic light, imperceptible)
            }
            else
                orig(i, j, torchId, lightAmount);
        }

        private static void Intercept_int_int_float_float_float(
            Action<int, int, float, float, float> orig,
            int i, int j, float r, float g, float b)
        {
            if (_enabled)
            {
                int idx = Interlocked.Increment(ref _buf2Count) - 1;
                if ((uint)idx < MaxQueueSize)
                    _buf2[idx] = (i, j, r, g, b);
            }
            else
                orig(i, j, r, g, b);
        }
    }
}
