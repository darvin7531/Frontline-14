using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server.War;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class TerritoryVictoryTest : GameTest
{
    [Test]
    public async Task FourOwnedTerritoriesEndWarForTheirFaction()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var faction = new FactionId("FrontlineFactionOne");
        var war = server.System<WarStateSystem>();
        var territories = server.System<TerritorySystem>();
        _ = server.System<WarVictorySystem>();

        await server.WaitPost(() =>
        {
            war.StartNewWar();

            for (var i = 0; i < 5; i++)
            {
                var coordinates = new EntityCoordinates(map.Grid.Owner, map.GridCoords.Position + new Vector2(i * 0.2f, 0));
                var territory = SEntMan.SpawnEntity(null, coordinates);
                SEntMan.AddComponent<TerritoryComponent>(territory)
                    .Configure(new TerritoryId($"territory-{i}"), coordinates.Position - new Vector2(0.5f), coordinates.Position + new Vector2(0.5f));

                if (i == 4)
                    continue;

                var hall = SEntMan.SpawnEntity(null, coordinates);
                SEntMan.AddComponent<TownHallComponent>(hall).Configure(new TerritoryId($"territory-{i}"), faction);
            }
        });

        await Pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(war.State?.Status, Is.EqualTo(WarStatus.Ended));
            Assert.That(war.State?.Winner, Is.EqualTo(faction));
            Assert.That(territories.CountOwned(faction), Is.EqualTo(4));
        });
    }

    [Test]
    public async Task TerritoryBoundsAreRelativeToMarker()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var territorySystem = server.System<TerritorySystem>();
        var markerCoordinates = new EntityCoordinates(map.Grid.Owner, map.GridCoords.Position + new Vector2(10, 0));

        await server.WaitPost(() =>
        {
            var marker = SEntMan.SpawnEntity(null, markerCoordinates);
            SEntMan.AddComponent<TerritoryComponent>(marker)
                .Configure(new TerritoryId("relative"), new Vector2(-1, -1), new Vector2(1, 1));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(territorySystem.Contains(new TerritoryId("relative"), markerCoordinates), Is.True);
            Assert.That(territorySystem.Contains(new TerritoryId("relative"), map.GridCoords), Is.False);
        });
    }

    [Test]
    public async Task FactionTwoTownHallCoreBelongsToFactionTwo()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();

        await server.WaitPost(() =>
        {
            var hall = SEntMan.SpawnEntity("TownHallCoreFactionTwo", map.GridCoords);
            Assert.That(SEntMan.GetComponent<TownHallComponent>(hall).FactionId,
                Is.EqualTo("FrontlineFactionTwo"));
        });
    }
}
