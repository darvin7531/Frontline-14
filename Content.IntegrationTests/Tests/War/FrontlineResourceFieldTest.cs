using System;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.War;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stacks;
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

        - type: entity
          id: TestFrontlineTechNode
          components:
          - type: FrontlineResourceNode
            output: Steel
            maxYield: 1
            harvestAmount: 1
            extractionTime: 0

        - type: entity
          id: TestFrontlineHarvester
          components:
          - type: DoAfter
          - type: Hands

        - type: entity
          id: TestFrontlinePickaxe
          components:
          - type: Item
          - type: Tag
            tags:
            - Pickaxe
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

    [Test]
    public async Task DeletedNodeIsReplacedAndDebitsReserve()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        _ = server.System<FrontlineResourceFieldSystem>();
        EntityUid field = default;

        await server.WaitPost(() =>
        {
            field = SEntMan.SpawnEntity(null, map.GridCoords);
            var fieldComp = SEntMan.AddComponent<FrontlineResourceFieldComponent>(field);
            fieldComp.FieldId = "replacement-field";
            fieldComp.PrimaryNodePrototype = new EntProtoId("TestFrontlineIronNode");
            fieldComp.MaxReserveNodes = 3;
            fieldComp.MaxActiveNodes = 1;

            var slot = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<FrontlineResourceSpawnPointComponent>(slot).FieldId = "replacement-field";
        });

        await Pair.RunTicksSync(1);
        EntityUid original = default;
        await server.WaitPost(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            original = fieldComp.ActiveNodes.Single();
            SEntMan.QueueDeleteEntity(original);
        });

        await Pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            Assert.That(SEntMan.EntityExists(original), Is.False);
            Assert.That(fieldComp.RemainingReserveNodes, Is.EqualTo(1));
            Assert.That(fieldComp.ActiveNodes.Count, Is.EqualTo(1));
            Assert.That(fieldComp.ActiveNodes.Single(), Is.Not.EqualTo(original));
        });
    }

    [Test]
    public async Task ExhaustedFieldReplenishesAfterDelay()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        _ = server.System<FrontlineResourceFieldSystem>();
        EntityUid field = default;

        await server.WaitPost(() =>
        {
            field = SEntMan.SpawnEntity(null, map.GridCoords);
            var fieldComp = SEntMan.AddComponent<FrontlineResourceFieldComponent>(field);
            fieldComp.FieldId = "replenishing-field";
            fieldComp.PrimaryNodePrototype = new EntProtoId("TestFrontlineIronNode");
            fieldComp.MaxReserveNodes = 2;
            fieldComp.MaxActiveNodes = 1;
            fieldComp.ReplenishmentDelay = TimeSpan.FromSeconds(1);

            var slot = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<FrontlineResourceSpawnPointComponent>(slot).FieldId = "replenishing-field";
        });

        await Pair.RunTicksSync(1);
        await server.WaitPost(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            SEntMan.DeleteEntity(fieldComp.ActiveNodes.Single());
        });

        await Pair.RunTicksSync(2);
        await server.WaitPost(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            SEntMan.DeleteEntity(fieldComp.ActiveNodes.Single());
        });

        await Pair.RunTicksSync(1);
        await server.WaitAssertion(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            Assert.That(fieldComp.RemainingReserveNodes, Is.Zero);
            Assert.That(fieldComp.ActiveNodes, Is.Empty);
        });

        await Pair.RunSeconds(1.1f);

        await server.WaitAssertion(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            Assert.That(fieldComp.ActiveNodes.Count, Is.EqualTo(1));
            Assert.That(fieldComp.RemainingReserveNodes, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ReplacementWaitsForConfiguredDelay()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        _ = server.System<FrontlineResourceFieldSystem>();
        EntityUid field = default;

        await server.WaitPost(() =>
        {
            field = SEntMan.SpawnEntity(null, map.GridCoords);
            var fieldComp = SEntMan.AddComponent<FrontlineResourceFieldComponent>(field);
            fieldComp.FieldId = "delayed-replacement-field";
            fieldComp.PrimaryNodePrototype = new EntProtoId("TestFrontlineIronNode");
            fieldComp.MaxReserveNodes = 2;
            fieldComp.MaxActiveNodes = 1;
            fieldComp.ReplacementDelay = TimeSpan.FromSeconds(1);

            var slot = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<FrontlineResourceSpawnPointComponent>(slot).FieldId = fieldComp.FieldId;
        });

        await Pair.RunTicksSync(1);
        await server.WaitPost(() =>
        {
            var fieldComp = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field);
            SEntMan.DeleteEntity(fieldComp.ActiveNodes.Single());
        });

        await Pair.RunSeconds(0.5f);
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<FrontlineResourceFieldComponent>(field).ActiveNodes, Is.Empty);
        });

        await Pair.RunSeconds(0.6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<FrontlineResourceFieldComponent>(field).ActiveNodes.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task BonusChanceOneSpawnsConfiguredNode()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        _ = server.System<FrontlineResourceFieldSystem>();
        EntityUid field = default;

        await server.WaitPost(() =>
        {
            field = SEntMan.SpawnEntity(null, map.GridCoords);
            var fieldComp = SEntMan.AddComponent<FrontlineResourceFieldComponent>(field);
            fieldComp.FieldId = "bonus-field";
            fieldComp.PrimaryNodePrototype = new EntProtoId("TestFrontlineIronNode");
            fieldComp.BonusNodePrototype = new EntProtoId("TestFrontlineTechNode");
            fieldComp.BonusNodeChance = 1f;
            fieldComp.MaxReserveNodes = 1;
            fieldComp.MaxActiveNodes = 1;

            var slot = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<FrontlineResourceSpawnPointComponent>(slot).FieldId = fieldComp.FieldId;
        });

        await Pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var node = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field).ActiveNodes.Single();
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(node).EntityPrototype?.ID,
                Is.EqualTo("TestFrontlineTechNode"));
        });
    }

    [Test]
    public async Task ExtractionProducesPhysicalStackAndConsumesYield()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineResourceFieldSystem>();
        var hands = server.System<SharedHandsSystem>();
        EntityUid field = default;
        EntityUid user = default;
        EntityUid tool = default;

        await server.WaitPost(() =>
        {
            field = SEntMan.SpawnEntity(null, map.GridCoords);
            var fieldComp = SEntMan.AddComponent<FrontlineResourceFieldComponent>(field);
            fieldComp.FieldId = "extraction-field";
            fieldComp.PrimaryNodePrototype = new EntProtoId("TestFrontlineIronNode");
            fieldComp.MaxReserveNodes = 1;
            fieldComp.MaxActiveNodes = 1;

            var slot = SEntMan.SpawnEntity(null, map.GridCoords);
            SEntMan.AddComponent<FrontlineResourceSpawnPointComponent>(slot).FieldId = "extraction-field";
            user = SEntMan.SpawnEntity("TestFrontlineHarvester", map.GridCoords);
            tool = SEntMan.SpawnEntity("TestFrontlinePickaxe", map.GridCoords);
            hands.AddHand(user, "hand", HandLocation.Left);
            Assert.That(hands.TryPickupAnyHand(user, tool), Is.True);
        });

        await Pair.RunTicksSync(1);
        await server.WaitPost(() =>
        {
            var node = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field).ActiveNodes.Single();
            Assert.That(system.TryStartExtraction(node, user, tool), Is.True);
        });

        await Pair.RunTicksSync(1);

        await server.WaitAssertion(() =>
        {
            var node = SEntMan.GetComponent<FrontlineResourceFieldComponent>(field).ActiveNodes.Single();
            Assert.That(SEntMan.GetComponent<FrontlineResourceNodeComponent>(node).RemainingYield, Is.EqualTo(4));

            var steel = SEntMan.EntityQuery<StackComponent>()
                .Single(stack => stack.StackTypeId == "Steel");
            Assert.That(steel.Count, Is.EqualTo(1));
        });
    }
}
