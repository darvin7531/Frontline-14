using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server.War;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class TerritoryVictoryTest : GameTest
{
    [Test]
    public async Task VictoryUsesConfiguredTerritoryPoints()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var otherMap = await Pair.CreateTestMap();
        var faction = new FactionId("FrontlineFactionOne");
        var war = server.System<WarStateSystem>();
        var territories = server.System<TerritorySystem>();
        var definition = server.ResolveDependency<IPrototypeManager>().Index<FrontlineWarPrototype>(FrontlineWarPrototype.MainWar);
        var victory = server.System<WarVictorySystem>();

        await server.WaitPost(() =>
        {
            war.StartNewWar();

            for (var i = 0; i < definition.Territories.Count; i++)
            {
                var coordinates = new EntityCoordinates(map.Grid.Owner, map.GridCoords.Position + new Vector2(i * 0.2f, 0));
                var territory = SEntMan.SpawnEntity(null, coordinates);
                SEntMan.AddComponent<TerritoryComponent>(territory)
                    .Configure(new TerritoryId(definition.Territories[i].Id), new Vector2(-0.5f), new Vector2(0.5f));

                if (i >= 2)
                    continue;

                var hall = SEntMan.SpawnEntity(null, coordinates);
                SEntMan.AddComponent<TownHallComponent>(hall).Configure(new TerritoryId(definition.Territories[i].Id), faction);
            }

            var otherMarker = SEntMan.SpawnEntity(null, otherMap.GridCoords);
            SEntMan.AddComponent<TerritoryComponent>(otherMarker)
                .Configure(new TerritoryId(definition.Territories[0].Id), new Vector2(-0.5f), new Vector2(0.5f));
            var otherHall = SEntMan.SpawnEntity(null, otherMap.GridCoords);
            SEntMan.AddComponent<TownHallComponent>(otherHall)
                .Configure(new TerritoryId(definition.Territories[0].Id), new FactionId("FrontlineFactionTwo"));

            var spawn = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<FactionSpawnPointComponent>(spawn).TerritoryId = definition.Territories[0].Id;
            var otherSpawn = SEntMan.SpawnEntity(null, otherMap.GridCoords);
            SEntMan.AddComponent<FactionSpawnPointComponent>(otherSpawn).TerritoryId = definition.Territories[0].Id;
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(territories.TryGetOwner(new TerritoryId(definition.Territories[0].Id), out var owner, map.MapId), Is.True);
            Assert.That(owner, Is.EqualTo(faction));
            Assert.That(territories.GetState(new TerritoryId(definition.Territories[0].Id), map.MapId), Is.EqualTo(TerritoryState.Owned));
            Assert.That(server.System<FactionSpawnSystem>().GetAvailableSpawns(faction, map.MapId),
                Is.EquivalentTo(new[] { map.GridCoords }));
        });

        var premature = true;
        await server.WaitPost(() => premature = victory.CheckForVictory(map.MapId));
        await server.WaitAssertion(() =>
        {
            Assert.That(premature, Is.False);
            Assert.That(war.State?.Status, Is.EqualTo(WarStatus.Active));
        });

        await server.WaitPost(() =>
        {
            var coordinates = new EntityCoordinates(map.Grid.Owner, map.GridCoords.Position + new Vector2(0.4f, 0));
            var hall = SEntMan.SpawnEntity(null, coordinates);
            SEntMan.AddComponent<TownHallComponent>(hall).Configure(new TerritoryId(definition.Territories[2].Id), faction);
        });
        var won = false;
        await server.WaitPost(() => won = victory.CheckForVictory(map.MapId));
        await server.WaitAssertion(() =>
        {
            Assert.That(won, Is.True);
            Assert.That(war.State?.Status, Is.EqualTo(WarStatus.Ended));
            Assert.That(war.State?.Winner, Is.EqualTo(faction));
            Assert.That(territories.CountOwned(faction, map.MapId), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task TerritoryBoundsAreRelativeToMarker()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var territorySystem = server.System<TerritorySystem>();
        var markerCoordinates = new EntityCoordinates(map.Grid.Owner, map.GridCoords.Position + new Vector2(0.5f, 0));

        await server.WaitPost(() =>
        {
            var marker = SEntMan.SpawnEntity(null, markerCoordinates);
            SEntMan.AddComponent<TerritoryComponent>(marker)
                .Configure(new TerritoryId("relative"), new Vector2(-0.1f, -0.1f), new Vector2(0.1f, 0.1f));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(territorySystem.Contains(new TerritoryId("relative"), markerCoordinates), Is.True);
            Assert.That(territorySystem.Contains(new TerritoryId("relative"), map.GridCoords), Is.False);
        });
    }

    [Test]
    public async Task TerritoryContainsAnyOfItsAreas()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var territories = server.System<TerritorySystem>();
        var territory = new TerritoryId("multi-area");
        var first = map.GridCoords;
        var second = new EntityCoordinates(map.Grid.Owner, first.Position + new Vector2(0.5f, 0));

        await server.WaitPost(() =>
        {
            var firstMarker = SEntMan.SpawnEntity(null, first);
            SEntMan.AddComponent<TerritoryComponent>(firstMarker)
                .Configure(territory, new Vector2(-0.1f), new Vector2(0.1f));
            var secondMarker = SEntMan.SpawnEntity(null, second);
            SEntMan.AddComponent<TerritoryComponent>(secondMarker)
                .Configure(territory, new Vector2(-0.1f), new Vector2(0.1f));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(territories.GetTerritories(map.MapId), Does.Contain(territory));
            Assert.That(territories.Contains(territory, first), Is.True);
            Assert.That(territories.Contains(territory, second), Is.True);
        });
    }

    [Test]
    public async Task FactionTwoTownHallCoreBelongsToFactionTwo()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        EntityUid hall = default;

        await server.WaitPost(() =>
        {
            hall = SEntMan.SpawnEntity("TownHallCoreFactionTwo", map.GridCoords);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<TownHallComponent>(hall).FactionId,
                Is.EqualTo("FrontlineFactionTwo"));
        });
    }

    [Test]
    public async Task ForceCaptureReplacesObjective()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var war = server.System<WarStateSystem>();
        var territories = server.System<TerritorySystem>();
        var halls = server.System<TownHallSystem>();
        var territory = new TerritoryId("debug-territory");
        var factionOne = new FactionId("FrontlineFactionOne");
        var factionTwo = new FactionId("FrontlineFactionTwo");

        await server.WaitPost(() =>
        {
            war.StartNewWar();
            var marker = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<TerritoryComponent>(marker)
                .Configure(territory, new Vector2(-0.5f), new Vector2(0.5f));
            var hall = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<TownHallComponent>(hall).Configure(territory, factionOne);
            var duplicate = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<TownHallRuinComponent>(duplicate).TerritoryId = territory.Id;

            Assert.That(halls.ForceCapture(territory, factionTwo, map.MapId), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(territories.TryGetOwner(territory, out var owner), Is.True);
            Assert.That(owner, Is.EqualTo(factionTwo));

            var objectives = 0;
            var halls = SEntMan.EntityQueryEnumerator<TownHallComponent>();
            while (halls.MoveNext(out _, out var hall))
            {
                if (hall.TerritoryId == territory.Id)
                    objectives++;
            }

            var ruins = SEntMan.EntityQueryEnumerator<TownHallRuinComponent>();
            while (ruins.MoveNext(out _, out var ruin))
            {
                if (ruin.TerritoryId == territory.Id)
                    objectives++;
            }

            Assert.That(objectives, Is.EqualTo(1));
        });
    }
}
