# UF Heavy Industries — CE Compatibility Patch

Combat Extended compatibility patch for [UF Heavy Industries](https://steamcommunity.com/sharedfiles/filedetails/?id=3555662938) (KindSeal.LOL).

## Requirements

- [Combat Extended](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
- [UF Heavy Industries](https://steamcommunity.com/sharedfiles/filedetails/?id=3555662938)
- [SRALib](https://steamcommunity.com/sharedfiles/filedetails/?id=3565275325)

## Load Order

```
Harmony
Core
Royalty / Biotech / ...
SRALib
UF Heavy Industries
Combat Extended
UF Heavy Industries CE Patch  <-- this mod
```

## What This Patch Does

- Converts all handheld weapons to CE ballistics with custom ammo systems
- Adds CE melee stats (ToolCE, armor penetration) to all melee weapons
- Preserves original mod visual effects (laser beams, slash arcs, impact effecters)
- Preserves special weapon mechanics (tracking, penetration, multi-explosion, AOE melee)
- Custom Harmony patches for laser beam rendering and projectile effects

## Coverage

| Category | Count | Status |
|----------|-------|--------|
| Ranged weapons (standard) | 17 | CE ammo system |
| Ranged weapons (special) | 8 | CE ammo + DefModExtension mechanics |
| Ranged weapons (tracking/guided) | 4 | Vanilla ballistics, CE stats |
| Transform weapons | 3 | CE projectiles, no ammo |
| Melee weapons | 8 | ToolCE + AOE preserved |

## Known Limitations

- Tracking/guided weapons (KT_WLL, KT_101Rifle, KT_SniperII, KT_Analnail) keep original SRA/ECT projectiles and do not use CE ballistic physics
- Transform weapons (Seal series) do not use the CE ammo system by design
- Turrets, armor, mechanoids, and defense buildings are not yet patched
