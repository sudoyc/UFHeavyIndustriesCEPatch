using Verse;
using CombatExtended;

namespace UFHeavyIndustries_CE
{
    public class ProjectileCE_Explosive_WithBeam : ProjectileCE_Explosive
    {
        public override void Impact(Thing hitThing)
        {
            // Must capture Map/Position/launcher BEFORE base.Impact — Destroy() nulls Map
            Map map = base.Map;
            IntVec3 pos = base.Position;
            Thing launcherRef = launcher;

            base.Impact(hitThing);

            if (map == null) return;

            var ext = def.GetModExtension<BeamVisualExtension>();
            if (ext == null) return;

            // Spawn beam mote connecting launcher to impact point
            if (ext.beamMoteDef != null && launcherRef != null && launcherRef.Spawned)
            {
                var mote = (MoteDualAttached)ThingMaker.MakeThing(ext.beamMoteDef);
                mote.Attach(launcherRef, new TargetInfo(pos, map));
                GenSpawn.Spawn(mote, launcherRef.Position, map);
            }

            // Spawn impact flash effect
            if (ext.impactEffecter != null)
            {
                var effecter = ext.impactEffecter.Spawn(pos, map);
                effecter.Trigger(new TargetInfo(pos, map), null);
                effecter.Cleanup();
            }
        }
    }
}
