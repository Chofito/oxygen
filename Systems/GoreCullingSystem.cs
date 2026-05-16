using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    public class GoreCullingSystem : ModSystem
    {
        // Tracks how many consecutive ticks each gore slot has been off-screen
        private readonly int[] _offscreenTicks = new int[601];

        public override void OnWorldLoad()
        {
            for (int i = 0; i < _offscreenTicks.Length; i++)
                _offscreenTicks[i] = 0;
        }

        public override void PreUpdateGores()
        {
            if (Main.gameMenu)
                return;

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null || !cfg.GoreCullingEnabled)
                return;

            const int margin = 32;
            int viewL = (int)Main.screenPosition.X - margin;
            int viewT = (int)Main.screenPosition.Y - margin;
            int viewR = (int)Main.screenPosition.X + Main.screenWidth + margin;
            int viewB = (int)Main.screenPosition.Y + Main.screenHeight + margin;

            for (int i = 0; i < 600; i++)
            {
                Gore g = Main.gore[i];
                if (!g.active)
                {
                    _offscreenTicks[i] = 0;
                    continue;
                }

                bool offscreen = g.position.X < viewL || g.position.X > viewR ||
                                 g.position.Y < viewT || g.position.Y > viewB;

                if (offscreen)
                {
                    _offscreenTicks[i]++;
                    if (_offscreenTicks[i] >= cfg.GoreTimeoutTicks)
                    {
                        g.active = false;
                        _offscreenTicks[i] = 0;
                    }
                }
                else
                {
                    _offscreenTicks[i] = 0;
                }
            }
        }
    }
}
