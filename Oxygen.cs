using System.Collections.Generic;
using Terraria.ModLoader;
using Oxygen.Config;

namespace Oxygen
{
    public class Oxygen : Mod
    {
        public static readonly HashSet<int> ExemptNPCs = new();
        public static readonly HashSet<int> ExemptProjectiles = new();

        public override object Call(params object[] args)
        {
            if (args == null || args.Length == 0)
                return null;

            string command = args[0] as string;
            if (command == null)
                return null;

            switch (command)
            {
                case "ExemptNPCFromThrottling":
                    if (args.Length >= 2 && args[1] is int npcType)
                        ExemptNPCs.Add(npcType);
                    return null;

                case "ExemptProjectileFromHiding":
                    if (args.Length >= 2 && args[1] is int projType)
                        ExemptProjectiles.Add(projType);
                    return null;

                case "IsFeatureActive":
                    if (args.Length < 2 || args[1] is not string feature)
                        return false;
                    var cfg = ModContent.GetInstance<ClientConfig>();
                    if (cfg == null)
                        return false;
                    return feature switch
                    {
                        "DustLOD"           => cfg.DustUpdateThrottling,
                        "ProjectileHiding"  => cfg.HideAllyProjectilesEnabled,
                        "ProjectileAI"      => cfg.ProjectileAIThrottlingEnabled,
                        "NPCThrottling"     => cfg.NPCThrottlingEnabled,
                        "TownNPCThrottling" => cfg.TownNPCThrottlingEnabled,
                        "GoreCulling"       => cfg.GoreCullingEnabled,
                        "SoundThrottling"   => cfg.SoundThrottlingEnabled,
                        "WorldUpdate"       => cfg.WorldUpdateThrottlingEnabled,
                        "GCManagement"      => cfg.GCManagementEnabled,
                        "DustParallelism"   => cfg.DustParallelismEnabled,
                        "ProjParallelism"   => cfg.ProjectileParallelismEnabled,
                        "LightingOpt"       => cfg.LightingOptimizationEnabled,
                        _ => false
                    };
            }

            return null;
        }

        public override void Unload()
        {
            ExemptNPCs.Clear();
            ExemptProjectiles.Clear();
        }
    }
}
