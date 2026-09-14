using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.War;
using Content.Shared.War;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.GameRules;

[TestFixture]
public sealed class WarFactionMembershipTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
    };

    [Test]
    public async Task MembershipSurvivesTechnicalRestartAndLocksFaction()
    {
        var server = Pair.Server;
        var ticker = server.System<GameTicker>();
        var war = server.System<WarStateSystem>();
        var factions = server.System<WarFactionSystem>();
        var account = ServerSession!.UserId;
        var first = new FactionId("FrontlineFactionOne");
        var second = new FactionId("FrontlineFactionTwo");

        await server.WaitAssertion(() =>
        {
            war.StartNewWar();
            factions.ClearFaction(account);

            Assert.That(factions.TrySelectFaction(account, first), Is.True);
            ticker.RestartRound();

            Assert.That(factions.TryGetFaction(account, out var selected), Is.True);
            Assert.That(selected, Is.EqualTo(first));
            Assert.That(factions.TrySelectFaction(account, second), Is.False);
        });
    }

    [Test]
    public async Task NewWarHasNoFactionLock()
    {
        var server = Pair.Server;
        var war = server.System<WarStateSystem>();
        var factions = server.System<WarFactionSystem>();
        var account = ServerSession!.UserId;
        var first = new FactionId("FrontlineFactionOne");
        var second = new FactionId("FrontlineFactionTwo");

        await server.WaitAssertion(() =>
        {
            war.StartNewWar();
            factions.ClearFaction(account);
            Assert.That(factions.TrySelectFaction(account, first), Is.True);

            war.EndWar();
            war.StartNewWar();

            Assert.That(factions.TryGetFaction(account, out _), Is.False);
            Assert.That(factions.TrySelectFaction(account, second), Is.True);
        });
    }
}
