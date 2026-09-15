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
        _ = server.System<WarVictorySystem>();

        await server.WaitPost(() =>
        {
            war.StartNewWar();

            for (var i = 0; i < 5; i++)
            {
                var coordinates = new EntityCoordinates(map.Grid.Owner, map.GridCoords.Position + new Vector2(i * 2, 0));
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
        });
    }
}
