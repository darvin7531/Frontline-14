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
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
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
using Content.Shared.Tag;
using Content.Shared.War;

using Robust.Shared.ContentPack;
using Robust.Shared.Containers;
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
        MissingResourceSlotId,
        DuplicateResourceSlotId,
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
    [TestCase(InvalidMapCase.MissingResourceSlotId,
        "PersistentWar resource slot 'frontline-test-iron/' is missing or duplicated.")]
    [TestCase(InvalidMapCase.DuplicateResourceSlotId,
        "PersistentWar resource slot 'frontline-test-iron/iron-west' is missing or duplicated.")]
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
                case InvalidMapCase.MissingResourceSlotId:
                    SComp<FrontlineResourceSpawnPointComponent>(FindMapEntity<FrontlineResourceSpawnPointComponent>(mapId,
                        component => component.SlotId == "iron-west")).SlotId = "";
                    break;
                case InvalidMapCase.DuplicateResourceSlotId:
                    SComp<FrontlineResourceSpawnPointComponent>(FindMapEntity<FrontlineResourceSpawnPointComponent>(mapId,
                        component => component.SlotId == "iron-east")).SlotId = "iron-west";
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
            Assert.That(saved!.SnapshotVersion, Is.EqualTo(WarStrategicSnapshotSystem.SnapshotVersion));
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
    public async Task TechnicalRestartPreservesResourceReserveAndPartialNodeYield()
    {
        const string fieldId = "frontline-test-iron";
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var factions = Server.System<WarFactionSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var interaction = Server.System<SharedInteractionSystem>();
        var maps = Server.System<SharedMapSystem>();
        _ = Server.System<FrontlineResourceFieldSystem>();
        var session = ServerSession!;
        EntityUid oldMap = default;
        EntityUid field = default;
        EntityUid slot = default;
        EntityUid node = default;
        EntityUid tool = default;
        WarState beforeRestart = default!;
        Dictionary<Vector2, (Vector2 Position, int Yield)> nodesBefore = default!;
        var reserveBefore = 0;
        var stateBefore = default(FrontlineResourceFieldState);
        var selected = false;
        var pickedUp = false;
        var extractionStarted = false;

        // Slot UIDs change on reload. Use the mapper's local position on the same grid instead.
        List<EntityUid> MapNodes()
        {
            var result = new List<EntityUid>();
            var query = SEntMan.EntityQueryEnumerator<FrontlineResourceNodeComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var component, out var transform))
            {
                if (component.FieldId == fieldId && transform.MapID == ticker.DefaultMap)
                    result.Add(uid);
            }
            return result;
        }

        try
        {
            await Server.WaitPost(() =>
            {
                ticker.RestartRound();
                war.StartNewWar();
                factions.ClearFaction(session.UserId);
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
            });
            await Pair.RunUntilSynced();
            await Pair.RunTicksSync(1);
            await Server.WaitPost(() =>
            {
                selected = factions.TrySelectFaction(session.UserId, new FactionId("FrontlineFactionOne"));
                ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
                oldMap = maps.GetMapOrInvalid(ticker.DefaultMap);
                field = FindMapEntity<FrontlineResourceFieldComponent>(ticker.DefaultMap,
                    component => component.FieldId == fieldId);
                node = SComp<FrontlineResourceFieldComponent>(field).ActiveNodes
                    .OrderBy(uid => SComp<TransformComponent>(uid).LocalPosition.X).First();
                slot = SComp<FrontlineResourceNodeComponent>(node).SpawnPoint;
                tool = FindMapEntity<MetaDataComponent>(ticker.DefaultMap,
                    metadata => metadata.EntityPrototype?.ID == "Pickaxe");
            });
            await Server.WaitAssertion(() =>
            {
                Assert.That(selected, Is.True);
                Assert.That(session.AttachedEntity, Is.Not.Null);
                Assert.That(SComp<FrontlineResourceFieldComponent>(field).RemainingReserveNodes, Is.EqualTo(3));
                Assert.That(MapNodes(), Has.Count.EqualTo(3));
            });
            await Server.WaitPost(() =>
            {
                var body = session.AttachedEntity!.Value;
                Server.System<SharedTransformSystem>().SetCoordinates(body,
                    SComp<TransformComponent>(node).Coordinates.Offset(new Vector2(0, 1)));
                pickedUp = hands.TryPickupAnyHand(body, tool);
            });
            await Server.WaitAssertion(() => Assert.That(pickedUp, Is.True));

            // Exhaust one real 20-yield node, paying four native 5-unit do-afters.
            for (var i = 0; i < 4; i++)
            {
                await Server.WaitPost(() => extractionStarted = interaction.InteractDoAfter(
                    session.AttachedEntity!.Value, tool, node, SComp<TransformComponent>(node).Coordinates, true));
                await Pair.RunSeconds(1.1f);
                await Server.WaitAssertion(() => Assert.That(extractionStarted, Is.True));
            }
            await Server.WaitAssertion(() => Assert.That(SEntMan.EntityExists(node), Is.False));
            await Pair.RunSeconds(1.1f);
            await Server.WaitPost(() =>
            {
                node = SComp<FrontlineResourceFieldComponent>(field).ActiveNodes
                    .Single(uid => SComp<FrontlineResourceNodeComponent>(uid).SpawnPoint == slot);
                extractionStarted = interaction.InteractDoAfter(session.AttachedEntity!.Value, tool, node,
                    SComp<TransformComponent>(node).Coordinates, true);
            });
            await Pair.RunSeconds(1.1f);
            await Server.WaitAssertion(() =>
            {
                Assert.That(extractionStarted, Is.True);
                Assert.That(SComp<FrontlineResourceNodeComponent>(node).RemainingYield, Is.EqualTo(15));
                Assert.That(SComp<FrontlineResourceFieldComponent>(field).RemainingReserveNodes, Is.EqualTo(2));
                Assert.That(SComp<FrontlineResourceFieldComponent>(field).State, Is.EqualTo(FrontlineResourceFieldState.Active));
                Assert.That(MapNodes(), Has.Count.EqualTo(3));
                Assert.That(SComp<FrontlineResourceFieldComponent>(field).ActiveNodes, Is.EquivalentTo(MapNodes()));
                Assert.That(SEntMan.EntityQuery<StackComponent>().Where(stack =>
                    stack.StackTypeId == "FrontlineRawIron" &&
                    SComp<TransformComponent>(stack.Owner).MapID == ticker.DefaultMap).Sum(stack => stack.Count),
                    Is.EqualTo(25), "Reserve and partial yield must come from paid native harvesting.");
            });
            await Server.WaitPost(() =>
            {
                var component = SComp<FrontlineResourceFieldComponent>(field);
                reserveBefore = component.RemainingReserveNodes;
                stateBefore = component.State;
                nodesBefore = MapNodes().ToDictionary(
                    uid => SComp<TransformComponent>(SComp<FrontlineResourceNodeComponent>(uid).SpawnPoint).LocalPosition,
                    uid => (SComp<TransformComponent>(uid).LocalPosition,
                        SComp<FrontlineResourceNodeComponent>(uid).RemainingYield));
                beforeRestart = war.State!;
                ticker.RestartRound();
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
            });
            await Pair.RunUntilSynced();
            await Pair.RunTicksSync(1);
            await Server.WaitAssertion(() => Assert.Multiple(() =>
            {
                using var stream = Server.ResolveDependency<IResourceManager>().UserData.OpenRead(WarStrategicSnapshotSystem.SavePath);
                var saved = JsonSerializer.Deserialize<WarStrategicSnapshot>(stream)!;
                Assert.That(saved.SnapshotVersion, Is.EqualTo(WarStrategicSnapshotSystem.SnapshotVersion));
                Assert.That(saved.WarId, Is.EqualTo(beforeRestart.WarId));
                var savedField = saved.Resources.Single(entry => entry.FieldId == fieldId);
                Assert.That(savedField.RemainingReserveNodes, Is.EqualTo(reserveBefore));
                Assert.That(savedField.FieldInitialized, Is.True);
                Assert.That(savedField.State, Is.EqualTo(stateBefore));
                Assert.That(savedField.Nodes.Select(entry => entry.RemainingYield),
                    Is.EquivalentTo(nodesBefore.Values.Select(entry => entry.Yield)));
                Assert.That(savedField.Nodes.Select(entry => entry.SlotId).Distinct().Count(), Is.EqualTo(nodesBefore.Count));
                Assert.That(savedField.Nodes.All(entry => entry.Prototype == "FrontlineIronResourceNode"), Is.True);
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
                Assert.That(war.State, Is.EqualTo(beforeRestart));
                Assert.That(SEntMan.EntityExists(oldMap), Is.False);
                Assert.That(SEntMan.EntityExists(field), Is.False);
                Assert.That(SEntMan.EntityExists(node), Is.False);
                Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.Not.EqualTo(oldMap));
                var restored = FindMapEntity<FrontlineResourceFieldComponent>(ticker.DefaultMap,
                    component => component.FieldId == fieldId);
                var restoredField = SComp<FrontlineResourceFieldComponent>(restored);
                Assert.That(restoredField.RemainingReserveNodes, Is.EqualTo(reserveBefore),
                    "Technical restart must not mint the spent replacement reserve (2, not fresh-map 3).");
                Assert.That(restoredField.State, Is.EqualTo(stateBefore));
                Assert.That(restoredField.FieldInitialized, Is.True);
                var nodes = MapNodes();
                Assert.That(nodes, Has.Count.EqualTo(nodesBefore.Count), "Reload must not duplicate active nodes.");
                Assert.That(restoredField.ActiveNodes, Is.EquivalentTo(nodes), "Rebuild ownership caches after reload.");
                var restoredNodes = new Dictionary<Vector2, (Vector2 Position, int Yield)>();
                foreach (var uid in nodes)
                {
                    var component = SComp<FrontlineResourceNodeComponent>(uid);
                    var transform = SComp<TransformComponent>(uid);
                    var point = SComp<TransformComponent>(component.SpawnPoint);
                    Assert.That(component.Field, Is.EqualTo(restored));
                    Assert.That(SComp<FrontlineResourceSpawnPointComponent>(component.SpawnPoint).FieldId,
                        Is.EqualTo(fieldId));
                    Assert.That(component.SlotId,
                        Is.EqualTo(SComp<FrontlineResourceSpawnPointComponent>(component.SpawnPoint).SlotId));
                    Assert.That(savedField.Nodes.Single(entry => entry.SlotId == component.SlotId).RemainingYield,
                        Is.EqualTo(component.RemainingYield));
                    Assert.That(point.MapID, Is.EqualTo(ticker.DefaultMap));
                    Assert.That(transform.ParentUid, Is.EqualTo(point.ParentUid));
                    Assert.That(point.ParentUid, Is.EqualTo(SComp<TransformComponent>(restored).ParentUid));
                    Assert.That(restoredNodes.TryAdd(point.LocalPosition, (transform.LocalPosition, component.RemainingYield)),
                        Is.True, "Each mapper slot may own only one active node.");
                }
                Assert.That(restoredNodes, Is.EquivalentTo(nodesBefore),
                    "Same-parent/local-position slots must retain exact node positions and yields (15, 20, 20), not refill to 20.");
            }));
        }
        finally
        {
            // Native cleanup on RED too; existing teardown deletes all save paths and restores the preset.
            await Server.WaitPost(() => ticker.RestartRound());
        }
    }

    [TestCase(FrontlineResourceFieldState.Active)]
    [TestCase(FrontlineResourceFieldState.Depleted)]
    [TestCase(FrontlineResourceFieldState.Replenishing)]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task TechnicalRestartPreservesResourceCooldown(FrontlineResourceFieldState state)
    {
        const string fieldId = "frontline-test-iron";
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var maps = Server.System<SharedMapSystem>();
        _ = Server.System<FrontlineResourceFieldSystem>();
        var replenishing = state != FrontlineResourceFieldState.Active;
        var tick = SGameTiming.TickPeriod;
        EntityUid field = default;
        EntityUid oldField = default;
        EntityUid oldMap = default;
        EntityUid[] oldNodes = default!;
        WarState beforeRestart = default!;
        WarStrategicSnapshot saved = default!;
        Dictionary<string, int> fullYields = default!;
        Dictionary<string, int> yieldsBefore = default!;
        TimeSpan replacementDelay = default;
        TimeSpan replenishmentDelay = default;
        TimeSpan remaining = default;
        TimeSpan restoredAt = default;
        TimeSpan deadline = default;

        List<EntityUid> MapNodes()
        {
            var result = new List<EntityUid>();
            var query = SEntMan.AllEntityQueryEnumerator<FrontlineResourceNodeComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var transform))
            {
                if (transform.MapID == ticker.DefaultMap)
                    result.Add(uid);
            }
            return result;
        }

        void AssertField(int reserve, FrontlineResourceFieldState expectedState, Dictionary<string, int> yields)
        {
            var component = SComp<FrontlineResourceFieldComponent>(field);
            var nodes = MapNodes();
            Assert.That(component.FieldInitialized && component.CacheInitialized, Is.True);
            Assert.That(component.State, Is.EqualTo(expectedState));
            Assert.That(component.RemainingReserveNodes, Is.EqualTo(reserve));
            Assert.That(nodes, Has.Count.EqualTo(yields.Count));
            Assert.That(component.ActiveNodes, Is.EquivalentTo(nodes), "No duplicate or detached yield outside the ownership cache.");
            Assert.That(nodes.ToDictionary(uid => SComp<FrontlineResourceNodeComponent>(uid).SlotId,
                uid => SComp<FrontlineResourceNodeComponent>(uid).RemainingYield), Is.EquivalentTo(yields));
            foreach (var uid in nodes)
            {
                var node = SComp<FrontlineResourceNodeComponent>(uid);
                var slot = SComp<FrontlineResourceSpawnPointComponent>(node.SpawnPoint);
                Assert.That(SEntMan.IsQueuedForDeletion(uid), Is.False);
                Assert.That(node.Field, Is.EqualTo(field));
                Assert.That(node.FieldId, Is.EqualTo(fieldId));
                Assert.That(slot.FieldId, Is.EqualTo(fieldId));
                Assert.That(slot.SlotId, Is.EqualTo(node.SlotId));
                Assert.That(SComp<TransformComponent>(node.SpawnPoint).MapID, Is.EqualTo(ticker.DefaultMap));
                Assert.That(SComp<TransformComponent>(uid).Coordinates,
                    Is.EqualTo(SComp<TransformComponent>(node.SpawnPoint).Coordinates));
                Assert.That(SComp<TransformComponent>(uid).ParentUid, Is.EqualTo(SComp<TransformComponent>(field).ParentUid));
            }
            Assert.That(SEntMan.EntityQuery<StackComponent>().Where(stack => stack.StackTypeId == "FrontlineRawIron" &&
                SComp<TransformComponent>(stack.Owner).MapID == ticker.DefaultMap).Sum(stack => stack.Count), Is.Zero,
                "Deleting/reloading owned nodes must not mint harvested output.");
        }

        async Task RunThroughDeadline(TimeSpan due)
        {
            var ticks = 0;
            await Server.WaitPost(() => ticks = Math.Max(1,
                (int) Math.Ceiling((due - SGameTiming.CurTime).TotalSeconds / tick.TotalSeconds) + 1));
            // The last tick must execute Update at/after the deadline, not merely advance CurTime to it.
            await Pair.RunTicksSync(ticks);
            await Server.WaitAssertion(() => Assert.That(SGameTiming.CurTime,
                Is.InRange(due, due + tick * 2)));
        }

        try
        {
            await Server.WaitPost(() =>
            {
                ticker.RestartRound();
                war.StartNewWar();
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
            });
            await Pair.RunTicksSync(1);
            await Server.WaitPost(() =>
            {
                field = FindMapEntity<FrontlineResourceFieldComponent>(ticker.DefaultMap, component => component.FieldId == fieldId);
                var component = SComp<FrontlineResourceFieldComponent>(field);
                replacementDelay = component.ReplacementDelay;
                replenishmentDelay = component.ReplenishmentDelay;
                fullYields = MapNodes().ToDictionary(uid => SComp<FrontlineResourceNodeComponent>(uid).SlotId,
                    uid => SComp<FrontlineResourceNodeComponent>(uid).RemainingYield);
            });
            await Server.WaitAssertion(() =>
            {
                Assert.That(fullYields, Has.Count.EqualTo(3));
                Assert.That(fullYields.Values, Is.All.EqualTo(20));
                Assert.That(replacementDelay, Is.GreaterThan(tick * 2));
                Assert.That(replenishmentDelay, Is.GreaterThan(tick * 2));
                AssertField(3, FrontlineResourceFieldState.Active, fullYields);
            });
            await Server.WaitPost(() =>
            {
                var nodes = SComp<FrontlineResourceFieldComponent>(field).ActiveNodes.ToArray();
                // Native termination schedules the timer. Spend the second batch before depleting it.
                foreach (var node in replenishing ? nodes : nodes.Take(1))
                    SEntMan.DeleteEntity(node);
                deadline = SComp<FrontlineResourceFieldComponent>(field).NextReplacement;
            });
            if (replenishing)
            {
                await RunThroughDeadline(deadline);
                await Server.WaitAssertion(() => AssertField(0, FrontlineResourceFieldState.Active, fullYields));
                await Server.WaitPost(() =>
                {
                    foreach (var node in SComp<FrontlineResourceFieldComponent>(field).ActiveNodes.ToArray())
                        SEntMan.DeleteEntity(node);
                });
            }
            // Preserve Depleted before its first Update; one tick otherwise spends real cooldown time.
            if (state != FrontlineResourceFieldState.Depleted)
                await Pair.RunTicksSync(1);
            await Server.WaitPost(() =>
            {
                yieldsBefore = MapNodes().ToDictionary(uid => SComp<FrontlineResourceNodeComponent>(uid).SlotId,
                    uid => SComp<FrontlineResourceNodeComponent>(uid).RemainingYield);
                var component = SComp<FrontlineResourceFieldComponent>(field);
                remaining = (replenishing ? component.NextReplenishment : component.NextReplacement) - SGameTiming.CurTime;
            });
            await Server.WaitAssertion(() =>
            {
                AssertField(replenishing ? 0 : 3, state, yieldsBefore);
                Assert.That(yieldsBefore, Has.Count.EqualTo(replenishing ? 0 : 2));
                Assert.That(remaining, Is.GreaterThan(tick * 2));
                Assert.That(remaining, state == FrontlineResourceFieldState.Depleted
                    ? Is.EqualTo(replenishmentDelay)
                    : Is.InRange((replenishing ? replenishmentDelay : replacementDelay) - tick * 2,
                        (replenishing ? replenishmentDelay : replacementDelay) - tick));
            });
            await Server.WaitPost(() =>
            {
                oldMap = maps.GetMapOrInvalid(ticker.DefaultMap);
                oldField = field;
                oldNodes = MapNodes().ToArray();
                beforeRestart = war.State!;
                ticker.RestartRound();
                using var stream = Server.ResolveDependency<IResourceManager>().UserData.OpenRead(WarStrategicSnapshotSystem.SavePath);
                saved = JsonSerializer.Deserialize<WarStrategicSnapshot>(stream)!;
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
                restoredAt = SGameTiming.CurTime;
                field = FindMapEntity<FrontlineResourceFieldComponent>(ticker.DefaultMap, component => component.FieldId == fieldId);
                var component = SComp<FrontlineResourceFieldComponent>(field);
                deadline = replenishing ? component.NextReplenishment : component.NextReplacement;
            });
            await Server.WaitAssertion(() =>
            {
                var entry = saved.Resources.Single(resource => resource.FieldId == fieldId);
                Assert.That(saved.SnapshotVersion, Is.EqualTo(WarStrategicSnapshotSystem.SnapshotVersion));
                Assert.That(saved.WarId, Is.EqualTo(beforeRestart.WarId));
                Assert.That(entry.FieldInitialized, Is.True);
                Assert.That(entry.RemainingReserveNodes, Is.EqualTo(replenishing ? 0 : 3));
                Assert.That(entry.State, Is.EqualTo(state));
                Assert.That(entry.ReplacementRemainingTicks, Is.EqualTo(replenishing ? 0 : remaining.Ticks));
                Assert.That(entry.ReplenishmentRemainingTicks, Is.EqualTo(replenishing ? remaining.Ticks : 0));
                Assert.That(entry.Nodes.ToDictionary(node => node.SlotId, node => node.RemainingYield), Is.EquivalentTo(yieldsBefore));
                Assert.That(entry.Nodes.All(node => node.Prototype == "FrontlineIronResourceNode"), Is.True);
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
                Assert.That(war.State, Is.EqualTo(beforeRestart));
                Assert.That(SEntMan.EntityExists(oldMap) || SEntMan.EntityExists(oldField), Is.False);
                Assert.That(oldNodes.All(uid => !SEntMan.EntityExists(uid)), Is.True);
                Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.Not.EqualTo(oldMap));
                var component = SComp<FrontlineResourceFieldComponent>(field);
                Assert.That(component.ReplacementDelay, Is.EqualTo(replacementDelay));
                Assert.That(component.ReplenishmentDelay, Is.EqualTo(replenishmentDelay));
                Assert.That(deadline - restoredAt, Is.EqualTo(remaining), "Restore the disk duration, not a fresh configured delay.");
                Assert.That(replenishing ? component.NextReplacement : component.NextReplenishment, Is.EqualTo(TimeSpan.Zero));
                AssertField(replenishing ? 0 : 3, state, yieldsBefore);
            });
            // Stop strictly before the restored deadline; Depleted must transition but neither case may refill.
            var earlyTicks = Math.Max(1, (int) (remaining.Ticks / tick.Ticks) - 1);
            await Pair.RunTicksSync(earlyTicks);
            await Server.WaitAssertion(() =>
            {
                Assert.That(SGameTiming.CurTime - restoredAt,
                    Is.InRange(tick * earlyTicks, tick * (earlyTicks + 1)));
                Assert.That(SGameTiming.CurTime, Is.LessThan(deadline));
                AssertField(replenishing ? 0 : 3,
                    replenishing ? FrontlineResourceFieldState.Replenishing : FrontlineResourceFieldState.Active, yieldsBefore);
                Assert.That((replenishing ? SComp<FrontlineResourceFieldComponent>(field).NextReplenishment
                    : SComp<FrontlineResourceFieldComponent>(field).NextReplacement), Is.EqualTo(deadline));
            });
            await RunThroughDeadline(deadline);
            await Server.WaitAssertion(() => AssertField(replenishing ? 3 : 2, FrontlineResourceFieldState.Active, fullYields));
        }
        finally
        {
            // Existing teardown removes all saves and restores the preset, including a behavioral RED.
            await Server.WaitPost(() => ticker.RestartRound());
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task TechnicalRestartPreservesPaidRefineryQueueAndRetainedMaterials()
    {
        ProtoId<FrontlineRefineryRecipePrototype> recipe = "FrontlineSteel";
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var factions = Server.System<WarFactionSystem>();
        var refineries = Server.System<FrontlineRefinerySystem>();
        var stacks = Server.System<StackSystem>();
        var containers = Server.System<SharedContainerSystem>();
        var maps = Server.System<SharedMapSystem>();
        var session = ServerSession!;
        var duration = refineries.GetAvailableRecipes().Single(entry => entry.ID == recipe.Id).Duration;
        EntityUid refinery = default;
        EntityUid oldRefinery = default;
        EntityUid oldMap = default;
        EntityUid input = default;
        WarState beforeRestart = default!;
        FrontlineRefineryJob[] restoredJobs = default!;
        TimeSpan remaining = default;
        var selected = false;
        var inserted = false;
        var submitted = false;

        // base_test.yaml has refineries at (6,-20) and (44,-20); anchoring snaps to tile centers.
        // Test lookup only: persistence must introduce stable mapper IDs, never save positions or UIDs.
        EntityUid FindRefinery(Vector2 position) => SEntMan.EntityQuery<FrontlineRefineryComponent>()
            .Select(component => component.Owner).Single(uid =>
                SComp<TransformComponent>(uid).MapID == ticker.DefaultMap &&
                SComp<TransformComponent>(uid).LocalPosition == position &&
                SComp<MetaDataComponent>(uid).EntityPrototype?.ID == "FrontlineRefinery");

        void AssertGoods(int outputAmount)
        {
            AssertContainer(refinery, FrontlineRefineryComponent.InputContainerId, "FrontlineRawIron", 3);
            AssertContainer(refinery, FrontlineRefineryComponent.OutputContainerId, "BasicMaterials", outputAmount);
            var other = FindRefinery(new Vector2(44.5f, -19.5f));
            Assert.That(refineries.GetJobs(other), Is.Empty, "Do not restore the paid claim into both refineries.");
            AssertContainer(other, FrontlineRefineryComponent.InputContainerId, "FrontlineRawIron", 0);
            AssertContainer(other, FrontlineRefineryComponent.OutputContainerId, "BasicMaterials", 0);
            foreach (var (type, amount) in new[] { ("FrontlineRawIron", 3), ("BasicMaterials", outputAmount) })
            {
                Assert.That(SEntMan.EntityQuery<StackComponent>().Where(stack => stack.StackTypeId == type &&
                    SComp<TransformComponent>(stack.Owner).MapID == ticker.DefaultMap).Sum(stack => stack.Count),
                    Is.EqualTo(amount), "Retained goods must not also appear loose or in another machine.");
            }
        }

        void AssertContainer(EntityUid owner, string id, string type, int amount)
        {
            Assert.That(containers.TryGetContainer(owner, id, out var container), Is.True);
            Assert.That(container.ContainedEntities.All(uid => SEntMan.EntityExists(uid) &&
                !SEntMan.IsQueuedForDeletion(uid) && SComp<MetaDataComponent>(uid).EntityLifeStage < EntityLifeStage.Terminating), Is.True);
            var goods = container.ContainedEntities.Select(uid => SComp<StackComponent>(uid)).ToArray();
            Assert.That(goods.All(stack => stack.StackTypeId == type && !stack.Unlimited && stack.Count > 0), Is.True);
            Assert.That(goods.Sum(stack => stack.Count), Is.EqualTo(amount), $"{id} must retain exactly {amount} {type}.");
        }

        try
        {
            await Server.WaitPost(() =>
            {
                ticker.RestartRound();
                war.StartNewWar();
                factions.ClearFaction(session.UserId);
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
            });
            await Pair.RunUntilSynced();
            await Server.WaitPost(() =>
            {
                selected = factions.TrySelectFaction(session.UserId, new FactionId("FrontlineFactionOne"));
                ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
                refinery = FindRefinery(new Vector2(6.5f, -19.5f));
                var coordinates = SComp<TransformComponent>(refinery).Coordinates;
                Server.System<SharedTransformSystem>().SetCoordinates(session.AttachedEntity!.Value,
                    coordinates.Offset(new Vector2(0, 1)));
                // Owning insertion API; submission is the public player/container-only API, not the BUI.
                input = stacks.SpawnAtPosition(13, "FrontlineRawIron", coordinates);
                inserted = refineries.TryInsertInput(refinery, input);
                submitted = refineries.TrySubmitPlayerJob(refinery, session.AttachedEntity.Value, recipe);
            });
            await Server.WaitAssertion(() =>
            {
                Assert.That(selected && inserted && submitted, Is.True);
                Assert.That(SComp<StackComponent>(input).Count, Is.EqualTo(8));
                Assert.That(refineries.GetJobs(refinery), Has.Count.EqualTo(1));
            });
            await Pair.RunSeconds((float) duration.TotalSeconds + 0.1f);
            await Server.WaitAssertion(() =>
            {
                Assert.That(refineries.GetJobs(refinery), Is.Empty);
                AssertContainer(refinery, FrontlineRefineryComponent.OutputContainerId, "BasicMaterials", 5);
            });
            await Server.WaitPost(() => submitted = refineries.TrySubmitPlayerJob(refinery,
                session.AttachedEntity!.Value, recipe));
            await Pair.RunTicksSync(30);
            await Server.WaitAssertion(() =>
            {
                Assert.That(submitted, Is.True);
                Assert.That(refineries.GetJobs(refinery), Has.Count.EqualTo(1));
                Assert.That(refineries.GetJobs(refinery)[0].Remaining, Is.InRange(SGameTiming.TickPeriod, duration - SGameTiming.TickPeriod));
                AssertGoods(5); // Three independent claims: paid pending work, committed output, unused input.
            });
            await Server.WaitPost(() =>
            {
                remaining = refineries.GetJobs(refinery)[0].Remaining;
                oldRefinery = refinery;
                oldMap = maps.GetMapOrInvalid(ticker.DefaultMap);
                beforeRestart = war.State!;
                ticker.RestartRound();
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
                refinery = FindRefinery(new Vector2(6.5f, -19.5f));
                // Observe restore before any native Update spends processing time.
                restoredJobs = refineries.GetJobs(refinery).ToArray();
            });
            await Server.WaitAssertion(() =>
            {
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
                Assert.That(war.State, Is.EqualTo(beforeRestart));
                Assert.That(SEntMan.EntityExists(oldMap) || SEntMan.EntityExists(oldRefinery) || SEntMan.EntityExists(input), Is.False);
                Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.Not.EqualTo(oldMap));
                Assert.That(restoredJobs, Has.Length.EqualTo(1),
                    "Technical restart must restore the paid refinery job instead of reloading an empty queue.");
                Assert.That(restoredJobs[0].Recipe, Is.EqualTo(recipe));
                Assert.That(restoredJobs[0].Remaining, Is.EqualTo(remaining), "Restore exact paid progress, not a fresh recipe duration.");
                AssertGoods(5);
            });
            await Pair.RunTicksSync(1);
            await Server.WaitAssertion(() =>
            {
                Assert.That(refineries.GetJobs(refinery), Has.Count.EqualTo(1));
                Assert.That(refineries.GetJobs(refinery)[0].Remaining, Is.GreaterThan(TimeSpan.Zero).And.LessThan(remaining),
                    "Restoration after MapInit must activate the paid queue for native processing.");
                AssertGoods(5);
            });
            await Pair.RunSeconds((float) remaining.TotalSeconds + 0.1f);
            await Server.WaitAssertion(() =>
            {
                Assert.That(refineries.GetJobs(refinery), Is.Empty);
                AssertGoods(10);
            });
            await Pair.RunSeconds((float) duration.TotalSeconds + 0.1f);
            await Server.WaitAssertion(() =>
            {
                Assert.That(refineries.GetJobs(refinery), Is.Empty);
                AssertGoods(10); // Completion is once-only and must not consume the remaining three raw iron.
            });
        }
        finally
        {
            // Native cleanup on RED too; fixture teardown deletes all saves and restores the preset.
            await Server.WaitPost(() => ticker.RestartRound());
        }
    }

    // Exact damage is the persistence contract, not a player-facing health measurement.
