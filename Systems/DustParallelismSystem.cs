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
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace Oxygen.Systems
{
    public class DustParallelismSystem : ModSystem
    {
        private static readonly MethodInfo _fillerInfo =
            typeof(DustParallelismSystem).GetMethod(
                nameof(UpdateDustFiller), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static readonly MethodInfo _runParallelInfo =
            typeof(DustParallelismSystem).GetMethod(
                nameof(RunParallel), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static MethodBody? _capturedBody;

        private ILHook? _captureHook;
        private ILHook? _fillerHook;
        private ILHook? _patchLoopHook;

        private const int MinParallelDustCount = 500;

        private static bool _parallelEnabled;
        private static Mod? _mod;

        public override void OnModLoad()
        {
            _mod = Mod;

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.DustParallelismEnabled)
                return;

            if (Environment.ProcessorCount < 2)
            {
                Mod.Logger.Info("[Oxygen] DustParallelism: single-core CPU, skipped.");
                return;
            }

            try
            {
                var updateDustMethod = typeof(Dust).GetMethod(
                    "UpdateDust",
                    BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null)
                    ?? throw new Exception("Dust.UpdateDust() not found.");

                _captureHook  = new ILHook(updateDustMethod, CaptureBody);
                _fillerHook   = new ILHook(_fillerInfo, PatchFiller);
                _patchLoopHook = new ILHook(updateDustMethod, PatchLoop);

                OxygenParallel.Initialize();
                _parallelEnabled = true;

                Mod.Logger.Info(
                    $"[Oxygen] DustParallelism: active with {Environment.ProcessorCount - 1} workers.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] DustParallelism: hook failed, falling back to vanilla. " +
                    $"({ex.GetType().Name}: {ex.Message})");
                Unregister();
            }
        }

        public override void OnModUnload() => Unregister();

        private void Unregister()
        {
            _patchLoopHook?.Dispose(); _patchLoopHook = null;
            _fillerHook?.Dispose();    _fillerHook    = null;
            _captureHook?.Dispose();   _captureHook   = null;

            OxygenParallel.Shutdown();
        }

        // ── IL hooks ─────────────────────────────────────────────────────────────

        private static void CaptureBody(ILContext il)
        {
            _capturedBody = il.Body;
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
                    throw new Exception("Cannot find loop entry branch in Dust.UpdateDust.");

                c.Emit(OpCodes.Br, skipLabel);

                c.GotoLabel(loopCheckLabel);
                if (!c.TryGotoNext(MoveType.After, x => x.MatchBlt(out _)))
                    throw new Exception("Cannot find loop condition (Blt) in Dust.UpdateDust.");

                c.Emit(OpCodes.Call, _runParallelInfo);

                c.GotoPrev(MoveType.After, x => x.MatchBlt(out _));
                c.MarkLabel(skipLabel);
            }
            catch (Exception ex)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] DustParallelism: PatchLoop failed. ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private static void PatchFiller(ILContext il)
        {
            if (_capturedBody is null)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error("[Oxygen] DustParallelism: PatchFiller ran before body capture.");
                return;
            }

            var c = new ILCursor(il);
            try
            {
                ILMethodBodyCloner.CloneBodyToCursor(_capturedBody, c);

                if (!c.TryGotoNext(MoveType.After, x => x.MatchLdcI4(Main.maxDust)))
                    throw new Exception("Cannot find Main.maxDust in cloned body.");
                c.Emit(OpCodes.Pop);
                c.Emit(OpCodes.Ldarg_1);

                int loopVarIdx = -1;
                c.GotoPrev(x => x.MatchLdloc(out loopVarIdx));
                if (loopVarIdx < 0)
                    throw new Exception("Cannot identify loop counter local.");

                c.Index = 0;
                if (!c.TryGotoNext(x => x.MatchStloc(loopVarIdx)))
                    throw new Exception("Cannot find loop counter initialisation.");
                c.GotoPrev(MoveType.After, x => x.MatchLdcI4(0));
                c.Emit(OpCodes.Pop);
                c.Emit(OpCodes.Ldarg_0);
            }
            catch (Exception ex)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] DustParallelism: PatchFiller failed. ({ex.GetType().Name}: {ex.Message})");
            }
            finally
            {
                _capturedBody = null;
            }
        }

        // ── Stubs / runtime ───────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpdateDustFiller(int inclusive, int exclusive) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RunParallel()
        {
            if (!_parallelEnabled || SharedBossState.ActiveDustCount < MinParallelDustCount)
            {
                UpdateDustFiller(0, Main.maxDust);
                return;
            }

            try
            {
                OxygenParallel.For(0, Main.maxDust, UpdateDustFiller);
            }
            catch (Exception ex)
            {
                _parallelEnabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] DustParallelism: runtime error, auto-disabled. " +
                    $"({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message})");
                UpdateDustFiller(0, Main.maxDust);
            }
        }
    }
}
