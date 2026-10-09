using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Fluids.Components;
using Content.Shared.Gibbing;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.War;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class FrontlinePlayerCleanupTest : GameTest
{
    [Test]
    public async Task BleedingStillLosesBloodWithoutPuddles()
    {
        var map = await Pair.CreateTestMap();
        var blood = Server.System<BloodstreamSystem>();
        await Server.WaitPost(() =>
        {
            var player = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            SEntMan.AddComponent<FrontlinePlayerComponent>(player);
            var before = blood.GetBloodLevel(player);
            Assert.That(blood.TryBleedOut(player, 40), Is.True);
            Assert.That(blood.GetBloodLevel(player), Is.LessThan(before));
            Assert.That(SEntMan.EntityQuery<PuddleComponent>(), Is.Empty);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task GibPreservesExactEquipmentWithoutOrgans(bool dropGiblets)
    {
        var map = await Pair.CreateTestMap();
        var gib = Server.System<GibbingSystem>();
        var inventory = Server.System<InventorySystem>();
        var hands = Server.System<SharedHandsSystem>();
        EntityUid player = default;
        EntityUid bag = default;
        EntityUid tool = default;
        EntityUid nested = default;
        await Server.WaitPost(() =>
        {
            player = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            SEntMan.AddComponent<FrontlinePlayerComponent>(player);
            bag = SEntMan.SpawnEntity("ClothingBackpack", map.GridCoords);
            tool = SEntMan.SpawnEntity("Pickaxe", map.GridCoords);
            nested = SEntMan.SpawnEntity("Crowbar", map.GridCoords);
            Assert.That(inventory.TryEquip(player, bag, "back", silent: true, force: true), Is.True);
            Assert.That(hands.TryPickupAnyHand(player, tool), Is.True);
            Assert.That(Server.System<Content.Shared.Storage.EntitySystems.SharedStorageSystem>()
                .Insert(bag, nested, out _), Is.True);
            var giblets = gib.Gib(player, dropGiblets);
            Assert.That(giblets, Does.Contain(bag));
            Assert.That(giblets, Does.Contain(tool));
            Assert.That(giblets.Any(SEntMan.HasComponent<OrganComponent>), Is.False);
            Assert.That(SEntMan.EntityQuery<PuddleComponent>(), Is.Empty);
        });
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(player), Is.False);
            Assert.That(SEntMan.EntityExists(bag) && SEntMan.EntityExists(tool) && SEntMan.EntityExists(nested), Is.True);
            Assert.That(SEntMan.EntityQuery<OrganComponent>(), Is.Empty);
        });
    }
}
