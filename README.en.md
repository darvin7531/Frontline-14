<div align="center">

# FRONTLINE 14

**One war. Two factions. One shared world.**

Long-running territorial warfare built around resource extraction, industry, physical logistics, and combat — powered by **Space Station 14 / RobustToolbox**.

[Русский](./README.md) · [**English**](./README.en.md)

![Development](https://img.shields.io/badge/status-in%20development-D89B45?style=flat-square)
![C#](https://img.shields.io/badge/C%23-gameplay-7952B3?style=flat-square)
![RobustToolbox](https://img.shields.io/badge/engine-RobustToolbox-397E8D?style=flat-square)

</div>

---

> [!IMPORTANT]
> **Frontline 14 is in active development.** The `master` branch already contains the technical foundations for warfare, industry, vehicles, and strategic persistence. This is **not a public-release-ready game**: the playable frontline map, interfaces, and multiplayer validation still need work.

## About the game

Frontline 14 turns the familiar SS14 world into a **persistent conflict between two factions**. Winning takes more than firefights: players extract ore, refine materials, manufacture equipment, deliver supplies by truck, and hold bases.

- **One shared physical world:** characters, cargo, vehicles, and strategic structures exist directly on the map.
- **Territorial warfare:** physical `Town Hall` objectives determine ownership; territorial victory points determine the winner.
- **Meaningful logistics:** weapons, ammunition, medical supplies, and respawn supplies must move through production and storage.
- **War survives technical restarts:** selected strategic campaign state, including supported vehicles and their cargo, is restored.
- **Roleplay is optional:** no mandatory station jobs, roleplay hierarchy, or standard SS14 short-round gameplay loop.

### Core gameplay loop

1. **Resource extraction** — collect resources from deposits.
2. **Refining** — turn raw resources into materials.
3. **Factory** — manufacture equipment and supplies.
4. **Supply crates** — physical batches of finished goods.
5. **Truck delivery** — transport crates and materials.
6. **Base stockpile** — deposit and store supplies.
7. **Supply and combat** — retrieve equipment and respawn using Soldier Supplies.
8. **Territory control** — destroy and rebuild strategic bases.

Delivered `Soldier Supplies` pay for respawns at eligible supplied bases. Destroying a `Town Hall` creates a ruin; restoring it costs `BasicMaterials` and can change control of the territory.

## Implemented in `master`

| Area | Current implementation |
| --- | --- |
| **Campaign and factions** | Dedicated `PersistentWar` mode, `WarId`, two factions, side selection, campaign state, and war ending. |
| **Territory control** | Five configured territories, physical `Town Hall` objectives, destruction and rebuilding, territorial victory points. |
| **Resources and industry** | Resource fields, physical raw materials, `Refinery`, `BasicMaterials`, `Factory`, recipes, and production jobs. |
| **Supplies** | Sealed crates of weapons, ammo, medicine, and `Soldier Supplies`; public base stockpiles with aggregated inventory counts. |
| **Respawning** | Eligible base selection and `Soldier Supplies` consumption on respawn. |
| **Vehicles** | Driveable `FrontlineLogisticsTruck` with one driver, 10 physical cargo slots, road-dependent speed, and directional vehicle artwork. |
| **Strategic persistence** | Supported state for bases, stockpiles, resource fields, factories, refineries, trucks, and their supported cargo survives technical restarts. |
| **Planetary art** | An initial, attributed set of dirt, grass, sand, asphalt, concrete, dirt roads, and scenery assets. |
| **Developer tooling** | A test map, `PersistentWar` map validation, integration tests, admin/debug commands, and a `Frontline` entity spawn-menu filter. |

**Example supply chain:** `FrontlineRawIron` → `BasicMaterials` → manufacture a physical supply crate → transport → deposit into a base stockpile.

> [!NOTE]
> **Strategic persistence is not full-world persistence.** Snapshots cover defined systems and entities, not every arbitrary change to the map or every entity. Orderly technical restarts are supported; continuous crash-proof world recovery is not currently claimed.

## Current limitations and next steps

The near-term milestone is a **closed technical playtest for 20 players (10 vs. 10) on one frontline**, not an immediate launch of a large persistent campaign.

| Priority | Work remaining |
| --- | --- |
| **High** | Build a playable single-front map with bases, roads, resource sites, production, and supply routes. The existing `Resources/Maps/Frontline/base_test.yaml` is primarily a technical test map. |
| **High** | Strengthen server-side faction authorization for base stockpile operations. |
| **High** | Validate the complete gameplay loop and actual 20-player concurrency; integration tests are not a substitute for a load test. |
| **In progress / planned** | Redesign stockpile, refinery, and factory UIs with item grids, clearer recipes, and readable production status. |
| **Planned** | Add shared/public and per-player personal production queues. The current machine job lists do not yet model this ownership split. |
| **Planned** | Improve practical infantry supply and add a minimal view of territorial status. |

**Beyond the initial MVP:** automatic low-population frontline freezing, heavy vehicles, fuel logistics, extensive fortifications, and full faction technology progression. These are ideas for later stages, not advertised as implemented features.

## Technology and project structure

The project is primarily written in **C#** and builds on **RobustToolbox / Space Station 14** systems: ECS, networking, physics, containers, hands, inventory, interactions, prototypes, localization, and UI. Rather than rebuilding the engine, Frontline 14 develops its own gameplay on top.

| Path | Purpose |
| --- | --- |
| `Content.Server/War/` | Server-side war, logistics, and persistence systems. |
| `Content.Shared/War/` | Shared components, prototypes, and network messages. |
| `Content.Client/War/` | Frontline client interfaces. |
| `Resources/Prototypes/War/` | Game entities, resources, recipes, vehicles, and territories. |
| `Resources/Prototypes/Frontline/` | Planetary tiles and decorations. |
| `Resources/Maps/Frontline/` | Project maps and test environment. |
| `Resources/Textures/Frontline/` and `Resources/Textures/_Frontline/` | Imported graphics and their attribution records. |

### Getting started

Development requires an appropriate .NET SDK (see `global.json`), Git, and the dependencies used by SS14.

```bash
git clone https://github.com/darvin7531/Frontline-14.git
cd Frontline-14
python RUN_THIS.py
dotnet build SpaceStation14.sln
```

Local launcher scripts are provided: `runserver.sh` / `runclient.sh` (Linux) and `runserver.bat` / `runclient.bat` (Windows). See the [SS14 documentation](https://docs.spacestation14.com/) for engine setup and development prerequisites.

## Origins and licensing

Frontline 14 began from a specific **Space Syndicate / Corvax** snapshot:

- **Upstream revision:** [`91cf10ac16807d3d168f96808c3baba5896d6a36`](https://github.com/space-syndicate/space-station-14/commit/91cf10ac16807d3d168f96808c3baba5896d6a36), dated September 14, 2026.
- **RobustToolbox revision at the fork point:** [`edf061e7450a4074f173e3000bf1552b6f54082f`](https://github.com/space-wizards/RobustToolbox/commit/edf061e7450a4074f173e3000bf1552b6f54082f).
- Primary upstream projects: [Space Station 14](https://github.com/space-wizards/space-station-14), [Space Syndicate / Corvax](https://github.com/space-syndicate/space-station-14), and [RobustToolbox](https://github.com/space-wizards/RobustToolbox).
- Selected graphical assets were adapted from [RMC-14](https://github.com/RMC-14/RMC-14) with their source and license notices preserved.

**Licensing is split by origin.** Third-party code and assets retain their applicable licenses; original Frontline 14 material follows the project's own license. Do not assume the entire repository is either exclusively MIT-licensed or exclusively proprietary.

- [`LICENSE.TXT`](./LICENSE.TXT) — base code license.
- [`LICENSE-FRONTLINE14.md`](./LICENSE-FRONTLINE14.md) — original Frontline 14 material.
- [`THIRD_PARTY_LICENSES.md`](./THIRD_PARTY_LICENSES.md) — third-party licensing and attribution index.
- [`THIRD_PARTY_ASSETS.md`](./THIRD_PARTY_ASSETS.md) — the specific RMC-14 planetary asset import.
- [`CONTRIBUTING.md`](./CONTRIBUTING.md) and [`CLA.md`](./CLA.md) — contributor guidelines.

---

<div align="center">
  <sub>Frontline 14 is a working title. The project remains under active development.</sub>
</div>
