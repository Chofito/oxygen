using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;
using Oxygen.Utilities;

namespace Oxygen.Systems
{
    public class LightingOptimizationSystem : ModSystem
    {
        private ILHook? _rgbHook;
        private Hook? _torchHook;

        private const int ViewportMarginTiles = 40;

        public override void OnModLoad()
        {
            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.LightingOptimizationEnabled)
                return;

            try
            {
                var rgbMethod = typeof(Lighting).GetMethod(
                    "AddLight",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int), typeof(int), typeof(float), typeof(float), typeof(float) },
                    null);

                if (rgbMethod is not null)
                    _rgbHook = new ILHook(rgbMethod, PatchAddLightRgb);
                else
                    Mod.Logger.Warn("[Oxygen] LightingOpt: AddLight(int,int,float,float,float) not found.");

                var torchMethod = typeof(Lighting).GetMethod(
                    "AddLight",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int), typeof(int), typeof(int), typeof(float) },
                    null);

                if (torchMethod is not null)
                    _torchHook = new Hook(torchMethod,
                        new Action<Action<int, int, int, float>, int, int, int, float>(InterceptTorch));
                else
                    Mod.Logger.Warn("[Oxygen] LightingOpt: AddLight(int,int,int,float) not found.");

                Mod.Logger.Info("[Oxygen] LightingOpt: active.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn($"[Oxygen] LightingOpt: hook failed — {ex.GetType().Name}: {ex.Message}");
            }
        }

        public override void OnModUnload()
        {
            _rgbHook?.Dispose();
            _rgbHook = null;
            _torchHook?.Dispose();
            _torchHook = null;
        }

        private static void PatchAddLightRgb(ILContext il)
        {
            var c = new ILCursor(il);

            c.Index = il.Instrs.Count - 1;
            if (il.Instrs[c.Index].OpCode != OpCodes.Ret)
            {
                ModContent.GetInstance<LightingOptimizationSystem>()?.Mod.Logger.Warn(
                    "[Oxygen] LightingOpt: unexpected IL in AddLight — patch skipped.");
                return;
            }

            var retLabel = c.DefineLabel();
            c.MarkLabel(retLabel);
            c.Index = 0;

            var skipMethod = typeof(LightingOptimizationSystem).GetMethod(
                nameof(ShouldSkip),
                BindingFlags.Static | BindingFlags.NonPublic)!;

            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldarg_1);
            c.Emit(OpCodes.Call, skipMethod);
            c.Emit(OpCodes.Brtrue, retLabel);
        }

        private static void InterceptTorch(
            Action<int, int, int, float> orig,
            int i, int j, int torchId, float lightAmount)
        {
            if (!OxygenWorker.Active)
                orig(i, j, torchId, lightAmount);
        }

        private static bool ShouldSkip(int tileX, int tileY)
        {
            if (OxygenWorker.Active)
                return true;

            int screenTileX = (int)(Main.screenPosition.X / 16f);
            int screenTileY = (int)(Main.screenPosition.Y / 16f);
            int screenTileW = (Main.screenWidth  / 16) + 1;
            int screenTileH = (Main.screenHeight / 16) + 1;

            return tileX < screenTileX - ViewportMarginTiles
                || tileX > screenTileX + screenTileW + ViewportMarginTiles
                || tileY < screenTileY - ViewportMarginTiles
                || tileY > screenTileY + screenTileH + ViewportMarginTiles;
        }
    }
}
