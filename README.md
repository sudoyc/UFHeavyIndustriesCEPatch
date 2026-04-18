# UF Heavy Industries — CE Compatibility Patch

Combat Extended compatibility patch for [UF Heavy Industries](https://steamcommunity.com/sharedfiles/filedetails/?id=3555662938).

## Requirements

- [UF Heavy Industries](https://steamcommunity.com/sharedfiles/filedetails/?id=3555662938)
- [Combat Extended](https://steamcommunity.com/sharedfiles/filedetails/?id=2890901044)
- [SRALib](https://steamcommunity.com/sharedfiles/filedetails/?id=3565275325) should be installed alongside this patch. Several supported UF Heavy Industries weapons still rely on behavior provided by SRALib.

## Load Order

Load this patch after SRALib, UF Heavy Industries, and Combat Extended.

Recommended order:

```text
Harmony
Core
Royalty / Ideology / Biotech / Anomaly / Odyssey
SRALib
UF Heavy Industries
Combat Extended
UF Heavy Industries - CE Patch
```

## Current Coverage

- Standard ranged weapons currently covered by this patch use CE handling, including CE ammo where that weapon has been fully converted.
- Special weapons currently fall into three user-visible groups:
  - Fully converted special weapons use CE projectiles and the CE ammo system.
  - Partially converted special weapons keep their original firing behavior and projectile logic, but receive CE-oriented weapon stats.
  - Transforming weapon forms use CE projectiles without switching to the CE ammo system.
- Patched melee weapons use CE melee stats while preserving special melee effects where those effects were explicitly carried over.
- Misc compatibility work currently includes a fix for `KT_Powerarmor` and a fix for trader shell stock generation.

## Compatibility Approach

- Convert weapons to CE where that can be done cleanly without stripping out the original mod's intended weapon behavior.
- Keep special weapons on different compatibility paths when a full CE conversion would break their distinctive mechanics.
- Use the included assembly only where extra support is needed to preserve special effects or weapon behavior.

## Known Limitations

- Not every handheld weapon currently uses CE ballistics and CE ammo.
- Some special handheld weapons intentionally retain their original upstream projectile or verb behavior and only receive CE-oriented stats.
- Turrets, armor, mechanoids, and defense buildings are not yet comprehensively converted.
- If required upstream behavior or classes are missing or changed, some supported weapons may lose effects or fail to behave as expected. See `docs/reference/dependency-and-runtime-assumptions.md`.

## Build And Packaging

- C# sources live in `Source/`.
- Project file: `Source/UFHeavyIndustries_CE.csproj`.
- Built assembly output: `Assemblies/UFHeavyIndustries_CE.dll`.
- Repository build helper: `build.sh`.
