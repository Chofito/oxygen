using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    public class LaserRulerRenderSystem : ModSystem
    {
        private Hook? _hook;

        public override void OnModLoad()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.NewLaserRulerRenderingEnabled)
                return;

            try
            {
                var method = typeof(Main).GetMethod(
                    "DrawInterface_3_LaserRuler",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (method is null)
                {
                    Mod.Logger.Warn("[Oxygen] LaserRuler: DrawInterface_3_LaserRuler not found.");
                    return;
                }

                _hook = new Hook(method, new Action<Action<Main>, Main>(OnDraw));
                Mod.Logger.Info("[Oxygen] LaserRuler: optimized ruler active (~200 draw calls vs ~14 000).");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn($"[Oxygen] LaserRuler: hook failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void OnModUnload()
        {
            _hook?.Dispose();
            _hook = null;
        }

        private static void OnDraw(Action<Main> orig, Main self)
        {
            if (Main.LocalPlayer.rulerLine && DrawOptimized())
                return;

            orig(self);
        }

        private static bool DrawOptimized()
        {
            try
            {
                float scaleX = (Main.screenWidth  + 100f) / 16f;
                float scaleY = (Main.screenHeight + 100f) / 16f;

                float speed = Main.LocalPlayer.velocity.Length();
                float alpha = MathHelper.Lerp(0.2f, 0.7f,
                    MathHelper.Clamp(1f - speed / 6f, 0f, 1f));

                var colorBg    = new Color(0.24f, 0.8f, 0.9f, 1.0f) * 0.125f * alpha;
                var colorLines = new Color(0.24f, 0.8f, 0.9f, 1.0f) * 0.250f * alpha;
                var colorMouse = new Color(1.0f,  0.1f, 0.1f, 0.0f) * 0.250f * alpha;

                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Vector2.Zero, null,
                    colorBg, 0f, Vector2.Zero,
                    new Vector2(scaleX, scaleY),
                    SpriteEffects.None, 0f);

                Vector2 origin = Main.screenPosition + new Vector2(-50f);
                origin = (origin / 16f).ToPoint().ToVector2() * 16f;

                int tileW = (Main.screenWidth  + 100) / 16;
                int tileH = (Main.screenHeight + 100) / 16;

                for (int x = 0; x < tileW; x++)
                {
                    Main.spriteBatch.Draw(
                        TextureAssets.BlackTile.Value,
                        Main.ReverseGravitySupport(
                            new Vector2(x, 0f) * 16f - Main.screenPosition + origin - Vector2.One, 16f),
                        new Rectangle(0, 0, 2, 16),
                        colorLines, 0f, Vector2.Zero,
                        new Vector2(1f, scaleY),
                        SpriteEffects.None, 0f);
                }

                for (int y = 0; y < tileH; y++)
                {
                    Main.spriteBatch.Draw(
                        TextureAssets.BlackTile.Value,
                        Main.ReverseGravitySupport(
                            new Vector2(0f, y) * 16f - Main.screenPosition + origin - Vector2.One, 16f),
                        new Rectangle(0, 0, 16, 2),
                        colorLines, 0f, Vector2.Zero,
                        new Vector2(scaleX, 1f),
                        SpriteEffects.None, 0f);
                }

                var mouseTile = Main.MouseWorld.ToTileCoordinates();
                int mx = mouseTile.X - (int)origin.X / 16;
                int my = mouseTile.Y - (int)origin.Y / 16;

                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Main.ReverseGravitySupport(
                        new Vector2(mx, 0f) * 16f - Main.screenPosition + origin - Vector2.One, 16f),
                    new Rectangle(0, 0, 18, 16),
                    colorMouse, 0f, Vector2.Zero,
                    new Vector2(1f, my),
                    SpriteEffects.None, 0f);

                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Main.ReverseGravitySupport(
                        new Vector2(0f, my) * 16f - Main.screenPosition + origin - Vector2.One, 16f),
                    new Rectangle(0, 0, 16, 18),
                    colorMouse, 0f, Vector2.Zero,
                    new Vector2(scaleX, 1f),
                    SpriteEffects.None, 0f);

                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Main.ReverseGravitySupport(
                        new Vector2(mx, my + 1.125f) * 16f - Main.screenPosition + origin - Vector2.One, 16f),
                    new Rectangle(0, 0, 18, 16),
                    colorMouse, 0f, Vector2.Zero,
                    new Vector2(1f, tileH - my),
                    SpriteEffects.None, 0f);

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
