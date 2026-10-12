using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Content.Shared.War;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class FrontlineInventoryTest : GameTest
{
    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    public async Task ProductionContainersAreNativeStorage(string prototype)
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var machine = SEntMan.SpawnEntity(prototype, map.GridCoords);
            var input = prototype == "FrontlineFactory"
                ? SEntMan.GetComponent<FrontlineFactoryComponent>(machine).InputContainer
                : SEntMan.GetComponent<FrontlineRefineryComponent>(machine).InputContainer;
            var output = prototype == "FrontlineFactory"
                ? SEntMan.GetComponent<FrontlineFactoryComponent>(machine).OutputContainer
                : SEntMan.GetComponent<FrontlineRefineryComponent>(machine).OutputContainer;
            Assert.That(SEntMan.HasComponent<StorageComponent>(input.Owner), Is.True);
            Assert.That(SEntMan.HasComponent<StorageComponent>(output.Owner), Is.True);
            Assert.That(input.Owner, Is.Not.EqualTo(output.Owner), "Native storage has one grid per entity.");
        });
    }

    [Test]
    public async Task StockpileHasNativeStorage()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var hall = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            Assert.That(SEntMan.HasComponent<StorageComponent>(hall), Is.True);
        });
    }

    [Test]
    public async Task StockpileLedgerMaterializesOnceAndRestoresWithoutDuplicates()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var hall = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var stockpile = SEntMan.GetComponent<FrontlineStockpileComponent>(hall);
            var system = Server.System<Content.Server.War.FrontlineStockpileSystem>();
            stockpile.Counts["BasicMaterials"] = 65;
            stockpile.Counts["SoldierSupplies"] = 9;
            system.Materialize(hall);
            system.Materialize(hall);
            Assert.That(stockpile.Counts["BasicMaterials"], Is.Zero);
            Assert.That(stockpile.Counts["SoldierSupplies"], Is.EqualTo(9));
            var storage = SEntMan.GetComponent<StorageComponent>(hall);
            Assert.That(storage.Container.ContainedEntities.Sum(uid => SEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(uid).Count), Is.EqualTo(65));
            var counts = system.ReadCounts(hall);
            Assert.That(counts["BasicMaterials"], Is.EqualTo(65));
            system.RestoreCounts(hall, counts);
            system.Materialize(hall);
            Assert.That(storage.Container.ContainedEntities.Sum(uid => SEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(uid).Count), Is.EqualTo(65));
            Assert.That(system.TrySpendSoldierSupply(hall, () => true), Is.True);
            Assert.That(system.ReadCounts(hall)["SoldierSupplies"], Is.EqualTo(8));
        });
    }

    [TestCase(101)]
    [TestCase(10100)]
    public async Task NativeTruckCargoBecomesOneNonPickupLootBag(int damage)
    {
        var map = await Pair.CreateTestMap();
        EntityUid truck = default;
        EntityUid crate = default;
        Robust.Shared.Prototypes.ProtoId<Content.Shared.Damage.Prototypes.DamageTypePrototype> blunt = "Blunt";
        await Server.WaitAssertion(() =>
        {
            truck = SEntMan.SpawnEntity("FrontlineLogisticsTruck", map.GridCoords);
            Assert.That(SEntMan.HasComponent<StorageComponent>(truck), Is.True);
            Assert.That(SEntMan.HasComponent<Content.Shared.Containers.ItemSlots.ItemSlotsComponent>(truck), Is.False);
            crate = SEntMan.SpawnEntity("FrontlineFactoryWeaponCrate", map.GridCoords);
            Assert.That(Server.System<Content.Shared.Storage.EntitySystems.SharedStorageSystem>().Insert(truck, crate, out _, stackAutomatically: false), Is.True);
            Assert.That(Server.System<Content.Shared.Damage.Systems.DamageableSystem>().TryChangeDamage(truck,
                new Content.Shared.Damage.DamageSpecifier(Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index(blunt), damage),
                ignoreResistances: true, ignoreGlobalModifiers: true), Is.True);
        });
        await Pair.RunTicksSync(3);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(truck), Is.False);
            Assert.That(SEntMan.EntityExists(crate), Is.True);
            Assert.That(Server.System<SharedContainerSystem>().TryGetContainingContainer(crate, out var owner), Is.True);
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(owner!.Owner).EntityPrototype!.ID, Is.EqualTo("FrontlineVehicleLootBag"));
            Assert.That(SEntMan.HasComponent<Content.Shared.Item.ItemComponent>(owner.Owner), Is.False);
            Assert.That(SEntMan.GetComponent<StorageComponent>(owner.Owner).Container.ContainedEntities, Is.EqualTo(new[] { crate }));
        });
    }

    [Test]
    public async Task BackpackIsBoundUntilDeathAndEmptyGroundLootDisappears()
    {
        var map = await Pair.CreateTestMap();
        EntityUid human = default, bag = default, nested = default;
        await Server.WaitPost(() =>
        {
            var inventory = Server.System<InventorySystem>();
            human = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            SEntMan.AddComponent<FrontlinePlayerComponent>(human);
            bag = SEntMan.SpawnEntity("ClothingBackpack", map.GridCoords);
            nested = SEntMan.SpawnEntity("Crowbar", map.GridCoords);
            Assert.That(inventory.TryEquip(human, bag, "back", force: true), Is.True);
            Assert.That(Server.System<Content.Shared.Storage.EntitySystems.SharedStorageSystem>().Insert(bag, nested, out _), Is.True);
            Assert.That(inventory.TryUnequip(human, "back", silent: true), Is.False,
                "Players cannot remove or replace their bound backpack.");
            var looter = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            Assert.That(inventory.TryUnequip(looter, human, "back", silent: true), Is.False);
            Assert.That(Server.System<SharedContainerSystem>().Remove(nested, SEntMan.GetComponent<StorageComponent>(bag).Container), Is.True);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(bag), Is.True, "An empty equipped bag is never ground-loot cleanup.");
            Assert.That(Server.System<InventorySystem>().TryGetSlotEntity(human, "back", out var equipped), Is.True);
            Assert.That(equipped, Is.EqualTo(bag));
        });
        await Server.WaitPost(() =>
        {
            Assert.That(Server.System<SharedContainerSystem>().Insert(nested, SEntMan.GetComponent<StorageComponent>(bag).Container), Is.True);
            Server.System<Content.Shared.Mobs.Systems.MobStateSystem>().ChangeMobState(human, Content.Shared.Mobs.MobState.Dead);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(Server.System<InventorySystem>().TryGetSlotEntity(human, "back", out _), Is.False);
            Assert.That(Server.System<SharedContainerSystem>().TryGetContainingContainer(bag, out _), Is.False);
            Assert.That(SEntMan.HasComponent<Content.Shared.Item.ItemComponent>(bag), Is.False,
                "Ground loot opens normally but cannot be picked up or equipped.");
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities, Is.EqualTo(new[] { nested }));
        });
        await Server.WaitPost(() =>
        {
            var container = SEntMan.GetComponent<StorageComponent>(bag).Container;
            var containers = Server.System<SharedContainerSystem>();
            Assert.That(containers.Remove(nested, container), Is.True);
            Assert.That(containers.Insert(nested, container), Is.True,
                "A transfer rollback must not delete a temporarily empty bag.");
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() => Assert.That(SEntMan.EntityExists(bag), Is.True));
        await Server.WaitPost(() => Assert.That(Server.System<SharedContainerSystem>().Remove(nested,
            SEntMan.GetComponent<StorageComponent>(bag).Container), Is.True));
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(bag), Is.False);
            Assert.That(SEntMan.EntityExists(nested), Is.True);
        });
    }

    [TestCase("TownHallRuin")]
    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    [TestCase("FrontlineResourceField")]
    [TestCase("FrontlineResourceSpawnPoint")]
    public async Task StrategicRepairAndResourceAnchorsSurviveOverwhelmingDamage(string prototype)
    {
        var map = await Pair.CreateTestMap();
        EntityUid anchor = default;
        await Server.WaitPost(() =>
        {
            anchor = SEntMan.SpawnEntity(prototype, map.GridCoords);
            Robust.Shared.Prototypes.ProtoId<Content.Shared.Damage.Prototypes.DamageTypePrototype> blunt = "Blunt";
            Server.System<Content.Shared.Damage.Systems.DamageableSystem>().TryChangeDamage(anchor,
                new Content.Shared.Damage.DamageSpecifier(Server.ResolveDependency<Robust.Shared.Prototypes.IPrototypeManager>().Index(blunt), 10100),
                ignoreResistances: true, ignoreGlobalModifiers: true);
        });
        await Pair.RunTicksSync(3);
        await Server.WaitAssertion(() => Assert.That(SEntMan.EntityExists(anchor), Is.True,
            "Strategic targets must remain available for repair, capture and resource replacement."));
    }

    [Test]
    public async Task FrontlineTemplatePreservesClothingHandsBackpackAndRemovedSlotContents()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitPost(() =>
        {
            var inventory = Server.System<InventorySystem>();
            var hands = Server.System<SharedHandsSystem>();
            var containers = Server.System<SharedContainerSystem>();
            var human = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var normal = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var bag = SEntMan.SpawnEntity("ClothingBackpack", map.GridCoords);
            var uniform = SEntMan.SpawnEntity("ClothingUniformJumpsuitColorGrey", map.GridCoords);
            var tool = SEntMan.SpawnEntity("Crowbar", map.GridCoords);
            var pocket = SEntMan.SpawnEntity("Pen", map.GridCoords);
            var nested = SEntMan.SpawnEntity("Pen", map.GridCoords);
            Assert.That(inventory.TryEquip(human, uniform, "jumpsuit", force: true), Is.True);
            Assert.That(inventory.TryEquip(human, bag, "back", force: true), Is.True);
            Assert.That(inventory.TryEquip(human, pocket, "pocket1", force: true), Is.True);
            Assert.That(hands.TryPickupAnyHand(human, tool), Is.True);
            Assert.That(Server.System<Content.Shared.Storage.EntitySystems.SharedStorageSystem>().Insert(bag, nested, out _), Is.True);
            SEntMan.AddComponent<FrontlinePlayerComponent>(human);
            Assert.That(SEntMan.GetComponent<InventoryComponent>(human).TemplateId.Id, Is.EqualTo("frontline"));
            foreach (var removed in new[] { "pocket1", "pocket2", "suitstorage", "id", "belt" })
                Assert.That(inventory.HasSlot(human, removed), Is.False, removed);
            Assert.That(inventory.TryGetSlotEntity(human, "back", out var retained), Is.True);
            Assert.That(retained, Is.EqualTo(bag));
            Assert.That(inventory.TryGetSlotEntity(human, "jumpsuit", out retained), Is.True);
            Assert.That(retained, Is.EqualTo(uniform));
            Assert.That(SEntMan.GetComponent<HandsComponent>(human).Hands.Count, Is.EqualTo(2));
            Assert.That(hands.GetActiveItem(human), Is.EqualTo(tool));
            Assert.That(SEntMan.EntityExists(pocket), Is.True);
            Assert.That(containers.TryGetContainingContainer(pocket, out _), Is.False);
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.Contains(nested), Is.True);
            Assert.That(inventory.HasSlot(normal, "pocket1"), Is.True, "Other game modes keep the human template.");
        });
    }
}
