using System;
using System.IO;
using System.Text.Json;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Mind;
using Content.Server.War;
using Content.Shared.Atmos.Components;
using Content.Shared.CCVar;
using Content.Shared.GameTicking.Components;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;

using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests.GameRules;

[TestFixture]
[TestOf(typeof(PersistentWarRuleComponent))]
public sealed class PersistentWarRuleTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
        InLobby = true,
    };

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
            var atmos = SEntMan.GetComponent<MapAtmosphereComponent>(map);
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
                Assert.That(error.Message, Does.Contain("PersistentWar map must include a starting town hall for FrontlineFactionOne."));
                Assert.That(error.Message, Does.Contain("PersistentWar map must include a starting town hall for FrontlineFactionTwo."));
            });
        });
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
}
