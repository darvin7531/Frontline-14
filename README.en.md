[Русский](./README.md) | [English](./README.en.md)

# Frontline 14

**Frontline 14** is an independent game project built on Space Station 14 / RobustToolbox, focused on a long-running war between two factions in one shared physical world.

The project keeps the technical foundation of SS14 while moving away from classic station gameplay and short rounds toward territorial warfare, logistics, industry, research, and frontline combat.

> **Status:** early development. The repository already contains a dedicated PersistentWar foundation, but it is still far from a complete playable release. A significant amount of upstream SS14 code remains in the codebase and will gradually be reused, disabled, replaced, or removed where appropriate.

## What kind of game is this?

Frontline 14 is built around a single long-running war between two opposing factions.

Core principles:

- one shared server world with no mandatory roleplay;
- two factions, with players choosing a side for each war;
- territory capture through physical objects in the world;
- resource extraction, refining, and production;
- physical logistics: resources, crates, storage, and vehicles exist in the world instead of an abstract global inventory;
- faction-wide technological progression;
- technical server restarts should not mean the end of the war;
- a new campaign begins only after the previous war has ended.

Roleplay is allowed, but it is not required and should not provide a mechanical advantage. Frontline 14 is not built around station jobs, mandatory command hierarchy, or the traditional SS14 roleplay loop.

## Current state

The current `master` branch already contains the core war framework:

- a dedicated `PersistentWar` game mode;
- persistent `WarId` and campaign state across technical restarts;
- two data-driven factions and per-war faction selection;
- a custom deploy / death / respawn flow;
- five territories with server-authoritative ownership;
- Town Halls as physical territory-control objectives;
- Town Hall destruction, ruins, rebuilding, and capture by the opposing faction;
- victory at 4 out of 5 controlled territories;
- a dedicated stationless ground test map;
- breathable ground atmosphere and a day/night cycle;
- day/night phase continuity based on the war start time;
- validation of required PersistentWar map invariants;
- admin/debug commands for war state, territories, factions, and time-of-day testing;
- a working `newwar` flow that creates a new `WarId` and reloads a clean initial map state.

**Resource Extraction v1** is currently under development: mapper-defined resource fields, finite reserves, physical Raw Iron, and rare Raw Technology Material.

The next major development stages are:

1. Refinery;
2. Factory and physical production crates;
3. faction research and the Engineering Center;
4. stockpiles and logistics;
5. full physical-world persistence.

### Important current limitation

There is no full **world persistence** yet.

Campaign metadata such as `WarId`, war state, and player faction membership can survive a technical restart, but the physical map is not yet a fully persisted world. Until the dedicated world-persistence layer is implemented, the map is recreated from its YAML source when reloaded.

## Source code base

Frontline 14 started as an independent continuation of a specific snapshot of the Russian-language **Space Syndicate / Corvax** codebase.

Base upstream revision:

- **Space Syndicate / Corvax:** [`91cf10ac16807d3d168f96808c3baba5896d6a36`](https://github.com/space-syndicate/space-station-14/commit/91cf10ac16807d3d168f96808c3baba5896d6a36)
- upstream commit date: **September 14, 2026**
- upstream commit message: **`Corvax maps tweaks (#3729)`**
- **RobustToolbox** revision in that snapshot: [`edf061e7450a4074f173e3000bf1552b6f54082f`](https://github.com/space-wizards/RobustToolbox/commit/edf061e7450a4074f173e3000bf1552b6f54082f)

Upstream Git history has been preserved, so code ancestry can still be traced back through Corvax / Space Syndicate and Space Station 14.

Primary upstream projects:

- [Space Syndicate / Corvax](https://github.com/space-syndicate/space-station-14)
- [Space Station 14](https://github.com/space-wizards/space-station-14)
- [RobustToolbox](https://github.com/space-wizards/RobustToolbox)

Compatible implementations may also be adapted from other open-source projects such as [RMC-14](https://github.com/RMC-14/RMC-14), while preserving the applicable licenses, copyright notices, and attribution requirements.

The primary project language is **C#**.

## What we keep from SS14

Frontline 14 does not try to rewrite the entire engine from scratch. Existing SS14 / RobustToolbox systems are reused where they fit the project:

- ECS and networking / PVS;
- maps, grids, and physics;
- entities, containers, inventory, and hands;
- damage and medical foundations;
- DoAfter and interaction systems;
- UI and localization;
- prototype/data-driven infrastructure;
- administration and integration-test infrastructure.

At the same time, the standard station round loop, emergency shuttle as a game-ending mechanism, station jobs, antagonists, standard objectives, random station events, and other systems that do not fit Frontline 14 are gradually being removed from the main gameplay flow.

## Building

The project currently keeps the upstream SS14 build infrastructure.

Basic setup:

1. clone the repository;
2. run `RUN_THIS.py` to initialize the required components and submodules;
3. build the project using the usual .NET tooling or an IDE.

For general RobustToolbox and SS14 infrastructure topics, the [official Space Station 14 documentation](https://docs.spacestation14.io/) is still relevant.

## Licensing

Frontline 14 uses split licensing.

Code and assets originating from Space Station 14, Space Syndicate / Corvax, RobustToolbox, RMC-14, or other third-party sources remain governed by their original licenses. The original MIT license text for the base code is retained in [`LICENSE.TXT`](./LICENSE.TXT).

Known third-party sources and attribution rules are documented in:

**[`THIRD_PARTY_LICENSES.md`](./THIRD_PARTY_LICENSES.md)**

Original Frontline 14 code, systems, maps, documentation, design, UI, and assets are governed by the separate project license:

**[`LICENSE-FRONTLINE14.md`](./LICENSE-FRONTLINE14.md)**

That license does not override or restrict rights already granted by the licenses of upstream code or third-party assets.

## Contributing

Contribution rules are documented in:

**[`CONTRIBUTING.md`](./CONTRIBUTING.md)**

Original contributions use:

**[`CLA.md`](./CLA.md)**

---

**Frontline 14** is a working project title.
