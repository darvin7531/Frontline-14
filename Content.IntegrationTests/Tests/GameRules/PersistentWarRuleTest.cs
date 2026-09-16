using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Mind;
using Content.Server.War;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.CCVar;
using Content.Shared.GameTicking.Components;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.War;

using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests.GameRules;

[TestFixture]
[TestOf(typeof(PersistentWarRuleComponent))]
public sealed class PersistentWarRuleTest : GameTest
{
    public enum InvalidMapCase
    {
        UnassignedTerritory,
        FourTerritories,
        MissingSpawn,
        WrongHallFaction,
        ObjectiveOutsideBounds,
        DuplicateObjective,
        WrongObjectiveComposition,
    }

    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
        InLobby = true,
    };

    public override async Task DoTeardown()
    {
        await Server.WaitPost(() => Server.ResolveDependency<IResourceManager>().UserData.Delete(WarStateSystem.SavePath));
        await base.DoTeardown();
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GameMap), "")]
    public async Task StartsGroundMapWithPersistentDayNight()
    {
        var ticker = Server.System<GameTicker>();
        var atmosphere = Server.System<AtmosphereSystem>();
        var resources = Server.ResolveDependency<IResourceManager>();
        var war = Server.System<WarStateSystem>();
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

            map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            atmos = SEntMan.GetComponent<MapAtmosphereComponent>(map);
            var light = SEntMan.GetComponent<MapLightComponent>(map);
            cycle = SEntMan.GetComponent<LightCycleComponent>(map);

            Assert.Multiple(() =>
            {
                Assert.That(atmos.Space, Is.False);
                Assert.That(cycle.Duration, Is.GreaterThan(TimeSpan.Zero));
                Assert.That(SharedLightCycleSystem.GetColor((map, cycle), light.AmbientLightColor, 0),
                    Is.Not.EqualTo(SharedLightCycleSystem.GetColor((map, cycle), light.AmbientLightColor, (float) cycle.Duration.TotalSeconds / 2)));
            });

            Assert.That(cycle.Offset, Is.InRange(TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(8)));

            var validator = Server.System<PersistentWarMapValidatorSystem>();
            Assert.DoesNotThrow(() => validator.Validate(map));
            atmosphere.SetMapSpace(map, true, atmos);
            var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(map));
            Assert.That(error!.Message, Is.EqualTo("PersistentWar map must set MapAtmosphere.space to false."));
            atmosphere.SetMapSpace(map, false, atmos);
        });

        await Pair.RunUntilSynced();
        await Pair.Client.WaitAssertion(() =>
        {
            var clientCycle = CEntMan.GetComponent<LightCycleComponent>(Pair.ToClientUid(map));
            Assert.That(clientCycle.Offset, Is.EqualTo(cycle.Offset));
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
            Assert.Multiple(() =>
            {
                Assert.That(error!.Message, Does.Contain("PersistentWar map must include MapAtmosphere."));
                Assert.That(error.Message, Does.Contain("PersistentWar map must include MapLight."));
                Assert.That(error.Message, Does.Contain("PersistentWar map must include LightCycle."));
                Assert.That(error.Message, Does.Contain("PersistentWar map must define exactly five territories (found 0)."));
                Assert.That(error.Message, Does.Contain("PersistentWar map must include a town hall or ruin for each territory."));
                Assert.That(error.Message, Does.Contain("PersistentWar map must include a faction spawn point for each territory."));
                Assert.That(error.Message, Does.Contain("PersistentWar map must include exactly one starting town hall for FrontlineFactionOne"));
                Assert.That(error.Message, Does.Contain("PersistentWar map must include exactly one starting town hall for FrontlineFactionTwo"));
            });
        });
    }

    [TestCase(InvalidMapCase.UnassignedTerritory,
        "PersistentWar map territory markers must not use Unassigned.")]
    [TestCase(InvalidMapCase.FourTerritories,
        "PersistentWar map must define exactly five territories (found 4).")]
    [TestCase(InvalidMapCase.MissingSpawn,
        "PersistentWar map territory 'frontline-one' must include a faction spawn point within its bounds.")]
    [TestCase(InvalidMapCase.WrongHallFaction,
        "PersistentWar map must include exactly one starting town hall for FrontlineFactionOne")]
    [TestCase(InvalidMapCase.ObjectiveOutsideBounds,
        "PersistentWar map territory 'frontline-one' must include exactly one objective within its bounds")]
    [TestCase(InvalidMapCase.DuplicateObjective,
        "PersistentWar map territory 'frontline-one' must include exactly one objective within its bounds")]
    [TestCase(InvalidMapCase.WrongObjectiveComposition,
        "PersistentWar map must include exactly two town halls and three ruins")]
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
                case InvalidMapCase.MissingSpawn:
                    SEntMan.DeleteEntity(FindMapEntity<FactionSpawnPointComponent>(mapId,
                        spawn => spawn.TerritoryId == "frontline-one"));
                    break;
                case InvalidMapCase.WrongHallFaction:
                    SComp<TownHallComponent>(FindMapEntity<TownHallComponent>(mapId,
                        hall => hall.FactionId == "FrontlineFactionOne")).FactionId = "FrontlineFactionTwo";
                    break;
                case InvalidMapCase.ObjectiveOutsideBounds:
                    Server.System<SharedTransformSystem>().SetLocalPosition(
                        FindMapEntity<TownHallComponent>(mapId, hall => hall.TerritoryId == "frontline-one"),
                        new Vector2(-10f, -10f));
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
            Assert.Multiple(() =>
            {
                Assert.That(newWar.WarId, Is.EqualTo(42));
                Assert.That(newWar.StartedAt, Is.GreaterThan(oldStartedAt));
                Assert.That(war.State, Is.EqualTo(newWar));
            });

            var map = Server.System<SharedMapSystem>().GetMapOrInvalid(ticker.DefaultMap);
            Assert.That(SComp<LightCycleComponent>(map).Offset, Is.LessThan(TimeSpan.FromMinutes(1)));
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
