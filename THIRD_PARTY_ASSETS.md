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

Candidates blocked by the planetary-pack audit remain excluded until their conflicting, missing or mutable provenance is resolved. The truck states cleared by the subsequent historical audit are documented separately below. Their exact reasons are retained in the manifest; no NC or uncertain files are imported. Approved but unused variants, second biomes, road decals, truck art, mechanics and maps are not part of this pack.

---

## Frontline logistics truck

`Resources/Textures/_Frontline/Vehicles/logistics_truck.rsi` contains only
`truck_base` and static `wheels_intact`, four directions, 96×96 pixels per frame.
`wheels_intact` selects the first frame of each direction from the separately
cleared RMC `wheels_0` state (source SHA-256
`9e20be63c183249dedf350189a4b895296e24ba6248e630a1ef9e305bc8bbeb2`).
The 32-frame upstream PNG is preserved as `upstream-wheels_0.png`; only frame
selection/repacking changes, not visible pixels. Its complete ordered visible
frames match the same independently licensed historical CM DMI. The previous
`wheels_1` overlay is no longer used; static intact wheels avoid idle animation.

- **Attribution:** CM-SS13 active development team (the source project's designated attribution; no individual artist inferred). RMC-14 supplied the RSI packaging.
- **License:** [Creative Commons Attribution-ShareAlike 3.0 Unported](https://creativecommons.org/licenses/by-sa/3.0/). The preserved legal text is in `Resources/Textures/_Frontline/Attribution/logistics-truck/LICENSE-CC-BY-SA-3.0.txt`. These assets and adaptations remain under CC-BY-SA-3.0, independently of software licensing.
- **Immediate source:** [RMC-14/RMC-14 van.rsi](https://github.com/RMC-14/RMC-14/tree/46bc517117f195f0b6a3f0de629fbe110e21ff6f/Resources/Textures/_RMC14/Structures/Vehicles/van.rsi), pinned revision `46bc517117f195f0b6a3f0de629fbe110e21ff6f`; first imported in `a53a8ec5fc45a6b8d1c2995a10c996f00258c1e8`.
- **Independent historical provenance:** [CM-SS13 icons/obj/vehicles/van.dmi](https://github.com/cmss13-devs/cmss13/blob/30057db45851612e733a0ad759461f81ebb65360/icons/obj/vehicles/van.dmi), Git blob `caef206e03c2ccdd33d52f4f1cc2eb99b5759420`, also unchanged at import-era revision `12a894facff4ac60d0dcc11c854cf7a283ac8786`. The historical README expressly grants icon assets CC-BY-SA-3.0 and designates the CM-SS13 active development team as author. A byte-identical copy of that README is preserved beside the legal text.
- **Provenance limit:** This is an independently verified historical visible-pixel-equivalent source, not a claim that the exact CM revision downloaded by the RMC importer is known. RSI repackaging/transparent RGB differences exist between DMI and RMC; visible RGBA frames match. Frontline retains `truck_base` unchanged and selects only the first static directional frames of `wheels_0` for `wheels_intact`. The raw RMC PNG is preserved; no visible pixels are changed or scaled.

The upstream metadata's literal attribution is preserved unchanged in
`Resources/Textures/_Frontline/Attribution/logistics-truck/upstream-meta.json`:

> Taken from cmss13 at https://github.com/cmss13-devs/cmss13/tree/master/icons/obj/vehicles/van

That literal directory is missing. It is **not** presented as a working source
or silently repaired; the independent historical provenance above supplies
the source evidence. `manifest.json` records exact source URLs, revisions,
SHA-256 and Git blob hashes for both PNGs and preserved raw evidence.
