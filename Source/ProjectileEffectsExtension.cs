using System;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace UFHeavyIndustries_CE
{
    /// <summary>
    /// DefModExtension for adding impact effecters and flight trail flecks to CE projectiles.
    /// </summary>
    public class ProjectileEffectsExtension : DefModExtension
    {
        /// <summary>Effecter to trigger at the impact point.</summary>
        public EffecterDef impactEffecter;

        /// <summary>Fleck to spawn during flight as a trail.</summary>
        public FleckDef tailFleckDef;

        /// <summary>Spawn trail fleck every N ticks.</summary>
        public int tailFleckTickInterval = 10;

        /// <summary>Delay (in ticks) before trail starts spawning.</summary>
        public int tailFleckDelayTicks = 5;

        /// <summary>Number of flecks to spawn per interval.</summary>
        public int tailFleckCount = 4;
    }

    /// <summary>
    /// Harmony patches on ProjectileCE to support impact effecters and flight trail flecks.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Impact))]
    public static class Patch_ProjectileCE_Impact
    {
        /// <summary>
        /// Prefix: capture Map, Position, and ExactPosition before Impact destroys the projectile.
        /// </summary>
        public static void Prefix(ProjectileCE __instance, ref (Map map, IntVec3 position, Vector3 exactPosition)? __state)
        {
            try
            {
                var ext = __instance.def.GetModExtension<ProjectileEffectsExtension>();
                if (ext?.impactEffecter == null)
                    return;

                Map map = __instance.Map;
                if (map == null)
                    return;

                __state = (map, __instance.Position, __instance.ExactPosition);
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] ProjectileCE.Impact Prefix failed: {e}");
            }
        }

        /// <summary>
        /// Postfix: trigger the impact effecter at the captured position.
        /// </summary>
        public static void Postfix(ProjectileCE __instance, (Map map, IntVec3 position, Vector3 exactPosition)? __state)
        {
            try
            {
                if (__state == null)
                    return;

                var ext = __instance.def.GetModExtension<ProjectileEffectsExtension>();
                if (ext?.impactEffecter == null)
                    return;

                var (map, position, exactPosition) = __state.Value;
                if (!position.InBounds(map))
                    return;

                var targetInfo = new TargetInfo(position, map);
                Effecter effecter = ext.impactEffecter.Spawn();
                effecter.Trigger(targetInfo, targetInfo);
                effecter.Cleanup();
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] ProjectileCE.Impact Postfix failed: {e}");
            }
        }
    }

    /// <summary>
    /// Harmony Postfix on ProjectileCE.Tick to spawn trail flecks during flight.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Tick))]
    public static class Patch_ProjectileCE_Tick
    {
        public static void Postfix(ProjectileCE __instance)
        {
            try
            {
                var ext = __instance.def.GetModExtension<ProjectileEffectsExtension>();
                if (ext?.tailFleckDef == null)
                    return;

                Map map = __instance.Map;
                if (map == null || __instance.Destroyed)
                    return;

                int ticksGame = Find.TickManager.TicksGame;

                // Respect the delay before trail starts
                // ticksGame mod check — use interval after delay has passed
                if (ticksGame % ext.tailFleckTickInterval != 0)
                    return;

                // We can't easily know the exact launch tick from the outside, so use
                // TicksGame relative to tailFleckDelayTicks to gate the first spawn.
                // A simple approach: skip the first N ticks by checking against
                // the projectile's age via ticksGame (approximated).
                // Since we don't have easy access to launch tick, use a modulo-based gate:
                // only spawn if (ticksGame / tailFleckTickInterval) > tailFleckDelayTicks.
                int intervals = ticksGame / ext.tailFleckTickInterval;
                if (intervals < ext.tailFleckDelayTicks)
                    return;

                Vector3 exactPos = __instance.ExactPosition;
                IntVec3 cell = exactPos.ToIntVec3();
                if (!cell.InBounds(map))
                    return;

                for (int i = 0; i < ext.tailFleckCount; i++)
                {
                    FleckCreationData fcd = FleckMaker.GetDataStatic(exactPos, map, ext.tailFleckDef);
                    map.flecks.CreateFleck(fcd);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] ProjectileCE.Tick Postfix failed: {e}");
            }
        }
    }
}
