using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.War;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.War;

[RegisterComponent]
public sealed partial class OreMergeProbeComponent : Component;

[TestFixture]
public sealed class FrontlineOreInventoryTest : GameTest
{
    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: TestFrontlineInventoryNode
          parent: FrontlineIronResourceNode
          components:
          - type: FrontlineResourceNode
            maxYield: 10
            harvestAmount: 5
            extractionTime: 0
            bonusDrops:
            - output: RawTechnologyMaterial
              chance: 1
              minAmount: 2
              maxAmount: 2
        """;

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task NativeMergeConservesHeldOrStoredOreAndRejectsReentrantCountChanges(bool stored, bool restoreCount)
    {
        var map = await Pair.CreateTestMap();
        var hands = Server.System<SharedHandsSystem>();
        var inventory = Server.System<InventorySystem>();
        var storage = Server.System<Content.Shared.Storage.EntitySystems.SharedStorageSystem>();
        var stacks = Server.System<Content.Server.Stack.StackSystem>();
        var probe = Server.System<OreMergeProbe>();
        EntityUid ore = default;
        EntityUid node = default;
        await Server.WaitPost(() =>
        {
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            SEntMan.AddComponent<FrontlinePlayerComponent>(user);
            var pickaxe = SEntMan.SpawnEntity("Pickaxe", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, pickaxe), Is.True);
            var bag = SEntMan.SpawnEntity("ClothingBackpack", map.GridCoords);
            Assert.That(inventory.TryEquip(user, bag, "back", silent: true, force: true), Is.True);
            ore = stacks.SpawnAtPosition(10, new Robust.Shared.Prototypes.ProtoId<StackPrototype>("FrontlineRawIron"), map.GridCoords);
            if (stored)
                Assert.That(storage.Insert(bag, ore, out _), Is.True);
            else
                Assert.That(hands.TryPickupAnyHand(user, ore), Is.True);
            SEntMan.AddComponent<OreMergeProbeComponent>(ore);
            probe.Fired = false;
            probe.Enabled = restoreCount;
            node = SEntMan.SpawnEntity("TestFrontlineInventoryNode", map.GridCoords);
            Assert.That(Server.System<SharedInteractionSystem>().InteractDoAfter(user, pickaxe, node, map.GridCoords, true), Is.True);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(probe.Fired, Is.EqualTo(restoreCount));
            Assert.That(SEntMan.EntityExists(ore), Is.True);
            Assert.That(SEntMan.GetComponent<StackComponent>(ore).Count, Is.EqualTo(restoreCount ? 10 : 15));
            Assert.That(SEntMan.EntityQuery<StackComponent>().Count(s => s.StackTypeId == "FrontlineRawIron"), Is.EqualTo(1));
            Assert.That(SEntMan.EntityQuery<StackComponent>().Where(s => s.StackTypeId == "RawTechnologyMaterial").Sum(s => s.Count), Is.EqualTo(restoreCount ? 0 : 2));
            Assert.That(SEntMan.GetComponent<FrontlineResourceNodeComponent>(node).RemainingYield, Is.EqualTo(restoreCount ? 10 : 5));
        });
    }

    public sealed class OreMergeProbe : EntitySystem
    {
        public bool Enabled;
        public bool Fired;
        public override void Initialize() => SubscribeLocalEvent<OreMergeProbeComponent, StackCountChangedEvent>(OnCount);
        private void OnCount(Entity<OreMergeProbeComponent> ent, ref StackCountChangedEvent args)
        {
            if (!Enabled || Fired)
                return;
            Fired = true;
            EntityManager.System<Content.Server.Stack.StackSystem>().SetCount((ent.Owner, null), 10);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PrimaryAndBonusAreDeliveredTogetherOrYieldIsUnchanged(bool hasBackpack)
    {
        var map = await Pair.CreateTestMap();
        var hands = Server.System<SharedHandsSystem>();
        var inventory = Server.System<InventorySystem>();
        var interaction = Server.System<SharedInteractionSystem>();
        EntityUid user = default;
        EntityUid node = default;
        EntityUid bag = default;
        await Server.WaitPost(() =>
        {
            user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            SEntMan.AddComponent<FrontlinePlayerComponent>(user);
            node = SEntMan.SpawnEntity("TestFrontlineInventoryNode", map.GridCoords);
            var pick = SEntMan.SpawnEntity("Pickaxe", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, pick), Is.True);
            if (hasBackpack)
            {
                bag = SEntMan.SpawnEntity("ClothingBackpack", map.GridCoords);
                Assert.That(inventory.TryEquip(user, bag, "back", silent: true, force: true), Is.True);
            }
            Assert.That(interaction.InteractDoAfter(user, pick, node, map.GridCoords, true), Is.True);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<FrontlineResourceNodeComponent>(node).RemainingYield,
                Is.EqualTo(hasBackpack ? 5 : 10));
            var output = SEntMan.EntityQuery<StackComponent>()
                .Where(s => s.StackTypeId == "FrontlineRawIron" || s.StackTypeId == "RawTechnologyMaterial").ToArray();
            if (!hasBackpack)
            {
                Assert.That(output, Is.Empty, "No partial delivery or loose unpaid ore when the bonus cannot fit.");
                return;
            }
            Assert.That(output.Single(s => s.StackTypeId == "FrontlineRawIron").Count, Is.EqualTo(5));
            Assert.That(output.Single(s => s.StackTypeId == "RawTechnologyMaterial").Count, Is.EqualTo(2));
            var owned = inventory.GetHandOrInventoryEntities(user).ToHashSet();
            owned.UnionWith(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities);
            Assert.That(output.All(s => owned.Contains(s.Owner)), Is.True);
        });
    }
}
