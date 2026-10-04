using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Linq;
using Content.Client.Markers;
using Content.Client.War;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Mind;
using Content.Server.Stack;
using Content.Server.War;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.CCVar;
using Content.Shared.GameTicking.Components;
using Content.Shared.Ghost.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.War;

using Robust.Shared.ContentPack;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.GameRules;

[TestFixture]
[TestOf(typeof(PersistentWarRuleComponent))]
public sealed class PersistentWarRuleTest : GameTest
{
    [TestPrototypes]
    private const string ResourceValidationPrototypes = """
        - type: entity
          id: TestInvalidFrontlineResourceNode
          components:
          - type: FrontlineResourceNode
            output: FrontlineRawIron
            maxYield: 0
            harvestAmount: 1

        - type: entity
          id: TestInvalidFrontlineBonusNode
          components:
          - type: FrontlineResourceNode
            output: FrontlineRawIron
            maxYield: 1
            harvestAmount: 1
            bonusDrops:
            - output: RawTechnologyMaterial
              chance: 2
              minAmount: 1
              maxAmount: 1

        - type: entity
          id: TestOverflowingFrontlineBonusNode
          components:
          - type: FrontlineResourceNode
            output: FrontlineRawIron
            maxYield: 1
            harvestAmount: 1
            bonusDrops:
            - output: RawTechnologyMaterial
              chance: 1
              minAmount: 1
              maxAmount: 2147483647
        """;

