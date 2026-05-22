using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    public class PylonProximitySystem : ModSystem
    {
        private Hook? _hook;

        public override void OnModLoad()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.FasterPylonEnabled)
                return;

            try
            {
                var method = typeof(TeleportPylonsSystem).GetMethod(
                    "IsPlayerNearAPylon",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Player) },
                    null);

                if (method is null)
                {
                    Mod.Logger.Warn("[Oxygen] PylonProximity: IsPlayerNearAPylon not found.");
                    return;
                }

                _hook = new Hook(method, new Func<Func<Player, bool>, Player, bool>(Check));
                Mod.Logger.Info("[Oxygen] PylonProximity: O(pylons) proximity check active.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn($"[Oxygen] PylonProximity: hook failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void OnModUnload()
        {
            _hook?.Dispose();
            _hook = null;
        }

        private static bool Check(Func<Player, bool> orig, Player player)
        {
            TileReachCheckSettings.Pylons.GetRanges(player, out int rangeX, out int rangeY);
            var pt = player.position.ToTileCoordinates();

            foreach (var info in Main.PylonSystem.Pylons)
            {
                var pos = info.PositionInTiles;
                var lr  = new Point16((short)(pos.X + 2), (short)(pos.Y + 3));

                int minX = Utils.Clamp(pos.X - rangeX,      0, Main.maxTilesX - 1);
                int maxX = Utils.Clamp(lr.X  + rangeX - 1,  0, Main.maxTilesX - 1);
                int minY = Utils.Clamp(pos.Y - rangeY - 1,  0, Main.maxTilesY - 1);
                int maxY = Utils.Clamp(lr.Y  + rangeY - 1,  0, Main.maxTilesY - 1);

                if (pt.X >= minX && pt.X <= maxX && pt.Y >= minY && pt.Y <= maxY)
                    return true;
            }

            return false;
        }
    }
}
