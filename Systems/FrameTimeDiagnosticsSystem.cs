using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen.Systems
{
    /// <summary>
    /// HUD overlay with full per-system timing breakdown.
    ///
    /// Update phase - measured via ModSystem Pre/Post hooks:
    ///   Players · NPC · Gore · Projectiles · Items · Dust · World
    ///
    ///   ?Upd = total update window (PreUpdatePlayers → PostUpdateEverything)
    ///          minus the sum of per-system measurements.
    ///          Captures Calamity/Infernum hooks, GlobalNPC/GlobalProjectile
    ///          callbacks, and any inter-system tModLoader overhead that falls
    ///          outside our Pre/Post windows.
    ///
    /// Render phase - measured via PostUpdateEverything + PostDrawTiles + PostDrawInterface:
    ///   Tile : PostUpdateEverything → PostDrawTiles  (tile geometry draw calls)
    ///   GFX  : PostDrawTiles → PostDrawInterface
    ///          (NPC bodies, projectile sprites, player, Infernum shaders,
    ///           post-processing effects, UI - everything drawn after tiles)
    ///
    ///   ?Oth = frame − updateTotal − GFX.
    ///          Captures lighting engine (LightTiles thread sync), SoundEngine.Update,
    ///          XNA/FNA housekeeping, and any overhead between PostDrawInterface
    ///          and the next PreUpdatePlayers.
    ///
    /// All stopwatches run every frame regardless of overlay visibility - overhead
    /// is ~16 Stopwatch.Restart/Stop calls ≈ 1.6 µs/frame, negligible.
    ///
    /// Entity counts read from SharedBossState (single scan/tick shared across systems).
    /// </summary>
    public class FrameTimeDiagnosticsSystem : ModSystem
    {
        // ── Stopwatches ───────────────────────────────────────────────────────

        private static readonly Stopwatch _playersWatch = new();
        private static readonly Stopwatch _npcWatch     = new();
        private static readonly Stopwatch _goreWatch    = new();
        private static readonly Stopwatch _projWatch    = new();
        private static readonly Stopwatch _itemsWatch   = new();
        private static readonly Stopwatch _dustWatch    = new();
        private static readonly Stopwatch _worldWatch   = new();

        // Total update phase: PreUpdatePlayers → PostUpdateEverything (captures other mods' hooks too)
        private static readonly Stopwatch _updateTotalWatch = new();

        // Render phase: both start at PostUpdateEverything, stop at different draw events
        private static readonly Stopwatch _tileWatch    = new(); // stops at PostDrawTiles
        private static readonly Stopwatch _gfxWatch     = new(); // stops at PostDrawInterface

        // Full frame: PostDrawInterface → PostDrawInterface
        private static readonly Stopwatch _frameWatch   = Stopwatch.StartNew();

        // ── 60-frame rolling averages ─────────────────────────────────────────

        private static readonly RollingAvg _playersAvg = new(60);
        private static readonly RollingAvg _npcAvg     = new(60);
        private static readonly RollingAvg _goreAvg    = new(60);
        private static readonly RollingAvg _projAvg    = new(60);
        private static readonly RollingAvg _itemsAvg   = new(60);
        private static readonly RollingAvg _dustAvg    = new(60);
        private static readonly RollingAvg _worldAvg   = new(60);
        private static readonly RollingAvg _updateTotalAvg = new(60);
        private static readonly RollingAvg _tileAvg        = new(60);
        private static readonly RollingAvg _gfxAvg         = new(60);
        private static readonly RollingAvg _frameAvg       = new(60);

        // Derived values cached for GetLineColor (computed in BuildLines each draw call)
        private static double _lastHiddenUpdate;
        private static double _lastHiddenOther;

        // How many Update() calls fired since the last Draw() call.
        // MonoGame fixed-timestep catch-up loop runs N updates per draw when the game lags.
        // If N > 1, the real CPU update cost per draw frame is updateTotal × N.
        private static int _updateCountThisFrame;
        private static int _lastUpdateCount;

        // ── Update-phase timing hooks ─────────────────────────────────────────

        public override void PreUpdatePlayers()
        {
            _updateCountThisFrame++;      // count every Update() call this draw frame
            _updateTotalWatch.Restart();  // start of the full update window
            _playersWatch.Restart();
        }
        public override void PostUpdatePlayers() { _playersAvg.Add(_playersWatch.Elapsed.TotalMilliseconds); _playersWatch.Stop(); }

        public override void PreUpdateNPCs()     => _npcWatch.Restart();
        public override void PostUpdateNPCs()    { _npcAvg.Add(_npcWatch.Elapsed.TotalMilliseconds);     _npcWatch.Stop(); }

        public override void PreUpdateGores()    => _goreWatch.Restart();
        public override void PostUpdateGores()   { _goreAvg.Add(_goreWatch.Elapsed.TotalMilliseconds);   _goreWatch.Stop(); }

        public override void PreUpdateProjectiles()  => _projWatch.Restart();
        public override void PostUpdateProjectiles() { _projAvg.Add(_projWatch.Elapsed.TotalMilliseconds); _projWatch.Stop(); }

        public override void PreUpdateItems()    => _itemsWatch.Restart();
        public override void PostUpdateItems()   { _itemsAvg.Add(_itemsWatch.Elapsed.TotalMilliseconds);  _itemsWatch.Stop(); }

        public override void PreUpdateDusts()    => _dustWatch.Restart();
        public override void PostUpdateDusts()   { _dustAvg.Add(_dustWatch.Elapsed.TotalMilliseconds);   _dustWatch.Stop(); }

        public override void PreUpdateWorld()    => _worldWatch.Restart();
        public override void PostUpdateWorld()   { _worldAvg.Add(_worldWatch.Elapsed.TotalMilliseconds); _worldWatch.Stop(); }

        // ── Render-phase timing hooks ─────────────────────────────────────────

        /// <summary>
        /// Called after all update systems finish, before Main.DoDraw() starts.
        /// Stops the total-update timer, then starts both render stopwatches.
        /// Note: other mods' PostUpdateEverything hooks may fire before or after ours
        /// depending on load order; time outside our window appears in ?Oth.
        /// </summary>
        public override void PostUpdateEverything()
        {
            _updateTotalAvg.Add(_updateTotalWatch.Elapsed.TotalMilliseconds);
            _updateTotalWatch.Stop();
            _tileWatch.Restart();
            _gfxWatch.Restart();
        }

        /// <summary>
        /// Called after vanilla tile rendering completes.
        /// Stops the tile timer; GFX timer keeps running.
        /// </summary>
        public override void PostDrawTiles()
        {
            _tileAvg.Add(_tileWatch.Elapsed.TotalMilliseconds);
            _tileWatch.Stop();
        }

        // ── Frame draw + overlay ──────────────────────────────────────────────

        public override void PostDrawInterface(SpriteBatch spriteBatch)
        {
            // Stop GFX timer (captures everything drawn after tiles: NPCs, projectiles,
            // Infernum shaders, post-processing, UI).
            _gfxAvg.Add(_gfxWatch.Elapsed.TotalMilliseconds);
            _gfxWatch.Stop();

            // Frame timer: time between PostDrawInterface calls = full frame time.
            _frameAvg.Add(_frameWatch.Elapsed.TotalMilliseconds);
            _frameWatch.Restart();

            // Snapshot and reset the update-per-draw counter.
            _lastUpdateCount = _updateCountThisFrame;
            _updateCountThisFrame = 0;

            var cfg = ModContent.GetInstance<ClientConfig>();
            if (cfg == null || !cfg.ShowDiagnosticsOverlay)
                return;

            const int margin     = 10;
            const int lineHeight = 18;

            string[] lines = BuildLines(cfg);
            Vector2 blockSize = new Vector2(240, lines.Length * lineHeight + 6);

            Vector2 origin = cfg.DiagnosticsPosition switch
            {
                OverlayPosition.TopRight    => new Vector2(Main.screenWidth  - blockSize.X - margin, margin),
                OverlayPosition.BottomLeft  => new Vector2(margin,            Main.screenHeight - blockSize.Y - margin),
                OverlayPosition.BottomRight => new Vector2(Main.screenWidth  - blockSize.X - margin, Main.screenHeight - blockSize.Y - margin),
                _                           => new Vector2(margin, margin)
            };

            DrawBackground(spriteBatch, origin, blockSize);

            float scale = 0.75f;
            for (int i = 0; i < lines.Length; i++)
            {
                Vector2 pos   = origin + new Vector2(4, 3 + i * lineHeight);
                Color   color = GetLineColor(i);
                Utils.DrawBorderString(spriteBatch, lines[i], pos, color, scale);
            }
        }

        // ── Display ───────────────────────────────────────────────────────────

        private static string[] BuildLines(ClientConfig cfg)
        {
            double players     = _playersAvg.Average;
            double npc         = _npcAvg.Average;
            double gore        = _goreAvg.Average;
            double proj        = _projAvg.Average;
            double items       = _itemsAvg.Average;
            double dust        = _dustAvg.Average;
            double world       = _worldAvg.Average;
            double updateTotal = _updateTotalAvg.Average;
            double tile        = _tileAvg.Average;
            double gfx         = _gfxAvg.Average;
            double frame       = _frameAvg.Average;

            // ?Upd: time in the update window NOT covered by our per-system hooks.
            //   Includes Calamity/Infernum GlobalNPC/GlobalProjectile callbacks,
            //   other mods' Pre/PostUpdate hooks, and inter-system tModLoader dispatch.
            double knownUpdate  = players + npc + gore + proj + items + dust + world;
            _lastHiddenUpdate   = Math.Max(0.0, updateTotal - knownUpdate);

            // ?Oth: time in the full frame NOT covered by update phase or GFX.
            //   If MonoGame is running N updates per draw (catch-up loop), this will
            //   be approximately (N - 1) × updateTotal - the extra update cycles
            //   that fire between draw calls but outside our measured windows.
            _lastHiddenOther    = Math.Max(0.0, frame - updateTotal - gfx);

            // Total CPU update cost per draw frame = one measured cycle × number of cycles.
            int    n              = Math.Max(1, _lastUpdateCount);
            double realUpdateCost = updateTotal * n;

            return new[]
            {
                // ── Frame summary ──────────────────────────────────────────────
                // Upd/Draw: how many Update() calls fire per Draw() call.
                // If > 1, MonoGame catch-up loop is running - real CPU cost = updateTotal × N.
                $"FPS: {(int)Main.frameRate}  ({frame:F1}ms)  Upd×{n}",

                // ── Update phase (one cycle) ────────────────────────────────────
                $"Plyr: {players:F1}ms",
                $"NPC : {npc:F1}ms",
                $"Gore: {gore:F1}ms",
                $"Proj: {proj:F1}ms",
                $"Itms: {items:F1}ms",
                $"Dust: {dust:F1}ms",
                $"Wrld: {world:F1}ms",
                // Hidden update cost - Calamity/Infernum/other mods
                $"?Upd: {_lastHiddenUpdate:F1}ms",
                // Real total CPU update cost this draw frame (= one cycle × N)
                $"ΣUpd: {realUpdateCost:F1}ms  ({n}×)",

                // ── Render phase ───────────────────────────────────────────────
                $"Tile: {tile:F1}ms",
                $"GFX : {gfx:F1}ms",
                // Hidden non-update, non-GFX cost - lighting, sound, XNA
                $"?Oth: {_lastHiddenOther:F1}ms",

                // ── Entity counts ──────────────────────────────────────────────
                $"Dust: {SharedBossState.ActiveDustCount}/{Main.maxDustToDraw}",
                $"Proj: {SharedBossState.ActiveProjectileCount}/{Main.maxProjectiles}",
                $"NPCs: {SharedBossState.ActiveNPCCount}/{Main.maxNPCs}",

                BuildFeatureBar(cfg)
            };
        }

        private static string BuildFeatureBar(ClientConfig cfg)
        {
            char d = cfg.DustUpdateThrottling         ? 'D' : 'd';
            char p = cfg.HideAllyProjectilesEnabled   ? 'P' : 'p';
            char n = cfg.NPCThrottlingEnabled          ? 'N' : 'n';
            char g = cfg.GoreCullingEnabled            ? 'G' : 'g';
            char s = cfg.SoundThrottlingEnabled        ? 'S' : 's';
            char t = cfg.TownNPCThrottlingEnabled      ? 'T' : 't';
            char l = cfg.LightingOptimizationEnabled   ? 'L' : 'l';
            return $"{d} {p} {n} {g} {s} {t} {l}";
        }

        // ── Color coding ──────────────────────────────────────────────────────

        private static Color GetLineColor(int i) => i switch
        {
            0  => GetFpsColor(),
            1  => GetMsColor(_playersAvg.Average),
            2  => GetMsColor(_npcAvg.Average),
            3  => GetMsColor(_goreAvg.Average),
            4  => GetMsColor(_projAvg.Average),
            5  => GetMsColor(_itemsAvg.Average),
            6  => GetMsColor(_dustAvg.Average),
            7  => GetMsColor(_worldAvg.Average),
            8  => GetMsColor(_lastHiddenUpdate),  // ?Upd
            9  => GetMsColor(_lastUpdateCount > 1  // ΣUpd - red if catch-up loop is active
                     ? _updateTotalAvg.Average * _lastUpdateCount
                     : 0),
            10 => GetMsColor(_tileAvg.Average),
            11 => GetMsColor(_gfxAvg.Average),
            12 => GetMsColor(_lastHiddenOther),   // ?Oth
            16 => Color.White,                    // feature bar
            _  => new Color(200, 200, 200)        // entity counts (13, 14, 15)
        };

        private static Color GetFpsColor()
        {
            float fps = Main.frameRate;
            if (fps >= 55) return Color.LightGreen;
            if (fps >= 35) return Color.Yellow;
            return Color.OrangeRed;
        }

        private static Color GetMsColor(double ms)
        {
            if (ms <= 0)   return new Color(200, 200, 200);
            if (ms < 5.0)  return Color.LightGreen;
            if (ms < 10.0) return Color.Yellow;
            return Color.OrangeRed;
        }

        // ── Background ────────────────────────────────────────────────────────

        private static void DrawBackground(SpriteBatch spriteBatch, Vector2 pos, Vector2 size)
        {
            spriteBatch.Draw(
                TextureAssets.MagicPixel.Value,
                new Rectangle((int)pos.X - 2, (int)pos.Y - 2, (int)size.X + 4, (int)size.Y + 4),
                new Color(0, 0, 0, 160)
            );
        }

        // ── RollingAvg ────────────────────────────────────────────────────────

        private sealed class RollingAvg
        {
            private readonly double[] _buf;
            private int    _head;
            private double _sum;
            private int    _count;

            public RollingAvg(int capacity) => _buf = new double[capacity];

            public void Add(double value)
            {
                _sum -= _buf[_head];
                _buf[_head] = value;
                _sum += value;
                _head = (_head + 1) % _buf.Length;
                if (_count < _buf.Length) _count++;
            }

            public double Average => _count > 0 ? _sum / _count : 0.0;
        }
    }
}
