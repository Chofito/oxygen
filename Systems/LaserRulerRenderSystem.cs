using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    /// <summary>
    /// Replaces the vanilla laser ruler UI renderer with one that draws ~200 shapes
    /// instead of 14,000+ individual tile calls.
    ///
    /// Vanilla behaviour:
    ///   DrawInterface_3_LaserRuler iterates every tile in the visible screen and calls
    ///   SpriteBatch.Draw once per tile. At 1080p that's ≈14,000 draw calls per frame
    ///   whenever the laser ruler is active.
    ///
    /// Optimized behaviour:
    ///   - 1 draw call: full-screen tinted background.
    ///   - N draws (screenW/16): vertical grid lines across X.
    ///   - M draws (screenH/16): horizontal grid lines across Y.
    ///   - 3 draws: red column/row highlight at mouse tile position.
    ///   Total at 1080p: ≈200 draw calls — a 70× reduction.
    ///
    /// IL strategy:
    ///   The patch navigates past two brfalse guards (laser ruler equipped check and
    ///   opacity check) then injects a delegate. The delegate draws the optimized ruler
    ///   and returns true (= handled). The injected code then branches past the vanilla
    ///   rendering loop via a ret. If the delegate returns false, vanilla code runs.
    ///
    /// Compatibility:
    ///   Zero interaction with Calamity, Infernum, or any content mod.
    ///   The laser ruler is a builder accessory — only active during building.
    ///
    /// Based on Nitrate mod's NewLaserRulerRenderSystem.cs
    /// Copyright (C) TeamCatalyst contributors — AGPL v3 (https://github.com/terraria-catalyst/nitrate-mod)
    /// Changes: reflection-based ILHook instead of HookGen events, Oxygen config integration,
    /// structured logging, and exception-safe fallback to vanilla rendering.
    /// </summary>
    public class LaserRulerRenderSystem : ModSystem
    {
        private ILHook? _laserHook;

        public override void OnModLoad()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.NewLaserRulerRenderingEnabled)
                return;

            try
            {
                // DrawInterface_3_LaserRuler is a private instance method on Main.
                var method = typeof(Main).GetMethod(
                    "DrawInterface_3_LaserRuler",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (method == null)
                {
                    Mod.Logger.Warn(
                        "[Oxygen] LaserRuler: Main.DrawInterface_3_LaserRuler not found — " +
                        "optimization inactive.");
                    return;
                }

                _laserHook = new ILHook(method, PatchLaserRuler);
                Mod.Logger.Info("[Oxygen] LaserRuler: optimized laser ruler renderer active (~200 draw calls vs ~14 000).");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] LaserRuler: hook failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void OnModUnload()
        {
            _laserHook?.Dispose();
            _laserHook = null;
        }

        // ── IL patch ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Navigates past the two entrance guards of DrawInterface_3_LaserRuler,
        /// then injects a delegate that draws the optimized ruler and returns true
        /// to skip the vanilla tile-by-tile loop. If the delegate returns false
        /// (e.g., feature disabled), control falls through to vanilla.
        /// </summary>
        private static void PatchLaserRuler(ILContext il)
        {
            var cursor = new ILCursor(il);

            try
            {
                // Skip past guard 1: check if laser ruler is equipped.
                cursor.GotoNext(MoveType.After, instr => instr.MatchBrfalse(out _));
                // Skip past guard 2: opacity/alpha check.
                cursor.GotoNext(MoveType.After, instr => instr.MatchBrfalse(out _));
                // Skip the 'ret' and the 'ldsfld' that starts the vanilla render loop.
                cursor.Index += 2;

                // Inject our rendering delegate. Returns true = we handled it, skip vanilla.
                cursor.EmitDelegate<Func<bool>>(DrawOptimizedLaserRuler);

                // if delegate returned false, jump over the ret (let vanilla run).
                var continueLabel = cursor.DefineLabel();
                cursor.Emit(OpCodes.Brfalse, continueLabel);
                // delegate returned true: discard SpriteBatch (ldsfld already on stack from
                // vanilla code we skipped over) and return early.
                cursor.Emit(OpCodes.Pop);
                cursor.Emit(OpCodes.Ret);
                cursor.MarkLabel(continueLabel);
            }
            catch (Exception ex)
            {
                ModContent.GetInstance<LaserRulerRenderSystem>()?.Mod.Logger.Warn(
                    $"[Oxygen] LaserRuler: PatchLaserRuler IL navigation failed — " +
                    $"falling back to vanilla. ({ex.GetType().Name}: {ex.Message})");
            }
        }

        // ── Optimized renderer ────────────────────────────────────────────────────

        /// <summary>
        /// Draws the laser ruler as a set of full-screen stretched rectangles.
        /// Returns true on success (vanilla loop should be skipped).
        /// Returns false if something is unexpected (vanilla loop runs as fallback).
        /// </summary>
        private static bool DrawOptimizedLaserRuler()
        {
            try
            {
                // Scale factors: how many tiles fit across the screen (plus a 100px bleed).
                float scaleX = (Main.screenWidth  + 100f) / 16f;
                float scaleY = (Main.screenHeight + 100f) / 16f;

                // Opacity matches vanilla: lerp based on player velocity.
                float speed  = Main.LocalPlayer.velocity.Length();
                const float maxSpeed = 6f;
                float alpha  = MathHelper.Lerp(0.2f, 0.7f,
                    MathHelper.Clamp(1f - speed / maxSpeed, 0f, 1f));

                var colorBg    = new Color(0.24f, 0.8f, 0.9f, 1.0f) * 0.125f * alpha;
                var colorLines = new Color(0.24f, 0.8f, 0.9f, 1.0f) * 0.250f * alpha;
                var colorMouse = new Color(1.0f,  0.1f, 0.1f, 0.0f) * 0.250f * alpha;

                // ── Background tint ───────────────────────────────────────────────
                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Vector2.Zero,
                    null,
                    colorBg,
                    0f, Vector2.Zero,
                    new Vector2(scaleX, scaleY),
                    SpriteEffects.None, 0f);

                // ── Grid origin (snapped to tile, with bleed offset) ──────────────
                // vec is the world-space position of the top-left tile that is partially
                // visible after accounting for screen scroll and the 50px bleed margin.
                Vector2 vec = Main.screenPosition + new Vector2(-50f);
                vec = (vec / 16f).ToPoint().ToVector2() * 16f; // snap to tile grid

                int tileCountX = (Main.screenWidth  + 100) / 16;
                int tileCountY = (Main.screenHeight + 100) / 16;

                // ── Vertical grid lines (one per tile column) ─────────────────────
                for (int x = 0; x < tileCountX; x++)
                {
                    Main.spriteBatch.Draw(
                        TextureAssets.BlackTile.Value,
                        Main.ReverseGravitySupport(
                            new Vector2(x, 0f) * 16f - Main.screenPosition + vec - Vector2.One,
                            16f),
                        new Rectangle(0, 0, 2, 16),
                        colorLines,
                        0f, Vector2.Zero,
                        new Vector2(1f, scaleY),
                        SpriteEffects.None, 0f);
                }

                // ── Horizontal grid lines (one per tile row) ──────────────────────
                for (int y = 0; y < tileCountY; y++)
                {
                    Main.spriteBatch.Draw(
                        TextureAssets.BlackTile.Value,
                        Main.ReverseGravitySupport(
                            new Vector2(0f, y) * 16f - Main.screenPosition + vec - Vector2.One,
                            16f),
                        new Rectangle(0, 0, 16, 2),
                        colorLines,
                        0f, Vector2.Zero,
                        new Vector2(scaleX, 1f),
                        SpriteEffects.None, 0f);
                }

                // ── Mouse-tile cross highlight (red column + row) ─────────────────
                var mouseTile = Main.MouseWorld.ToTileCoordinates();
                int tileOffsetX = (int)vec.X / 16;
                int tileOffsetY = (int)vec.Y / 16;
                int mx = mouseTile.X - tileOffsetX;
                int my = mouseTile.Y - tileOffsetY;

                float remainingY = tileCountY - my;

                // Red vertical strip — upper half of column
                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Main.ReverseGravitySupport(
                        new Vector2(mx, 0f) * 16f - Main.screenPosition + vec - Vector2.One,
                        16f),
                    new Rectangle(0, 0, 18, 16),
                    colorMouse,
                    0f, Vector2.Zero,
                    new Vector2(1f, my),
                    SpriteEffects.None, 0f);

                // Red horizontal strip — full row at mouse tile
                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Main.ReverseGravitySupport(
                        new Vector2(0f, my) * 16f - Main.screenPosition + vec - Vector2.One,
                        16f),
                    new Rectangle(0, 0, 16, 18),
                    colorMouse,
                    0f, Vector2.Zero,
                    new Vector2(scaleX, 1f),
                    SpriteEffects.None, 0f);

                // Red vertical strip — lower half of column (below mouse row)
                Main.spriteBatch.Draw(
                    TextureAssets.BlackTile.Value,
                    Main.ReverseGravitySupport(
                        new Vector2(mx, my + 1.125f) * 16f - Main.screenPosition + vec - Vector2.One,
                        16f),
                    new Rectangle(0, 0, 18, 16),
                    colorMouse,
                    0f, Vector2.Zero,
                    new Vector2(1f, remainingY),
                    SpriteEffects.None, 0f);

                return true;
            }
            catch
            {
                // If anything goes wrong, fall back to vanilla rendering for this frame.
                return false;
            }
        }
    }
}
