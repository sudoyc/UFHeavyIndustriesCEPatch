using System;
using System.Collections.Generic;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace UFHeavyIndustries_CE
{
    /// <summary>
    /// DefModExtension that enables ECT-style "skimming penetration" behavior on CE BulletCE projectiles.
    /// After first impact on a non-impassable target, the projectile enters skimming mode and
    /// sweeps damage along its flight path until hitting an impassable wall or leaving the map.
    /// </summary>
    public class PenetrationCEExtension : DefModExtension
    {
        public float hitWidthRadius = 1.5f;
        public int maxHitsPerTarget = 1;
        public float knockbackDistance = 3f;
        public int stunDuration = 120;
        public SoundDef skimHitSound;
        public FleckDef sparkFleckDef;
        public IntRange sparkCountRange = new IntRange(5, 10);
        public FloatRange sparkSpeedRange = new FloatRange(10f, 20f);
        public float sparkSpreadAngle = 15f;
        public float lightningLength = 20f;
        public int lightningDuration = 20;
        public int lightningGrowthTicks = 5;
        public float lightningVariance = 1f;
        public float lightningWidth = 2.5f;
    }

    /// <summary>
    /// Per-projectile state tracker for skimming penetration mode.
    /// </summary>
    public class SkimmingState
    {
        public Map map;
        public Vector3 direction;
        public Vector3 currentPos;
        public Vector3 prevPos;
        public float speed;
        public Thing launcher;
        public ThingDef equipmentDef;
        public Dictionary<int, int> hitCounts = new Dictionary<int, int>();
        public HashSet<IntVec3> visitedCells = new HashSet<IntVec3>();
        public int lastSoundTick = -999;
        public int lastLightningTick = -999;
        public PenetrationCEExtension ext;
    }

    /// <summary>
    /// Static tracker that maps projectile thingIDNumber to their SkimmingState.
    /// </summary>
    public static class SkimmingTracker
    {
        public static readonly Dictionary<int, SkimmingState> States = new Dictionary<int, SkimmingState>();

        public static bool IsSkimming(int id)
        {
            return States.ContainsKey(id);
        }

        public static SkimmingState Get(int id)
        {
            States.TryGetValue(id, out var state);
            return state;
        }

        public static void Register(int id, SkimmingState state)
        {
            States[id] = state;
        }

        public static void Remove(int id)
        {
            States.Remove(id);
        }
    }

    /// <summary>
    /// Shared helper methods for skimming penetration logic.
    /// </summary>
    public static class PenetrationHelper
    {
        // Cached reflection for ECT.WeatherEvent_LightningTrail constructor
        private static bool _lightningTypeResolved;
        private static ConstructorInfo _lightningCtor;

        /// <summary>
        /// Resolve the ECT.WeatherEvent_LightningTrail constructor via reflection (cached).
        /// Constructor signature: (Map map, Vector3 start, Vector3 dir, float length, int duration, int growTicks, float variance, float width)
        /// </summary>
        private static ConstructorInfo GetLightningTrailCtor()
        {
            if (_lightningTypeResolved)
                return _lightningCtor;

            _lightningTypeResolved = true;
            try
            {
                // Search all loaded assemblies for the ECT type
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType("ECT.WeatherEvent_LightningTrail", false);
                    if (type != null)
                    {
                        _lightningCtor = type.GetConstructor(new Type[]
                        {
                            typeof(Map),
                            typeof(Vector3),
                            typeof(Vector3),
                            typeof(float),
                            typeof(int),
                            typeof(int),
                            typeof(float),
                            typeof(float)
                        });
                        if (_lightningCtor != null)
                        {
                            Log.Message("[UFHeavyIndustries_CE] Resolved ECT.WeatherEvent_LightningTrail via reflection.");
                        }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] Failed to resolve ECT.WeatherEvent_LightningTrail: {e}");
            }
            return _lightningCtor;
        }

        /// <summary>
        /// Apply damage to a thing during skimming, with stun and knockback for pawns.
        /// </summary>
        public static void ApplySkimmingDamage(
            ProjectileCE projectile,
            Thing target,
            Vector3 flightDir,
            bool isFirstHit,
            PenetrationCEExtension ext)
        {
            try
            {
                float damageAmount = projectile.DamageAmount;
                float penetration = projectile.PenetrationAmount;
                float angle = projectile.ExactRotation.eulerAngles.y;
                Thing launcher = projectile.launcher;
                ThingDef equipDef = projectile.equipmentDef;
                DamageDef damageDef = projectile.def.projectile.damageDef;

                var dinfo = new DamageInfo(
                    damageDef,
                    damageAmount,
                    penetration,
                    angle,
                    launcher,
                    null,
                    equipDef,
                    instigatorGuilty: true);

                target.TakeDamage(dinfo);

                if (target is Pawn pawn && !pawn.Dead && !pawn.Destroyed)
                {
                    // Stun
                    if (ext.stunDuration > 0)
                    {
                        float stunAmount = ext.stunDuration / 30f;
                        if (stunAmount < 0.1f) stunAmount = 0.5f;
                        var stunInfo = new DamageInfo(
                            DamageDefOf.Stun,
                            stunAmount,
                            999f,
                            angle,
                            launcher,
                            null,
                            equipDef,
                            instigatorGuilty: true);
                        pawn.TakeDamage(stunInfo);
                    }

                    // Knockback on first hit only
                    if (isFirstHit && ext.knockbackDistance > 0f)
                    {
                        KnockbackTarget(pawn, flightDir, ext.knockbackDistance, projectile.Map);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE ApplySkimmingDamage failed: {e}");
            }
        }

        /// <summary>
        /// Teleport-knockback a pawn along the flight direction.
        /// </summary>
        public static void KnockbackTarget(Pawn pawn, Vector3 direction, float distance, Map map)
        {
            try
            {
                IntVec3 startPos = pawn.Position;
                IntVec3 bestPos = startPos;

                for (int i = 1; i <= (int)distance; i++)
                {
                    IntVec3 candidate = startPos + (direction * i).ToIntVec3();
                    if (candidate.InBounds(map) && candidate.Walkable(map))
                    {
                        bestPos = candidate;
                    }
                    else
                    {
                        break;
                    }
                }

                if (bestPos != startPos)
                {
                    pawn.Position = bestPos;
                    pawn.Notify_Teleported(true, true);
                    FleckMaker.ThrowDustPuff(startPos, map, 1f);
                    FleckMaker.ThrowDustPuff(bestPos, map, 1f);
                    if (pawn.jobs != null)
                    {
                        pawn.jobs.StopAll(false, true);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE KnockbackTarget failed: {e}");
            }
        }

        /// <summary>
        /// Trigger a final explosion at the given position and destroy the projectile.
        /// </summary>
        public static void DoFinalExplosion(ProjectileCE projectile, IntVec3 pos)
        {
            try
            {
                float explosionRadius = projectile.def.projectile.explosionRadius;
                if (explosionRadius <= 0f)
                    return;

                Map map = projectile.Map;
                if (map == null)
                    return;

                GenExplosion.DoExplosion(
                    center: pos,
                    map: map,
                    radius: explosionRadius,
                    damType: projectile.def.projectile.damageDef,
                    instigator: projectile.launcher,
                    damAmount: Mathf.FloorToInt(projectile.DamageAmount),
                    armorPenetration: projectile.PenetrationAmount,
                    explosionSound: projectile.def.projectile.soundExplode,
                    weapon: projectile.equipmentDef,
                    projectile: projectile.def,
                    intendedTarget: null,
                    postExplosionSpawnThingDef: projectile.def.projectile.postExplosionSpawnThingDef,
                    postExplosionSpawnChance: projectile.def.projectile.postExplosionSpawnChance,
                    postExplosionSpawnThingCount: projectile.def.projectile.postExplosionSpawnThingCount,
                    postExplosionGasType: null,
                    preExplosionSpawnThingDef: projectile.def.projectile.preExplosionSpawnThingDef,
                    preExplosionSpawnChance: projectile.def.projectile.preExplosionSpawnChance,
                    preExplosionSpawnThingCount: projectile.def.projectile.preExplosionSpawnThingCount,
                    chanceToStartFire: projectile.def.projectile.explosionChanceToStartFire,
                    damageFalloff: projectile.def.projectile.explosionDamageFalloff);
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE DoFinalExplosion failed: {e}");
            }
        }

        /// <summary>
        /// Spawn visual effects (sparks, lightning, sound) at the given position.
        /// </summary>
        public static void DoSkimmingVisualsAt(
            Vector3 drawPos, Map map, Vector3 flightDir,
            bool playSound, bool forceLightning, bool spawnSparks,
            SkimmingState state)
        {
            try
            {
                if (map == null)
                    return;

                var ext = state.ext;
                int ticksGame = Find.TickManager.TicksGame;

                // Sound
                if (playSound && ext.skimHitSound != null && ticksGame > state.lastSoundTick)
                {
                    state.lastSoundTick = ticksGame;
                    IntVec3 soundCell = drawPos.ToIntVec3();
                    if (soundCell.InBounds(map))
                    {
                        SoundInfo info = SoundInfo.InMap(new TargetInfo(soundCell, map, false));
                        ext.skimHitSound.PlayOneShot(info);
                    }
                }

                // Sparks
                if (spawnSparks)
                {
                    FleckDef sparkFleck = ext.sparkFleckDef ?? FleckDefOf.MicroSparks;
                    float baseAngle = Vector3Utility.AngleFlat(flightDir);
                    int sparkCount = ext.sparkCountRange.RandomInRange;
                    float spreadAngle = ext.sparkSpreadAngle;

                    for (int i = 0; i < sparkCount; i++)
                    {
                        FleckCreationData fcd = FleckMaker.GetDataStatic(drawPos, map, sparkFleck, Rand.Range(3f, 6f));
                        fcd.velocityAngle = baseAngle + Rand.Range(-spreadAngle, spreadAngle);
                        fcd.velocitySpeed = ext.sparkSpeedRange.RandomInRange;
                        map.flecks.CreateFleck(fcd);
                    }
                }

                // Lightning trail (via reflection to avoid hard dependency on ECT DLL)
                if (ticksGame - state.lastLightningTick > 3)
                {
                    state.lastLightningTick = ticksGame;
                    if (map.weatherManager != null)
                    {
                        Vector3 start = drawPos;
                        start.y = AltitudeLayer.MetaOverlays.AltitudeFor();

                        var ctor = GetLightningTrailCtor();
                        if (ctor != null)
                        {
                            var lightningEvent = (WeatherEvent)ctor.Invoke(new object[]
                            {
                                map, start, flightDir,
                                ext.lightningLength,
                                ext.lightningDuration,
                                ext.lightningGrowthTicks,
                                ext.lightningVariance,
                                ext.lightningWidth
                            });
                            map.weatherManager.eventHandler.AddEvent(lightningEvent);
                        }

                        float glowSize = forceLightning ? 3f : 2.5f;
                        FleckMaker.ThrowLightningGlow(drawPos, map, glowSize);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE DoSkimmingVisualsAt failed: {e}");
            }
        }
    }

    /// <summary>
    /// Harmony Prefix on BulletCE.Impact — intercepts first impact to enter skimming mode.
    /// Uses Priority.High to run before other Impact patches (ProjectileEffects, MultiExplosion).
    /// </summary>
    [HarmonyPatch(typeof(BulletCE), nameof(BulletCE.Impact))]
    public static class Patch_BulletCE_Impact_Penetration
    {
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(BulletCE __instance, Thing hitThing)
        {
            try
            {
                var ext = __instance.def.GetModExtension<PenetrationCEExtension>();
                if (ext == null)
                    return true; // No extension, let original run

                int id = __instance.thingIDNumber;
                Map map = __instance.Map;

                if (map == null)
                    return true;

                // --- Already skimming ---
                if (SkimmingTracker.IsSkimming(id))
                {
                    var state = SkimmingTracker.Get(id);

                    if (hitThing != null && hitThing.def.passability == Traversability.Impassable)
                    {
                        // Hit impassable wall while skimming — final explosion
                        PenetrationHelper.DoSkimmingVisualsAt(
                            hitThing.DrawPos, map, state.direction,
                            true, true, true, state);
                        PenetrationHelper.DoFinalExplosion(__instance, hitThing.Position);
                        SkimmingTracker.Remove(id);
                        __instance.Destroy(DestroyMode.Vanish);
                        return false;
                    }

                    // Skimming but didn't hit impassable — ignore this Impact call,
                    // damage is handled in the Tick sweep
                    return false;
                }

                // --- First impact (not yet skimming) ---

                // Calculate flight direction
                Vector3 dir = (__instance.ExactPosition - new Vector3(__instance.origin.x, 0f, __instance.origin.y)).Yto0().normalized;
                if (dir == Vector3.zero)
                    dir = Vector3.forward;

                // Hit impassable on first impact — final explosion immediately, no skimming
                if (hitThing != null && hitThing.def.passability == Traversability.Impassable)
                {
                    PenetrationHelper.DoFinalExplosion(__instance, hitThing.Position);
                    // Let original Impact run for cleanup (it calls base.Impact which calls Destroy)
                    return true;
                }

                // Hit a non-impassable target or null (ground) — enter skimming mode
                if (hitThing != null)
                {
                    // Record first hit
                    var hitCounts = new Dictionary<int, int>();
                    if (hitThing.thingIDNumber >= 0)
                    {
                        hitCounts[hitThing.thingIDNumber] = 1;
                    }

                    // Apply damage to first target
                    PenetrationHelper.ApplySkimmingDamage(__instance, hitThing, dir, true, ext);

                    // Spawn visuals at hit point
                    var tempState = new SkimmingState
                    {
                        map = map,
                        direction = dir,
                        ext = ext,
                        lastSoundTick = -999,
                        lastLightningTick = Find.TickManager.TicksGame,
                    };
                    PenetrationHelper.DoSkimmingVisualsAt(
                        hitThing.DrawPos, map, dir,
                        true, true, true, tempState);

                    // Now set up the real skimming state
                    tempState.currentPos = __instance.ExactPosition;
                    tempState.prevPos = __instance.ExactPosition;
                    tempState.speed = __instance.def.projectile.speed / 60f; // speed is tiles/second, convert to tiles/tick
                    tempState.launcher = __instance.launcher;
                    tempState.equipmentDef = __instance.equipmentDef;
                    tempState.hitCounts = hitCounts;

                    SkimmingTracker.Register(id, tempState);
                }
                else
                {
                    // Hit ground / no target — still enter skimming
                    var state = new SkimmingState
                    {
                        map = map,
                        direction = dir,
                        currentPos = __instance.ExactPosition,
                        prevPos = __instance.ExactPosition,
                        speed = __instance.def.projectile.speed / 60f,
                        launcher = __instance.launcher,
                        equipmentDef = __instance.equipmentDef,
                        hitCounts = new Dictionary<int, int>(),
                        lastSoundTick = -999,
                        lastLightningTick = Find.TickManager.TicksGame,
                        ext = ext,
                    };

                    SkimmingTracker.Register(id, state);
                }

                // Play the explosive sound on entering skimming
                if (__instance.def.projectile.soundExplode != null)
                {
                    __instance.def.projectile.soundExplode.PlayOneShot(SoundInfo.InMap(new TargetInfo(__instance.Position, map, false)));
                }

                // Skip original Impact entirely — we don't want the projectile destroyed
                return false;
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE BulletCE.Impact Prefix failed: {e}");
                return true; // Fall through to original on error
            }
        }
    }

    /// <summary>
    /// Harmony Postfix on ProjectileCE.Tick — advances skimming projectiles and sweeps damage.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Tick))]
    public static class Patch_ProjectileCE_Tick_Penetration
    {
        public static void Postfix(ProjectileCE __instance)
        {
            try
            {
                int id = __instance.thingIDNumber;
                if (!SkimmingTracker.IsSkimming(id))
                    return;

                if (__instance.Destroyed)
                {
                    SkimmingTracker.Remove(id);
                    return;
                }

                var state = SkimmingTracker.Get(id);
                if (state == null)
                    return;

                Map map = __instance.Map;
                if (map == null)
                {
                    SkimmingTracker.Remove(id);
                    return;
                }

                var ext = state.ext;

                // Advance position
                state.prevPos = state.currentPos;
                state.currentPos += state.direction * state.speed;

                // Update the projectile's actual position to follow our skimming path
                Vector3 newPos = state.currentPos;
                newPos.y = 0f;

                // Check map bounds
                IntVec3 newCell = newPos.ToIntVec3();
                if (!newCell.InBounds(map))
                {
                    SkimmingTracker.Remove(id);
                    __instance.Destroy(DestroyMode.Vanish);
                    return;
                }

                // Update projectile position
                __instance.ExactPosition = newPos;

                // Sweep damage between prevPos and currentPos
                DoSkimmingDamage(__instance, state.prevPos, state.currentPos, state, map);
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE Tick Postfix failed: {e}");
            }
        }

        private static void DoSkimmingDamage(
            ProjectileCE projectile,
            Vector3 start, Vector3 end,
            SkimmingState state, Map map)
        {
            var ext = state.ext;
            Vector3 delta = end - start;
            Vector3 dir = state.direction;
            float totalDist = delta.magnitude;
            if (totalDist < 0.1f)
                totalDist = 0.1f;

            float stepSize = 0.5f;
            int steps = Mathf.CeilToInt(totalDist / stepSize);

            state.visitedCells.Clear();
            var tmpThings = new List<Thing>();

            for (int i = 0; i <= steps; i++)
            {
                float dist = i * stepSize;
                if (dist > totalDist) dist = totalDist;

                Vector3 samplePos = start + dir * dist;
                IntVec3 centerCell = samplePos.ToIntVec3();

                foreach (IntVec3 cell in GenRadial.RadialCellsAround(centerCell, ext.hitWidthRadius, true))
                {
                    if (!cell.InBounds(map) || !state.visitedCells.Add(cell))
                        continue;

                    List<Thing> thingList = cell.GetThingList(map);
                    tmpThings.Clear();
                    tmpThings.AddRange(thingList);

                    for (int j = tmpThings.Count - 1; j >= 0; j--)
                    {
                        Thing thing = tmpThings[j];
                        if (thing.Destroyed)
                            continue;
                        if (!(thing is Pawn) && !(thing is Building))
                            continue;

                        int hitCount = 0;
                        if (state.hitCounts.TryGetValue(thing.thingIDNumber, out int existing))
                            hitCount = existing;

                        if (hitCount >= ext.maxHitsPerTarget)
                            continue;

                        // Check for impassable wall
                        if (thing.def.passability == Traversability.Impassable)
                        {
                            float distToThing = (thing.Position.ToVector3Shifted() - samplePos).Yto0().magnitude;
                            if (distToThing < 0.9f)
                            {
                                PenetrationHelper.DoSkimmingVisualsAt(
                                    thing.DrawPos, map, dir,
                                    true, true, true, state);
                                PenetrationHelper.DoFinalExplosion(projectile, thing.Position);
                                SkimmingTracker.Remove(projectile.thingIDNumber);
                                projectile.Destroy(DestroyMode.Vanish);
                                return;
                            }
                        }
                        else
                        {
                            // Damage non-impassable target
                            state.hitCounts[thing.thingIDNumber] = hitCount + 1;
                            bool isFirstHit = hitCount == 0;

                            PenetrationHelper.DoSkimmingVisualsAt(
                                thing.DrawPos, map, dir,
                                true, true, true, state);
                            PenetrationHelper.ApplySkimmingDamage(
                                projectile, thing, dir, isFirstHit, ext);
                        }
                    }
                }

                tmpThings.Clear();

                if (projectile.Destroyed)
                    break;
            }
        }
    }

    /// <summary>
    /// Harmony Prefix on ProjectileCE.Destroy — cleans up SkimmingTracker state.
    /// Note: Thing.Destroy is the actual method since ProjectileCE doesn't override it.
    /// We patch ProjectileCE specifically to narrow the scope.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Destroy))]
    public static class Patch_ProjectileCE_Destroy_Penetration
    {
        public static void Prefix(ProjectileCE __instance)
        {
            try
            {
                SkimmingTracker.Remove(__instance.thingIDNumber);
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE Destroy cleanup failed: {e}");
            }
        }
    }
}