#pragma warning disable CS0618
    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task TechnicalRestartPreservesObjectiveDamageAndRuinRepairProgress()
    {
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var factions = Server.System<WarFactionSystem>();
        var territories = Server.System<TerritorySystem>();
        var damage = Server.System<DamageableSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var interaction = Server.System<SharedInteractionSystem>();
        var maps = Server.System<SharedMapSystem>();
        var session = ServerSession!;
        var faction = new FactionId("FrontlineFactionOne");
        EntityUid oldMap = default;
        EntityUid liveHall = default;
        EntityUid destroyedHall = default;
        EntityUid ruin = default;
        EntityUid materials = default;
        WarState beforeRestart = default!;
        DamageSpecifier damageBefore = default!;
        var progressBefore = 0;
        var selected = false;
        var damaged = false;
        var destroyed = false;
        var pickedUp = false;
        var repairStarted = false;

        try
        {
            await Server.WaitPost(() =>
            {
                ticker.RestartRound();
                war.StartNewWar();
                factions.ClearFaction(session.UserId);
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
            });
            await Pair.RunUntilSynced();
            await Server.WaitPost(() =>
            {
                selected = factions.TrySelectFaction(session.UserId, faction);
                ticker.MakeJoinGame(session, EntityUid.Invalid, silent: true);
                oldMap = maps.GetMapOrInvalid(ticker.DefaultMap);
                liveHall = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                    hall => hall.TerritoryId == "frontline-one");
                destroyedHall = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                    hall => hall.TerritoryId == "frontline-five");
                // Below the native 50 breakage / 100 destruction thresholds; retain both exact types.
                damaged = damage.TryChangeDamage(liveHall, new DamageSpecifier
                {
                    DamageDict = new() { ["Blunt"] = FixedPoint2.New(7.25), ["Slash"] = FixedPoint2.New(3.5) },
                }, ignoreResistances: true, ignoreGlobalModifiers: true);
                destroyed = damage.TryChangeDamage(destroyedHall, new DamageSpecifier
                {
                    DamageDict = new() { ["Blunt"] = FixedPoint2.New(100) },
                }, ignoreResistances: true, ignoreGlobalModifiers: true);
            });
            await Pair.RunTicksSync(2);
            await Server.WaitAssertion(() =>
            {
                Assert.That(selected && damaged && destroyed, Is.True);
                Assert.That(session.AttachedEntity, Is.Not.Null);
                Assert.That(SEntMan.EntityExists(liveHall), Is.True);
                Assert.That(SEntMan.EntityExists(destroyedHall), Is.False,
                    "Native damage/destruction must remove the hall before testing persistence.");
                Assert.That(territories.GetState(new TerritoryId("frontline-five"), ticker.DefaultMap),
                    Is.EqualTo(TerritoryState.Neutral));
            });
            await Server.WaitPost(() =>
            {
                ruin = FindMapEntity<TownHallRuinComponent>(ticker.DefaultMap,
                    objective => objective.TerritoryId == "frontline-five");
                var body = session.AttachedEntity!.Value;
                var coordinates = SComp<TransformComponent>(ruin).Coordinates;
                Server.System<SharedTransformSystem>().SetCoordinates(body, coordinates);
                materials = Server.System<StackSystem>().SpawnAtPosition(2, "BasicMaterials", coordinates);
                pickedUp = hands.TryPickupAnyHand(body, materials);
                repairStarted = interaction.InteractDoAfter(body, materials, ruin, coordinates, true);
            });
            await Pair.RunSeconds(2.1f);
            await Server.WaitAssertion(() =>
            {
                Assert.That(pickedUp && repairStarted, Is.True);
                Assert.That(damage.GetAllDamage(liveHall).DamageDict["Blunt"], Is.EqualTo(FixedPoint2.New(7.25)));
                Assert.That(damage.GetAllDamage(liveHall).DamageDict["Slash"], Is.EqualTo(FixedPoint2.New(3.5)));
                Assert.That(SComp<TownHallRuinComponent>(ruin).DepositedBasicMaterials, Is.EqualTo(1));
                Assert.That(SComp<TownHallRuinComponent>(ruin).RequiredBasicMaterials, Is.GreaterThan(1));
                Assert.That(SComp<StackComponent>(materials).Count, Is.EqualTo(1),
                    "Partial progress must come from consuming a real held BasicMaterials stack.");
                Assert.That(territories.CountOwned(faction, ticker.DefaultMap), Is.EqualTo(1));
                Assert.That(war.State!.Status, Is.EqualTo(WarStatus.Active), "Setup must remain below victory.");
            });
            await Server.WaitPost(() =>
            {
                damageBefore = damage.GetAllDamage(liveHall);
                progressBefore = SComp<TownHallRuinComponent>(ruin).DepositedBasicMaterials;
                beforeRestart = war.State!;
                ticker.RestartRound();
                ticker.SetGamePreset("PersistentWar");
                ticker.ToggleReadyAll(true);
                ticker.StartRound(true);
            });
            await Pair.RunUntilSynced();
            await Server.WaitAssertion(() => Assert.Multiple(() =>
            {
                using var stream = Server.ResolveDependency<IResourceManager>().UserData
                    .OpenRead(WarStrategicSnapshotSystem.SavePath);
                var saved = JsonSerializer.Deserialize<WarStrategicSnapshot>(stream)!;
                Assert.That(saved.SnapshotVersion, Is.EqualTo(WarStrategicSnapshotSystem.SnapshotVersion));
                Assert.That(saved.WarId, Is.EqualTo(beforeRestart.WarId));
                var savedHall = saved.Bases.Single(entry => entry.TerritoryId == "frontline-one");
                var savedRuin = saved.Bases.Single(entry => entry.TerritoryId == "frontline-five");
                Assert.That(savedHall.ObjectiveKind, Is.EqualTo("hall"));
                Assert.That(savedHall.DamageHundredths, Is.EquivalentTo(damageBefore.DamageDict
                    .ToDictionary(pair => pair.Key.Id, pair => pair.Value.Value)),
                    "Disk snapshot must contain exact integer hundredths for each damage type.");
                Assert.That(savedRuin.ObjectiveKind, Is.EqualTo("ruin"));
                Assert.That(savedRuin.RuinMaterialDeposited, Is.EqualTo(progressBefore),
                    "Disk snapshot must contain paid repair progress before native map reload.");
                Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
                Assert.That(war.State, Is.EqualTo(beforeRestart));
                Assert.That(SEntMan.EntityExists(oldMap), Is.False);
                Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.Not.EqualTo(oldMap));
                Assert.That(SEntMan.EntityExists(liveHall), Is.False);
                Assert.That(SEntMan.EntityExists(ruin), Is.False);
                var restoredRuin = FindMapEntity<TownHallRuinComponent>(ticker.DefaultMap,
                    objective => objective.TerritoryId == "frontline-five");
                Assert.That(SEntMan.HasComponent<TownHallComponent>(restoredRuin), Is.False,
                    "A destroyed hall must remain a ruin after normal preset reload.");
                Assert.That(SComp<TownHallRuinComponent>(restoredRuin).DepositedBasicMaterials,
                    Is.EqualTo(progressBefore), "Technical restart must not erase paid ruin repair progress.");
                var restoredHall = FindMapEntity<TownHallComponent>(ticker.DefaultMap,
                    hall => hall.TerritoryId == "frontline-one");
                Assert.That(damage.GetAllDamage(restoredHall).DamageDict, Is.EquivalentTo(damageBefore.DamageDict),
                    "Technical restart must retain exact per-type objective damage, not heal the hall.");
            }));
        }
        finally
        {
            // Native cleanup on behavioral RED too; fixture teardown removes saves and restores the preset.
            await Server.WaitPost(() => ticker.RestartRound());
        }
    }
