using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Ghost;
using Content.Server.Mind;
using Content.Server.Roles;
using Content.Shared.CCVar;
using Content.Shared.GameTicking.Components;
using Content.Shared.Roles.Components;
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
    public async Task StartsWithoutJobRoleAndBlocksNormalGhosting()
    {
        var ticker = Server.System<GameTicker>();
        var mindSystem = Server.System<MindSystem>();
        var roleSystem = Server.System<RoleSystem>();
        var ghostSystem = Server.System<GhostSystem>();

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

            Assert.That(mindSystem.TryGetMind(ServerSession!, out var mindId, out var mind));
            Assert.That(roleSystem.MindHasRole<JobRoleComponent>(mindId), Is.False);
            Assert.That(ghostSystem.OnGhostAttempt(mindId, true, viaCommand: true, mind: mind), Is.False);
        });

        ticker.SetGamePreset((GamePresetPrototype) null);
    }
}
