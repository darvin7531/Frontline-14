using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Mind;
using Content.Shared.CCVar;
using Content.Shared.GameTicking.Components;

using Robust.Shared.GameObjects;
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
    public async Task DoesNotStartWithoutConfiguredMap()
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
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.PreRoundLobby));
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
}
