using System.Collections.Generic;
using System.Linq;
using Content.Client.War;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.War;
using Content.Shared.War;
using Robust.Client.Console;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
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
    public async Task FactionSelectorShowsAvailableFactionsWhenOpened()
    {
        var server = Pair.Server;
        var war = server.System<WarStateSystem>();
        var factions = server.System<WarFactionSystem>();
        var account = ServerSession!.UserId;

        await server.WaitPost(() =>
        {
            war.StartNewWar();
            factions.ClearFaction(account);
            factions.OpenSelector(server.ResolveDependency<IPlayerManager>().Sessions.Single());
        });
        await Pair.RunUntilSynced();

        var ui = Pair.Client.ResolveDependency<IUserInterfaceManager>();
        await Pair.Client.WaitAssertion(() =>
        {
            var window = ui.WindowRoot.Children.OfType<FactionSelectionWindow>().Single(control => control.IsOpen);
            Assert.That(Descendants(window).OfType<Button>().ToArray(), Has.Length.EqualTo(2));
        });
    }

    [Test]
    public async Task MembershipSurvivesReconnectAndTechnicalRestartAndLocksFaction()
    {
        var server = Pair.Server;
        var ticker = server.System<GameTicker>();
        var war = server.System<WarStateSystem>();
        var factions = server.System<WarFactionSystem>();
        var account = ServerSession!.UserId;
        var accountName = ServerSession.Name;
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
        await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());
        client.SetConnectTarget(server);
        await client.WaitPost(() => network.ClientConnect(null, 0, accountName));
        await Pair.RunTicksSync(10);
        await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());

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

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (var child in parent.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
