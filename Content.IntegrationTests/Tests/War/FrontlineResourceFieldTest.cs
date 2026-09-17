using System;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.War;
using Content.Shared.War;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
[TestOf(typeof(FrontlineResourceFieldSystem))]
public sealed class FrontlineResourceFieldTest : GameTest
{
    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: TestFrontlineIronNode
          components:
          - type: FrontlineResourceNode
            output: Steel
            maxYield: 5
            harvestAmount: 1
            extractionTime: 0
        """;

    [Test]
    public async Task FieldInitializesNoMoreThanMaxActiveNodes()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        _ = server.System<FrontlineResourceFieldSystem>();
        EntityUid field = default;

        await server.WaitPost(() =>
        {
            field = SEntMan.SpawnEntity(null, map.GridCoords);
            var fieldComp = SEntMan.AddComponent<FrontlineResourceFieldComponent>(field);
            fieldComp.FieldId = "test-field";
            fieldComp.PrimaryNodePrototype = new EntProtoId("TestFrontlineIronNode");
            fieldComp.MaxReserveNodes = 5;
            fieldComp.MaxActiveNodes = 2;
            fieldComp.ReplacementDelay = TimeSpan.Zero;
            fieldComp.ReplenishmentDelay = TimeSpan.FromSeconds(1);

            for (var i = 0; i < 3; i++)
            {
                var slot = SEntMan.SpawnEntity(null, map.GridCoords);
                SEntMan.AddComponent<FrontlineResourceSpawnPointComponent>(slot).FieldId = "test-field";
            }
        });

        await Pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            Assert.That(fieldComp.RemainingReserveNodes, Is.EqualTo(3));
            Assert.That(fieldComp.ActiveNodes.Count, Is.EqualTo(2));

            var nodes = 0;
            var query = SEntMan.EntityQueryEnumerator<FrontlineResourceNodeComponent>();
            while (query.MoveNext(out _, out var node))
            {
                if (node.Field == field)
                    nodes++;
            }

            Assert.That(nodes, Is.EqualTo(2));
        });
    }
}