    public enum InvalidMapCase
    {
        UnassignedTerritory,
        FourTerritories,
        UnknownTerritory,
        InvalidTerritoryBounds,
        MissingSpawn,
        UnknownSpawnTerritory,
        WrongHallFaction,
        UnknownHallTerritory,
        ObjectiveOutsideBounds,
        DuplicateObjective,
        WrongObjectiveComposition,
        InvalidResourceField,
        ResourceFieldWithoutSpawnPoints,
        UnknownResourceFieldReference,
        EmptyResourceFieldId,
        ZeroResourceReserve,
        TooManyActiveResourceNodes,
        NegativeResourceDelay,
        InvalidResourceNode,
        InvalidResourceBonus,
        OverflowingResourceBonusAmount,
    }

    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
        InLobby = true,
    };

    private GamePresetPrototype _originalPreset;

    public override async Task DoSetup()
    {
        await base.DoSetup();
        await Server.WaitPost(() => _originalPreset = Server.System<GameTicker>().Preset);
    }

    public override async Task DoTeardown()
    {
        try
        {
            await Server.WaitPost(() =>
            {
                try
                {
                    var resources = Server.ResolveDependency<IResourceManager>();
                    resources.UserData.Delete(WarStateSystem.SavePath);
                    resources.UserData.Delete(WarFactionSystem.SavePath);
                    resources.UserData.Delete(WarStrategicSnapshotSystem.SavePath);
                    resources.UserData.Delete(WarStrategicSnapshotSystem.TemporaryPath);
                    resources.UserData.Delete(WarStrategicSnapshotSystem.BackupPath);
                }
                finally
                {
                    // RestartRound clears CurrentPreset, but retains the selected next-round preset.
                    Server.System<GameTicker>().SetGamePreset(_originalPreset);
                }
            });
        }
        finally
        {
            await base.DoTeardown();
        }
    }

    [Test]
    public async Task TeardownRestoresOriginalPresetBeforePoolReturn()
    {
        var ticker = Server.System<GameTicker>();
        var originalPreset = ticker.Preset;
        await Server.WaitPost(() => ticker.SetGamePreset("PersistentWar"));
        PreFinalizeHook += () => Assert.That(ticker.Preset, Is.EqualTo(originalPreset));
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task StartsGroundMapWithPersistentDayNight()
    {
        var ticker = Server.System<GameTicker>();
        var atmosphere = Server.System<AtmosphereSystem>();
        var resources = Server.ResolveDependency<IResourceManager>();
        var war = Server.System<WarStateSystem>();
        var mapSystem = Server.System<SharedMapSystem>();
        var startedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(7);
        EntityUid map = default;
        MapAtmosphereComponent atmos = default!;
        LightCycleComponent cycle = default!;

        await Server.WaitPost(() =>
        {
            resources.UserData.Delete(WarStateSystem.SavePath);
            using (var stream = resources.UserData.OpenWrite(WarStateSystem.SavePath))
                JsonSerializer.Serialize(stream, new WarState(1, WarStatus.Active, startedAt));

            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(war.State, Is.EqualTo(new WarState(1, WarStatus.Active, startedAt)));

            map = mapSystem.GetMapOrInvalid(ticker.DefaultMap);
            atmos = SEntMan.GetComponent<MapAtmosphereComponent>(map);
            var light = SEntMan.GetComponent<MapLightComponent>(map);
            cycle = SEntMan.GetComponent<LightCycleComponent>(map);
            var refineryCount = 0;
            var refineryQuery = SEntMan.EntityQueryEnumerator<FrontlineRefineryComponent, TransformComponent>();
            while (refineryQuery.MoveNext(out _, out _, out var transform))
            {
                if (transform.MapID == ticker.DefaultMap)
                    refineryCount++;
            }
            var factoryCount = 0;
            var factoryQuery = SEntMan.EntityQueryEnumerator<FrontlineFactoryComponent, TransformComponent>();
            while (factoryQuery.MoveNext(out _, out _, out var transform))
            {
                if (transform.MapID == ticker.DefaultMap)
                    factoryCount++;
            }
            var floorTiles = 0;
            var gridQuery = SEntMan.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out var gridUid, out var grid, out var transform))
            {
                if (transform.MapID != ticker.DefaultMap)
                    continue;

                foreach (var _ in mapSystem.GetAllTiles(gridUid, grid))
                    floorTiles++;
            }

            Assert.That(atmos.Space, Is.False);
            Assert.That(cycle.Duration, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(refineryCount, Is.EqualTo(2));
            Assert.That(factoryCount, Is.EqualTo(2));
            Assert.That(floorTiles, Is.GreaterThanOrEqualTo(256));
            Assert.That(SharedLightCycleSystem.GetColor((map, cycle), light.AmbientLightColor, 0),
                Is.Not.EqualTo(SharedLightCycleSystem.GetColor((map, cycle), light.AmbientLightColor, (float) cycle.Duration.TotalSeconds / 2)));

            Assert.That(cycle.Offset, Is.InRange(TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(8)));

            var validator = Server.System<PersistentWarMapValidatorSystem>();
            Assert.DoesNotThrow(() => validator.Validate(map));
            atmosphere.SetMapSpace(map, true, atmos);
            var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(map));
            Assert.That(error!.Message, Is.EqualTo("PersistentWar map must set MapAtmosphere.space to false."));
            atmosphere.SetMapSpace(map, false, atmos);
        });

        await Server.WaitPost(() =>
        {
            var firstMarker = FindMapEntity<TerritoryComponent>(ticker.DefaultMap,
                territory => territory.TerritoryId == "frontline-one");
            var secondArea = SSpawnAtPosition(null, SComp<TransformComponent>(firstMarker).Coordinates);
            SEntMan.AddComponent<TerritoryComponent>(secondArea)
                .Configure(new TerritoryId("frontline-one"), new Vector2(-1f), new Vector2(1f));
        });
        await Server.WaitAssertion(() => Assert.DoesNotThrow(() => Server.System<PersistentWarMapValidatorSystem>().Validate(map)));

        await Pair.RunUntilSynced();
        await Pair.Client.WaitAssertion(() =>
        {
            var clientCycle = CEntMan.GetComponent<LightCycleComponent>(Pair.ToClientUid(map));
            Assert.That(clientCycle.Offset, Is.EqualTo(cycle.Offset));

            var resourceSpawnMarkers = 0;
            var markerQuery = CEntMan.EntityQueryEnumerator<MarkerComponent, SpriteComponent, MetaDataComponent>();
            while (markerQuery.MoveNext(out _, out _, out var sprite, out var metadata))
            {
                if (metadata.EntityPrototype?.ID != "FrontlineResourceSpawnPoint")
                    continue;

                resourceSpawnMarkers++;
                Assert.That(sprite.Visible, Is.False);
                Assert.That(sprite.AllLayers.Any(layer => layer.RsiState.IsValid), Is.True);
            }

            Assert.That(resourceSpawnMarkers, Is.EqualTo(3));
            Pair.Client.System<MarkerSystem>().MarkersVisible = true;

            markerQuery = CEntMan.EntityQueryEnumerator<MarkerComponent, SpriteComponent, MetaDataComponent>();
            while (markerQuery.MoveNext(out _, out _, out var sprite, out var metadata))
            {
                if (metadata.EntityPrototype?.ID == "FrontlineResourceSpawnPoint")
                    Assert.That(sprite.Visible, Is.True);
            }
        });

        await Server.WaitPost(() => atmosphere.SetMapGasMixture(map, GasMixture.SpaceGas, atmos));
        await Server.WaitAssertion(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => Server.System<PersistentWarMapValidatorSystem>().Validate(map));
            Assert.That(error!.Message, Is.EqualTo("PersistentWar map MapAtmosphere must provide safe pressure and temperature."));
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }

    [Test]
    public async Task ValidatorReportsAllMissingMapRequirements()
    {
        var map = await Pair.CreateTestMap();
        var validator = Server.System<PersistentWarMapValidatorSystem>();

        await Server.WaitAssertion(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(map.MapUid));
            Assert.That(error!.Message, Does.Contain("PersistentWar map must include MapAtmosphere."));
            Assert.That(error.Message, Does.Contain("PersistentWar map must include MapLight."));
            Assert.That(error.Message, Does.Contain("PersistentWar map must include LightCycle."));
            Assert.That(error.Message, Does.Contain("PersistentWar map territory 'frontline-one' must have at least one area."));
            Assert.That(error.Message, Does.Contain("PersistentWar map must include a town hall or ruin for each territory."));
            Assert.That(error.Message, Does.Contain("PersistentWar map must include a faction spawn point for each territory."));
            Assert.That(error.Message, Does.Contain("PersistentWar map must include exactly one starting town hall for FrontlineFactionOne"));
            Assert.That(error.Message, Does.Contain("PersistentWar map must include exactly one starting town hall for FrontlineFactionTwo"));
        });
    }

    [TestCase(InvalidMapCase.UnassignedTerritory,
        "PersistentWar map territory markers must not use Unassigned.")]
    [TestCase(InvalidMapCase.FourTerritories,
        "PersistentWar map territory 'frontline-five' must have at least one area.")]
    [TestCase(InvalidMapCase.UnknownTerritory,
        "PersistentWar map references unknown territory 'unlisted'.")]
    [TestCase(InvalidMapCase.InvalidTerritoryBounds,
        "PersistentWar map territory 'frontline-one' has invalid bounds.")]
    [TestCase(InvalidMapCase.MissingSpawn,
        "PersistentWar map territory 'frontline-one' must include a faction spawn point within its bounds.")]
    [TestCase(InvalidMapCase.UnknownSpawnTerritory,
        "PersistentWar map faction spawn references unknown or non-containing territory 'unlisted'.")]
    [TestCase(InvalidMapCase.WrongHallFaction,
        "PersistentWar map must include exactly one starting town hall for FrontlineFactionOne")]
    [TestCase(InvalidMapCase.UnknownHallTerritory,
        "PersistentWar map town hall references unknown or non-containing territory 'unlisted'.")]
    [TestCase(InvalidMapCase.ObjectiveOutsideBounds,
        "PersistentWar map territory 'frontline-one' must include exactly one objective within its bounds")]
    [TestCase(InvalidMapCase.DuplicateObjective,
        "PersistentWar map territory 'frontline-one' must include exactly one objective within its bounds")]
    [TestCase(InvalidMapCase.WrongObjectiveComposition,
        "PersistentWar map must include exactly one starting town hall for FrontlineFactionOne")]
    [TestCase(InvalidMapCase.InvalidResourceField,
        "PersistentWar resource field 'invalid-field' must have MaxActiveNodes greater than zero.")]
    [TestCase(InvalidMapCase.ResourceFieldWithoutSpawnPoints,
        "PersistentWar resource field 'orphan-field' must have at least one spawn point.")]
    [TestCase(InvalidMapCase.UnknownResourceFieldReference,
        "PersistentWar resource spawn point references unknown field 'missing-field'.")]
    [TestCase(InvalidMapCase.EmptyResourceFieldId,
        "PersistentWar resource field ID must not be empty.")]
    [TestCase(InvalidMapCase.ZeroResourceReserve,
        "PersistentWar resource field 'frontline-test-iron' must have MaxReserveNodes greater than zero.")]
    [TestCase(InvalidMapCase.TooManyActiveResourceNodes,
        "PersistentWar resource field 'frontline-test-iron' cannot have more active nodes than spawn points.")]
    [TestCase(InvalidMapCase.NegativeResourceDelay,
        "PersistentWar resource field 'frontline-test-iron' delays must not be negative.")]
    [TestCase(InvalidMapCase.InvalidResourceNode,
        "PersistentWar resource field 'frontline-test-iron' primary node must have positive yield and harvest amount")]
    [TestCase(InvalidMapCase.InvalidResourceBonus,
        "PersistentWar resource field 'frontline-test-iron' primary node has an invalid bonus drop")]
    [TestCase(InvalidMapCase.OverflowingResourceBonusAmount,
        "PersistentWar resource field 'frontline-test-iron' primary node has an invalid bonus drop")]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task ValidatorRejectsInvalidGroundMap(InvalidMapCase invalidCase, string expectedError)
    {
        var ticker = Server.System<GameTicker>();
        var validator = Server.System<PersistentWarMapValidatorSystem>();
        EntityUid map = default;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
            map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);

            var mapId = SComp<MapComponent>(map).MapId;
            switch (invalidCase)
            {
                case InvalidMapCase.UnassignedTerritory:
                    SComp<TerritoryComponent>(FindMapEntity<TerritoryComponent>(mapId,
                        territory => territory.TerritoryId == "frontline-five")).TerritoryId = "Unassigned";
                    break;
                case InvalidMapCase.FourTerritories:
                    SEntMan.DeleteEntity(FindMapEntity<TerritoryComponent>(mapId,
                        territory => territory.TerritoryId == "frontline-five"));
                    break;
                case InvalidMapCase.UnknownTerritory:
                    SComp<TerritoryComponent>(FindMapEntity<TerritoryComponent>(mapId,
                        territory => territory.TerritoryId == "frontline-five")).TerritoryId = "unlisted";
                    break;
                case InvalidMapCase.InvalidTerritoryBounds:
                    SComp<TerritoryComponent>(FindMapEntity<TerritoryComponent>(mapId,
                        territory => territory.TerritoryId == "frontline-one")).BoundsMin = new Vector2(100f);
                    break;
                case InvalidMapCase.MissingSpawn:
                    SEntMan.DeleteEntity(FindMapEntity<FactionSpawnPointComponent>(mapId,
                        spawn => spawn.TerritoryId == "frontline-one"));
                    break;
                case InvalidMapCase.UnknownSpawnTerritory:
                    SComp<FactionSpawnPointComponent>(FindMapEntity<FactionSpawnPointComponent>(mapId,
                        spawn => spawn.TerritoryId == "frontline-one")).TerritoryId = "unlisted";
                    break;
                case InvalidMapCase.WrongHallFaction:
                    SComp<TownHallComponent>(FindMapEntity<TownHallComponent>(mapId,
                        hall => hall.FactionId == "FrontlineFactionOne")).FactionId = "FrontlineFactionTwo";
                    break;
                case InvalidMapCase.UnknownHallTerritory:
                    SComp<TownHallComponent>(FindMapEntity<TownHallComponent>(mapId,
                        hall => hall.FactionId == "FrontlineFactionOne")).TerritoryId = "unlisted";
                    break;
                case InvalidMapCase.ObjectiveOutsideBounds:
                    var territory = SComp<TerritoryComponent>(FindMapEntity<TerritoryComponent>(mapId,
                        marker => marker.TerritoryId == "frontline-one"));
                    territory.BoundsMin = Vector2.One;
                    territory.BoundsMax = new Vector2(2f, 2f);
                    break;
                case InvalidMapCase.DuplicateObjective:
                    var original = FindMapEntity<TownHallComponent>(mapId,
                        hall => hall.TerritoryId == "frontline-one");
                    var duplicate = SSpawnAtPosition(null, SComp<TransformComponent>(original).Coordinates);
                    SEntMan.AddComponent<TownHallRuinComponent>(duplicate).TerritoryId = "frontline-one";
                    break;
                case InvalidMapCase.WrongObjectiveComposition:
                    var ruin = FindMapEntity<TownHallRuinComponent>(mapId,
                        objective => objective.TerritoryId == "frontline-three");
                    SEntMan.RemoveComponent<TownHallRuinComponent>(ruin);
                    SEntMan.AddComponent<TownHallComponent>(ruin)
                        .Configure(new TerritoryId("frontline-three"), new FactionId("FrontlineFactionOne"));
                    break;
                case InvalidMapCase.InvalidResourceField:
                    var invalidField = SSpawnAtPosition(null, SComp<TransformComponent>(map).Coordinates);
                    SEntMan.AddComponent<FrontlineResourceFieldComponent>(invalidField).FieldId = "invalid-field";
                    break;
                case InvalidMapCase.ResourceFieldWithoutSpawnPoints:
                    var orphanField = SSpawnAtPosition(null, SComp<TransformComponent>(map).Coordinates);
                    var orphanFieldComp = SEntMan.AddComponent<FrontlineResourceFieldComponent>(orphanField);
                    orphanFieldComp.FieldId = "orphan-field";
                    orphanFieldComp.MaxActiveNodes = 1;
                    break;
                case InvalidMapCase.UnknownResourceFieldReference:
                    var unknownSlot = SSpawnAtPosition(null, SComp<TransformComponent>(map).Coordinates);
                    SEntMan.AddComponent<FrontlineResourceSpawnPointComponent>(unknownSlot).FieldId = "missing-field";
                    break;
                case InvalidMapCase.EmptyResourceFieldId:
                    SComp<FrontlineResourceFieldComponent>(FindMapEntity<FrontlineResourceFieldComponent>(mapId, _ => true)).FieldId = "";
                    break;
                case InvalidMapCase.ZeroResourceReserve:
                    SComp<FrontlineResourceFieldComponent>(FindMapEntity<FrontlineResourceFieldComponent>(mapId, _ => true))
                        .MaxReserveNodes = 0;
                    break;
                case InvalidMapCase.TooManyActiveResourceNodes:
                    SComp<FrontlineResourceFieldComponent>(FindMapEntity<FrontlineResourceFieldComponent>(mapId, _ => true))
                        .MaxActiveNodes = int.MaxValue;
                    break;
                case InvalidMapCase.NegativeResourceDelay:
                    SComp<FrontlineResourceFieldComponent>(FindMapEntity<FrontlineResourceFieldComponent>(mapId, _ => true))
                        .ReplacementDelay = TimeSpan.FromSeconds(-1);
                    break;
                case InvalidMapCase.InvalidResourceNode:
                    SComp<FrontlineResourceFieldComponent>(FindMapEntity<FrontlineResourceFieldComponent>(mapId, _ => true))
                        .PrimaryNodePrototype = "TestInvalidFrontlineResourceNode";
                    break;
                case InvalidMapCase.InvalidResourceBonus:
                    SComp<FrontlineResourceFieldComponent>(FindMapEntity<FrontlineResourceFieldComponent>(mapId, _ => true))
                        .PrimaryNodePrototype = "TestInvalidFrontlineBonusNode";
                    break;
                case InvalidMapCase.OverflowingResourceBonusAmount:
                    SComp<FrontlineResourceFieldComponent>(FindMapEntity<FrontlineResourceFieldComponent>(mapId, _ => true))
                        .PrimaryNodePrototype = "TestOverflowingFrontlineBonusNode";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null);
            }
        });

        await Server.WaitAssertion(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(map));
            Assert.That(error!.Message, Does.Contain(expectedError));
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task TechnicalRestartRestoresPersistentDayNightPhase()
    {
        var ticker = Server.System<GameTicker>();
        var resources = Server.ResolveDependency<IResourceManager>();
        var war = Server.System<WarStateSystem>();
        var startedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(7);
        TimeSpan beforeRestart = default;

        await Server.WaitPost(() =>
        {
            resources.UserData.Delete(WarStateSystem.SavePath);
            using (var stream = resources.UserData.OpenWrite(WarStateSystem.SavePath))
                JsonSerializer.Serialize(stream, new WarState(1, WarStatus.Active, startedAt));

            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() =>
        {
            var map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            beforeRestart = SEntMan.GetComponent<LightCycleComponent>(map).Offset;
            Assert.That(war.State?.StartedAt, Is.EqualTo(startedAt));
        });

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() =>
        {
            var map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            var restored = SEntMan.GetComponent<LightCycleComponent>(map).Offset;
            Assert.That(war.State?.StartedAt, Is.EqualTo(startedAt));
            Assert.That(restored, Is.InRange(beforeRestart, beforeRestart + TimeSpan.FromMinutes(1)));
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task TechnicalRestartPreservesOwnedBasesAndStockpileCounts()
    {
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var territories = Server.System<TerritorySystem>();
        var halls = Server.System<TownHallSystem>();
        var maps = Server.System<SharedMapSystem>();
        var faction = new FactionId("FrontlineFactionOne");
        var capturedTerritory = new TerritoryId("frontline-three");
        var baseTerritories = new[] { "frontline-one", capturedTerritory.Id, "frontline-five" };
        WarState beforeRestart = default!;
        EntityUid oldMap = default;
        EntityUid[] oldBases = default!;
        Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>[] countsBefore = default!;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            war.StartNewWar();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(territories.GetState(capturedTerritory, ticker.DefaultMap), Is.EqualTo(TerritoryState.Neutral));
        });

        await Server.WaitPost(() =>
        {
            Assert.That(halls.ForceCapture(capturedTerritory, faction, ticker.DefaultMap), Is.True);
            oldMap = maps.GetMapOrInvalid(ticker.DefaultMap);
            oldBases = baseTerritories.Select(territory => FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == territory)).ToArray();
            countsBefore = new[]
            {
                new Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>
                    { ["SoldierSupplies"] = 0, ["BasicMaterials"] = 11 },
                new Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>
                    { ["SoldierSupplies"] = 7, ["BasicMaterials"] = 23 },
                new Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>
                    { ["SoldierSupplies"] = 13, ["BasicMaterials"] = 37 },
            };
            for (var i = 0; i < oldBases.Length; i++)
            {
                var counts = SComp<FrontlineStockpileComponent>(oldBases[i]).Counts;
                counts.Clear();
                foreach (var (product, count) in countsBefore[i])
                    counts[product] = count;
            }
            beforeRestart = war.State!;
        });
        await Pair.RunTicksSync(1);
        await Server.WaitAssertion(() =>
        {
            Assert.That(territories.TryGetOwner(capturedTerritory, out var owner, ticker.DefaultMap), Is.True);
            Assert.That(owner, Is.EqualTo(faction));
            Assert.That(territories.CountOwned(faction, ticker.DefaultMap), Is.EqualTo(2));
            Assert.That(war.State, Is.EqualTo(beforeRestart));
            Assert.That(beforeRestart.Status, Is.EqualTo(WarStatus.Active), "One neutral capture must stay below victory.");
        });

        await Server.WaitPost(() => ticker.RestartRound());
        await Server.WaitAssertion(() =>
        {
            var resources = Server.ResolveDependency<IResourceManager>();
            using var stream = resources.UserData.Open(WarStrategicSnapshotSystem.SavePath, FileMode.Open);
            var saved = JsonSerializer.Deserialize<WarStrategicSnapshot>(stream);
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved!.SnapshotVersion, Is.EqualTo(1));
            Assert.That(saved.WarId, Is.EqualTo(beforeRestart.WarId));
            Assert.That(saved.Bases.Count, Is.EqualTo(5));
            var captured = saved.Bases.Single(entry => entry.TerritoryId == capturedTerritory.Id);
            Assert.That(captured.ObjectiveKind, Is.EqualTo("hall"));
            Assert.That(captured.FactionId, Is.EqualTo(faction.Id));
            Assert.That(captured.Prototype, Is.EqualTo("TownHallCoreFactionOne"));
            for (var i = 0; i < baseTerritories.Length; i++)
                Assert.That(saved.Bases.Single(entry => entry.TerritoryId == baseTerritories[i]).Counts,
                    Is.EquivalentTo(countsBefore[i].ToDictionary(entry => entry.Key.Id, entry => entry.Value)));
        });
        await Server.WaitPost(() =>
        {
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(territories.TryGetOwner(capturedTerritory, out var owner, ticker.DefaultMap), Is.True,
                "Technical restart must restore the captured base instead of reloading its neutral ruin.");
            Assert.That(owner, Is.EqualTo(faction));
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(war.State, Is.EqualTo(beforeRestart));
            Assert.That(territories.CountOwned(faction, ticker.DefaultMap), Is.EqualTo(2));
            Assert.That(SEntMan.EntityExists(oldMap), Is.False);
            Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.Not.EqualTo(oldMap));
            for (var i = 0; i < oldBases.Length; i++)
            {
                Assert.That(SEntMan.EntityExists(oldBases[i]), Is.False);
                var restored = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                    hall => hall.TerritoryId == baseTerritories[i]);
                Assert.That(SComp<TownHallComponent>(restored).FactionId,
                    Is.EqualTo(i == 2 ? "FrontlineFactionTwo" : faction.Id));
                Assert.That(SComp<FrontlineStockpileComponent>(restored).Counts, Is.EquivalentTo(countsBefore[i]),
                    $"Base {baseTerritories[i]} must retain exact counts, including zero supplies, without a fresh 20-supply grant.");
            }
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task TechnicalRestartSaveFailureRetainsLiveBasesForRetry()
    {
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var maps = Server.System<SharedMapSystem>();
        var data = Server.ResolveDependency<IResourceManager>().UserData;
        var territoryIds = new[] { "frontline-one", "frontline-three", "frontline-five" };
        EntityUid oldMap = default;
        EntityUid[] bases = default!;
        Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>[] counts = default!;
        WarState state = default!;
        Exception failure = null;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            war.StartNewWar();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();
        try
        {
            await Server.WaitPost(() =>
            {
                // Post exceptions kill the server; retain assertion failures for the test thread.
                try
                {
                    Assert.That(Server.System<TownHallSystem>().ForceCapture(new TerritoryId("frontline-three"),
                        new FactionId("FrontlineFactionOne"), ticker.DefaultMap), Is.True);
                    oldMap = maps.GetMapOrInvalid(ticker.DefaultMap);
                    state = war.State!;
                    bases = territoryIds.Select(id => FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                        hall => hall.TerritoryId == id)).ToArray();
                    counts = bases.Select((uid, i) => new Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>
                        { ["SoldierSupplies"] = i * 7, ["BasicMaterials"] = 11 + i * 12 }).ToArray();
                    for (var i = 0; i < bases.Length; i++)
                    {
                        var live = SComp<FrontlineStockpileComponent>(bases[i]).Counts;
                        live.Clear();
                        foreach (var (product, count) in counts[i])
                            live[product] = count;
                    }
                    data.Delete(WarStrategicSnapshotSystem.TemporaryPath);
                    data.CreateDir(WarStrategicSnapshotSystem.TemporaryPath);
                    Assert.That(data.IsDir(WarStrategicSnapshotSystem.TemporaryPath), Is.True);
                    // Native VirtualWritableDirProvider.Open(Create) rejects a directory with ArgumentException.
                    Assert.Multiple(() =>
                    {
                        Assert.Throws<ArgumentException>(() => ticker.RestartRound(),
                            "A failed save must escape native cleanup before FlushEntities.");
                        Assert.That(Server.UnhandledException, Is.Null);
                        Assert.That(SEntMan.EntityExists(oldMap), Is.True);
                        Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.EqualTo(oldMap));
                        Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby),
                            "RestartRound changes RunLevel before cleanup; retry must work from this partial transition.");
                        Assert.That(ticker.CurrentPreset?.ID, Is.EqualTo("PersistentWar"));
                        Assert.That(war.State, Is.EqualTo(state));
                        for (var i = 0; i < bases.Length; i++)
                        {
                            Assert.That(SEntMan.EntityExists(bases[i]), Is.True);
                            if (SEntMan.EntityExists(bases[i]))
                            {
                                Assert.That(FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                                    hall => hall.TerritoryId == territoryIds[i]), Is.EqualTo(bases[i]));
                                Assert.That(SComp<FrontlineStockpileComponent>(bases[i]).Counts, Is.EquivalentTo(counts[i]));
                            }
                        }
                    });
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    data.Delete(WarStrategicSnapshotSystem.TemporaryPath);
                }
            });
            Assert.That(Server.UnhandledException, Is.Null);
            if (failure != null)
                throw failure;

            await Server.WaitPost(() =>
            {
                // A later successful cleanup must still track this map and save its current, not stale, counts.
                counts[1]["BasicMaterials"] = 53;
                SComp<FrontlineStockpileComponent>(bases[1]).Counts["BasicMaterials"] = 53;
                ticker.RestartRound();
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
            });
            await Pair.RunUntilSynced();
            await Server.WaitAssertion(() =>
            {
                Assert.That(Server.UnhandledException, Is.Null);
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
                Assert.That(war.State, Is.EqualTo(state));
                Assert.That(SEntMan.EntityExists(oldMap), Is.False);
                Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.Not.EqualTo(oldMap));
                for (var i = 0; i < bases.Length; i++)
                {
                    Assert.That(SEntMan.EntityExists(bases[i]), Is.False);
                    var restored = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                        hall => hall.TerritoryId == territoryIds[i]);
                    Assert.That(SComp<TownHallComponent>(restored).FactionId,
                        Is.EqualTo(i == 2 ? "FrontlineFactionTwo" : "FrontlineFactionOne"));
                    Assert.That(SComp<FrontlineStockpileComponent>(restored).Counts, Is.EquivalentTo(counts[i]));
                }
            });
        }
        finally
        {
            await Server.WaitPost(() => data.Delete(WarStrategicSnapshotSystem.TemporaryPath));
        }
    }

    [TestCase(1, 0)]
    [TestCase(2, 41)]
    public async Task MalformedStrategicHeaderCannotBypassValidationAsAnotherWar(int version, int savedWarId)
    {
        const int currentWarId = 42;
        var snapshot = Server.System<WarStrategicSnapshotSystem>();
        var data = Server.ResolveDependency<IResourceManager>().UserData;
        var map = await Pair.LoadTestMap(new ResPath("/Maps/Frontline/base_test.yaml"));
        Exception failure = null;
        try
        {
            await Server.WaitPost(() =>
            {
                try
                {
                    Server.System<PersistentWarMapValidatorSystem>().Validate(map.MapUid);
                    var objectives = Server.System<TerritorySystem>().GetTerritories(map.MapId).ToDictionary(id => id.Id,
                        id => id.Id is "frontline-one" or "frontline-five"
                            ? FindMapEntity<TownHallComponent>(map.MapId, hall => hall.TerritoryId == id.Id)
                            : FindMapEntity<TownHallRuinComponent>(map.MapId, ruin => ruin.TerritoryId == id.Id));
                    var bases = objectives.Select(entry =>
                    {
                        var hall = SEntMan.TryGetComponent<TownHallComponent>(entry.Value, out var component);
                        return new WarBaseSnapshot(entry.Key, hall ? "hall" : "ruin",
                            SComp<MetaDataComponent>(entry.Value).EntityPrototype!.ID, hall ? component!.FactionId : null,
                            hall ? SComp<FrontlineStockpileComponent>(entry.Value).Counts
                                .ToDictionary(count => count.Key.Id, count => count.Value) : new Dictionary<string, int>());
                    }).ToList();
                    data.Delete(WarStrategicSnapshotSystem.BackupPath);
                    using (var stream = data.OpenWrite(WarStrategicSnapshotSystem.SavePath))
                        JsonSerializer.Serialize(stream, new WarStrategicSnapshot(version, savedWarId, bases));
                    Assert.Multiple(() =>
                    {
                        Assert.Throws<InvalidDataException>(() => snapshot.Restore(map.MapUid, currentWarId));
                        foreach (var entry in bases)
                        {
                            var uid = entry.ObjectiveKind == "hall"
                                ? FindMapEntity<TownHallComponent>(map.MapId, hall => hall.TerritoryId == entry.TerritoryId)
                                : FindMapEntity<TownHallRuinComponent>(map.MapId, ruin => ruin.TerritoryId == entry.TerritoryId);
                            Assert.That(uid, Is.EqualTo(objectives[entry.TerritoryId]));
                            if (entry.ObjectiveKind == "hall")
                                Assert.That(SComp<FrontlineStockpileComponent>(uid).Counts,
                                    Is.EquivalentTo(entry.Counts.ToDictionary(count => new ProtoId<FrontlineSupplyProductPrototype>(count.Key), count => count.Value)));
                        }
                    });
                    // Repaired JSON must be read on this same map: refusal must not set _loadedMap.
                    foreach (var entry in bases.Where(entry => entry.ObjectiveKind == "hall"))
                        entry.Counts["SoldierSupplies"] = 7;
                    using (var stream = data.OpenWrite(WarStrategicSnapshotSystem.SavePath))
                        JsonSerializer.Serialize(stream, new WarStrategicSnapshot(1, currentWarId, bases));
                    snapshot.Restore(map.MapUid, currentWarId);
                    foreach (var entry in bases)
                    {
                        Assert.That(SEntMan.EntityExists(objectives[entry.TerritoryId]), Is.False);
                        if (entry.ObjectiveKind == "hall")
                        {
                            var restored = FindMapEntity<TownHallComponent>(map.MapId, hall => hall.TerritoryId == entry.TerritoryId);
                            Assert.That(SComp<FrontlineStockpileComponent>(restored).Counts,
                                Is.EquivalentTo(entry.Counts.ToDictionary(count => new ProtoId<FrontlineSupplyProductPrototype>(count.Key), count => count.Value)));
                        }
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            Assert.That(Server.UnhandledException, Is.Null);
            if (failure != null)
                throw failure;
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                data.Delete(WarStrategicSnapshotSystem.SavePath);
                data.Delete(WarStrategicSnapshotSystem.TemporaryPath);
                data.Delete(WarStrategicSnapshotSystem.BackupPath);
            });
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task NewWarStartsNewPersistentDayNightPhase()
    {
        var ticker = Server.System<GameTicker>();
        var resources = Server.ResolveDependency<IResourceManager>();
        var war = Server.System<WarStateSystem>();
        var oldStartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(7);
        WarState newWar = default!;

        await Server.WaitPost(() =>
        {
            resources.UserData.Delete(WarStateSystem.SavePath);
            using (var stream = resources.UserData.OpenWrite(WarStateSystem.SavePath))
                JsonSerializer.Serialize(stream, new WarState(41, WarStatus.Active, oldStartedAt));

            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);

            var oldMap = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            Assert.That(SComp<LightCycleComponent>(oldMap).Offset, Is.GreaterThan(TimeSpan.FromMinutes(6)));
            newWar = war.StartNewWar();

            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() =>
        {
            Assert.That(newWar.WarId, Is.EqualTo(42));
            Assert.That(newWar.StartedAt, Is.GreaterThan(oldStartedAt));
            Assert.That(war.State, Is.EqualTo(newWar));

            var map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            Assert.That(SComp<LightCycleComponent>(map).Offset, Is.LessThan(TimeSpan.FromMinutes(1)));
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task StartingHomeSupplyReserveIsGrantedOnlyOnce()
    {
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var factions = Server.System<WarFactionSystem>();
        var validator = Server.System<PersistentWarMapValidatorSystem>();
        var stacks = Server.System<StackSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var interaction = Server.System<SharedInteractionSystem>();
        var session = ServerSession!;
        var faction = new FactionId("FrontlineFactionOne");
        EntityUid home = default;
        EntityUid enemy = default;
        EntityUid map = default;
        EntityUid body = default;
        EntityUid ruin = default;
        int warId = default;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            war.StartNewWar();
            factions.ClearFaction(session.UserId);
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
            map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            warId = war.State!.WarId;
            home = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-one" && hall.FactionId == faction.Id);
            enemy = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-five" && hall.FactionId == "FrontlineFactionTwo");
        });
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(war.State!.Status, Is.EqualTo(WarStatus.Active));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts.GetValueOrDefault("SoldierSupplies"),
                Is.EqualTo(20), "Faction one's starting home must receive exactly 20 SoldierSupplies.");
            Assert.That(SComp<FrontlineStockpileComponent>(enemy).Counts.GetValueOrDefault("SoldierSupplies"),
                Is.EqualTo(20), "Faction two's starting home must receive exactly 20 SoldierSupplies.");
            var neutralRuins = 0;
            var ruins = SEntMan.EntityQueryEnumerator<TownHallRuinComponent, TransformComponent>();
            while (ruins.MoveNext(out var uid, out _, out var transform))
            {
                if (transform.MapID != ticker.DefaultMap)
                    continue;
                neutralRuins++;
                if (SEntMan.TryGetComponent<FrontlineStockpileComponent>(uid, out var stockpile))
                    Assert.That(stockpile.Counts.GetValueOrDefault("SoldierSupplies"), Is.Zero);
            }
            Assert.That(neutralRuins, Is.EqualTo(3));
            var stockpiles = SEntMan.EntityQueryEnumerator<FrontlineStockpileComponent, TransformComponent>();
            while (stockpiles.MoveNext(out var uid, out var stockpile, out var transform))
            {
                if (transform.MapID == ticker.DefaultMap && uid != home && uid != enemy)
                    Assert.That(stockpile.Counts.GetValueOrDefault("SoldierSupplies"), Is.Zero,
                        "Only the two initial home cores may receive a reserve.");
            }
        }));

        await Server.WaitPost(() =>
        {
            SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"] = 7;
            SComp<FrontlineStockpileComponent>(enemy).Counts["SoldierSupplies"] = 7;
            Assert.That(factions.TrySelectFaction(session.UserId, faction), Is.True);
            validator.Validate(map);
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            body = session.AttachedEntity!.Value;
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            validator.Validate(map);
        });
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap), Is.EqualTo(map));
            Assert.That(war.State!.WarId, Is.EqualTo(warId));
            Assert.That(session.AttachedEntity, Is.EqualTo(body));
            Assert.That(SComp<MobStateComponent>(body).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts.GetValueOrDefault("SoldierSupplies"),
                Is.EqualTo(7), "Initial/repeated deployment and validation must not spend or top up the reserve.");
            Assert.That(SComp<FrontlineStockpileComponent>(enemy).Counts.GetValueOrDefault("SoldierSupplies"), Is.EqualTo(7));
        }));

        await Server.WaitPost(() =>
        {
            var coordinates = SComp<TransformComponent>(home).Coordinates;
            SEntMan.System<SharedTransformSystem>().SetCoordinates(body, coordinates);
            SEntMan.DeleteEntity(home);
            ruin = FindMapEntity<TownHallRuinComponent>(ticker.DefaultMap,
                objective => objective.TerritoryId == "frontline-one");
            SComp<TownHallRuinComponent>(ruin).RequiredBasicMaterials = 1;
            var materials = stacks.SpawnAtPosition(1, "BasicMaterials", coordinates);
            Assert.That(hands.TryPickupAnyHand(body, materials), Is.True);
            Assert.That(interaction.InteractDoAfter(body, materials, ruin, coordinates, true), Is.True);
        });
        await Pair.RunSeconds(2.1f);
        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(SEntMan.EntityExists(home), Is.False);
            Assert.That(SEntMan.EntityExists(ruin), Is.False);
            var replacement = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-one" && hall.FactionId == faction.Id);
            Assert.That(replacement, Is.Not.EqualTo(home));
            Assert.That(SComp<FrontlineStockpileComponent>(replacement).Counts.GetValueOrDefault("SoldierSupplies"),
                Is.Zero, "Repairing a destroyed home must not mint another starting reserve.");
            Assert.That(SComp<FrontlineStockpileComponent>(enemy).Counts.GetValueOrDefault("SoldierSupplies"), Is.EqualTo(7));
            Assert.That(war.State!.WarId, Is.EqualTo(warId));
        }));
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task DeadRespawnWithoutSoldierSuppliesPreservesBodyAndMind()
    {
        var ticker = Server.System<GameTicker>();
        var factions = Server.System<WarFactionSystem>();
        var minds = Server.System<MindSystem>();
        var mobState = Server.System<MobStateSystem>();
        var lifecycle = Server.System<WarPlayerLifecycleSystem>();
        var session = ServerSession!;
        var account = session.UserId;
        EntityUid home = default;
        EntityUid body = default;
        EntityUid? mindId = default;
        EntityUid? attachment = default;
        var respawned = false;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitPost(() =>
        {
            factions.ClearFaction(account);
            Assert.That(factions.TrySelectFaction(account, new FactionId("FrontlineFactionOne")), Is.True);
            home = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-one" && hall.FactionId == "FrontlineFactionOne");
            SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"] = 0;
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            attachment = session.AttachedEntity;
            body = attachment!.Value;
            Assert.That(minds.TryGetMind(account, out mindId, out _), Is.True);
            mobState.ChangeMobState(body, MobState.Dead);
            respawned = lifecycle.RequestRespawn(account);
        });

        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(respawned, Is.False);
            Assert.That(SEntMan.EntityExists(body), Is.True);
            SEntMan.TryGetComponent<MobStateComponent>(body, out var state);
            Assert.That(state?.CurrentState, Is.EqualTo(MobState.Dead));
            Assert.That(minds.TryGetMind(account, out var currentMindId, out var mind), Is.True);
            Assert.That(currentMindId, Is.EqualTo(mindId));
            Assert.That(mind?.UserId, Is.EqualTo(account));
            Assert.That(mind?.OwnedEntity, Is.EqualTo(body));
            Assert.That(mind?.CurrentEntity, Is.EqualTo(body));
            Assert.That(session.AttachedEntity, Is.EqualTo(attachment));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.Zero);
        }));
    }

    [TestCase("UnknownTerritory")]
    [TestCase("EnemyFunded")]
    [TestCase("EmptyHome")]
    [TestCase("QueuedHall")]
    [TestCase("DeletedHall")]
    [TestCase("QueuedSpawn")]
    [TestCase("DeletedSpawn")]
    [TestCase("UnavailableSpawn")]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task SelectedBaseRefusalPreservesBodyMindAndSupplies(string invalidCase)
    {
        var ticker = Server.System<GameTicker>();
        var factions = Server.System<WarFactionSystem>();
        var minds = Server.System<MindSystem>();
        var mobState = Server.System<MobStateSystem>();
        var lifecycle = Server.System<WarPlayerLifecycleSystem>();
        var halls = Server.System<TownHallSystem>();
        var session = ServerSession!;
        var account = session.UserId;
        var faction = new FactionId("FrontlineFactionOne");
        EntityUid home = default;
        EntityUid enemy = default;
        EntityUid body = default;
        EntityUid? mindId = default;
        EntityUid? attachment = default;
        FrontlineStockpileComponent[] stockpiles = default!;
        Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>[] countsBefore = default!;
        var respawned = false;
        var entitiesBefore = 0;
        var entitiesAfter = 0;
        var mobsBefore = 0;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitPost(() =>
        {
            factions.ClearFaction(account);
            Assert.That(factions.TrySelectFaction(account, faction), Is.True);
            home = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-one" && hall.FactionId == faction.Id);
            enemy = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-five" && hall.FactionId == "FrontlineFactionTwo");
            SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"] = 2;
            SComp<FrontlineStockpileComponent>(enemy).Counts["SoldierSupplies"] = 5;
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            attachment = session.AttachedEntity;
            body = attachment!.Value;
            Assert.That(minds.TryGetMind(account, out mindId, out _), Is.True);
        });
        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(SComp<MobStateComponent>(body).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(2),
                "Initial deployment must be free.");
        }));

        await Server.WaitPost(() =>
        {
            // Keep a funded alternative even when the selected home or its spawn is removed.
            Assert.That(halls.ForceCapture(new TerritoryId("frontline-three"), faction, ticker.DefaultMap), Is.True);
            var other = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-three" && hall.FactionId == faction.Id);
            SComp<FrontlineStockpileComponent>(other).Counts["SoldierSupplies"] = 2;
            stockpiles = new[]
            {
                SComp<FrontlineStockpileComponent>(home),
                SComp<FrontlineStockpileComponent>(enemy),
                SComp<FrontlineStockpileComponent>(other),
            };
            mobState.ChangeMobState(body, MobState.Dead);
            var selected = "frontline-one";
            switch (invalidCase)
            {
                case "UnknownTerritory":
                    selected = "unlisted";
                    break;
                case "EnemyFunded":
                    selected = "frontline-five";
                    break;
                case "EmptyHome":
                    stockpiles[0].Counts["SoldierSupplies"] = 0;
                    break;
                case "QueuedHall":
                    SEntMan.QueueDeleteEntity(home);
                    break;
                case "DeletedHall":
                    SEntMan.DeleteEntity(home);
                    break;
                case "QueuedSpawn":
                case "DeletedSpawn":
                case "UnavailableSpawn":
                    var spawn = FindMapEntity<FactionSpawnPointComponent>(ticker.DefaultMap,
                        point => point.TerritoryId == "frontline-one");
                    if (invalidCase == "QueuedSpawn")
                        SEntMan.QueueDeleteEntity(spawn);
                    else if (invalidCase == "DeletedSpawn")
                        SEntMan.DeleteEntity(spawn);
                    else
                        SEntMan.RemoveComponent<FactionSpawnPointComponent>(spawn);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null);
            }

            countsBefore = stockpiles.Select(stockpile => new Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>(stockpile.Counts)).ToArray();
            entitiesBefore = SEntMan.Count<MetaDataComponent>();
            mobsBefore = SEntMan.Count<MobStateComponent>();
            respawned = lifecycle.RequestRespawn(account, new TerritoryId(selected));
            entitiesAfter = SEntMan.Count<MetaDataComponent>();
        });

        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(respawned, Is.False);
            Assert.That(SEntMan.EntityExists(body), Is.True);
            Assert.That(SEntMan.IsQueuedForDeletion(body), Is.False);
            Assert.That(SComp<MobStateComponent>(body).CurrentState, Is.EqualTo(MobState.Dead));
            Assert.That(minds.TryGetMind(account, out var currentMindId, out var mind), Is.True);
            Assert.That(currentMindId, Is.EqualTo(mindId));
            Assert.That(mind?.UserId, Is.EqualTo(account));
            Assert.That(mind?.OwnedEntity, Is.EqualTo(body));
            Assert.That(mind?.CurrentEntity, Is.EqualTo(body));
            Assert.That(session.AttachedEntity, Is.EqualTo(attachment));
            Assert.That(SComp<ActorComponent>(body).PlayerSession, Is.SameAs(session));
            for (var i = 0; i < stockpiles.Length; i++)
                Assert.That(stockpiles[i].Counts, Is.EquivalentTo(countsBefore[i]), "Refusal must not debit any base.");
            Assert.That(entitiesAfter, Is.EqualTo(entitiesBefore), "Refusal must not create a replacement or its gear.");
            Assert.That(SEntMan.Count<MobStateComponent>(), Is.EqualTo(mobsBefore));
        }));
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task MindAddedCallbackRejectionRefundsSupplyAndRestoresCorpseForRetry()
    {
        var ticker = Server.System<GameTicker>();
        var factions = Server.System<WarFactionSystem>();
        var minds = Server.System<MindSystem>();
        var lifecycle = Server.System<WarPlayerLifecycleSystem>();
        var probe = Server.System<QueueRespawnOnMindAddedSystem>();
        var session = ServerSession!;
        var account = session.UserId;
        var territory = new TerritoryId("frontline-one");
        EntityUid home = default;
        EntityUid corpse = default;
        EntityUid retryBody = default;
        EntityUid? mindId = default;
        var respawned = false;
        var queuedAtReturn = false;
        var attachedAtReturn = false;
        var mobsBefore = 0;
        var ghostsBefore = 0;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            Server.System<WarStateSystem>().StartNewWar();
            factions.ClearFaction(account);
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitPost(() =>
        {
            Assert.That(factions.TrySelectFaction(account, new FactionId("FrontlineFactionOne")), Is.True);
            home = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == territory.Id && hall.FactionId == "FrontlineFactionOne");
            SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"] = 2;
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            corpse = session.AttachedEntity!.Value;
            Assert.That(minds.TryGetMind(account, out mindId, out _), Is.True);
            Assert.That(SComp<MobStateComponent>(corpse).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(2));
            Server.System<MobStateSystem>().ChangeMobState(corpse, MobState.Dead);
            mobsBefore = SEntMan.Count<MobStateComponent>();
            ghostsBefore = SEntMan.Count<GhostComponent>();
            probe.Corpse = corpse;
            probe.Mind = mindId!.Value;
            probe.Home = home;
            probe.Enabled = true;
            try
            {
                respawned = lifecycle.RequestRespawn(account, territory);
                queuedAtReturn = SEntMan.IsQueuedForDeletion(probe.Replacement);
                attachedAtReturn = session.AttachedEntity == corpse;
            }
            finally
            {
                probe.Enabled = false;
            }
        });
        // Drain the queued replacement before assertions, including on a behavioral RED.
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(probe.Fired, Is.True, "The real transfer must reach MindAddedMessage on the replacement.");
            Assert.That(probe.AttachedDuringCallback, Is.True,
                "Rejection must happen after native mind ownership and session attachment, not during staging.");
            Assert.That(probe.SuppliesDuringCallback, Is.EqualTo(1), "The callback must run inside the paid reservation.");
            Assert.That(respawned, Is.False);
            Assert.That(queuedAtReturn, Is.True);
            Assert.That(attachedAtReturn, Is.True, "Rollback must reattach the corpse before returning.");
            Assert.That(probe.Replacement, Is.Not.EqualTo(corpse));
            Assert.That(SEntMan.EntityExists(probe.Replacement), Is.False);
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(2));
            Assert.That(SEntMan.EntityExists(corpse), Is.True);
            Assert.That(SEntMan.IsQueuedForDeletion(corpse), Is.False);
            Assert.That(SComp<MobStateComponent>(corpse).CurrentState, Is.EqualTo(MobState.Dead));
            Assert.That(minds.TryGetMind(account, out var currentMindId, out var mind), Is.True);
            Assert.That(currentMindId, Is.EqualTo(mindId));
            Assert.That(mind?.UserId, Is.EqualTo(account));
            Assert.That(mind?.OwnedEntity, Is.EqualTo(corpse));
            Assert.That(mind?.CurrentEntity, Is.EqualTo(corpse));
            Assert.That(session.AttachedEntity, Is.EqualTo(corpse));
            Assert.That(SComp<ActorComponent>(corpse).PlayerSession, Is.SameAs(session));
            Assert.That(SEntMan.Count<GhostComponent>(), Is.EqualTo(ghostsBefore), "Rollback must not create a ghost.");
            Assert.That(SEntMan.Count<MobStateComponent>(), Is.EqualTo(mobsBefore), "No extra body may survive rejection.");
        }));

        await Server.WaitPost(() =>
        {
            Assert.That(lifecycle.RequestRespawn(account, territory), Is.True, "Refund and waiting state must permit a funded retry.");
            retryBody = session.AttachedEntity!.Value;
        });
        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(retryBody, Is.Not.EqualTo(corpse));
            Assert.That(retryBody, Is.Not.EqualTo(probe.Replacement));
            Assert.That(SEntMan.EntityExists(corpse), Is.False);
            Assert.That(SEntMan.IsQueuedForDeletion(retryBody), Is.False);
            Assert.That(SComp<MobStateComponent>(retryBody).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(1));
            Assert.That(minds.TryGetMind(account, out var currentMindId, out var mind), Is.True);
            Assert.That(currentMindId, Is.EqualTo(mindId));
            Assert.That(mind?.UserId, Is.EqualTo(account));
            Assert.That(mind?.OwnedEntity, Is.EqualTo(retryBody));
            Assert.That(mind?.CurrentEntity, Is.EqualTo(retryBody));
            Assert.That(session.AttachedEntity, Is.EqualTo(retryBody));
            Assert.That(SComp<ActorComponent>(retryBody).PlayerSession, Is.SameAs(session));
            Assert.That(SEntMan.Count<GhostComponent>(), Is.EqualTo(ghostsBefore));
            Assert.That(SEntMan.Count<MobStateComponent>(), Is.EqualTo(mobsBefore));
        }));
    }

    public sealed class QueueRespawnOnMindAddedSystem : EntitySystem
    {
        public bool Enabled;
        public bool Fired;
        public EntityUid Corpse;
        public EntityUid Mind;
        public EntityUid Home;
        public EntityUid Replacement;
        public bool AttachedDuringCallback;
        public int SuppliesDuringCallback;

        public override void Initialize()
        {
            // MobState is already on every eligible replacement; this native event pair is otherwise unclaimed.
            SubscribeLocalEvent<MobStateComponent, MindAddedMessage>(OnMindAdded);
        }

        private void OnMindAdded(EntityUid uid, MobStateComponent component, MindAddedMessage args)
        {
            if (!Enabled || Fired || uid == Corpse || args.TransferEntity != Corpse || args.Mind.Owner != Mind)
                return;

            Fired = true;
            Replacement = uid;
            AttachedDuringCallback = args.Mind.Comp.OwnedEntity == uid && args.Mind.Comp.CurrentEntity == uid &&
                TryComp<ActorComponent>(uid, out var actor) && actor.PlayerSession.AttachedEntity == uid;
            SuppliesDuringCallback = Comp<FrontlineStockpileComponent>(Home).Counts["SoldierSupplies"];
            QueueDel(uid);
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task DeadRespawnConsumesOneSoldierSupplyAndKeepsExistingMind()
    {
        var ticker = Server.System<GameTicker>();
        var factions = Server.System<WarFactionSystem>();
        var minds = Server.System<MindSystem>();
        var mobState = Server.System<MobStateSystem>();
        var lifecycle = Server.System<WarPlayerLifecycleSystem>();
        var territories = Server.System<TerritorySystem>();
        var session = ServerSession!;
        var account = session.UserId;
        var faction = new FactionId("FrontlineFactionOne");
        var territory = new TerritoryId("frontline-one");
        EntityUid home = default;
        EntityUid oldBody = default;
        EntityUid newBody = default;
        EntityUid? mindId = default;
        EntityUid? attachment = default;
        var respawned = false;
        var repeated = false;
        var suppliesAfterRespawn = 0;
        var entitiesAfterRespawn = 0;
        var entitiesAfterRepeat = 0;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitPost(() =>
        {
            factions.ClearFaction(account);
            Assert.That(factions.TrySelectFaction(account, faction), Is.True);
            home = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == territory.Id && hall.FactionId == faction.Id);
            SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"] = 2;
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            attachment = session.AttachedEntity;
            oldBody = attachment!.Value;
            Assert.That(minds.TryGetMind(account, out mindId, out _), Is.True);
        });

        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(2),
                "Initial deployment must be free.");
            Assert.That(SComp<MobStateComponent>(oldBody).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(session.AttachedEntity, Is.EqualTo(attachment));
            Assert.That(territories.CountOwned(faction, ticker.DefaultMap), Is.EqualTo(1));
        }));

        await Server.WaitPost(() =>
        {
            mobState.ChangeMobState(oldBody, MobState.Dead);
            respawned = lifecycle.RequestRespawn(account);
            newBody = session.AttachedEntity!.Value;
            suppliesAfterRespawn = SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"];
            entitiesAfterRespawn = SEntMan.Count<MetaDataComponent>();
            repeated = lifecycle.RequestRespawn(account);
            entitiesAfterRepeat = SEntMan.Count<MetaDataComponent>();
        });

        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(respawned, Is.True);
            Assert.That(suppliesAfterRespawn, Is.EqualTo(1), "Dead respawn must consume exactly one SoldierSupply.");
            Assert.That(SEntMan.EntityExists(oldBody), Is.False);
            Assert.That(newBody, Is.Not.EqualTo(oldBody));
            Assert.That(SEntMan.EntityExists(newBody), Is.True);
            Assert.That(SEntMan.IsQueuedForDeletion(newBody), Is.False);
            Assert.That(SComp<MetaDataComponent>(newBody).EntityLifeStage, Is.LessThan(EntityLifeStage.Terminating));
            Assert.That(SComp<MobStateComponent>(newBody).CurrentState, Is.EqualTo(MobState.Alive));
            var transform = SComp<TransformComponent>(newBody);
            Assert.That(transform.MapID, Is.EqualTo(ticker.DefaultMap));
            Assert.That(territories.Contains(territory, transform.Coordinates), Is.True);
            Assert.That(territories.TryGetOwner(territory, out var owner, ticker.DefaultMap), Is.True);
            Assert.That(owner, Is.EqualTo(faction));
            Assert.That(territories.CountOwned(faction, ticker.DefaultMap), Is.EqualTo(1));
            Assert.That(factions.TryGetFaction(account, out var selectedFaction), Is.True);
            Assert.That(selectedFaction, Is.EqualTo(faction));
            Assert.That(minds.TryGetMind(account, out var currentMindId, out var mind), Is.True);
            Assert.That(currentMindId, Is.EqualTo(mindId));
            Assert.That(mind?.UserId, Is.EqualTo(account));
            Assert.That(mind?.OwnedEntity, Is.EqualTo(newBody));
            Assert.That(mind?.CurrentEntity, Is.EqualTo(newBody));
            Assert.That(session.AttachedEntity, Is.EqualTo(newBody));
            Assert.That(repeated, Is.False);
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(1));
            Assert.That(entitiesAfterRepeat, Is.EqualTo(entitiesAfterRespawn));
        }));
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task DeadRespawnChoiceListsOwnedBaseAndRespawnsThroughClientButton()
    {
        var ticker = Server.System<GameTicker>();
        var factions = Server.System<WarFactionSystem>();
        var minds = Server.System<MindSystem>();
        var mobState = Server.System<MobStateSystem>();
        var territories = Server.System<TerritorySystem>();
        var ui = Client.ResolveDependency<IUserInterfaceManager>();
        var localization = Client.ResolveDependency<ILocalizationManager>();
        var session = ServerSession!;
        var account = session.UserId;
        var faction = new FactionId("FrontlineFactionOne");
        var territory = new TerritoryId("frontline-one");
        ProtoId<FrontlineTerritoryPrototype> territoryPrototype = territory.Id;
        EntityUid home = default;
        EntityUid enemy = default;
        EntityUid oldBody = default;
        EntityUid? mindId = default;
        Button respawn = default!;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitPost(() =>
        {
            factions.ClearFaction(account);
            Assert.That(factions.TrySelectFaction(account, faction), Is.True);
            home = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == territory.Id && hall.FactionId == faction.Id);
            enemy = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == "frontline-five" && hall.FactionId == "FrontlineFactionTwo");
            SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"] = 2;
            SComp<FrontlineStockpileComponent>(enemy).Counts["SoldierSupplies"] = 5;
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            oldBody = session.AttachedEntity!.Value;
            Assert.That(minds.TryGetMind(account, out mindId, out _), Is.True);
        });
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<MobStateComponent>(oldBody).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(2),
                "Initial deployment must be free.");
        });

        await Server.WaitPost(() => mobState.ChangeMobState(oldBody, MobState.Dead));
        await Pair.RunUntilSynced();
        await Client.WaitAssertion(() =>
        {
            var windows = ui.WindowRoot.Children.OfType<RespawnChoiceWindow>()
                .Where(window => window.IsOpen).ToArray();
            Assert.That(windows, Has.Length.EqualTo(1), "Death must open the real respawn EUI window.");
            var buttons = Descendants(windows.Single()).OfType<Button>().ToArray();
            var homeButtons = buttons.Where(button => button.Name == "Respawn:frontline-one").ToArray();
            Assert.That(homeButtons, Has.Length.EqualTo(1),
                "The respawn EUI must advertise the funded owned home base, not a generic respawn button.");
            respawn = homeButtons.Single();
            Assert.That(respawn.Text, Does.Contain(localization.GetString(CProtoMan.Index(territoryPrototype).Name)));
            Assert.That(respawn.Text, Does.Contain("2"), "The base choice must show its SoldierSupplies count.");
            Assert.That(respawn.Disabled, Is.False);
            Assert.That(buttons.Any(button => button.Name == "Respawn:frontline-five"), Is.False,
                "A funded enemy base must not be offered.");
            Assert.That(buttons.Any(button =>
                button.Text == localization.GetString("frontline-respawn-choice-wait") && !button.Disabled), Is.True);
        });

        // Use the same native key down/up path as InteractionTest.ClickControl, not a server message.
        var screenCoords = new ScreenCoordinates(
            respawn.GlobalPixelPosition + respawn.PixelSize / 2,
            respawn.Window?.Id ?? default);
        var relativePos = screenCoords.Position / respawn.UIScale - respawn.GlobalPosition;
        var relativePixelPos = screenCoords.Position - respawn.GlobalPixelPosition;
        foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
        {
            await Client.DoGuiEvent(respawn, new GUIBoundKeyEventArgs(
                EngineKeyFunctions.UIClick, state, screenCoords, default, relativePos, relativePixelPos));
            await Pair.RunTicksSync(1);
        }
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            var newBody = session.AttachedEntity;
            Assert.That(newBody, Is.Not.Null);
            Assert.That(newBody, Is.Not.EqualTo(oldBody));
            Assert.That(SEntMan.EntityExists(oldBody), Is.False);
            Assert.That(SEntMan.EntityExists(newBody!.Value), Is.True);
            Assert.That(SComp<MobStateComponent>(newBody.Value).CurrentState, Is.EqualTo(MobState.Alive));
            var transform = SComp<TransformComponent>(newBody.Value);
            Assert.That(transform.MapID, Is.EqualTo(ticker.DefaultMap));
            Assert.That(territories.Contains(territory, transform.Coordinates), Is.True);
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(1),
                "Clicking the home base must debit exactly one SoldierSupply.");
            Assert.That(SComp<FrontlineStockpileComponent>(enemy).Counts["SoldierSupplies"], Is.EqualTo(5));
            Assert.That(minds.TryGetMind(account, out var currentMindId, out var mind), Is.True);
            Assert.That(currentMindId, Is.EqualTo(mindId));
            Assert.That(mind?.UserId, Is.EqualTo(account));
            Assert.That(mind?.OwnedEntity, Is.EqualTo(newBody));
            Assert.That(mind?.CurrentEntity, Is.EqualTo(newBody));
        }));

        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (var child in parent.Children)
            {
                yield return child;
                foreach (var descendant in Descendants(child))
                    yield return descendant;
            }
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task DeadRespawnChoiceRefreshesOpenWindowAfterStockpileReplenishment()
    {
        var ticker = Server.System<GameTicker>();
        var factions = Server.System<WarFactionSystem>();
        var minds = Server.System<MindSystem>();
        var mobState = Server.System<MobStateSystem>();
        var territories = Server.System<TerritorySystem>();
        var hands = Server.System<SharedHandsSystem>();
        var stockpiles = Server.System<FrontlineStockpileSystem>();
        var ui = Client.ResolveDependency<IUserInterfaceManager>();
        var localization = Client.ResolveDependency<ILocalizationManager>();
        var session = ServerSession!;
        var account = session.UserId;
        var faction = new FactionId("FrontlineFactionOne");
        var territory = new TerritoryId("frontline-one");
        ProtoId<FrontlineTerritoryPrototype> territoryPrototype = territory.Id;
        EntityUid home = default;
        EntityUid oldBody = default;
        EntityUid? mindId = default;
        RespawnChoiceWindow window = default!;
        Button respawn = default!;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitPost(() =>
        {
            factions.ClearFaction(account);
            Assert.That(factions.TrySelectFaction(account, faction), Is.True);
            home = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                hall => hall.TerritoryId == territory.Id && hall.FactionId == faction.Id);
            SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"] = 0;
            ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
            oldBody = session.AttachedEntity!.Value;
            Assert.That(minds.TryGetMind(account, out mindId, out _), Is.True);
        });
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<MobStateComponent>(oldBody).CurrentState, Is.EqualTo(MobState.Alive));
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.Zero,
                "Initial deployment must remain free at an empty base.");
        });

        await Server.WaitPost(() => mobState.ChangeMobState(oldBody, MobState.Dead));
        await Pair.RunUntilSynced();
        await Client.WaitAssertion(() =>
        {
            var windows = ui.WindowRoot.Children.OfType<RespawnChoiceWindow>()
                .Where(entry => entry.IsOpen).ToArray();
            Assert.That(windows, Has.Length.EqualTo(1));
            window = windows.Single();
            respawn = Descendants(window).OfType<Button>()
                .Single(button => button.Name == "Respawn:frontline-one");
            Assert.That(respawn.Disabled, Is.True);
            Assert.That(respawn.Text, Is.EqualTo(localization.GetString("frontline-respawn-choice-base",
                ("base", localization.GetString(CProtoMan.Index(territoryPrototype).Name)), ("count", 0))));
        });

        await Server.WaitPost(() =>
        {
            var coordinates = SComp<TransformComponent>(home).Coordinates;
            var courier = SEntMan.SpawnEntity("MobHuman", coordinates);
            var crate = SEntMan.SpawnEntity("FrontlineSupplyCrate", coordinates);
            Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Product.Id, Is.EqualTo("SoldierSupplies"));
            SComp<FrontlineSupplyCrateComponent>(crate).Amount = 2;
            Assert.That(hands.TryPickupAnyHand(courier, crate), Is.True);
            Assert.That(stockpiles.TrySubmitHeld(home, courier), Is.True);
            Assert.That(SEntMan.EntityExists(crate), Is.False);
            SEntMan.DeleteEntity(courier);
        });
        await Pair.RunSeconds(2);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(2));
            Assert.That(session.AttachedEntity, Is.EqualTo(oldBody));
            Assert.That(SComp<MobStateComponent>(oldBody).CurrentState, Is.EqualTo(MobState.Dead));
        });
        await Client.WaitAssertion(() =>
        {
            var windows = ui.WindowRoot.Children.OfType<RespawnChoiceWindow>()
                .Where(entry => entry.IsOpen).ToArray();
            Assert.That(windows, Has.Length.EqualTo(1));
            Assert.That(windows.Single(), Is.SameAs(window), "Replenishment must refresh the still-open death window.");
            respawn = Descendants(window).OfType<Button>()
                .Single(button => button.Name == "Respawn:frontline-one");
            Assert.That(respawn.Disabled, Is.False, "Logistics replenishment must enable the open base choice.");
            Assert.That(respawn.Text, Is.EqualTo(localization.GetString("frontline-respawn-choice-base",
                ("base", localization.GetString(CProtoMan.Index(territoryPrototype).Name)), ("count", 2))));
        });

        var screenCoords = new ScreenCoordinates(
            respawn.GlobalPixelPosition + respawn.PixelSize / 2,
            respawn.Window?.Id ?? default);
        var relativePos = screenCoords.Position / respawn.UIScale - respawn.GlobalPosition;
        var relativePixelPos = screenCoords.Position - respawn.GlobalPixelPosition;
        foreach (var state in new[] { BoundKeyState.Down, BoundKeyState.Up })
        {
            await Client.DoGuiEvent(respawn, new GUIBoundKeyEventArgs(
                EngineKeyFunctions.UIClick, state, screenCoords, default, relativePos, relativePixelPos));
            await Pair.RunTicksSync(1);
        }
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() => Assert.Multiple(() =>
        {
            var newBody = session.AttachedEntity;
            Assert.That(newBody, Is.Not.Null);
            Assert.That(newBody, Is.Not.EqualTo(oldBody));
            Assert.That(SEntMan.EntityExists(oldBody), Is.False);
            Assert.That(SEntMan.EntityExists(newBody!.Value), Is.True);
            Assert.That(SComp<MobStateComponent>(newBody.Value).CurrentState, Is.EqualTo(MobState.Alive));
            var transform = SComp<TransformComponent>(newBody.Value);
            Assert.That(transform.MapID, Is.EqualTo(ticker.DefaultMap));
            Assert.That(territories.Contains(territory, transform.Coordinates), Is.True);
            Assert.That(SComp<FrontlineStockpileComponent>(home).Counts["SoldierSupplies"], Is.EqualTo(1),
                "The real client click must debit exactly one replenished SoldierSupply.");
            Assert.That(minds.TryGetMind(account, out var currentMindId, out var mind), Is.True);
            Assert.That(currentMindId, Is.EqualTo(mindId));
            Assert.That(mind?.UserId, Is.EqualTo(account));
            Assert.That(mind?.OwnedEntity, Is.EqualTo(newBody));
            Assert.That(mind?.CurrentEntity, Is.EqualTo(newBody));
        }));

        static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (var child in parent.Children)
            {
                yield return child;
                foreach (var descendant in Descendants(child))
                    yield return descendant;
            }
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task RuinRepairUsesBasicMaterialsInsteadOfSteel()
    {
        var ticker = Server.System<GameTicker>();
        var factions = Server.System<WarFactionSystem>();
        var stacks = Server.System<StackSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var interaction = Server.System<SharedInteractionSystem>();
        var territory = new TerritoryId("frontline-three");
        var faction = new FactionId("FrontlineFactionOne");
        EntityUid ruin = default;
        EntityUid body = default;
        EntityUid materials = default;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitPost(() =>
        {
            factions.ClearFaction(ServerSession!.UserId);
            Assert.That(factions.TrySelectFaction(ServerSession.UserId, faction), Is.True);
            ticker.MakeJoinGame(ServerSession, EntityUid.Invalid, silent: true);
            body = ServerSession.AttachedEntity!.Value;
            ruin = FindMapEntity<TownHallRuinComponent>(ticker.DefaultMap, entry => entry.TerritoryId == territory.Id);
            var coordinates = SComp<TransformComponent>(ruin).Coordinates;
            SEntMan.System<SharedTransformSystem>().SetCoordinates(body, coordinates);
            SComp<TownHallRuinComponent>(ruin).RequiredBasicMaterials = 2;

            var steel = stacks.SpawnAtPosition(2, "Steel", coordinates);
            Assert.That(interaction.InteractDoAfter(body, steel, ruin, coordinates, true), Is.False);

            materials = stacks.SpawnAtPosition(2, "BasicMaterials", coordinates);
            Assert.That(hands.TryPickupAnyHand(body, materials), Is.True);
            Assert.That(interaction.InteractDoAfter(body, materials, ruin, coordinates, true), Is.True);
        });

        await Pair.RunSeconds(2.1f);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<TownHallRuinComponent>(ruin).DepositedBasicMaterials, Is.EqualTo(1));
            Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(1));
        });

        await Server.WaitPost(() =>
        {
            var coordinates = SComp<TransformComponent>(ruin).Coordinates;
            Assert.That(interaction.InteractDoAfter(body, materials, ruin, coordinates, true), Is.True);
        });
        await Pair.RunSeconds(2.1f);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(ruin), Is.False);
            var hall = FindMapEntity<TownHallComponent>(ticker.DefaultMap, entry => entry.TerritoryId == territory.Id);
            Assert.That(SComp<TownHallComponent>(hall).FactionId, Is.EqualTo(faction.Id));
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task NewWarCommandRestartsWithCleanCampaignState()
    {
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var factions = Server.System<WarFactionSystem>();
        var territories = Server.System<TerritorySystem>();
        var halls = Server.System<TownHallSystem>();
        var victory = Server.System<WarVictorySystem>();
        var lightCycle = Server.System<Content.Server.Light.EntitySystems.LightCycleSystem>();
        var console = Server.ResolveDependency<IConsoleHost>();
        var factionOne = new FactionId("FrontlineFactionOne");
        var account = ServerSession!.UserId;
        WarState oldWar = default!;
        EntityUid oldBody = default;
        List<WarFactionMembership> memberships = default!;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
            var map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);

            Assert.That(console.AvailableCommands.Keys, Is.SupersetOf(new[]
            {
                "warstate", "territories", "captureterritory", "newwar", "warphase",
            }));

            console.ExecuteCommand("warstate");
            console.ExecuteCommand("territories");
            console.ExecuteCommand("warphase 120");

            factions.ClearFaction(account);
            Assert.That(factions.TrySelectFaction(account, factionOne), Is.True);
            ticker.MakeJoinGame(ServerSession!, EntityUid.Invalid, silent: true);
            oldBody = ServerSession.AttachedEntity!.Value;

            var captured = 0;
            foreach (var territory in territories.GetTerritories(ticker.DefaultMap))
            {
                if (captured++ == 4)
                    break;
                Assert.That(halls.ForceCapture(territory, factionOne, ticker.DefaultMap), Is.True);
            }

            var cores = SEntMan.EntityQueryEnumerator<TownHallComponent, TransformComponent>();
            while (cores.MoveNext(out var core, out _, out var xform))
            {
                if (xform.MapID == ticker.DefaultMap)
                    SComp<FrontlineStockpileComponent>(core).Counts["SoldierSupplies"] = 77;
            }
            Assert.That(victory.CheckForVictory(), Is.True);
            oldWar = war.State!;
            Assert.That(oldWar.Status, Is.EqualTo(WarStatus.Ended));
        });

        await Server.WaitAssertion(() =>
        {
            var map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            Assert.That(lightCycle.GetPhase((map, SComp<LightCycleComponent>(map))),
                Is.InRange(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(121)));
        });

        await Server.WaitPost(() => console.ExecuteCommand("newwar"));
        await Pair.RunUntilSynced();
        await Server.WaitPost(() =>
        {
            var resources = Server.ResolveDependency<IResourceManager>();
            using var stream = resources.UserData.Open(WarFactionSystem.SavePath, FileMode.Open);
            memberships = JsonSerializer.Deserialize<List<WarFactionMembership>>(stream)!;
        });
        await Server.WaitAssertion(() =>
        {
            var resources = Server.ResolveDependency<IResourceManager>();
            using var stream = resources.UserData.Open(WarStrategicSnapshotSystem.SavePath, FileMode.Open);
            var saved = JsonSerializer.Deserialize<WarStrategicSnapshot>(stream)!;
            Assert.That(saved.WarId, Is.EqualTo(oldWar.WarId),
                "Cleanup must save the old loaded map's war, not the already-created new war.");
            Assert.That(saved.Bases.Where(entry => entry.ObjectiveKind == "hall")
                .All(entry => entry.Counts["SoldierSupplies"] == 77), Is.True);
            Assert.That(war.State?.WarId, Is.EqualTo(oldWar.WarId + 1));
            Assert.That(war.State?.Status, Is.EqualTo(WarStatus.Active));
            Assert.That(war.State?.Winner, Is.Null);
            Assert.That(war.State?.StartedAt, Is.GreaterThan(oldWar.StartedAt));
            Assert.That(factions.TryGetFaction(account, out _), Is.False);
            Assert.That(memberships.Exists(member =>
                member.AccountId == account.UserId && member.WarId == oldWar.WarId), Is.True);
            Assert.That(SEntMan.EntityExists(oldBody), Is.False);
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby));
        });

        await Server.WaitPost(() =>
        {
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();
        await Pair.RunTicksSync(1);

        await Server.WaitAssertion(() =>
        {
            var map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            var mapId = ticker.DefaultMap;
            var factionOneHalls = 0;
            var factionTwoHalls = 0;
            var ruins = 0;

            var hallQuery = SEntMan.EntityQueryEnumerator<TownHallComponent, TransformComponent>();
            while (hallQuery.MoveNext(out var core, out var hall, out var transform))
            {
                if (transform.MapID != mapId)
                    continue;

                Assert.That(SComp<FrontlineStockpileComponent>(core).Counts["SoldierSupplies"],
                    Is.EqualTo(20), "Newwar must use fresh YAML supplies, not old inventory.");
                if (hall.FactionId == "FrontlineFactionOne")
                    factionOneHalls++;
                else if (hall.FactionId == "FrontlineFactionTwo")
                    factionTwoHalls++;
            }

            var ruinQuery = SEntMan.EntityQueryEnumerator<TownHallRuinComponent, TransformComponent>();
            while (ruinQuery.MoveNext(out _, out _, out var transform))
            {
                if (transform.MapID == mapId)
                    ruins++;
            }

            Assert.That(war.State?.WarId, Is.EqualTo(oldWar.WarId + 1));
            Assert.That(war.State?.Status, Is.EqualTo(WarStatus.Active));
            Assert.That(war.State?.Winner, Is.Null);
            Assert.That(territories.GetTerritories(mapId), Has.Count.EqualTo(5));
            Assert.That(factionOneHalls, Is.EqualTo(1));
            Assert.That(factionTwoHalls, Is.EqualTo(1));
            Assert.That(ruins, Is.EqualTo(3));
            Assert.That(territories.CountOwned(factionOne), Is.EqualTo(1));

            // New-war startup is wall-clock based, so do not require the whole restart/map-load path
            // to complete within one exact second. The important invariant is that neither the old
            // 120-second debug override nor the old campaign age survives the new war.
            var freshWarAge = DateTimeOffset.UtcNow - war.State!.StartedAt;
            var phase = lightCycle.GetPhase((map, SComp<LightCycleComponent>(map)));
            Assert.That(freshWarAge, Is.InRange(TimeSpan.Zero, TimeSpan.FromSeconds(30)));
            Assert.That(phase, Is.InRange(TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }

    [Test]
    public async Task StartsWithoutSpawningUnselectedPlayers()
    {
        var ticker = Server.System<GameTicker>();
        var mindSystem = Server.System<MindSystem>();


        await Server.WaitPost(() =>
        {
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(ticker.CurrentPreset?.ID, Is.EqualTo("PersistentWar"));
            Assert.That(SEntMan.Count<ActiveGameRuleComponent>(), Is.EqualTo(1));
            Assert.That(SEntMan.Count<PersistentWarRuleComponent>(), Is.EqualTo(1));

            Assert.That(mindSystem.TryGetMind(ServerSession!, out _, out _), Is.False);
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }

    [Test]
    public async Task NormalEndRoundDoesNotEndPersistentWar()
    {
        var ticker = Server.System<GameTicker>();

        await Server.WaitPost(() =>
        {
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunUntilSynced();

        await Server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));

            ticker.EndRound();

            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }

    private EntityUid FindMapEntity<T>(MapId mapId, Func<T, bool> predicate)
        where T : IComponent
    {
        var query = SEntMan.EntityQueryEnumerator<T, TransformComponent>();
        while (query.MoveNext(out var uid, out var component, out var transform))
        {
            if (transform.MapID == mapId && predicate(component))
                return uid;
        }

        throw new InvalidOperationException($"No matching {typeof(T).Name} found on map {mapId}.");
    }
}
