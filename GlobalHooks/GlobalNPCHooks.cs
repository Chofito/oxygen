using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Oxygen.Config;
using Oxygen.Systems;

namespace Oxygen.GlobalHooks
{
    // Companion ModSystem handles initialization that GlobalNPC can't do directly
    public class GlobalNPCHooksSystem : ModSystem
    {
        public override void OnModLoad()
        {
            GlobalNPCHooks.CacheConfig();
            GlobalProjectileHooks.CacheConfig();
        }

        public override void OnWorldLoad()
        {
            System.Array.Clear(GlobalNPCHooks.LastTargetedLocalPlayer, 0, GlobalNPCHooks.LastTargetedLocalPlayer.Length);
        }
    }

    public class GlobalNPCHooks : GlobalNPC
    {
        internal static readonly int[] LastTargetedLocalPlayer = new int[201];

        // Cached once at load — config is a singleton that never changes during a session.
        // Avoids a dictionary lookup on every PreAI call (up to 200 NPCs per frame).
        internal static ClientConfig? Cfg;
        internal static void CacheConfig() => Cfg = ModContent.GetInstance<ClientConfig>();

        public override bool PreAI(NPC npc)
        {
            var cfg = Cfg;
            if (cfg == null)
                return true;

            // ── Multiplayer client guard ──────────────────────────────────────
            // On a non-host client, NPC positions are authoritative from the server.
            // Throttling the local AI prediction causes rubber-banding: the client
            // diverges from server state and snaps back every sync packet.
            // Let the server send corrections - never throttle on client.
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return true;

            // ── Town NPC throttling ───────────────────────────────────────────
            // Handled separately from hostile NPC throttling: more conservative
            // (max 1-in-3 skip), only when truly off-screen, never when near a player.
            // Helps significantly in large bases with 50+ town NPCs.
            if (npc.townNPC)
            {
                if (!cfg.TownNPCThrottlingEnabled)
                    return true;

                // Town NPCs near any player always run full AI.
                // Use 2× the hostile threshold so shop/dialogue interactions are seamless.
                float townThreshPx = System.Math.Max(cfg.NPCThrottleStartDistance, 60) * 2 * 16f;
                float townThreshSq = townThreshPx * townThreshPx;

                float nearestSq = float.MaxValue;
                for (int i = 0; i < Main.maxPlayers; i++)
                {
                    Player p = Main.player[i];
                    if (p == null || !p.active) continue;
                    float dsq = npc.Center.DistanceSQ(p.Center);
                    if (dsq < nearestSq) nearestSq = dsq;
                }

                if (nearestSq < townThreshSq)
                    return true; // close enough - never throttle

                // Far from all players: run AI 1 tick out of every 3 (~20 Hz).
                // Pathfinding and house assignment are imperceptible at this rate.
                if ((int)Main.GameUpdateCount % 3 == 0)
                    return true;

                npc.position += npc.velocity;
                return false;
            }

            // ── Hostile NPC throttling ────────────────────────────────────────
            if (!cfg.NPCThrottlingEnabled)
                return true;

            // Never throttle bosses or worm/multi-segment boss components.
            // realLife >= 0 means this NPC shares health with another (the head).
            // Breaking the chain causes the worm to collapse and forces expensive recalculations.
            if (npc.boss)
                return true;

            if (npc.realLife >= 0 && npc.realLife < Main.maxNPCs
                && Main.npc[npc.realLife].active && Main.npc[npc.realLife].boss)
                return true;

            if (Oxygen.ExemptNPCs.Contains(npc.type))
                return true;

            // Never throttle hostile NPCs during a boss fight.
            // Mods (Infernum, Calamity) spawn non-boss helper NPCs to drive phase
            // transitions and cinematic intro sequences. These NPCs require tick-perfect
            // AI to advance their state machines. Throttling them causes the sequence to
            // stall indefinitely — freezing the player, breaking the camera, and making
            // UI elements render at wrong positions. Exploration throttling (no boss)
            // is unaffected, which is where the real gain comes from anyway.
            if (SharedBossState.BossActive)
                return true;

            int startDist = cfg.NPCThrottleStartDistance;

            int slot = npc.whoAmI;

            // In singleplayer/client: track when the NPC targets the local player so we
            // never throttle an NPC that is actively chasing us.
            // On a dedicated server Main.myPlayer is not a real player index - skip this
            // check entirely (server tracks all players via the distance check below).
            if (Main.netMode != NetmodeID.Server && npc.target == Main.myPlayer)
                LastTargetedLocalPlayer[slot] = (int)Main.GameUpdateCount;

            if (Main.netMode != NetmodeID.Server &&
                Main.GameUpdateCount - LastTargetedLocalPlayer[slot] < 60)
                return true;

            // Find distance to nearest player
            float nearestDistSq = float.MaxValue;
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player p = Main.player[i];
                if (p == null || !p.active)
                    continue;
                float distSq = npc.Center.DistanceSQ(p.Center);
                if (distSq < nearestDistSq)
                    nearestDistSq = distSq;
            }

            float startDistPx = startDist * 16f;
            float dist = System.MathF.Sqrt(nearestDistSq);

            if (dist < startDistPx)
                return true;

            // Determine skip ratio based on distance bracket.
            // On a dedicated server be more conservative: only skip every other tick at most,
            // since all players share the same NPC state and desyncs are unrecoverable.
            int skipEvery;
            if (Main.netMode == NetmodeID.Server)
            {
                skipEvery = 2; // max 50% skip on server, regardless of distance
            }
            else if (dist < startDistPx * 1.5f)
                skipEvery = 2;
            else if (dist < startDistPx * 2f)
                skipEvery = 3;
            else
                skipEvery = 4;

            if ((int)Main.GameUpdateCount % skipEvery == 0)
                return true;

            // Skipped tick: apply basic position update only
            npc.position += npc.velocity;
            return false;
        }
    }
}
