# Third-party assets — Frontline Planetary Asset Pack v1

RMC-14 source pinned at `46bc517117f195f0b6a3f0de629fbe110e21ff6f`. Only the following whitelist is imported.

All imported graphics are **CC-BY-SA-3.0**. The art license does not license game code. PNG bytes are unchanged; RSI metadata is reduced to used states. These selected/repackaged graphics remain CC-BY-SA-3.0; downstream adaptations must retain attribution and the same or a compatible ShareAlike license. See the bundled full legal code and upstream notices under `Resources/Textures/Frontline/Attribution/notices/`.

CM-SS13 notices designate its active development team as the author unless otherwise stated. No individual original artists are inferred. tgstation attribution is preserved verbatim. Barrel metadata names @IceNoobXD and drazamuffin for other colors; those modified colors are not imported.

The machine-readable `Resources/Textures/Frontline/Attribution/manifest.json` records exact file hashes, pinned source links, untouched raw metadata, source grants/notices and blocked candidates. Raw mixed-source tile metadata is evidence only: it does **not** authorize excluded ice/snow exports.

## `Tiles/planet`
- Selected states: `dirt`, `grass1`, `sand`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Tiles/planet/meta.json
- Original notice (verbatim): Taken from cmss13 at https://github.com/cmss13-devs/cmss13/blob/718b6531ec71e34f671ab37c269469e1638cc2ec/icons/turf/ground_map.dmi, ice from https://github.com/cmss13-devs/cmss13/blob/8d0e113dfafcc590812ff4d55bc002b9b93ece67/icons/turf/ice.dmi, snow from https://github.com/cmss13-devs/cmss13/blob/d0648b581f5f063a3191cf954e9806f09d2c8763/icons/turf/floors/snow2.dmi

## `Tiles/asphalt`
- Selected states: `asphalt`, `cement1`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Tiles/asphalt/meta.json
- Original notice (verbatim): Taken from cmss13 at https://github.com/cmss13-devs/cmss13/blob/09a5191fb11aab8ddffe3f9be94292b53e4d96f6/icons/turf/floors/asphalt.dmi

## `Tiles/planet/browndirt_road`
- Selected states: `browndirt_road`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Tiles/planet/browndirt_road/meta.json
- Original notice (verbatim): Taken from cmss13 at https://github.com/cmss13-devs/cmss13/blob/48e570bd697f2476e28d89cd255d0539a5228228/icons/turf/floors/ground_map_dirt.dmi

## `Structures/Flora/Grass/tyrargo_wood_flora.rsi`
- Selected states: `stick_01`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Structures/Flora/Grass/tyrargo_wood_flora.rsi/meta.json
- Original notice (verbatim): Taken from cmss13 at https://github.com/cmss13-devs/cmss13/blob/2f4c415e5681333e6de474f1f027087101452d31/icons/obj/structures/props/natural/vegetation/tyrargo_wood_flora.dmi

## `Structures/Flora/Trees/flora_trees_tyrargo.rsi`
- Selected states: `treetyrargo01`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Structures/Flora/Trees/flora_trees_tyrargo.rsi/meta.json
- Original notice (verbatim): Taken from tgstation at commit https://github.com/tgstation/tgstation/blob/d388dee8b7b6d854f6f0d844988552acf5962b1f/icons/obj/flora/jungletrees.dmi

## `Structures/Walls/Barricades/barricade.rsi`
- Selected states: `sandbag`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Structures/Walls/Barricades/barricade.rsi/meta.json
- Original notice (verbatim): Taken from cmss13 at https://github.com/cmss13-devs/cmss13/blob/2f4c415e5681333e6de474f1f027087101452d31/icons/obj/structures/barricades.dmi

## `Structures/barrels.rsi`
- Selected states: `barrel_blue`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Structures/barrels.rsi/meta.json
- Original notice (verbatim): https://github.com/cmss13-devs/cmss13/blob/7cb618c69b75873f3ce893022fe08d1233b3152d/icons/obj/structures/crates.dmi, @IceNoobXD on Github modified barrel_black and barrel_purewhite, barrel_yellow modified by drazamuffin

## `Structures/Storage/Crates/supply.rsi`
- Selected states: `icon`.
- License: CC-BY-SA-3.0; source metadata: https://raw.githubusercontent.com/RMC-14/RMC-14/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Structures/Storage/Crates/supply.rsi/meta.json
- Original notice (verbatim): Taken from cmss13 at https://github.com/cmss13-devs/cmss13/blob/7cb618c69b75873f3ce893022fe08d1233b3152d/icons/obj/structures/crates.dmi

## Explicit omissions
Only six base tile strips, two temperate flora states, one sandbag state, one barrel color and the supply-crate icon are used. Decorations are non-colliding graphics, not combat cover or harvestable plants. Crate art reskins native sealed Frontline crates, not RMC storage.

Blocked candidates (including `van.rsi`) remain excluded until their conflicting, missing or mutable provenance is resolved. Their exact reasons are retained in the manifest; no NC or uncertain files are imported. Approved but unused variants, second biomes, road decals, truck art, mechanics and maps are not part of this pack.