#pragma warning restore CS0618

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

    [Test]
    public async Task PausedMapCapturePreservesResourceFieldsNodesAndSlots()
    {
        var fields = Server.System<FrontlineResourceFieldSystem>();
        var maps = Server.System<SharedMapSystem>();
        var map = await Pair.LoadTestMap(new ResPath("/Maps/Frontline/base_test.yaml"));
        Exception failure = null;
        try
        {
            await Server.WaitPost(() =>
            {
                try
                {
                    var field = FindMapEntity<FrontlineResourceFieldComponent>(map.MapId, _ => true);
                    var component = SComp<FrontlineResourceFieldComponent>(field);
                    component.RemainingReserveNodes = 2;
                    var partial = component.ActiveNodes.Single(uid => SComp<FrontlineResourceNodeComponent>(uid).SlotId == "iron-west");
                    SComp<FrontlineResourceNodeComponent>(partial).RemainingYield = 15;
                    var before = JsonSerializer.Serialize(fields.CaptureSnapshot(map.MapId));
                    maps.SetPaused(map.MapId, true);
                    Assert.That(SComp<MetaDataComponent>(field).EntityPaused, Is.True);
                    foreach (var node in component.ActiveNodes)
                    {
                        Assert.That(SComp<MetaDataComponent>(node).EntityPaused, Is.True);
                        Assert.That(SComp<MetaDataComponent>(SComp<FrontlineResourceNodeComponent>(node).SpawnPoint).EntityPaused,
                            Is.True);
                    }
                    var saved = fields.CaptureSnapshot(map.MapId);
                    Assert.That(saved, Has.Count.EqualTo(1), "Paused controllers still own durable resource claims.");
                    Assert.That(saved.Single().Nodes, Has.Count.EqualTo(3), "Paused nodes and their mapper slots must remain covered.");
                    Assert.That(JsonSerializer.Serialize(saved), Is.EqualTo(before));
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    maps.SetPaused(map.MapId, false);
                }
            });
            Assert.That(Server.UnhandledException, Is.Null);
            if (failure != null)
                throw failure;
        }
        finally
        {
            await Server.WaitPost(() => SEntMan.DeleteEntity(map.MapUid));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PausedResourceCaptureDoesNotSpendCooldown(bool replenishing)
    {
        var fields = Server.System<FrontlineResourceFieldSystem>();
        var maps = Server.System<SharedMapSystem>();
        var timing = Server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();
        var map = await Pair.LoadTestMap(new ResPath("/Maps/Frontline/base_test.yaml"));
        EntityUid field = default;
        long remaining = 0;
        try
        {
            await Server.WaitPost(() =>
            {
                field = FindMapEntity<FrontlineResourceFieldComponent>(map.MapId, _ => true);
                var component = SComp<FrontlineResourceFieldComponent>(field);
                if (replenishing)
                {
                    component.RemainingReserveNodes = 0;
                    foreach (var node in component.ActiveNodes.ToArray())
                        SEntMan.DeleteEntity(node);
                    component.ReplenishmentDelay = TimeSpan.FromSeconds(60);
                    component.NextReplenishment = timing.CurTime + component.ReplenishmentDelay;
                }
                else
                {
                    component.ReplacementDelay = TimeSpan.FromSeconds(60);
                    component.NextReplacement = timing.CurTime + component.ReplacementDelay;
                }
                maps.SetPaused(map.MapId, true);
                var saved = fields.CaptureSnapshot(map.MapId).Single();
                remaining = replenishing ? saved.ReplenishmentRemainingTicks : saved.ReplacementRemainingTicks;
            });
            await Pair.RunTicksSync(10);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Server.System<MetaDataSystem>().GetPauseTime(field), Is.GreaterThan(TimeSpan.Zero));
                var saved = fields.CaptureSnapshot(map.MapId).Single();
                Assert.That(replenishing ? saved.ReplenishmentRemainingTicks : saved.ReplacementRemainingTicks,
                    Is.EqualTo(remaining), "Paused time must not spend a durable resource cooldown.");
            });
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                maps.SetPaused(map.MapId, false);
                SEntMan.DeleteEntity(map.MapUid);
            });
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task QueuedDepletedResourceControllerBlocksRestartBeforeEntityFlush(bool paused)
    {
        var ticker = Server.System<GameTicker>();
        var war = Server.System<WarStateSystem>();
        var fields = Server.System<FrontlineResourceFieldSystem>();
        var maps = Server.System<SharedMapSystem>();
        var data = Server.ResolveDependency<IResourceManager>().UserData;
        Exception failure = null;

        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            war.StartNewWar();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunTicksSync(1);
        // Produce a real committed save, then load its matching campaign map.
        await Server.WaitPost(() =>
        {
            ticker.RestartRound();
            ticker.SetGamePreset("PersistentWar");
            ticker.ToggleReadyAll(true);
            ticker.StartRound(true);
        });
        await Pair.RunTicksSync(1);
        await Server.WaitPost(() =>
        {
            try
            {
                var map = maps.GetMapOrInvalid(ticker.DefaultMap);
                var field = FindMapEntity<FrontlineResourceFieldComponent>(ticker.DefaultMap, _ => true);
                var component = SComp<FrontlineResourceFieldComponent>(field);
                // Valid owned depleted setup; native node termination schedules the cooldown.
                component.RemainingReserveNodes = 0;
                foreach (var node in component.ActiveNodes.ToArray())
                    SEntMan.DeleteEntity(node);
                Assert.That(component.State, Is.EqualTo(FrontlineResourceFieldState.Depleted));
                Assert.That(component.ActiveNodes, Is.Empty);
                Assert.That(fields.CaptureSnapshot(ticker.DefaultMap), Has.Count.EqualTo(1));
                var objectives = SEntMan.EntityQuery<TownHallComponent>()
                    .Where(hall => SComp<TransformComponent>(hall.Owner).MapID == ticker.DefaultMap)
                    .Select(hall => hall.Owner).ToArray();
                string saved;
                using (var before = data.OpenRead(WarStrategicSnapshotSystem.SavePath))
                    saved = new StreamReader(before).ReadToEnd();

                if (paused)
                {
                    // Leave objectives live; pause the controller and its slots so omission cannot hide behind an orphan-slot error.
                    var metadata = Server.System<MetaDataSystem>();
                    metadata.SetEntityPaused(field, true);
                    foreach (var slot in SEntMan.EntityQuery<FrontlineResourceSpawnPointComponent>().Where(slot =>
                                 slot.FieldId == component.FieldId && SComp<TransformComponent>(slot.Owner).MapID == ticker.DefaultMap).ToArray())
                    {
                        metadata.SetEntityPaused(slot.Owner, true);
                        Assert.That(SComp<MetaDataComponent>(slot.Owner).EntityPaused, Is.True);
                    }
                    Assert.That(SComp<MetaDataComponent>(field).EntityPaused, Is.True);
                }
                // Do not yield: capture must see the native queue before deletion settles.
                SEntMan.QueueDeleteEntity(field);
                Assert.That(SEntMan.IsQueuedForDeletion(field), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.Throws<InvalidDataException>(() => ticker.RestartRound(),
                        "A queued controller must not be silently omitted from a supposedly complete save.");
                    Assert.That(SEntMan.EntityExists(map), Is.True, "Capture refusal must precede FlushEntities.");
                    Assert.That(maps.GetMapOrInvalid(ticker.DefaultMap), Is.EqualTo(map));
                    foreach (var objective in objectives)
                        Assert.That(SEntMan.EntityExists(objective), Is.True);
                    using var after = data.OpenRead(WarStrategicSnapshotSystem.SavePath);
                    Assert.That(new StreamReader(after).ReadToEnd(), Is.EqualTo(saved),
                        "Do not replace the last restorable save with a snapshot missing its depleted field.");
                });
            }
            catch (Exception e)
            {
                // Assertion failures belong on the test thread, not the server's unhandled boundary.
                failure = e;
            }
        });
        Assert.That(Server.UnhandledException, Is.Null);
        if (failure != null)
            throw failure;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OldEntityTerminationCannotCommitInvalidResourceSnapshot(bool spawnExtraNode)
    {
        const int warId = 42;
        const string fieldId = "frontline-test-iron";
        var snapshots = Server.System<WarStrategicSnapshotSystem>();
        var fields = Server.System<FrontlineResourceFieldSystem>();
        var probe = Server.System<QueueStagedResourceOnOldNodeTerminationSystem>();
        var data = Server.ResolveDependency<IResourceManager>().UserData;
        var source = await Pair.LoadTestMap(new ResPath("/Maps/Frontline/base_test.yaml"));
        string saved = null;
        Exception failure = null;
        try
        {
            await Server.WaitPost(() =>
            {
                try
                {
                    var field = FindMapEntity<FrontlineResourceFieldComponent>(source.MapId, _ => true);
                    var component = SComp<FrontlineResourceFieldComponent>(field);
                    Assert.That(component.ActiveNodes, Has.Count.EqualTo(3));
                    component.RemainingReserveNodes = 2;
                    var partial = component.ActiveNodes.Single(uid => SComp<FrontlineResourceNodeComponent>(uid).SlotId == "iron-west");
                    SComp<FrontlineResourceNodeComponent>(partial).RemainingYield = 15;
                    var bases = Server.System<TerritorySystem>().GetTerritories(source.MapId).Select(id =>
                    {
                        var hall = id.Id is "frontline-one" or "frontline-five";
                        var uid = hall
                            ? FindMapEntity<TownHallComponent>(source.MapId, objective => objective.TerritoryId == id.Id)
                            : FindMapEntity<TownHallRuinComponent>(source.MapId, objective => objective.TerritoryId == id.Id);
                        if (hall)
                        {
                            var counts = SComp<FrontlineStockpileComponent>(uid).Counts;
                            counts.Clear();
                            counts["SoldierSupplies"] = 7;
                            counts["BasicMaterials"] = 11;
                        }
                        return new WarBaseSnapshot(id.Id, hall ? "hall" : "ruin",
                            SComp<MetaDataComponent>(uid).EntityPrototype!.ID,
                            hall ? SComp<TownHallComponent>(uid).FactionId : null,
                            hall ? SComp<FrontlineStockpileComponent>(uid).Counts.ToDictionary(entry => entry.Key.Id, entry => entry.Value)
                                : new Dictionary<string, int>());
                    }).ToList();
                    var resources = fields.CaptureSnapshot(source.MapId);
                    Assert.That(resources.Single().RemainingReserveNodes, Is.EqualTo(2));
                    Assert.That(resources.Single().Nodes.Select(node => node.RemainingYield), Is.EquivalentTo(new[] { 15, 20, 20 }));
                    saved = JsonSerializer.Serialize(new WarStrategicSnapshot(WarStrategicSnapshotSystem.SnapshotVersion, warId, bases)
                    {
                        Resources = resources,
                        Refineries = Server.System<FrontlineRefinerySystem>().CaptureSnapshot(source.MapId),
                    });
                    data.Delete(WarStrategicSnapshotSystem.BackupPath);
                    using var stream = data.OpenWrite(WarStrategicSnapshotSystem.SavePath);
                    using var writer = new StreamWriter(stream);
                    writer.Write(saved);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            if (failure != null)
                throw failure;

            var fresh = await Pair.LoadTestMap(new ResPath("/Maps/Frontline/base_test.yaml"));
            await Server.WaitPost(() =>
            {
                try
                {
                    probe.SpawnExtraNode = spawnExtraNode;
                    probe.OldNode = spawnExtraNode
                        ? FindMapEntity<TownHallComponent>(fresh.MapId, hall => hall.TerritoryId == "frontline-one")
                        : FindMapEntity<FrontlineResourceNodeComponent>(fresh.MapId,
                            node => node.FieldId == fieldId && node.SlotId == "iron-west");
                    Assert.That(SEntMan.HasComponent<TagComponent>(probe.OldNode), Is.True);
                    if (!spawnExtraNode)
                        Assert.That(SComp<FrontlineResourceNodeComponent>(probe.OldNode).RemainingYield, Is.EqualTo(20));
                    probe.Enabled = true;
                    Assert.Multiple(() =>
                    {
                        // Abort startup is allowed; retaining the original entity IDs is not required.
                        Assert.Throws<InvalidDataException>(() => snapshots.Restore(fresh.MapUid, warId),
                            "Native old-entity deletion changed the map's resource claims after preflight; restore must fail closed.");
                        Assert.That(probe.Fired, Is.True, "The observer must run on real EntityTerminatingEvent, not a synthetic event.");
                        Assert.That(probe.StagedNode, Is.Not.EqualTo(EntityUid.Invalid));
                        Assert.That(probe.DetachedDuringCallback, Is.True);
                        Assert.That(probe.QueuedDuringCallback, Is.EqualTo(!spawnExtraNode));
                        Assert.That(probe.YieldDuringCallback, Is.EqualTo(spawnExtraNode ? 20 : 15));
                        Assert.That(SEntMan.EntityExists(fresh.MapUid), Is.False, "Reject the entire unaccepted map after commit started.");
                        Assert.That(SEntMan.EntityExists(probe.StagedNode), Is.False, "No extra or staged yield may survive refusal.");
                        Assert.That(Server.UnhandledException, Is.Null);
                        using var stream = data.OpenRead(WarStrategicSnapshotSystem.SavePath);
                        Assert.That(new StreamReader(stream).ReadToEnd(), Is.EqualTo(saved));
                    });
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    probe.Enabled = false;
                }
            });
            Assert.That(Server.UnhandledException, Is.Null);
            if (failure != null)
                throw failure;
            await Pair.RunTicksSync(2);

            // Retry startup on a clean map from the same disk claim, not fresh reserve/yield defaults.
            var retry = await Pair.LoadTestMap(new ResPath("/Maps/Frontline/base_test.yaml"));
            await Server.WaitPost(() =>
            {
                try
                {
                    snapshots.Restore(retry.MapUid, warId);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            if (failure != null)
                throw failure;
            await Pair.RunTicksSync(1);
            await Server.WaitAssertion(() =>
            {
                var field = FindMapEntity<FrontlineResourceFieldComponent>(retry.MapId, component => component.FieldId == fieldId);
                var component = SComp<FrontlineResourceFieldComponent>(field);
                Assert.That(component.RemainingReserveNodes, Is.EqualTo(2), "Retry must not mint the fresh-map reserve of 3.");
                Assert.That(component.ActiveNodes, Has.Count.EqualTo(3));
                foreach (var uid in component.ActiveNodes)
                {
                    var node = SComp<FrontlineResourceNodeComponent>(uid);
                    Assert.That(SEntMan.EntityExists(uid), Is.True);
                    Assert.That(SEntMan.IsQueuedForDeletion(uid), Is.False);
                    Assert.That(node.Field, Is.EqualTo(field));
                    Assert.That(node.RemainingYield, Is.EqualTo(node.SlotId == "iron-west" ? 15 : 20));
                }
                Assert.That(fields.CaptureSnapshot(retry.MapId).Single().Nodes, Has.Count.EqualTo(3));
                var mapNodes = new List<EntityUid>();
                var query = SEntMan.AllEntityQueryEnumerator<FrontlineResourceNodeComponent, TransformComponent>();
                while (query.MoveNext(out var uid, out _, out var transform))
                {
                    if (transform.MapID == retry.MapId)
                        mapNodes.Add(uid);
                }
                Assert.That(mapNodes, Is.EquivalentTo(component.ActiveNodes), "No detached or paused extra yield may survive retry.");
                Assert.That(mapNodes.Sum(uid => SComp<FrontlineResourceNodeComponent>(uid).RemainingYield), Is.EqualTo(55));
                foreach (var territory in new[] { "frontline-one", "frontline-five" })
                {
                    var hall = FindMapEntity<TownHallComponent>(retry.MapId, objective => objective.TerritoryId == territory);
                    Assert.That(SComp<FrontlineStockpileComponent>(hall).Counts,
                        Is.EquivalentTo(new Dictionary<ProtoId<FrontlineSupplyProductPrototype>, int>
                            { ["SoldierSupplies"] = 7, ["BasicMaterials"] = 11 }));
                }
                using var stream = data.OpenRead(WarStrategicSnapshotSystem.SavePath);
                Assert.That(new StreamReader(stream).ReadToEnd(), Is.EqualTo(saved));
                Assert.That(Server.UnhandledException, Is.Null);
            });
        }
        finally
        {
            await Server.WaitPost(() => probe.Enabled = false);
        }
    }

    public sealed class QueueStagedResourceOnOldNodeTerminationSystem : EntitySystem
    {
        public bool Enabled;
        public bool Fired;
        public bool SpawnExtraNode;
        private static readonly EntProtoId ExtraNodePrototype = "FrontlineIronResourceNode";
        public EntityUid OldNode;
        public EntityUid StagedNode = EntityUid.Invalid;
        public bool DetachedDuringCallback;
        public bool QueuedDuringCallback;
        public int YieldDuringCallback;

        public override void Initialize()
        {
            // BaseStructure supplies Tag; this component/native event pair is otherwise unclaimed.
            SubscribeLocalEvent<TagComponent, EntityTerminatingEvent>(OnOldNodeTerminating);
        }

        private void OnOldNodeTerminating(Entity<TagComponent> ent, ref EntityTerminatingEvent args)
        {
            if (!Enabled || Fired || ent.Owner != OldNode)
                return;
            Fired = true;
            if (SpawnExtraNode)
            {
                // Runs after resource commit, during real old-base Del; no synthetic event or field ownership.
                var coordinates = Transform(ent).Coordinates;
                if (TerminatingOrDeleted(coordinates.EntityId))
                    return;
                StagedNode = Spawn(ExtraNodePrototype, coordinates);
                var extra = Comp<FrontlineResourceNodeComponent>(StagedNode);
                DetachedDuringCallback = extra.Field == EntityUid.Invalid && string.IsNullOrEmpty(extra.FieldId) &&
                    extra.SpawnPoint == EntityUid.Invalid && string.IsNullOrEmpty(extra.SlotId);
                YieldDuringCallback = extra.RemainingYield;
                QueuedDuringCallback = EntityManager.IsQueuedForDeletion(StagedNode);
                return;
            }
            var old = Comp<FrontlineResourceNodeComponent>(ent);
            var mapId = Transform(ent).MapID;
            var query = EntityQueryEnumerator<FrontlineResourceNodeComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var node, out var transform))
            {
                if (uid == OldNode || transform.MapID != mapId || node.SlotId != old.SlotId ||
                    node.Field != EntityUid.Invalid || !string.IsNullOrEmpty(node.FieldId) || TerminatingOrDeleted(uid))
                    continue;
                StagedNode = uid;
                DetachedDuringCallback = node.SpawnPoint == EntityUid.Invalid;
                YieldDuringCallback = node.RemainingYield;
                QueueDel(uid);
                QueuedDuringCallback = EntityManager.IsQueuedForDeletion(uid);
                return;
            }
        }
    }

    [Test]
    public async Task RefineryRestoreQueueObserverFailureCannotLeaveLaterStagedOutputObtainable()
    {
        const int warId = 42;
        var snapshots = Server.System<WarStrategicSnapshotSystem>();
        var refineries = Server.System<FrontlineRefinerySystem>();
        var stacks = Server.System<StackSystem>();
        var containers = Server.System<SharedContainerSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var probe = Server.System<ObserveRefineryRestoreInsertionSystem>();
        var manager = (EntityManager) SEntMan; // Public native C# event, not an ECS event.
        var data = Server.ResolveDependency<IResourceManager>().UserData;
        var fresh = await Pair.LoadTestMap(new ResPath("/Maps/Frontline/base_test.yaml"));
        var provisional = new List<EntityUid>();
        var queueFailure = new InvalidOperationException("One-shot native refinery queue observer failure.");
        var threw = false;
        var guardedDuringQueue = true;
        EntityUid refinery = default;
        EntityUid player = default;
        Exception failure = null;

        void OnQueued(EntityUid uid)
        {
            if (!provisional.Contains(uid))
                return;
            guardedDuringQueue &= !refineries.TryTakePlayerOutput(refinery, player);
            if (uid == provisional[0] && !threw)
            {
                threw = true;
                // QueueDeleteEntity has recorded the UID, but has not started native termination.
                throw queueFailure;
            }
        }

        try
        {
            await Server.WaitPost(() =>
            {
                try
                {
                    refinery = FindMapEntity<FrontlineRefineryComponent>(fresh.MapId, _ => true);
                    var output = SComp<FrontlineRefineryComponent>(refinery).OutputContainer;
                    var coordinates = SComp<TransformComponent>(refinery).Coordinates.Offset(new Vector2(0, 1));
                    player = SEntMan.SpawnEntity("MobHuman", coordinates);
                    // Positive control: the same actor can really take retained output at this machine.
                    var control = stacks.SpawnAtPosition(5, "BasicMaterials", coordinates);
                    Assert.That(containers.Insert(control, output), Is.True);
                    Assert.That(refineries.TryTakePlayerOutput(refinery, player), Is.True);
                    Assert.That(hands.GetActiveItem(player), Is.EqualTo(control));
                    SEntMan.DeleteEntity(control);
                    Assert.That(hands.GetActiveItem(player), Is.Null);

                    var bases = Server.System<TerritorySystem>().GetTerritories(fresh.MapId).Select(id =>
                    {
                        var hall = id.Id is "frontline-one" or "frontline-five";
                        var uid = hall
                            ? FindMapEntity<TownHallComponent>(fresh.MapId, objective => objective.TerritoryId == id.Id)
                            : FindMapEntity<TownHallRuinComponent>(fresh.MapId, objective => objective.TerritoryId == id.Id);
                        return new WarBaseSnapshot(id.Id, hall ? "hall" : "ruin",
                            SComp<MetaDataComponent>(uid).EntityPrototype!.ID,
                            hall ? SComp<TownHallComponent>(uid).FactionId : null,
                            hall ? SComp<FrontlineStockpileComponent>(uid).Counts.ToDictionary(entry => entry.Key.Id, entry => entry.Value)
                                : new Dictionary<string, int>());
                    }).ToList();
                    var claims = refineries.CaptureSnapshot(fresh.MapId);
                    var index = claims.FindIndex(entry => entry.RefineryId == SComp<FrontlineRefineryComponent>(refinery).RefineryId);
                    claims[index] = claims[index] with
                    {
                        Outputs = new()
                        {
                            new WarRefineryStackSnapshot("BasicMaterials", "BasicMaterials1", 5),
                            new WarRefineryStackSnapshot("BasicMaterials", "BasicMaterials1", 7),
                        },
                    };
                    data.Delete(WarStrategicSnapshotSystem.BackupPath);
                    using (var stream = data.OpenWrite(WarStrategicSnapshotSystem.SavePath))
                        JsonSerializer.Serialize(stream, new WarStrategicSnapshot(WarStrategicSnapshotSystem.SnapshotVersion, warId, bases)
                        {
                            Resources = Server.System<FrontlineResourceFieldSystem>().CaptureSnapshot(fresh.MapId),
                            Refineries = claims,
                        });
                    string saved;
                    using (var stream = data.OpenRead(WarStrategicSnapshotSystem.SavePath))
                        saved = new StreamReader(stream).ReadToEnd();

                    probe.Observe = args =>
                    {
                        if (args.Container != output)
                            return;
                        provisional.Add(args.Entity);
                        if (provisional.Count == 2)
                        {
                            // Real insertion + owning SetCount callback invalidate an earlier tracked claim.
                            var first = provisional[0];
                            stacks.SetCount((first, SComp<StackComponent>(first)), 6);
                        }
                    };
                    manager.EntityQueueDeleted += OnQueued;
                    var refusal = Assert.Throws<InvalidDataException>(() => snapshots.Restore(fresh.MapUid, warId));
                    Assert.Multiple(() =>
                    {
                        Assert.That(refusal!.Message, Is.EqualTo("Restored refinery contents changed during native callbacks."),
                            "Cleanup failure must not replace the original precommit restoration refusal.");
                        Assert.That(provisional, Has.Count.EqualTo(2));
                        Assert.That(threw, Is.True, "The first provisional claim must reach the native queue observer.");
                        Assert.That(guardedDuringQueue, Is.True);
                        foreach (var uid in provisional)
                            Assert.That(!SEntMan.EntityExists(uid) || SEntMan.IsQueuedForDeletion(uid), Is.True,
                                "EVERY provisional refinery claim must be queued/deleted before restoring guards release.");
                        // No tick may hide the leak between FinishSnapshotRestore and queue draining.
                        Assert.That(refineries.TryTakePlayerOutput(refinery, player), Is.False,
                            "A refused restore must not release a later provisional output to a player's hand.");
                        Assert.That(hands.GetActiveItem(player), Is.Null);
                        var retry = Assert.Throws<InvalidDataException>(() => snapshots.Restore(fresh.MapUid, warId));
                        Assert.That(retry!.Message, Is.EqualTo("Strategic restore requires a live map."));
                        using var stream = data.OpenRead(WarStrategicSnapshotSystem.SavePath);
                        Assert.That(new StreamReader(stream).ReadToEnd(), Is.EqualTo(saved));
                    });
                }
                catch (Exception e)
                {
                    failure = e; // Assertion failures must not escape the posted server callback.
                }
                finally
                {
                    probe.Observe = null;
                    manager.EntityQueueDeleted -= OnQueued;
                }
            });
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                probe.Observe = null;
                manager.EntityQueueDeleted -= OnQueued;
                SEntMan.QueueDeleteEntity(fresh.MapUid);
            });
            await Pair.RunTicksSync(2); // Native disposal also settles the leaked/held output on RED.
            await Server.WaitAssertion(() =>
            {
                Assert.That(SEntMan.EntityExists(fresh.MapUid), Is.False);
                Assert.That(provisional.All(uid => !SEntMan.EntityExists(uid)), Is.True);
            });
        }
        Assert.That(Server.UnhandledException, Is.Null);
        if (failure != null)
            throw failure;
    }

    public sealed class ObserveRefineryRestoreInsertionSystem : EntitySystem
    {
        public Action<EntInsertedIntoContainerMessage> Observe;

        public override void Initialize()
        {
            // BaseStructure supplies Tag; this native component/event pair is otherwise unclaimed.
            SubscribeLocalEvent<TagComponent, EntInsertedIntoContainerMessage>(OnInserted);
        }

        private void OnInserted(Entity<TagComponent> ent, ref EntInsertedIntoContainerMessage args)
        {
            Observe?.Invoke(args);
        }
    }

    [TestCase(2, 0)]
    [TestCase(WarStrategicSnapshotSystem.SnapshotVersion, 0)]
    [TestCase(1, 41)]
    [TestCase(2, 41)]
    [TestCase(5, 41)]
    [TestCase(WarStrategicSnapshotSystem.SnapshotVersion, 42)]
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
                    var resourceEntries = Server.System<FrontlineResourceFieldSystem>().CaptureSnapshot(map.MapId);
                    if (version == WarStrategicSnapshotSystem.SnapshotVersion && savedWarId == currentWarId)
                        resourceEntries[0] = resourceEntries[0] with { FieldId = "unknown-field" };
                    data.Delete(WarStrategicSnapshotSystem.BackupPath);
                    using (var stream = data.OpenWrite(WarStrategicSnapshotSystem.SavePath))
                        JsonSerializer.Serialize(stream, new WarStrategicSnapshot(version, savedWarId, bases)
                        {
                            Resources = resourceEntries,
                            Refineries = Server.System<FrontlineRefinerySystem>().CaptureSnapshot(map.MapId),
                        });
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
                        JsonSerializer.Serialize(stream, new WarStrategicSnapshot(WarStrategicSnapshotSystem.SnapshotVersion, currentWarId, bases)
                        {
                            Resources = Server.System<FrontlineResourceFieldSystem>().CaptureSnapshot(map.MapId),
                            Refineries = Server.System<FrontlineRefinerySystem>().CaptureSnapshot(map.MapId),
                        });
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
