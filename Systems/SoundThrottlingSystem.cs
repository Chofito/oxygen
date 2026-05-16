using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using ReLogic.Utilities;
using Terraria;
using Terraria.Audio;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    /// <summary>
    /// Prevents the same sound from playing more than N times within a rolling
    /// time window, reducing audio spam during heavy boss fights.
    ///
    /// Strategy:
    ///   Uses an ILHook on SoundEngine.PlaySound(in SoundStyle, Vector2?, SoundUpdateCallback?)
    ///   to inject a guard check before any sound is dispatched. The check uses the
    ///   SoundStyle.SoundPath string as the per-sound key, so both vanilla and modded
    ///   sounds are throttled by their actual asset path.
    ///
    /// Why ILHook instead of On.:
    ///   SoundEngine.PlaySound has two public overloads that differ only in SoundStyle nullability.
    ///   HookGen may fail to disambiguate them, and the `in` modifier on SoundStyle causes
    ///   additional delegate-signature complications. ILHook with reflection pinpoints the
    ///   exact overload at the IL level without any of those issues.
    /// </summary>
    public class SoundThrottlingSystem : ModSystem
    {
        // Per-sound-path rolling window: path → ticks when it played recently.
        // SoundEngine.PlaySound can be called from worker threads (e.g. from ModDust.Update
        // running inside DustParallelismSystem). All dictionary access must be locked.
        private static readonly Dictionary<string, Queue<uint>> _history = new();
        private static readonly List<string> _cleanupBuffer = new();
        private static readonly object _lock = new();

        private ILHook? _soundHook;
        private static bool _enabled;
        private static uint _lastCleanupTick;
        private static Mod? _mod; // captured for use in static callbacks

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void OnModLoad()
        {
            _mod = Mod; // capture before any early return so static callbacks can log

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg is not null && !cfg.SoundThrottlingEnabled)
                return;

            try
            {
                // Locate SoundEngine.PlaySound(in SoundStyle, Vector2?, SoundUpdateCallback?)
                // At CLR level, `in SoundStyle` is a by-ref type, same as `ref SoundStyle`.
                var targetMethod = typeof(SoundEngine).GetMethod(
                    "PlaySound",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[]
                    {
                        typeof(SoundStyle).MakeByRefType(), // in SoundStyle
                        typeof(Vector2?),
                        typeof(SoundUpdateCallback)
                    },
                    null)
                    ?? throw new Exception("Could not reflect SoundEngine.PlaySound(in SoundStyle, ...) - overload not found.");

                _soundHook = new ILHook(targetMethod, HookPlaySoundIL);
                _enabled = true;

                Mod.Logger.Info("[Oxygen] SoundThrottling: active.");
            }
            catch (Exception ex)
            {
                Mod.Logger.Warn(
                    $"[Oxygen] SoundThrottling: hook installation failed - feature disabled. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        public override void OnModUnload()
        {
            _soundHook?.Dispose();
            _soundHook = null;
            _enabled = false;
            _history.Clear();
        }

        // Periodic cleanup to prevent unbounded dictionary growth
        public override void PostUpdateEverything()
        {
            if (!_enabled) return;

            uint now = Main.GameUpdateCount;
            if (now - _lastCleanupTick < 600) // every 10 seconds
                return;

            _lastCleanupTick = now;

            var cfg = ModContent.GetInstance<ClientConfig>();
            uint windowTicks = (uint)(cfg?.SoundWindowTicks ?? 10);

            lock (_lock)
            {
                _cleanupBuffer.Clear();
                foreach (var (key, queue) in _history)
                {
                    while (queue.Count > 0 && now - queue.Peek() > windowTicks)
                        queue.Dequeue();
                    if (queue.Count == 0)
                        _cleanupBuffer.Add(key);
                }
                foreach (var key in _cleanupBuffer)
                    _history.Remove(key);
            }
        }

        // ── IL injection ─────────────────────────────────────────────────────

        /// <summary>
        /// Injects at the start of PlaySound:
        ///   ldarg.0                    ; push &SoundStyle (the `in` parameter reference)
        ///   call get_SoundPath()       ; push SoundPath string
        ///   call ShouldSuppress(string); push bool
        ///   brfalse normalFlow         ; if false, proceed normally
        ///   call GetInvalidSlotId()    ; push SlotId.Invalid
        ///   ret                        ; return it
        ///   [normalFlow: original body]
        /// </summary>
        private static void HookPlaySoundIL(ILContext il)
        {
            var c = new ILCursor(il);
            c.Index = 0;

            try
            {
                var getSoundPath = typeof(SoundStyle).GetProperty("SoundPath")
                    ?.GetGetMethod()
                    ?? throw new Exception("SoundStyle.SoundPath getter not found.");

                var shouldSuppress = typeof(SoundThrottlingSystem).GetMethod(
                    nameof(ShouldSuppress), BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new Exception("ShouldSuppress method not found.");

                var getInvalid = typeof(SoundThrottlingSystem).GetMethod(
                    nameof(GetInvalidSlotId), BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new Exception("GetInvalidSlotId method not found.");

                var normalFlow = c.DefineLabel();

                // Push SoundPath string from the `in SoundStyle` (arg0 = &SoundStyle)
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Call, getSoundPath);
                // Check throttle: bool ShouldSuppress(string?)
                c.Emit(OpCodes.Call, shouldSuppress);
                // If false (not suppressed), skip the early return
                c.Emit(OpCodes.Brfalse, normalFlow);
                // Suppressed: return SlotId.Invalid
                c.Emit(OpCodes.Call, getInvalid);
                c.Emit(OpCodes.Ret);
                // Mark the label at the start of the original body
                c.MarkLabel(normalFlow);
            }
            catch (Exception ex)
            {
                _enabled = false;
                _mod?.Logger.Error(
                    $"[Oxygen] SoundThrottling: HookPlaySoundIL failed - feature disabled. " +
                    $"({ex.GetType().Name}: {ex.Message})");
            }
        }

        // ── Static helpers called from injected IL ────────────────────────────

        /// <summary>
        /// Returns true if the sound at <paramref name="soundPath"/> has been played
        /// too many times recently and should be suppressed this tick.
        /// </summary>
        private static bool ShouldSuppress(string? soundPath)
        {
            if (Main.dedServ || string.IsNullOrEmpty(soundPath))
                return false;

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null || !cfg.SoundThrottlingEnabled)
                return false;

            uint now = Main.GameUpdateCount;
            int windowTicks = cfg.SoundWindowTicks;
            int maxInstances = cfg.MaxSoundInstancesPerWindow;

            // Lock required: DustParallelismSystem runs UpdateDust on worker threads,
            // and some ModDust.Update() calls can invoke SoundEngine.PlaySound, which
            // triggers this method from a non-main thread.
            lock (_lock)
            {
                if (!_history.TryGetValue(soundPath, out Queue<uint>? queue))
                {
                    queue = new Queue<uint>(maxInstances + 1);
                    _history[soundPath] = queue;
                }

                // Evict plays that have aged out of the window
                while (queue.Count > 0 && now - queue.Peek() > (uint)windowTicks)
                    queue.Dequeue();

                if (queue.Count >= maxInstances)
                    return true; // suppress - too many recent plays

                queue.Enqueue(now);
                return false;
            }
        }

        /// <summary>
        /// Returns <see cref="SlotId.Invalid"/>.
        /// Called from injected IL to avoid field-vs-property ambiguity at the IL emission level.
        /// </summary>
        private static SlotId GetInvalidSlotId() => SlotId.Invalid;
    }
}
