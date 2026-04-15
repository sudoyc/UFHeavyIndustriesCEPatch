using System;
using CombatExtended;
using CombatExtended.Lasers;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace UFHeavyIndustries_CE
{
    /// <summary>
    /// DefModExtension to specify which original-mod beam mote to spawn
    /// instead of CE's LaserBeamGraphicCE.
    /// </summary>
    public class BeamMoteExtension : DefModExtension
    {
        public ThingDef beamMoteDef;
        public float beamStartOffset = 0.5f;
        public EffecterDef impactEffecter;
    }

    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            new Harmony("ycyc.UFHeavyIndustries.CEPatch").PatchAll();
        }
    }

    /// <summary>
    /// Harmony Prefix on LaserBeamCE.SpawnBeam — for defs with BeamMoteExtension,
    /// spawn the original mod's MoteDualAttached beam mote and impact effecter,
    /// then skip CE's beam graphic.
    /// </summary>
    [HarmonyPatch(typeof(LaserBeamCE), nameof(LaserBeamCE.SpawnBeam))]
    public static class Patch_LaserBeamCE_SpawnBeam
    {
        public static bool Prefix(LaserBeamCE __instance, Vector3 a, Vector3 b)
        {
            try
            {
                var ext = __instance.def.GetModExtension<BeamMoteExtension>();
                if (ext?.beamMoteDef == null)
                    return true;

                Map map = __instance.Map;
                Thing launcher = __instance.launcher;
                if (map == null || launcher == null || launcher.Destroyed)
                    return false;

                // Beam mote: from launcher toward target
                Vector3 launcherPos = launcher.DrawPos;
                Vector3 dir = (b - launcherPos).Yto0().normalized;
                Vector3 offset = dir * ext.beamStartOffset;

                TargetInfo targetA = new TargetInfo(launcher);
                TargetInfo targetB = new TargetInfo(b.ToIntVec3(), map);

                MoteMaker.MakeInteractionOverlay(ext.beamMoteDef, targetA, targetB, offset, Vector3.zero);

                // Impact effecter at beam endpoint (b)
                if (ext.impactEffecter != null)
                {
                    IntVec3 impactCell = b.ToIntVec3();
                    if (impactCell.InBounds(map))
                    {
                        var targetInfo = new TargetInfo(impactCell, map);
                        Effecter effecter = ext.impactEffecter.Spawn();
                        effecter.Trigger(targetInfo, targetInfo);
                        effecter.Cleanup();
                    }
                }

                return false;
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] SpawnBeam patch failed: {e}");
                return true;
            }
        }
    }
}
