using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ModLoader;
using Oxygen.Config;
using Oxygen.Utilities;
using Terraria.ID;

namespace Oxygen.Systems
{
    public class ProjectileParallelismSystem : ModSystem
    {
        private static readonly MethodInfo _runParallelInfo =
            typeof(ProjectileParallelismSystem).GetMethod(
                nameof(RunParallel), BindingFlags.NonPublic | BindingFlags.Static)!;

        // Cached to avoid a delegate allocation on every OxygenParallel.For call.
        private static readonly Action<int, int> _updateSafeDelegate = UpdateSafePartition;

        // Pre-allocated slot lists — zero per-frame heap allocations.
        private static readonly int[] _safeSlots   = new int[Main.maxProjectiles];
        private static readonly int[] _unsafeSlots = new int[Main.maxProjectiles];

        private ILHook? _patchLoopHook;
        private static bool _parallelEnabled;
        private static Mod? _mod;

        public override void OnModLoad()
        {
            _mod = Mod;

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.ProjectileParallelismEnabled)
                return;

            if (Environment.ProcessorCount < 2)
            {
                Mod.Logger.Info("[Oxygen] ProjectileParallelism: single-core CPU, skipped.");
                return;
            }

            try
            {
                var targetMethod = typeof(Main).GetMethod(
                    "PreUpdateAllProjectiles",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null, Type.EmptyTypes, null)
                    ?? throw new Exception("Main.PreUpdateAllProjectiles() not found.");

                _patchLoopHook = new ILHook(targetMethod, PatchLoop);
                OxygenParallel.Initialize();
                _parallelEnabled = true;

                Mod.Logger.Info(
                    $"[Oxygen] ProjectileParallelism: active with {Environment.ProcessorCount - 1} workers.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] ProjectileParallelism: hook failed, falling back to vanilla. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        public override void OnModUnload()
        {
            _patchLoopHook?.Dispose();
            _patchLoopHook = null;
            _parallelEnabled = false;
            OxygenParallel.Shutdown();
        }

        private static void PatchLoop(ILContext il)
        {
            var c = new ILCursor(il);
            try
            {
                var skipLabel = c.DefineLabel();

                ILLabel? loopCheckLabel = null;
                if (!c.TryGotoNext(MoveType.Before, x => x.MatchBr(out loopCheckLabel))
                    || loopCheckLabel is null)
                    throw new Exception("Cannot find loop entry branch in PreUpdateAllProjectiles.");

                c.Emit(OpCodes.Br, skipLabel);

                c.GotoLabel(loopCheckLabel);
                if (!c.TryGotoNext(MoveType.After, x => x.MatchBlt(out _)))
                    throw new Exception("Cannot find loop condition (Blt) in PreUpdateAllProjectiles.");

                c.Emit(OpCodes.Call, _runParallelInfo);

                c.GotoPrev(MoveType.After, x => x.MatchBlt(out _));
                c.MarkLabel(skipLabel);
            }
            catch (Exception ex)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] ProjectileParallelism: PatchLoop failed. ({ex.GetType().Name}: {ex.Message})");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RunParallel()
        {
            // On a server, Projectile.Update() calls NetMessage.SendData() which writes
            // to shared packet buffers — not safe from worker threads.
            if (Main.netMode == NetmodeID.Server)
            {
                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    Projectile p = Main.projectile[i];
                    if (!p.active) continue;
                    Main.ProjectileUpdateLoopIndex = i;
                    p.Update(i);
                }
                return;
            }

            int safeCount   = 0;
            int unsafeCount = 0;

            for (int i = 0; i < Main.maxProjectiles; i++)
            {
                Projectile p = Main.projectile[i];
                if (!p.active) continue;

                // Vanilla-only restriction: mod projectile AI is not audited — even damage=0
                // mod projectiles may call SoundEngine or write player state in racy ways.
                if (p.damage <= 0 && !p.hostile && !p.friendly && p.ModProjectile == null)
                    _safeSlots[safeCount++] = i;
                else
                    _unsafeSlots[unsafeCount++] = i;
            }

            if (!_parallelEnabled || safeCount == 0)
            {
                RunSlotsSequentially(_safeSlots, safeCount);
            }
            else
            {
                try
                {
                    OxygenParallel.For(0, safeCount, _updateSafeDelegate);
                }
                catch (AggregateException ex)
                {
                    _parallelEnabled = false;
                    _mod?.Logger.Error(
                        $"[Oxygen] ProjectileParallelism: runtime error, auto-disabled. " +
                        $"({ex.InnerException?.Message ?? ex.Message})");
                    RunSlotsSequentially(_safeSlots, safeCount);
                }
            }

            RunSlotsSequentially(_unsafeSlots, unsafeCount);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpdateSafePartition(int from, int to)
        {
            for (int j = from; j < to; j++)
            {
                int slot = _safeSlots[j];
                Main.ProjectileUpdateLoopIndex = slot;
                Projectile p = Main.projectile[slot];
                if (p.active) p.Update(slot);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RunSlotsSequentially(int[] slots, int count)
        {
            for (int j = 0; j < count; j++)
            {
                int slot = slots[j];
                Main.ProjectileUpdateLoopIndex = slot;
                Projectile p = Main.projectile[slot];
                if (p.active) p.Update(slot);
            }
        }
    }
}
