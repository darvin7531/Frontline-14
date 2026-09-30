#nullable enable
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.War;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Robust.Shared.Maths;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.War;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Item;
using Content.Shared.Interaction.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[RegisterComponent]
public sealed partial class StockpileSpawnProbeComponent : Component;

[TestFixture]
public sealed class FrontlineStockpileTest : GameTest
{
    [TestPrototypes]
    private const string Prototypes = """
        - type: entity
          id: TestStockpileUnlimitedMaterials
          parent: BasicMaterials1
          components:
          - type: Stack
            stackType: BasicMaterials
            count: 1
            unlimited: true

        - type: frontlineSupplyProduct
          id: TestStockpileSpawnProduct
          name: frontline-supply-product-mk58
          entity: TestStockpileSpawnOutput

        - type: entity
          id: TestStockpileSpawnOutput
          parent: FrontlineWeaponPistolMk58
          components:
          - type: StockpileSpawnProbe
        """;

    [TestCase("Steel", false)]
    [TestCase("FrontlineRawIron", false)]
    [TestCase("TestStockpileUnlimitedMaterials", true)]
    public async Task OnlyFiniteExactMaterialsAreAccepted(string input, bool entity)
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var held = entity ? SEntMan.SpawnEntity(input, map.GridCoords)
                : Server.System<StackSystem>().SpawnAtPosition(5, input, map.GridCoords);
            Assert.That(Server.System<SharedHandsSystem>().TryPickupAnyHand(actor, held), Is.True);
            var before = SComp<StackComponent>(held).Count;
            Assert.That(Server.System<FrontlineStockpileSystem>().TrySubmitHeld(core, actor), Is.False);
            Assert.That(SComp<StackComponent>(held).Count, Is.EqualTo(before));
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(actor), Is.EqualTo(held));
            Assert.That(SComp<FrontlineStockpileComponent>(core).Counts, Is.Empty);
        });
    }

    [Test]
    public async Task FundedWithdrawalRejectsRemoteNegativeAndDeadEntities()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2i(10, 0)));
            var system = Server.System<FrontlineStockpileSystem>();
            var stockpile = SComp<FrontlineStockpileComponent>(core);
            stockpile.Counts["FrontlineWeaponPistolMk58"] = 5;
            Assert.That(system.TryWithdrawPlayerProduct(core, actor, "FrontlineWeaponPistolMk58"), Is.False);
            Assert.That(stockpile.Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(5));
            Assert.That(CountPrototype("FrontlineWeaponPistolMk58"), Is.Zero);
            Server.System<SharedTransformSystem>().SetCoordinates(actor, map.GridCoords);
            stockpile.Counts["FrontlineWeaponPistolMk58"] = -1;
            Assert.That(system.TryWithdrawPlayerProduct(core, actor, "FrontlineWeaponPistolMk58"), Is.False);
            Assert.That(stockpile.Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(-1));
            stockpile.Counts["FrontlineWeaponPistolMk58"] = 5;
            SEntMan.DeleteEntity(actor);
            Assert.That(system.TrySubmitHeld(core, actor), Is.False);
            Assert.That(system.TryWithdrawPlayerProduct(core, actor, "FrontlineWeaponPistolMk58"), Is.False);
            SEntMan.DeleteEntity(core);
            Assert.That(system.TryWithdrawPlayerProduct(core, actor, "FrontlineWeaponPistolMk58"), Is.False);
            Assert.That(stockpile.Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(5));
        });
    }

    [TestCase("TownHallCoreFactionOne")]
    [TestCase("TownHallCoreFactionTwo")]
    public async Task HeldCrateCreditsFiveWithoutGoodsUntilOneWithdrawal(string coreId)
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineStockpileSystem>();
        var hands = server.System<SharedHandsSystem>();
        await server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity(coreId, map.GridCoords);
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var crate = SEntMan.SpawnEntity("FrontlineWeaponCrate", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, crate), Is.True);
            Assert.That(system.TrySubmitHeld(core, user), Is.True);
            Assert.That(SEntMan.EntityExists(crate), Is.False);
            Assert.That(SComp<FrontlineStockpileComponent>(core).Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(5));
            Assert.That(CountPrototype("FrontlineWeaponPistolMk58"), Is.Zero);
            Assert.That(system.TrySubmitHeld(core, user), Is.False);
            Assert.That(system.TryWithdrawPlayerProduct(core, user, "FrontlineWeaponPistolMk58"), Is.True);
            Assert.That(SComp<FrontlineStockpileComponent>(core).Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(4));
            Assert.That(CountPrototype("FrontlineWeaponPistolMk58"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task MaterialsAggregate240AndWithdrawAtMostThirtyWithConservation()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineStockpileSystem>();
        var stacks = server.System<StackSystem>();
        var hands = server.System<SharedHandsSystem>();
        await server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            for (var i = 0; i < 8; i++)
            {
                var input = stacks.SpawnAtPosition(30, "BasicMaterials", map.GridCoords);
                Assert.That(hands.TryPickupAnyHand(user, input), Is.True);
                Assert.That(system.TrySubmitHeld(core, user), Is.True);
                Assert.That(SEntMan.EntityExists(input), Is.False);
            }
            var stockpile = SComp<FrontlineStockpileComponent>(core);
            Assert.That(stockpile.Counts["BasicMaterials"], Is.EqualTo(240));
            Assert.That(CountPrototype("BasicMaterials1"), Is.Zero);
            Assert.That(system.TryWithdrawPlayerProduct(core, user, "BasicMaterials"), Is.True);
            Assert.That(stockpile.Counts["BasicMaterials"], Is.EqualTo(210));
            Assert.That(SEntMan.EntityQuery<StackComponent>().Where(s => s.StackTypeId.Id == "BasicMaterials")
                .Sum(s => s.Count), Is.EqualTo(30));
            for (var i = 0; i < 7; i++)
                Assert.That(system.TryWithdrawPlayerProduct(core, user, "BasicMaterials"), Is.True);
            Assert.That(stockpile.Counts["BasicMaterials"], Is.Zero);
            Assert.That(system.TryWithdrawPlayerProduct(core, user, "BasicMaterials"), Is.False);
            var output = SEntMan.EntityQuery<StackComponent>().Where(s => s.StackTypeId.Id == "BasicMaterials").ToArray();
            Assert.That(output.Sum(s => s.Count), Is.EqualTo(240));
            Assert.That(output.All(s => s.Count <= 30), Is.True);
        });
    }

    [Test]
    public async Task InvalidCratesRangeOverflowAndLogicalSuppliesPreserveAccounting()
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var system = server.System<FrontlineStockpileSystem>();
        var hands = server.System<SharedHandsSystem>();
        await server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionTwo", map.GridCoords);
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var crate = SEntMan.SpawnEntity("FrontlineSupplyCrate", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(user, crate), Is.True);
            var metadata = SComp<FrontlineSupplyCrateComponent>(crate);
            var stockpile = SComp<FrontlineStockpileComponent>(core);
            metadata.Product = "UnknownStockpileProduct";
            Assert.That(system.TrySubmitHeld(core, user), Is.False);
            metadata.Product = "SoldierSupplies";
            foreach (var amount in new[] { -1, 0 })
            {
                metadata.Amount = amount;
                Assert.That(system.TrySubmitHeld(core, user), Is.False);
            }
            metadata.Amount = 5;
            stockpile.Counts[metadata.Product] = int.MaxValue - 4;
            Assert.That(system.TrySubmitHeld(core, user), Is.False);
            Assert.That(stockpile.Counts[metadata.Product], Is.EqualTo(int.MaxValue - 4));
            stockpile.Counts.Clear();
            server.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords.Offset(new Vector2i(10, 0)));
            Assert.That(system.TrySubmitHeld(core, user), Is.False);
            Assert.That(system.TryWithdrawPlayerProduct(core, user, "SoldierSupplies"), Is.False);
            Assert.That(stockpile.Counts, Is.Empty);
            Assert.That(hands.GetActiveItem(user), Is.EqualTo(crate));
            server.System<SharedTransformSystem>().SetCoordinates(user, map.GridCoords);
            Assert.That(system.TrySubmitHeld(core, user), Is.True);
            Assert.That(stockpile.Counts["SoldierSupplies"], Is.EqualTo(5));
            Assert.That(system.TryWithdrawPlayerProduct(core, user, "SoldierSupplies"), Is.False);
            Assert.That(system.TryWithdrawPlayerProduct(core, user, "UnknownStockpileProduct"), Is.False);
            Assert.That(stockpile.Counts["SoldierSupplies"], Is.EqualTo(5));
        });
    }

    [Test]
    public async Task SnapshotIsDetachedAndRuinsHaveNoStockpile()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionTwo", map.GridCoords);
            var ruin = SEntMan.SpawnEntity("TownHallRuin", map.GridCoords);
            Assert.That(SEntMan.HasComponent<FrontlineStockpileComponent>(ruin), Is.False);
            var counts = SComp<FrontlineStockpileComponent>(core).Counts;
            counts["SoldierSupplies"] = 7;
            var state = Server.System<FrontlineStockpileSystem>().BuildUiState(core);
            var supplies = state.Products.Single(p => p.Product.Id == "SoldierSupplies");
            Assert.That(supplies.Amount, Is.EqualTo(7));
            Assert.That(supplies.CanWithdraw, Is.False);
            state.Products[0] = supplies;
            counts["SoldierSupplies"] = 8;
            Assert.That(supplies.Amount, Is.EqualTo(7));
            Assert.That(counts["SoldierSupplies"], Is.EqualTo(8));
        });
    }

    [Test]
    public async Task ChangedActiveHandDuringDropPermissionCannotConsumeUnheldCrate()
    {
        var map = await Pair.CreateTestMap();
        var callbacks = Server.System<StockpileCallbacks>();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var crate = SEntMan.SpawnEntity("FrontlineWeaponCrate", map.GridCoords);
            var hands = Server.System<SharedHandsSystem>();
            Assert.That(hands.TryPickupAnyHand(actor, crate), Is.True);
            callbacks.Actor = actor;
            callbacks.Mode = "drop-hand";
            Assert.That(Server.System<FrontlineStockpileSystem>().TrySubmitHeld(core, actor), Is.False);
            Assert.That(callbacks.Fired, Is.True);
            Assert.That(SEntMan.EntityExists(crate), Is.True);
            Assert.That(SComp<FrontlineStockpileComponent>(core).Counts, Is.Empty);
        });
    }

    [Test]
    public async Task PickupAndDropPermissionsAreRealAndPreserveRejectedInput()
    {
        var map = await Pair.CreateTestMap();
        var callbacks = Server.System<StockpileCallbacks>();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var crate = SEntMan.SpawnEntity("FrontlineWeaponCrate", map.GridCoords);
            var hands = Server.System<SharedHandsSystem>();
            callbacks.Actor = actor;
            callbacks.Mode = "deny-pickup";
            Assert.That(hands.TryPickupAnyHand(actor, crate), Is.False);
            Assert.That(callbacks.Fired, Is.True);
            Assert.That(hands.TryPickupAnyHand(actor, crate), Is.True);
            callbacks.Mode = "deny-drop";
            Assert.That(Server.System<FrontlineStockpileSystem>().TrySubmitHeld(core, actor), Is.False);
            Assert.That(hands.GetActiveItem(actor), Is.EqualTo(crate));
            Assert.That(SComp<FrontlineStockpileComponent>(core).Counts, Is.Empty);
            Assert.That(Server.System<FrontlineStockpileSystem>().TrySubmitHeld(core, actor), Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CrateTerminationCannotReenterOrMutateDestroyedCore(bool destroyCore)
    {
        var map = await Pair.CreateTestMap();
        var callbacks = Server.System<StockpileCallbacks>();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var crate = SEntMan.SpawnEntity("FrontlineWeaponCrate", map.GridCoords);
            Assert.That(Server.System<SharedHandsSystem>().TryPickupAnyHand(actor, crate), Is.True);
            callbacks.Actor = actor;
            callbacks.Core = core;
            callbacks.Input = crate;
            callbacks.Mode = destroyCore ? "deposit-destroy" : "deposit-reenter";
            var stockpile = SComp<FrontlineStockpileComponent>(core);
            Assert.That(Server.System<FrontlineStockpileSystem>().TrySubmitHeld(core, actor), Is.True);
            Assert.That(callbacks.Fired, Is.True);
            Assert.That(callbacks.NestedAccepted, Is.False);
            Assert.That(SEntMan.EntityExists(crate), Is.False);
            Assert.That(stockpile.Counts["FrontlineWeaponPistolMk58"], Is.EqualTo(5));
            Assert.That(CountPrototype("FrontlineWeaponPistolMk58"), Is.Zero);
            Assert.That(SEntMan.EntityExists(core), Is.EqualTo(!destroyCore));
        });
    }

    [TestCase("withdraw-reenter", true, 0)]
    [TestCase("withdraw-delete-output", false, 10)]
    [TestCase("withdraw-destroy-core", false, 0)]
    public async Task SpawnCallbacksReserveOnceAndRollbackOnlySurvivingCore(string mode, bool accepted, int balance)
    {
        var map = await Pair.CreateTestMap();
        var callbacks = Server.System<StockpileCallbacks>();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var stockpile = SComp<FrontlineStockpileComponent>(core);
            stockpile.Counts["BasicMaterials"] = 10;
            callbacks.Core = core;
            callbacks.Actor = actor;
            callbacks.Mode = mode;
            Assert.That(Server.System<FrontlineStockpileSystem>().TryWithdrawPlayerProduct(core, actor, "BasicMaterials"), Is.EqualTo(accepted));
            Assert.That(callbacks.Fired, Is.True);
            Assert.That(callbacks.NestedAccepted, Is.False);
            Assert.That(stockpile.Counts["BasicMaterials"], Is.EqualTo(balance));
            Assert.That(SEntMan.EntityQuery<StackComponent>().Where(s => s.StackTypeId.Id == "BasicMaterials")
                .Sum(s => s.Count), Is.EqualTo(accepted ? 10 : 0));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ActualSpawnStartupCannotReenterAndDestroyedCoreDoesNotRollbackStaleComponent(bool destroyCore)
    {
        var map = await Pair.CreateTestMap();
        var callbacks = Server.System<StockpileCallbacks>();
        await Server.WaitPost(() =>
        {
            var core = SEntMan.SpawnEntity("TownHallCoreFactionOne", map.GridCoords);
            var actor = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            var stockpile = SComp<FrontlineStockpileComponent>(core);
            stockpile.Counts["TestStockpileSpawnProduct"] = 5;
            callbacks.Core = core;
            callbacks.Actor = actor;
            callbacks.Mode = destroyCore ? "spawn-destroy-core" : "spawn-reenter";
            Assert.That(Server.System<FrontlineStockpileSystem>().TryWithdrawPlayerProduct(core, actor, "TestStockpileSpawnProduct"), Is.EqualTo(!destroyCore));
            Assert.That(callbacks.Fired, Is.True);
            Assert.That(callbacks.NestedAccepted, Is.False);
            Assert.That(stockpile.Counts["TestStockpileSpawnProduct"], Is.EqualTo(4));
            Assert.That(CountPrototype("TestStockpileSpawnOutput"), Is.EqualTo(destroyCore ? 0 : 1));
        });
    }

    public sealed class StockpileCallbacks : EntitySystem
    {
        public EntityUid Core;
        public EntityUid Actor;
        public EntityUid Input;
        public string Mode = "";
        public bool Fired;
        public bool NestedAccepted;

        public override void Initialize()
        {
            SubscribeLocalEvent<HandsComponent, DropAttemptEvent>(OnDrop);
            SubscribeLocalEvent<HandsComponent, PickupAttemptEvent>(OnPickup);
            SubscribeLocalEvent<FrontlineSupplyCrateComponent, EntityTerminatingEvent>(OnCrateTerminating);
            SubscribeLocalEvent<ItemComponent, StackCountChangedEvent>(OnOutputCountChanged);
            SubscribeLocalEvent<StockpileSpawnProbeComponent, ComponentStartup>(OnSpawn);
        }

        private void OnPickup(Entity<HandsComponent> ent, ref PickupAttemptEvent args)
        {
            if (ent.Owner != Actor || Mode != "deny-pickup")
                return;
            Fired = true;
            Mode = "";
            args.Cancel();
        }

        private void OnDrop(Entity<HandsComponent> ent, ref DropAttemptEvent args)
        {
            if (ent.Owner != Actor || (Mode != "deny-drop" && Mode != "drop-hand"))
                return;
            Fired = true;
            var mode = Mode;
            Mode = "";
            if (mode == "deny-drop")
                args.Cancel();
            else
                EntityManager.System<SharedHandsSystem>().TryDrop(Actor, checkActionBlocker: false);
        }

        private void OnCrateTerminating(Entity<FrontlineSupplyCrateComponent> ent, ref EntityTerminatingEvent args)
        {
            if (ent.Owner != Input || !Mode.StartsWith("deposit-"))
                return;
            Fired = true;
            var mode = Mode;
            Mode = "";
            NestedAccepted = EntityManager.System<FrontlineStockpileSystem>().TrySubmitHeld(Core, Actor);
            if (mode == "deposit-destroy")
                Del(Core);
        }

        private void OnSpawn(Entity<StockpileSpawnProbeComponent> ent, ref ComponentStartup args)
        {
            if (!Mode.StartsWith("spawn-"))
                return;
            Fired = true;
            var mode = Mode;
            Mode = "";
            NestedAccepted = EntityManager.System<FrontlineStockpileSystem>().TryWithdrawPlayerProduct(Core, Actor, "TestStockpileSpawnProduct");
            if (mode == "spawn-destroy-core")
                Del(Core);
        }

        private void OnOutputCountChanged(Entity<ItemComponent> ent, ref StackCountChangedEvent args)
        {
            if (MetaData(ent).EntityPrototype?.ID != "BasicMaterials1" || !Mode.StartsWith("withdraw-"))
                return;
            Fired = true;
            var mode = Mode;
            Mode = "";
            NestedAccepted = EntityManager.System<FrontlineStockpileSystem>().TryWithdrawPlayerProduct(Core, Actor, "BasicMaterials");
            if (mode == "withdraw-delete-output")
                Del(ent);
            if (mode == "withdraw-destroy-core")
                Del(Core);
        }
    }

    private int CountPrototype(string id) => SEntMan.EntityQuery<MetaDataComponent>()
        .Count(metadata => metadata.EntityPrototype?.ID == id);

    [TestCase("TownHallCoreFactionOne")]
    [TestCase("TownHallCoreFactionTwo")]
    public void LiveCoreHasStockpile(string id)
    {
        EntProtoId prototype = id;
        Assert.That(Pair.Server.ResolveDependency<IPrototypeManager>().Index(prototype)
            .Components.ContainsKey("FrontlineStockpile"), Is.True);
    }
}
