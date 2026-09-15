using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server.War;
using Content.Shared.War;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
[TestOf(typeof(TerritorySystem))]
public sealed class TerritoryTownHallTest : GameTest
{
    [Test]
    public async Task ActiveTownHallOwnsTerritoryAndEnablesFactionSpawn()
    {
        await Pair.CreateTestMap();

        var spawns = Server.System<FactionSpawnSystem>();
        var territories = Server.System<TerritorySystem>();
        var faction = new FactionId("FrontlineFactionOne");
        var territoryId = new TerritoryId("TestTerritory");

        await Server.WaitPost(() =>
        {
            var territory = SEntMan.SpawnEntity(null, TestMap!.GridCoords);
            SEntMan.AddComponent<TerritoryComponent>(territory).Configure(territoryId, new Vector2(-5, -5), new Vector2(5, 5));

            var hall = SEntMan.SpawnEntity(null, new EntityCoordinates(TestMap.Grid, 0, 0));
            SEntMan.AddComponent<TownHallComponent>(hall).Configure(territoryId, faction);

            var spawn = SEntMan.SpawnEntity(null, new EntityCoordinates(TestMap.Grid, 1, 1));
            SEntMan.AddComponent<FactionSpawnPointComponent>(spawn).TerritoryId = territoryId.Id;
        });

        await Server.WaitPost(() =>
        {
            Assert.That(territories.GetState(territoryId), Is.EqualTo(TerritoryState.Owned));
            Assert.That(spawns.GetAvailableSpawns(faction), Has.Count.EqualTo(1));
        });
    }
}
