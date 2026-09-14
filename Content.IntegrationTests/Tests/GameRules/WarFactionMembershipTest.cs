using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.War;
using Content.Shared.War;
using Robust.Client.Console;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

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
    public async Task MembershipSurvivesReconnectAndTechnicalRestartAndLocksFaction()
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
        });

        var client = Pair.Client;
        var console = client.ResolveDependency<IClientConsoleHost>();
        var network = client.ResolveDependency<IClientNetManager>();
        await client.WaitPost(() => console.ExecuteCommand("disconnect"));
        await Pair.RunTicksSync(5);
        client.SetConnectTarget(server);
        await client.WaitPost(() => network.ClientConnect(null, 0, null));
        await Pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var reconnected = server.ResolveDependency<IPlayerManager>().Sessions.Single();
            Assert.That(reconnected.UserId, Is.EqualTo(account));

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
