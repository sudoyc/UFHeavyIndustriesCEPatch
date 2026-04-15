using System;
using System.Collections.Generic;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace UFHeavyIndustries_CE
{
    /// <summary>
    /// Defines a single sub-explosion to trigger on projectile impact.
    /// </summary>
    public class SubExplosionDef
    {
        public DamageDef damageDef;
        public float radius = 1f;
        public int damageAmount = 1;
        public float armorPenetration = 1f;
        public SoundDef explosionSound;
        public bool damageFalloff = true;
        public bool onlyAntiHostile = false;
        public EffecterDef explosionEffect;
        public GasType? postExplosionGasType;
        public float? postExplosionGasRadiusOverride;
    }

    /// <summary>
    /// Defines a scatter of vanilla sub-projectiles to launch on projectile impact.
    /// </summary>
    public class SubProjectileLaunchDef
    {
        public ThingDef projectileDef;
        public int count = 1;
        public float angleRange = 60f;
        public FloatRange distanceRange = new FloatRange(3f, 10f);
    }

    /// <summary>
    /// DefModExtension that triggers multiple sub-explosions and/or scattered sub-projectiles
    /// when a CE projectile with this extension impacts.
    /// </summary>
    public class MultiExplosionCEExtension : DefModExtension
    {
        public List<SubExplosionDef> subExplosions;
        public List<SubProjectileLaunchDef> subProjectiles;
    }

    /// <summary>
    /// Harmony Prefix + Postfix on ProjectileCE.Impact to fire sub-explosions and sub-projectiles.
    /// Uses Priority.Low so it runs after ProjectileEffectsExtension patches.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Impact))]
    public static class Patch_ProjectileCE_Impact_MultiExplosion
    {
        [HarmonyPriority(Priority.Low)]
        public static void Prefix(
            ProjectileCE __instance,
            ref (Map map, IntVec3 pos, Thing launcher, float rotation)? __state)
        {
            try
            {
                var ext = __instance.def.GetModExtension<MultiExplosionCEExtension>();
                if (ext == null)
                    return;

                Map map = __instance.Map;
                if (map == null)
                    return;

                float rotation = __instance.ExactRotation.eulerAngles.y;
                __state = (map, __instance.Position, __instance.launcher, rotation);
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] MultiExplosion Impact Prefix failed: {e}");
            }
        }

        [HarmonyPriority(Priority.Low)]
        public static void Postfix(
            ProjectileCE __instance,
            (Map map, IntVec3 pos, Thing launcher, float rotation)? __state)
        {
            try
            {
                if (__state == null)
                    return;

                var ext = __instance.def.GetModExtension<MultiExplosionCEExtension>();
                if (ext == null)
                    return;

                var (map, pos, launcher, rotation) = __state.Value;

                if (!pos.InBounds(map))
                    return;

                // --- Sub-explosions ---
                if (ext.subExplosions != null)
                {
                    foreach (var subExp in ext.subExplosions)
                    {
                        if (subExp?.damageDef == null)
                            continue;

                        List<Thing> ignoredThings = null;
                        if (subExp.onlyAntiHostile && launcher != null)
                        {
                            // Collect non-hostile pawns within radius to ignore them
                            var launcherFaction = launcher.Faction;
                            if (launcherFaction != null)
                            {
                                ignoredThings = new List<Thing>();
                                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                                {
                                    if (pawn.Faction == null || !pawn.Faction.HostileTo(launcherFaction))
                                    {
                                        float dist = pawn.Position.DistanceTo(pos);
                                        if (dist <= subExp.radius + 1f)
                                            ignoredThings.Add(pawn);
                                    }
                                }
                            }
                        }

                        GenExplosion.DoExplosion(
                            center: pos,
                            map: map,
                            radius: subExp.radius,
                            damType: subExp.damageDef,
                            instigator: launcher,
                            damAmount: subExp.damageAmount,
                            armorPenetration: subExp.armorPenetration,
                            explosionSound: subExp.explosionSound,
                            damageFalloff: subExp.damageFalloff,
                            ignoredThings: ignoredThings,
                            postExplosionGasType: subExp.postExplosionGasType,
                            postExplosionGasRadiusOverride: subExp.postExplosionGasRadiusOverride
                        );

                        // Trigger explosion effecter if specified
                        if (subExp.explosionEffect != null && pos.InBounds(map))
                        {
                            var targetInfo = new TargetInfo(pos, map);
                            Effecter effecter = subExp.explosionEffect.Spawn();
                            effecter.Trigger(targetInfo, targetInfo);
                            effecter.Cleanup();
                        }
                    }
                }

                // --- Sub-projectiles ---
                if (ext.subProjectiles != null)
                {
                    Vector3 sourcePos = pos.ToVector3Shifted();

                    foreach (var subProj in ext.subProjectiles)
                    {
                        if (subProj?.projectileDef == null || subProj.count <= 0)
                            continue;

                        for (int i = 0; i < subProj.count; i++)
                        {
                            // Random angle scattered around impact point
                            float halfRange = subProj.angleRange / 2f;
                            float randomAngleDeg = rotation + Rand.Range(-halfRange, halfRange);
                            float randomAngleRad = randomAngleDeg * Mathf.Deg2Rad;

                            // Random distance for target cell
                            float distance = subProj.distanceRange.RandomInRange;
                            Vector3 offset = new Vector3(
                                Mathf.Sin(randomAngleRad) * distance,
                                0f,
                                Mathf.Cos(randomAngleRad) * distance
                            );
                            IntVec3 targetCell = (sourcePos + offset).ToIntVec3();

                            // Clamp to map bounds
                            targetCell.x = Mathf.Clamp(targetCell.x, 0, map.Size.x - 1);
                            targetCell.z = Mathf.Clamp(targetCell.z, 0, map.Size.z - 1);

                            // Spawn and launch the vanilla projectile
                            Projectile proj = (Projectile)GenSpawn.Spawn(subProj.projectileDef, pos, map);
                            if (proj == null)
                                continue;

                            proj.Launch(
                                launcher: launcher,
                                origin: sourcePos,
                                usedTarget: new LocalTargetInfo(targetCell),
                                intendedTarget: new LocalTargetInfo(targetCell),
                                hitFlags: ProjectileHitFlags.All
                            );
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] MultiExplosion Impact Postfix failed: {e}");
            }
        }
    }
}
