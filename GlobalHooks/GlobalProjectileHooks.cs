using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;
using Oxygen.Systems;
using Terraria.ID;

namespace Oxygen.GlobalHooks
{
    public class GlobalProjectileHooks : GlobalProjectile
    {
        // ── PreAI - throttle off-screen visual projectiles ────────────────────

        /// <summary>
        /// Skips AI every other tick for visual-only projectiles (damage == 0, not hostile)
        /// that are outside the extended viewport. Targets Infernum/Calamity aura projectiles
        /// around boss segments - purely decorative, no hitbox, but each one runs AI at 60 Hz.
        ///
        /// Safety: position still advances via velocity on skipped ticks because tModLoader
        /// applies npc.position += npc.velocity outside of the AI call. Skipping AI on
        /// damage-0 projectiles cannot cause gameplay desyncs.
        /// </summary>
        public override bool PreAI(Projectile projectile)
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null || !cfg.ProjectileAIThrottlingEnabled)
                return true;

            // ── Multiplayer client guard ──────────────────────────────────────
            // Projectile AI authority belongs to the owner's client.
            // On a non-owning client, skipping AI causes visual desync: the aura
            // or effect drifts from the owner's position until the next sync packet
            // snaps it back, producing a visible jitter.
            // Only throttle projectiles this client owns (we are the authority for those).
            if (Main.netMode == NetmodeID.MultiplayerClient && projectile.owner != Main.myPlayer)
                return true;

            // Only throttle visual-only projectiles (damage=0, not hostile).
            // Hostile projectiles track the player inside their AI - never skip those.
            if (projectile.damage > 0 || projectile.hostile)
                return true;

            // Skip if on-screen - visual effects near the player should always run.
            const int aiMargin = 256; // 16 tiles
            if (projectile.position.X + projectile.width  >= Main.screenPosition.X - aiMargin &&
                projectile.position.X                      <= Main.screenPosition.X + Main.screenWidth  + aiMargin &&
                projectile.position.Y + projectile.height >= Main.screenPosition.Y - aiMargin &&
                projectile.position.Y                      <= Main.screenPosition.Y + Main.screenHeight + aiMargin)
                return true;

            // Off-screen visual projectile: run AI every other tick (30 Hz).
            // On skipped ticks, position += velocity is still applied by vanilla.
            return (int)Main.GameUpdateCount % 2 == 0;
        }

        // ── PreDraw - viewport cull + ally hiding ─────────────────────────────

        public override bool PreDraw(Projectile projectile, ref Color lightColor)
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null)
                return true;

            // --- Viewport culling (player-owned projectiles only) ---
            //
            // BUG FIX: Previously this cull applied to ALL projectiles, including
            // NPC/boss-owned ones. Infernum renders Astrum Deus visual effects (auras,
            // cosmic star bursts) as projectiles owned by NPC slots. With a 128 px margin,
            // those effects disappeared whenever a segment briefly moved off-screen,
            // making the boss look invisible.
            //
            // Fix: only cull player-owned projectiles. NPC projectiles can extend well
            // beyond the viewport and must not flicker out.
            // Margin raised to 256 px (16 tiles) - some player spells have large visual
            // offsets between their logical position and their rendered sprite.
            bool isPlayerOwned = projectile.owner >= 0 && projectile.owner < Main.maxPlayers;
            if (isPlayerOwned)
            {
                const int viewportMargin = 256;
                if (projectile.position.X + projectile.width  < Main.screenPosition.X - viewportMargin ||
                    projectile.position.X                      > Main.screenPosition.X + Main.screenWidth  + viewportMargin ||
                    projectile.position.Y + projectile.height < Main.screenPosition.Y - viewportMargin ||
                    projectile.position.Y                      > Main.screenPosition.Y + Main.screenHeight + viewportMargin)
                {
                    return false;
                }
            }

            // --- Ally projectile hiding ---
            if (!cfg.HideAllyProjectilesEnabled)
                return true;

            // Never hide our own projectiles
            if (projectile.owner == Main.myPlayer)
                return true;

            // Validate owner index - out-of-range means NPC or server projectile, don't hide
            int owner = projectile.owner;
            if (owner < 0 || owner >= Main.maxPlayers)
                return true;

            Player ownerPlayer = Main.player[owner];
            if (ownerPlayer == null || !ownerPlayer.active)
                return true;

            // Never hide hostile (PvP) projectiles
            if (ownerPlayer.hostile)
                return true;

            // Check boss condition if BossOnly mode is active.
            // Uses SharedBossState - O(1) read instead of O(maxNPCs) scan per projectile.
            if (cfg.HideAllyProjectilesBossOnly && !SharedBossState.BossActive)
                return true;

            // Check exemption list
            if (Oxygen.ExemptProjectiles.Contains(projectile.type))
                return true;

            // Opacity reduction mode instead of full hide
            int opacityPercent = cfg.ProjectileOpacityPercent;
            if (opacityPercent > 0)
            {
                float factor = opacityPercent / 100f;
                lightColor = new Color(
                    (int)(lightColor.R * factor),
                    (int)(lightColor.G * factor),
                    (int)(lightColor.B * factor),
                    (int)(lightColor.A * factor)
                );
                return true;
            }

            return false;
        }
    }
}
