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

        public static bool IsSkimming(int id) => States.ContainsKey(id);

        public static SkimmingState Get(int id)
        {
            States.TryGetValue(id, out var state);
            return state;
        }

        public static void Register(int id, SkimmingState state) => States[id] = state;
        public static void Remove(int id) => States.Remove(id);
    }

    /// <summary>
    /// Shared helper methods for skimming penetration logic.
    /// </summary>
    public static class PenetrationHelper
    {
        private static bool _lightningTypeResolved;
        private static ConstructorInfo _lightningCtor;

        private static ConstructorInfo GetLightningTrailCtor()
        {
            if (_lightningTypeResolved)
                return _lightningCtor;

            _lightningTypeResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = asm.GetType("ECT.WeatherEvent_LightningTrail", false);
                    if (type != null)
                    {
                        _lightningCtor = type.GetConstructor(new Type[]
                        {
                            typeof(Map), typeof(Vector3), typeof(Vector3),
                            typeof(float), typeof(int), typeof(int), typeof(float), typeof(float)
                        });
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
        /// Resets the CE projectile's trajectory to continue skimming in state.direction.
        /// This mirrors what ECT's StartSkimming() does for vanilla Bullet.
        /// Crucially, this also resets landed=false so CE's Tick continues to run.
        /// </summary>
        public static void ResetSkimmingTrajectory(ProjectileCE proj, SkimmingState state)
        {
            if (proj == null || proj.Destroyed) return;
            Map map = proj.Map;
            if (map == null) return;

            var dir = state.direction; // horizontal, y=0, normalized
            int mapSize = map.Size.x + map.Size.z;

            // Reset origin to current position
            proj.origin = new Vector2(proj.ExactPosition.x, proj.ExactPosition.z);

            // Destination: far away in the skimming direction
            proj.Destination = proj.origin + new Vector2(dir.x, dir.z) * mapSize;

            // How many ticks to travel mapSize cells at projectile speed
            float speedCellsPerTick = proj.def.projectile.speed / GenTicks.TicksPerRealSecond;
            if (speedCellsPerTick < 0.01f) speedCellsPerTick = 0.01f;
            float newTicks = mapSize / speedCellsPerTick;

            proj.startingTicksToImpact = newTicks;
            proj.ticksToImpact = Mathf.CeilToInt(newTicks);
            proj.FlightTicks = 0;

            // Flat trajectory at small positive height — prevents immediate ShouldCollideWithSomething re-trigger
            // (LerpedTrajectoryWorker: height = shotHeight - gravity*t^2/2; at shotHeight=0.2 this is ~27 ticks)
            proj.shotHeight = 0.2f;
            proj.shotAngle = 0f;

            // Essential: clear the landed flag so CE's Tick() doesn't early-return
            proj.landed = false;

            // Clear LerpedTrajectoryWorker's position cache
            if (proj.cachedPredictedPositions != null)
                proj.cachedPredictedPositions.Clear();
        }

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
                    damageDef, damageAmount, penetration, angle,
                    launcher, null, equipDef, instigatorGuilty: true);

                target.TakeDamage(dinfo);

                if (target is Pawn pawn && !pawn.Dead && !pawn.Destroyed)
                {
                    if (ext.stunDuration > 0)
                    {
                        float stunAmount = ext.stunDuration / 30f;
                        if (stunAmount < 0.1f) stunAmount = 0.5f;
                        var stunInfo = new DamageInfo(
                            DamageDefOf.Stun, stunAmount, 999f, angle,
                            launcher, null, equipDef, instigatorGuilty: true);
                        pawn.TakeDamage(stunInfo);
                    }

                    if (isFirstHit && ext.knockbackDistance > 0f)
                        KnockbackTarget(pawn, flightDir, ext.knockbackDistance, projectile.Map);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE ApplySkimmingDamage failed: {e}");
            }
        }

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
                        bestPos = candidate;
                    else
                        break;
                }

                if (bestPos != startPos)
                {
                    pawn.Position = bestPos;
                    pawn.Notify_Teleported(true, true);
                    FleckMaker.ThrowDustPuff(startPos, map, 1f);
                    FleckMaker.ThrowDustPuff(bestPos, map, 1f);
                    pawn.jobs?.StopAll(false, true);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE KnockbackTarget failed: {e}");
            }
        }

        public static void DoFinalExplosion(ProjectileCE projectile, IntVec3 pos)
        {
            try
            {
                float explosionRadius = projectile.def.projectile.explosionRadius;
                if (explosionRadius <= 0f) return;

                Map map = projectile.Map;
                if (map == null) return;

                GenExplosion.DoExplosion(
                    center: pos, map: map, radius: explosionRadius,
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

        public static void DoSkimmingVisualsAt(
            Vector3 drawPos, Map map, Vector3 flightDir,
            bool playSound, bool forceLightning, bool spawnSparks,
            SkimmingState state)
        {
            try
            {
                if (map == null) return;
                var ext = state.ext;
                int ticksGame = Find.TickManager.TicksGame;

                if (playSound && ext.skimHitSound != null && ticksGame > state.lastSoundTick)
                {
                    state.lastSoundTick = ticksGame;
                    IntVec3 soundCell = drawPos.ToIntVec3();
                    if (soundCell.InBounds(map))
                        ext.skimHitSound.PlayOneShot(SoundInfo.InMap(new TargetInfo(soundCell, map, false)));
                }

                if (spawnSparks)
                {
                    FleckDef sparkFleck = ext.sparkFleckDef ?? FleckDefOf.MicroSparks;
                    float baseAngle = Vector3Utility.AngleFlat(flightDir);
                    int sparkCount = ext.sparkCountRange.RandomInRange;
                    for (int i = 0; i < sparkCount; i++)
                    {
                        FleckCreationData fcd = FleckMaker.GetDataStatic(drawPos, map, sparkFleck, Rand.Range(3f, 6f));
                        fcd.velocityAngle = baseAngle + Rand.Range(-ext.sparkSpreadAngle, ext.sparkSpreadAngle);
                        fcd.velocitySpeed = ext.sparkSpeedRange.RandomInRange;
                        map.flecks.CreateFleck(fcd);
                    }
                }

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
                                ext.lightningLength, ext.lightningDuration,
                                ext.lightningGrowthTicks, ext.lightningVariance, ext.lightningWidth
                            });
                            map.weatherManager.eventHandler.AddEvent(lightningEvent);
                        }

                        FleckMaker.ThrowLightningGlow(drawPos, map, forceLightning ? 3f : 2.5f);
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
    /// Also handles skimming-mode impacts (wall = final explosion, pawn = reset trajectory).
    /// CRITICAL FIX: resets landed=false and calls ResetSkimmingTrajectory so CE's Tick
    /// continues to run the ballistic system instead of early-returning.
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
                    return true;

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

                    // Non-impassable or null: reset trajectory so CE keeps moving the projectile.
                    // Damage is swept by Patch_ProjectileCE_Tick_Penetration.Postfix.
                    PenetrationHelper.ResetSkimmingTrajectory(__instance, state);
                    return false;
                }

                // --- First impact ---

                // Calculate flight direction from origin → current position
                Vector3 dir = (__instance.ExactPosition - new Vector3(__instance.origin.x, 0f, __instance.origin.y)).Yto0().normalized;
                if (dir == Vector3.zero)
                    dir = Vector3.forward;

                // First hit on impassable wall: let CE handle it normally (explosion + destroy)
                if (hitThing != null && hitThing.def.passability == Traversability.Impassable)
                    return true;

                // First hit on pawn/passable-building OR ground (hitThing==null): enter skimming
                var newState = new SkimmingState
                {
                    map = map,
                    direction = dir,
                    launcher = __instance.launcher,
                    equipmentDef = __instance.equipmentDef,
                    lastSoundTick = -999,
                    lastLightningTick = Find.TickManager.TicksGame,
                    ext = ext,
                };

                if (hitThing != null)
                {
                    // Apply damage to the first target, record hit
                    newState.hitCounts[hitThing.thingIDNumber] = 1;
                    PenetrationHelper.ApplySkimmingDamage(__instance, hitThing, dir, true, ext);

                    var tempState = new SkimmingState
                    {
                        map = map, direction = dir, ext = ext,
                        lastSoundTick = -999, lastLightningTick = Find.TickManager.TicksGame
                    };
                    PenetrationHelper.DoSkimmingVisualsAt(
                        hitThing.DrawPos, map, dir, true, true, true, tempState);
                }

                // Play the entry sound
                if (__instance.def.projectile.soundExplode != null)
                {
                    __instance.def.projectile.soundExplode.PlayOneShot(
                        SoundInfo.InMap(new TargetInfo(__instance.Position, map, false)));
                }

                SkimmingTracker.Register(id, newState);

                // Reset CE trajectory so the projectile keeps flying in the skimming direction.
                // This also clears landed=false which is the core fix.
                PenetrationHelper.ResetSkimmingTrajectory(__instance, newState);
                return false;
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE BulletCE.Impact Prefix failed: {e}");
                return true;
            }
        }
    }

    /// <summary>
    /// Harmony Prefix on ProjectileCE.ImpactSomething — when already skimming, intercept
    /// end-of-trajectory / ground-hit triggers and reset trajectory instead of destroying.
    /// This mirrors ECT's override of ImpactSomething() in Projectile_HeavyArmourPiercer.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileCE), "ImpactSomething")]
    public static class Patch_ProjectileCE_ImpactSomething_Penetration
    {
        public static bool Prefix(ProjectileCE __instance)
        {
            try
            {
                int id = __instance.thingIDNumber;
                if (!SkimmingTracker.IsSkimming(id))
                    return true;

                if (__instance.Destroyed)
                {
                    SkimmingTracker.Remove(id);
                    return true;
                }

                Map map = __instance.Map;
                if (map == null)
                {
                    SkimmingTracker.Remove(id);
                    return true;
                }

                // If already out of bounds, destroy cleanly
                if (!__instance.Position.InBounds(map))
                {
                    SkimmingTracker.Remove(id);
                    __instance.Destroy(DestroyMode.Vanish);
                    return false;
                }

                var state = SkimmingTracker.Get(id);
                PenetrationHelper.ResetSkimmingTrajectory(__instance, state);
                return false; // Skip ImpactSomething — trajectory reset handles continuation
            }
            catch (Exception e)
            {
                Log.Warning($"[UFHeavyIndustries_CE] PenetrationCE ImpactSomething Prefix failed: {e}");
                return true;
            }
        }
    }

    /// <summary>
    /// Harmony Postfix on ProjectileCE.Tick — sweeps damage along the path CE moved the projectile.
    /// Uses LastPos (set by CE before movement) and ExactPosition (set by CE after movement).
    /// No manual position advancement needed — CE's own trajectory system handles movement.
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

                Map map = __instance.Map;
                if (map == null)
                {
                    SkimmingTracker.Remove(id);
                    return;
                }

                var state = SkimmingTracker.Get(id);

                // Use the positions CE computed this tick
                Vector3 sweepFrom = __instance.LastPos;
                Vector3 sweepTo = __instance.ExactPosition;

                // Skip trivial movement (landed=true tick where CE didn't move)
                if ((sweepTo - sweepFrom).sqrMagnitude < 0.001f)
                    return;

                DoSkimmingDamage(__instance, sweepFrom, sweepTo, state, map);
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
            Vector3 dir = state.direction;
            Vector3 delta = end - start;
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

                    tmpThings.Clear();
                    tmpThings.AddRange(cell.GetThingList(map));

                    for (int j = tmpThings.Count - 1; j >= 0; j--)
                    {
                        Thing thing = tmpThings[j];
                        if (thing.Destroyed)
                            continue;
                        if (!(thing is Pawn) && !(thing is Building))
                            continue;

                        int hitCount = 0;
                        state.hitCounts.TryGetValue(thing.thingIDNumber, out hitCount);

                        if (hitCount >= ext.maxHitsPerTarget)
                            continue;

                        if (thing.def.passability == Traversability.Impassable)
                        {
                            float distToThing = (thing.Position.ToVector3Shifted() - samplePos).Yto0().magnitude;
                            if (distToThing < 0.9f)
                            {
                                PenetrationHelper.DoSkimmingVisualsAt(
                                    thing.DrawPos, map, dir, true, true, true, state);
                                PenetrationHelper.DoFinalExplosion(projectile, thing.Position);
                                SkimmingTracker.Remove(projectile.thingIDNumber);
                                projectile.Destroy(DestroyMode.Vanish);
                                return;
                            }
                        }
                        else
                        {
                            state.hitCounts[thing.thingIDNumber] = hitCount + 1;
                            PenetrationHelper.DoSkimmingVisualsAt(
                                thing.DrawPos, map, dir, true, true, true, state);
                            PenetrationHelper.ApplySkimmingDamage(
                                projectile, thing, dir, hitCount == 0, ext);
                        }
                    }
                }

                if (projectile.Destroyed)
                    break;
            }
        }
    }
}
