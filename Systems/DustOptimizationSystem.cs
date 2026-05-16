using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    public class DustOptimizationSystem : ModSystem
    {
        public override void PreUpdateDusts()
        {
            if (Main.gameMenu)
                return;

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null || !cfg.DustUpdateThrottling)
                return;

            Player player = Main.LocalPlayer;
            if (player == null || !player.active)
                return;

            const int margin = 64 * 16; // 64 tiles in pixels
            int viewL = (int)Main.screenPosition.X - margin;
            int viewT = (int)Main.screenPosition.Y - margin;
            int viewR = (int)Main.screenPosition.X + Main.screenWidth + margin;
            int viewB = (int)Main.screenPosition.Y + Main.screenHeight + margin;

            float cullDistSq = cfg.DustCullDistance * 16f;
            cullDistSq *= cullDistSq;

            int densityCap = (int)(6000 * cfg.DustDensityPercent / 100f);
            int activeCount = 0;

            Vector2 playerCenter = player.Center;

            for (int i = 0; i < 6000; i++)
            {
                ref Dust d = ref Main.dust[i];
                if (!d.active)
                    continue;

                // Cull dust outside the extended viewport entirely
                if (d.position.X < viewL || d.position.X > viewR ||
                    d.position.Y < viewT || d.position.Y > viewB)
                {
                    d.active = false;
                    continue;
                }

                activeCount++;
            }

            // If still over the density cap, cull dust beyond the cull distance from the player
            if (activeCount > densityCap)
            {
                for (int i = 0; i < 6000 && activeCount > densityCap; i++)
                {
                    ref Dust d = ref Main.dust[i];
                    if (!d.active)
                        continue;

                    float distSq = Vector2.DistanceSquared(d.position, playerCenter);
                    if (distSq > cullDistSq)
                    {
                        d.active = false;
                        activeCount--;
                    }
                }
            }
        }
    }
}
