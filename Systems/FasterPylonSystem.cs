using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    /// <summary>
    /// Replaces TeleportPylonsSystem.IsPlayerNearAPylon() with an O(pylons) implementation.
    ///
    /// Vanilla behaviour:
    ///   The vanilla implementation scans an area around the player to find nearby pylons,
    ///   which is O(reach_area) — proportional to the pylon's tile-reach radius squared.
    ///
    /// Optimized behaviour:
    ///   Iterate directly over Main.PylonSystem.Pylons (typically 0-10 entries), compute
    ///   the exact reach bounds for each pylon, and return true on first match.
    ///   Complexity: O(pylons), effectively O(1) for any normal world.
    ///
    /// Compatibility:
    ///   - Calamity has zero references to TeleportPylonsSystem or IsPlayerNearAPylon.
    ///   - All reach/range logic uses TileReachCheckSettings.Pylons.GetRanges(), the same
    ///     method used internally by vanilla, so pylon reach is unchanged.
    ///
    /// Ported from Nitrate mod (TeamCatalyst/Nitrate, FasterPylonSystem.cs).
    /// Changes: reflection-based Hook (no HookGen), Oxygen config/logging conventions.
    /// </summary>
    public class FasterPylonSystem : ModSystem
    {
        private Hook? _pylonHook;

        public override void OnModLoad()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.FasterPylonEnabled)
                return;

            try
            {
                var method = typeof(TeleportPylonsSystem).GetMethod(
                    "IsPlayerNearAPylon",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Player) },
                    null);

                if (method == null)
                {
                    Mod.Logger.Warn(
                        "[Oxygen] FasterPylon: TeleportPylonsSystem.IsPlayerNearAPylon not found — " +
                        "optimization inactive.");
                    return;
                }

                _pylonHook = new Hook(
                    method,
                    new Func<TeleportPylonsSystem, Player, bool>(OnIsPlayerNearAPylon));

                Mod.Logger.Info("[Oxygen] FasterPylon: O(pylons) pylon proximity check active.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] FasterPylon: hook failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void OnModUnload()
        {
            _pylonHook?.Dispose();
            _pylonHook = null;
        }

        // ── Hook ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Checks all active pylons directly, using the same reach-range logic as vanilla
        /// but without scanning area tiles first.
        /// </summary>
        private static bool OnIsPlayerNearAPylon(TeleportPylonsSystem self, Player player)
        {
            foreach (var info in Main.PylonSystem.Pylons)
            {
                var pos = info.PositionInTiles;

                // Pylon footprint: 2 tiles wide, 3 tiles tall (standard pylon size).
                var lowerRight = new Point16((short)(pos.X + 2), (short)(pos.Y + 3));
                var playerTile = player.position.ToTileCoordinates();

                // GetRanges returns the tile-reach radius for the given player and reach type.
                TileReachCheckSettings.Pylons.GetRanges(player, out int rangeX, out int rangeY);

                int minX = Utils.Clamp(pos.X - rangeX,         0, Main.maxTilesX - 1);
                int maxX = Utils.Clamp(lowerRight.X + rangeX - 1, 0, Main.maxTilesX - 1);
                int minY = Utils.Clamp(pos.Y - rangeY - 1,     0, Main.maxTilesY - 1);
                int maxY = Utils.Clamp(lowerRight.Y + rangeY - 1, 0, Main.maxTilesY - 1);

                if (playerTile.X >= minX && playerTile.X <= maxX &&
                    playerTile.Y >= minY && playerTile.Y <= maxY)
                    return true;
            }

            return false;
        }
    }
}
