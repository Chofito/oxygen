using System.ComponentModel;
using Terraria.ModLoader.Config;

namespace Oxygen.Config
{
    public enum OverlayPosition
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public class ClientConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ClientSide;

        // --- Dust ---
        [Header("Dust")]
        [Label("Dust Update Throttling")]
        [Tooltip("Reduce update frequency for distant dust particles.")]
        [DefaultValue(true)]
        public bool DustUpdateThrottling { get; set; }

        [Label("Dust Density (%)")]
        [Tooltip("Percentage of dust particles allowed to exist. Lower = fewer particles.")]
        [Range(0, 100)]
        [DefaultValue(80)]
        public int DustDensityPercent { get; set; }

        [Label("Dust Cull Distance (tiles)")]
        [Tooltip("Dust beyond this distance from the player is culled.")]
        [Range(20, 200)]
        [DefaultValue(80)]
        public int DustCullDistance { get; set; }

        // --- Projectiles ---
        [Header("Projectiles")]
        [Label("Hide Ally Projectiles")]
        [Tooltip("Hides or dims projectiles from other players to reduce rendering load.")]
        [DefaultValue(true)]
        public bool HideAllyProjectilesEnabled { get; set; }

        [Label("Projectile AI Throttling")]
        [Tooltip("Skips AI every other tick for visual-only projectiles (damage=0) that are off-screen. " +
                 "Targets Infernum/Calamity boss aura projectiles that are purely decorative. " +
                 "Position still updates via velocity - no visible movement errors.")]
        [DefaultValue(true)]
        public bool ProjectileAIThrottlingEnabled { get; set; }

        [Label("Only During Boss Fights")]
        [Tooltip("When enabled, ally projectiles are only hidden while a boss is active.")]
        [DefaultValue(true)]
        public bool HideAllyProjectilesBossOnly { get; set; }

        [Label("Projectile Opacity (%)")]
        [Tooltip("0 = fully hidden. Values above 0 reduce opacity instead of hiding completely.")]
        [Range(0, 100)]
        [DefaultValue(0)]
        public int ProjectileOpacityPercent { get; set; }

        // --- NPC AI ---
        [Header("NPCThrottling")]
        [Label("NPC AI Throttling")]
        [Tooltip("Reduces AI update frequency for distant non-boss, non-town NPCs.")]
        [DefaultValue(true)]
        public bool NPCThrottlingEnabled { get; set; }

        [Label("NPC Throttle Start Distance (tiles)")]
        [Tooltip("NPCs beyond this distance from the player begin to be throttled.")]
        [Range(40, 300)]
        [DefaultValue(80)]
        public int NPCThrottleStartDistance { get; set; }

        [Label("Town NPC AI Throttling")]
        [Tooltip("Reduces AI update frequency for off-screen town NPCs (shopkeepers, etc.). " +
                 "Helps significantly in large bases with many NPCs. " +
                 "Off-screen town NPCs run AI at ~20 Hz instead of 60 Hz. " +
                 "Shop/dialogue interactions are unaffected - NPCs near you always run full AI.")]
        [DefaultValue(false)]
        public bool TownNPCThrottlingEnabled { get; set; }

        // --- Gore ---
        [Header("Gore")]
        [Label("Gore Culling")]
        [Tooltip("Hides gore outside the viewport and removes old off-screen gore.")]
        [DefaultValue(true)]
        public bool GoreCullingEnabled { get; set; }

        [Label("Gore Timeout (ticks)")]
        [Tooltip("Off-screen gore older than this many ticks is removed. 60 ticks = 1 second.")]
        [Range(60, 600)]
        [DefaultValue(300)]
        public int GoreTimeoutTicks { get; set; }

        // --- Sound ---
        [Header("Sound")]
        [Label("Sound Throttling")]
        [Tooltip("Prevents the same sound from playing too many times in a short window.")]
        [DefaultValue(true)]
        public bool SoundThrottlingEnabled { get; set; }

        [Label("Max Sound Instances Per Window")]
        [Tooltip("Maximum times the same sound can play within the time window.")]
        [Range(1, 10)]
        [DefaultValue(3)]
        public int MaxSoundInstancesPerWindow { get; set; }

        [Label("Sound Window (ticks)")]
        [Tooltip("Time window in ticks for sound throttling. 60 ticks = 1 second.")]
        [Range(5, 30)]
        [DefaultValue(10)]
        public int SoundWindowTicks { get; set; }

        // --- World ---
        [Header("World")]
        [Label("World Update Throttling")]
        [Tooltip("Reduces ambient world update frequency (grass/corruption spread) during boss fights.")]
        [DefaultValue(true)]
        public bool WorldUpdateThrottlingEnabled { get; set; }

        // --- GC ---
        [Header("GCManagement")]
        [Label("GC Management")]
        [Tooltip("Attempts to reduce garbage collection pauses during boss fights. Aggressive - enable with caution.")]
        [DefaultValue(false)]
        public bool GCManagementEnabled { get; set; }

        // --- Lighting ---
        [Header("Lighting")]
        [Label("Lighting Optimization")]
        [Tooltip("Skips off-screen Lighting.AddLight() calls, reducing lighting background thread work. " +
                 "Uses a 40-tile margin so light from partially-visible entities is never clipped. " +
                 "Pure visual optimization - no gameplay effect.")]
        [DefaultValue(true)]
        public bool LightingOptimizationEnabled { get; set; }

        // --- IL Parallelism ---
        [Header("Parallelism")]
        [Label("Dust Update Parallelism")]
        [Tooltip("Runs the dust update loop across multiple CPU cores using IL patching. " +
                 "Auto-disables on any error. Requires at least 2 logical cores. " +
                 "Most impactful during fights with heavy particle effects.")]
        [DefaultValue(true)]
        public bool DustParallelismEnabled { get; set; }

        [Label("Projectile Update Parallelism")]
        [Tooltip("Runs visual-only projectiles (damage=0, non-hostile) across multiple CPU cores. " +
                 "Targets Infernum/Calamity aura and burst projectiles around boss segments. " +
                 "Damage-dealing and hostile projectiles always run sequentially (safe). " +
                 "Auto-disables on any error. Requires at least 2 logical cores.")]
        [DefaultValue(true)]
        public bool ProjectileParallelismEnabled { get; set; }

        // --- Diagnostics ---
        [Header("Diagnostics")]
        [Label("Show Diagnostics Overlay")]
        [Tooltip("Displays an overlay with FPS, active entity counts, and active features.")]
        [DefaultValue(false)]
        public bool ShowDiagnosticsOverlay { get; set; }

        [Label("Overlay Position")]
        [Tooltip("Corner of the screen where the diagnostics overlay appears.")]
        [DefaultValue(OverlayPosition.TopLeft)]
        public OverlayPosition DiagnosticsPosition { get; set; }
    }
}
