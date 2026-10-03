[Русский](./README.md) | [English](./README.en.md)

# Frontline 14

**Frontline 14** — самостоятельный игровой проект на базе Space Station 14 / RobustToolbox про длительную войну двух фракций в общем физическом мире.

Проект использует технический фундамент SS14, но уходит от классического станционного и короткого раундового геймплея в сторону территориальной войны, логистики, промышленности, исследований и фронтовых боёв.

> **Статус:** активная разработка. На текущем `master` уже собран рабочий каркас PersistentWar: фракции, территории, добыча, переработка, производство supply crates и публичные склады баз. Полного стратегического сохранения мира и транспорта пока нет.

## Что за игра

В основе Frontline 14 — одна долгоживущая война между двумя фракциями.

Главные принципы:

- один общий серверный мир без обязательного RP;
- две фракции и выбор стороны на время конкретной войны;
- захват территорий через физические объекты мира;
- добыча ресурсов, переработка и производство;
- физическая логистика: ресурсы и supply crates существуют в мире, а запасы баз учитываются через публичные stockpile;
- фракционное технологическое развитие;
- технические рестарты сервера не должны означать конец войны;
- новая кампания начинается только после завершения предыдущей войны.

RP разрешён, но не является обязательным условием игры и не должен давать механического преимущества. Frontline 14 не строится вокруг станционных профессий, обязательной субординации или стандартного SS14-roleplay цикла.

## Текущее состояние

На текущем `master` реализованы основные системы первого игрового цикла:

- режим `PersistentWar` с отдельным `WarId`, состоянием кампании и победителем;
- сохранение кампании и принадлежности игроков к фракциям между техническими рестартами;
- две data-driven фракции и выбор стороны на время войны;
- базовый deploy / death / respawn flow;
- data-driven территории с локализуемыми названиями, несколькими областями одной территории и настраиваемыми Victory Points;
- server-authoritative владение территориями через Town Hall;
- разрушение Town Hall, руины, восстановление за `BasicMaterials` и переход территории другой фракции;
- data-driven условие победы по Victory Points;
- конечные ресурсные поля с физической добычей `FrontlineRawIron`;
- Refinery с физическим input, очередью производства и удерживаемым public output;
- отдельный ресурс `BasicMaterials`, используемый экономикой Frontline вместо vanilla Steel;
- Factory с физическим input, очередью производства и удерживаемым public output;
- sealed Supply Crates для оружия, боеприпасов, медицины и Soldier Supplies;
- публичный stockpile на действующих Town Hall: приём supply crates и Basic Materials, агрегированное хранение и выдача физических предметов;
- player-facing UI для Refinery, Factory и stockpile;
- day/night cycle, привязанный к времени начала текущей войны;
- PersistentWar map validation и набор integration/regression tests;
- admin/debug-команды для проверки состояния войны, территорий и фракций;
- отдельный `All / Frontline` фильтр в entity spawn menu для разработки и маппинга.

### Текущие ограничения

Физическое стратегическое состояние мира пока не сохраняется полностью. `WarId`, состояние войны и принадлежность игроков к фракциям persistent, но территории, stockpiles, производственные очереди и другие объекты мира ещё не имеют общего strategic persistence snapshot.

Транспорт и полноценная игровая war map также ещё не входят в текущий `master`. Soldier Supplies уже существуют как логистический продукт и stockpile-ресурс, но текущий respawn flow пока не расходует их.
## Исходная кодовая база

Frontline 14 был начат как самостоятельное продолжение конкретного snapshot русскоязычного билда **Space Syndicate / Corvax**.

Базовый upstream commit:

- **Space Syndicate / Corvax:** [`91cf10ac16807d3d168f96808c3baba5896d6a36`](https://github.com/space-syndicate/space-station-14/commit/91cf10ac16807d3d168f96808c3baba5896d6a36)
- дата upstream-коммита: **14 сентября 2026**
- upstream commit message: **`Corvax maps tweaks (#3729)`**
- **RobustToolbox** на этом snapshot: [`edf061e7450a4074f173e3000bf1552b6f54082f`](https://github.com/space-wizards/RobustToolbox/commit/edf061e7450a4074f173e3000bf1552b6f54082f)

История upstream сохранена в Git, поэтому происхождение кода можно отслеживать до исходных изменений Corvax / Space Syndicate и Space Station 14.

Основные upstream-проекты:

- [Space Syndicate / Corvax](https://github.com/space-syndicate/space-station-14)
- [Space Station 14](https://github.com/space-wizards/space-station-14)
- [RobustToolbox](https://github.com/space-wizards/RobustToolbox)

Отдельные совместимые реализации также могут адаптироваться из других открытых проектов, например [RMC-14](https://github.com/RMC-14/RMC-14), с обязательным сохранением соответствующих лицензий и attribution.

Основной язык проекта — **C#**.

## Что используется от SS14

Frontline 14 не пытается переписать весь движок с нуля. Там, где это разумно, используются существующие системы SS14 / RobustToolbox:

- ECS и networking / PVS;
- maps, grids и physics;
- entities, containers, inventory и hands;
- damage и medical foundation;
- DoAfter и interaction systems;
- UI и localization;
- prototype/data-driven infrastructure;
- admin и integration-test infrastructure.

При этом стандартный station round loop, emergency shuttle как завершение игры, station jobs, antags, обычные objectives, случайные station events и другие не подходящие проекту механики постепенно выводятся из основного игрового цикла.

## Сборка

Пока проект сохраняет upstream-инфраструктуру SS14.

Базовая последовательность:

1. клонировать репозиторий;
2. запустить `RUN_THIS.py` для инициализации необходимых компонентов и подмодулей;
3. собирать проект стандартными средствами .NET или IDE.

Для базовых технических вопросов по RobustToolbox и инфраструктуре SS14 пока также применима [официальная документация Space Station 14](https://docs.spacestation14.io/).

## Лицензирование

В проекте используется раздельное лицензирование.

Код и материалы, происходящие из Space Station 14, Space Syndicate / Corvax, RobustToolbox, RMC-14 и других сторонних источников, продолжают регулироваться своими исходными лицензиями. Исходный MIT-текст базовой кодовой базы сохранён в [`LICENSE.TXT`](./LICENSE.TXT).

Реестр известных сторонних источников и правила attribution:

**[`THIRD_PARTY_LICENSES.md`](./THIRD_PARTY_LICENSES.md)**

Оригинальный код, системы, карты, документация, дизайн, UI и ассеты Frontline 14 регулируются отдельной лицензией:

**[`LICENSE-FRONTLINE14.md`](./LICENSE-FRONTLINE14.md)**

Она не отменяет и не ограничивает права, которые уже предоставлены лицензиями стороннего upstream-кода или ассетов.

## Вклад в проект

Правила contribution:

**[`CONTRIBUTING.md`](./CONTRIBUTING.md)**

Для оригинальных contributions используется:

**[`CLA.md`](./CLA.md)**

---

**Frontline 14** — рабочее название проекта.
