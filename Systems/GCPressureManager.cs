using System;
using System.Runtime;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    /// <summary>
    /// Manages GC pressure to reduce mid-frame collection pauses during boss fights.
    ///
    /// Strategy:
    ///   - During a boss fight: switches to GCLatencyMode.SustainedLowLatency, which
    ///     tells the .NET 8 GC to prefer background/concurrent collection over
    ///     full-blocking stop-the-world GCs. This dramatically reduces the probability
    ///     of a 50-200 ms stutter from a GC pause during an intense fight.
    ///
    ///   - After a boss fight ends (2-second grace period): triggers a non-blocking,
    ///     optimized Gen-2 GC collection while gameplay is calm. This prevents garbage
    ///     accumulated during the fight from building up and causing a large pause later.
    ///
    ///   - Outside both windows: restores the original GC mode (usually Interactive),
    ///     letting the runtime manage collections normally.
    ///
    /// Caveats (why this defaults to disabled):
    ///   - SustainedLowLatency allows memory to grow during the boss fight without
    ///     being reclaimed by full GCs. In extreme cases (very long fights, heavily
    ///     modded sessions with high allocation rates) this can push the process close
    ///     to an OOM situation. Monitor task manager if enabling this.
    ///   - The post-fight GC.Collect call is non-blocking (background=true), so it
    ///     should not cause a visible freeze, but it does add background CPU load.
    ///   - Not all .NET environments support SustainedLowLatency; the setter is
    ///     wrapped in a try/catch.
    ///
    /// Config:
    ///   GCManagementEnabled = false (default)
    /// </summary>
    public class GCPressureManager : ModSystem
    {
        private GCLatencyMode _originalMode;
        private bool _inLowLatencyMode;

        // Ticks elapsed since the last boss died (used for the post-fight GC window)
        private int _ticksSinceLastBoss;

        // How long to wait after the last boss dies before triggering the cleanup GC.
        // 120 ticks = 2 seconds - enough time for the death animation and loot drops.
        private const int PostFightGCDelay = 120;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void OnModLoad()
        {
            // Capture baseline GC mode once at startup so we always restore to the
            // same value regardless of how many times the feature is toggled.
            _originalMode = GCSettings.LatencyMode;
        }

        public override void OnModUnload()
        {
            RestoreNormalGC();
        }

        public override void OnWorldUnload()
        {
            RestoreNormalGC();
            _ticksSinceLastBoss = 0;
        }

        // ── Update ────────────────────────────────────────────────────────────

        public override void PostUpdateEverything()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null || !cfg.GCManagementEnabled)
            {
                // If the feature was just disabled mid-session, restore GC mode.
                if (_inLowLatencyMode)
                    RestoreNormalGC();
                return;
            }

            // SharedBossState.BossActive is computed once per tick in PostUpdateNPCs -
            // guaranteed fresh here (PostUpdateEverything runs after PostUpdateNPCs).
            bool bossActive = SharedBossState.BossActive;

            if (bossActive)
            {
                _ticksSinceLastBoss = 0;
                EnableLowLatencyGC();
            }
            else
            {
                if (_inLowLatencyMode)
                    RestoreNormalGC();

                _ticksSinceLastBoss++;

                // One-shot cleanup GC shortly after the boss fight ends.
                // Non-blocking: runs on a background thread, doesn't freeze gameplay.
                if (_ticksSinceLastBoss == PostFightGCDelay)
                {
                    try
                    {
                        // Gen-2, optimized (lets GC decide best moment), blocking=false,
                        // compacting=true (reduces fragmentation after burst allocations).
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
                        Mod.Logger.Info("[Oxygen] GCPressureManager: triggered post-fight cleanup collection.");
                    }
                    catch (Exception ex)
                    {
                        Mod.Logger.Warn($"[Oxygen] GCPressureManager: GC.Collect failed - {ex.Message}");
                    }
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void EnableLowLatencyGC()
        {
            if (_inLowLatencyMode)
                return;

            try
            {
                // SustainedLowLatency: the GC avoids full-blocking Gen-2 collections
                // and prefers background GC, keeping pause times short.
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                _inLowLatencyMode = true;
            }
            catch (Exception ex)
            {
                // Some hosting environments (e.g., server GC mode) reject this.
                Mod.Logger.Warn(
                    $"[Oxygen] GCPressureManager: cannot set SustainedLowLatency - " +
                    $"GC mode unchanged. ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private void RestoreNormalGC()
        {
            if (!_inLowLatencyMode)
                return;

            try
            {
                GCSettings.LatencyMode = _originalMode;
            }
            catch
            {
                // Ignore - worst case GC stays in low-latency mode.
            }
            finally
            {
                _inLowLatencyMode = false;
            }
        }

    }
}
