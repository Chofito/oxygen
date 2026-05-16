using Terraria;
using Terraria.ModLoader;

namespace Oxygen.Systems
{
    /// <summary>
    /// Caches frequently-queried game state once per tick so every other system
    /// can read it as a static bool/int instead of scanning Main.npc[] / Main.projectile[]
    /// / Main.dust[] independently.
    ///
    /// Problem solved:
    ///   Before this system, BossIsActive() was a private O(maxNPCs=200) method
    ///   duplicated in GCPressureManager, WorldUpdateSystem, and GlobalProjectileHooks.
    ///   During a boss fight with 145 active projectiles, that was 3 × 200 = 600+
    ///   redundant NPC array iterations per frame just to answer "is a boss alive?".
    ///   Additionally, FrameTimeDiagnosticsSystem scanned all three entity arrays
    ///   (NPC + projectile + dust) a second time in its own PostUpdateEverything.
    ///
    /// Execution ordering guarantee:
    ///   BossActive is written in PostUpdateNPCs(), which tModLoader calls AFTER all
    ///   NPCs update but BEFORE any ModSystem.PostUpdateEverything(). Every system
    ///   that reads BossActive in PostUpdateEverything (GCPressureManager,
    ///   WorldUpdateSystem) therefore always sees the current tick's value.
    ///
    ///   ActiveProjectileCount and ActiveDustCount are written in PostUpdateEverything().
    ///   FrameTimeDiagnosticsSystem reads them in PostDrawInterface() (draw phase),
    ///   which runs after all PostUpdateEverything calls - so counts are always fresh.
    /// </summary>
    public class SharedBossState : ModSystem
    {
        // ── Cached state ──────────────────────────────────────────────────────

        /// <summary>True if at least one active boss NPC exists this tick.</summary>
        public static bool BossActive { get; private set; }

        /// <summary>Cached active NPC count (updated each tick in PostUpdateNPCs).</summary>
        public static int ActiveNPCCount { get; private set; }

        /// <summary>Cached active projectile count (updated in PostUpdateEverything).</summary>
        public static int ActiveProjectileCount { get; private set; }

        /// <summary>Cached active dust count (updated in PostUpdateEverything).</summary>
        public static int ActiveDustCount { get; private set; }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void OnWorldLoad()
        {
            BossActive = false;
            ActiveNPCCount = 0;
            ActiveProjectileCount = 0;
            ActiveDustCount = 0;
        }

        // ── Update ────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs after all NPC updates, before any PostUpdateEverything -
        /// guarantees BossActive is fresh when GCPressureManager / WorldUpdateSystem read it.
        /// </summary>
        public override void PostUpdateNPCs()
        {
            bool bossFound = false;
            int npcCount = 0;

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC n = Main.npc[i];
                if (!n.active) continue;
                npcCount++;
                if (n.boss) bossFound = true;
            }

            BossActive = bossFound;
            ActiveNPCCount = npcCount;
        }

        /// <summary>
        /// Caches projectile and dust counts for FrameTimeDiagnosticsSystem.
        /// These are read in PostDrawInterface (draw phase), so a 1-tick lag is fine.
        /// Skipped on dedicated servers - no HUD is ever drawn there.
        /// </summary>
        public override void PostUpdateEverything()
        {
            // Dedicated servers never draw the diagnostics overlay.
            // Skip the two array scans (~1,000 + ~6,000 iterations) entirely.
            if (Main.dedServ)
                return;

            int projCount = 0;
            for (int i = 0; i < Main.maxProjectiles; i++)
                if (Main.projectile[i].active) projCount++;
            ActiveProjectileCount = projCount;

            int dustCount = 0;
            for (int i = 0; i < Main.maxDustToDraw; i++)
                if (Main.dust[i].active) dustCount++;
            ActiveDustCount = dustCount;
        }
    }
}
