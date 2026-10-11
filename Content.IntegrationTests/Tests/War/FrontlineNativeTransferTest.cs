using System.Collections.Generic;
using System.Linq;
using Robust.Client.UserInterface;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineNativeTransferTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";
    public override PoolSettings PoolSettings => new() { Connected = true, Dirty = true };

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    public async Task ConnectedNativeDragMovesExactInputsAndRejectsOtherAccountsClaim(string prototype)
    {
        await SpawnTarget(prototype);
        var machine = STarget!.Value;
        EntityUid inputOwner = default;
        EntityUid outputOwner = default;
        EntityUid bag = default;
        EntityUid input = default;
        EntityUid output = default;
        await Server.WaitPost(() =>
        {
            SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(SPlayer);
            bag = SEntMan.SpawnEntity("ClothingBackpack", ToServer(PlayerCoords));
            Assert.That(Server.System<Content.Shared.Inventory.InventorySystem>().TryEquip(SPlayer, bag, "back", force: true), Is.True);
            var inputContainer = prototype == "FrontlineFactory"
                ? SEntMan.GetComponent<Content.Shared.War.FrontlineFactoryComponent>(machine).InputContainer
                : SEntMan.GetComponent<Content.Shared.War.FrontlineRefineryComponent>(machine).InputContainer;
            var outputContainer = prototype == "FrontlineFactory"
                ? SEntMan.GetComponent<Content.Shared.War.FrontlineFactoryComponent>(machine).OutputContainer
                : SEntMan.GetComponent<Content.Shared.War.FrontlineRefineryComponent>(machine).OutputContainer;
            inputOwner = inputContainer.Owner;
            outputOwner = outputContainer.Owner;
            input = SEntMan.SpawnEntity(prototype == "FrontlineFactory" ? "BasicMaterials1" : "FrontlineRawIron1", ToServer(PlayerCoords));
            output = SEntMan.SpawnEntity("BasicMaterials1", ToServer(PlayerCoords));
            Assert.That(Server.System<SharedStorageSystem>().Insert(bag, input, out _), Is.True);
            Assert.That(Server.System<Robust.Shared.Containers.SharedContainerSystem>().Insert(output, outputContainer), Is.True);
            Server.System<Content.Server.War.FrontlineProductionSystem>().CompleteClaim(output, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
            Server.System<SharedStorageSystem>().OpenStorageUI(bag, SPlayer);
        });
        await Interact();
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            var ui = Server.System<Robust.Server.GameObjects.UserInterfaceSystem>();
            var production = Server.System<Content.Server.War.FrontlineProductionSystem>();
            Assert.That(production.TryGetContainer(machine, Content.Shared.War.FrontlineFactoryComponent.InputContainerId, out _), Is.True, "Native container identity.");
            Assert.That(prototype == "FrontlineFactory"
                ? Server.System<Content.Server.War.FrontlineFactorySystem>().CanAccessNativeStorage(machine, SPlayer, input, false, true)
                : Server.System<Content.Server.War.FrontlineRefinerySystem>().CanAccessNativeStorage(machine, SPlayer, input, false, true), Is.True, "Authenticated parent access.");
            Assert.That(ui.IsUiOpen(machine, prototype == "FrontlineFactory" ? (Enum) Content.Shared.War.FrontlineFactoryUiKey.Key : Content.Shared.War.FrontlineRefineryUiKey.Key, SPlayer), Is.True, "Machine BUI opens through interaction.");
            Assert.That(ui.IsUiOpen(bag, StorageComponent.StorageUiKey.Key, SPlayer), Is.True, "Source backpack is open.");
            Assert.That(ui.IsUiOpen(inputOwner, StorageComponent.StorageUiKey.Key, SPlayer), Is.True, "Input native storage is open.");
            Assert.That(ui.IsUiOpen(outputOwner, StorageComponent.StorageUiKey.Key, SPlayer), Is.True, "Output native storage is open.");
        });
        var outputViewNet = SEntMan.GetNetEntity(outputOwner);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.System<SharedUserInterfaceSystem>().TryGetOpenUi<Content.Client.Storage.StorageBoundUserInterface>(
                CEntMan.GetEntity(outputViewNet), StorageComponent.StorageUiKey.Key, out var bui), Is.True);
            var native = bui!.Window!;
            Assert.That(Descendants(native).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>(), Is.Empty,
                "Public native view must not display another account's private goods.");
        });
        var inputNet = SEntMan.GetNetEntity(input);
        var outputNet = SEntMan.GetNetEntity(output);
        var inputOwnerNet = SEntMan.GetNetEntity(inputOwner);
        var bagNet = SEntMan.GetNetEntity(bag);
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            inputNet, inputOwnerNet, new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<StorageComponent>(inputOwner).Container.Contains(input), Is.True);
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.Contains(input), Is.False);
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            outputNet, bagNet, new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<StorageComponent>(outputOwner).Container.Contains(output), Is.True,
                "A forged native transfer must not bypass server account claims.");
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities, Is.Empty);
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
        await Server.WaitPost(() => Server.System<Content.Server.War.FrontlineProductionSystem>().CompleteClaim(
            output, ServerSession!.UserId.ToString(), DateTimeOffset.UtcNow));
        if (prototype == "FrontlineFactory")
            await SendBui(Content.Shared.War.FrontlineFactoryUiKey.Key, new Content.Shared.War.FrontlineFactoryModeMessage(true));
        else
            await SendBui(Content.Shared.War.FrontlineRefineryUiKey.Key, new Content.Shared.War.FrontlineRefineryModeMessage(true));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            outputNet, bagNet, new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<StorageComponent>(outputOwner).Container.Contains(output), Is.False);
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities, Is.EqualTo(new[] { output }));
            Assert.That(SEntMan.HasComponent<Content.Shared.War.FrontlineProductionClaimComponent>(output), Is.False,
                "Remove the private claim only after the exact native transfer commits.");
        });
    }

    [Test]
    public async Task NativePrivateOutputCannotBeSplitByAnotherAccount()
    {
        await SpawnTarget("FrontlineRefinery");
        EntityUid output = default;
        var machine = STarget!.Value;
        await Server.WaitPost(() =>
        {
            SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(SPlayer);
            output = SEntMan.SpawnEntity("BasicMaterials1", ToServer(PlayerCoords));
            var stacks = Server.System<Content.Server.Stack.StackSystem>();
            stacks.SetCount((output, null), 5);
            var container = SEntMan.GetComponent<Content.Shared.War.FrontlineRefineryComponent>(machine).OutputContainer;
            Assert.That(Server.System<Robust.Shared.Containers.SharedContainerSystem>().Insert(output, container), Is.True);
            Server.System<Content.Server.War.FrontlineProductionSystem>().CompleteClaim(output, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow);
        });
        await Interact();
        await Pair.RunUntilSynced();
        await Server.WaitPost(() => Server.System<Content.Server.Stack.StackSystem>().UserSplit(
            (output, SEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(output)), SPlayer, 2));
        await RunTicks(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(output).Count, Is.EqualTo(5));
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
            Assert.That(SEntMan.GetComponent<Content.Shared.War.FrontlineRefineryComponent>(machine).OutputContainer.Contains(output), Is.True);
        });
    }

    [Test]
    public async Task NativeTransferBlocksEconomicReentryDuringTargetInsertion()
    {
        await SpawnTarget("FrontlineFactory");
        var machine = STarget!.Value;
        EntityUid bag = default, first = default, second = default;
        await Server.WaitAssertion(() =>
        {
            SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(SPlayer);
            bag = SEntMan.SpawnEntity("FrontlineProductionStorage", ToServer(PlayerCoords));
            first = SEntMan.SpawnEntity("FrontlineFactoryWeaponCrate", ToServer(PlayerCoords));
            second = SEntMan.SpawnEntity("FrontlineFactoryWeaponCrate", ToServer(PlayerCoords));
            var output = SEntMan.GetComponent<Content.Shared.War.FrontlineFactoryComponent>(machine).OutputContainer;
            var containers = Server.System<Robust.Shared.Containers.SharedContainerSystem>();
            Assert.That(containers.Insert(first, output), Is.True);
            Assert.That(containers.Insert(second, output), Is.True);
            Server.System<SharedStorageSystem>().OpenStorageUI(bag, SPlayer);
        });
        await Interact();
        await Pair.RunUntilSynced();
        await Server.WaitPost(() =>
        {
            var probe = Server.System<NativeTransferReentryProbe>();
            probe.Target = bag;
            probe.Machine = machine;
            probe.Actor = SPlayer;
        });
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            SEntMan.GetNetEntity(first), SEntMan.GetNetEntity(bag), new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            var probe = Server.System<NativeTransferReentryProbe>();
            Assert.That(probe.Observed, Is.True);
            Assert.That(probe.NestedSucceeded, Is.False);
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities, Is.EqualTo(new[] { first }));
            Assert.That(SEntMan.GetComponent<Content.Shared.War.FrontlineFactoryComponent>(machine).OutputContainer.ContainedEntities, Is.EqualTo(new[] { second }));
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
    }

    [Test]
    public async Task NativeCargoRollbackPreservesOriginalEntityLocationAndLegacyId()
    {
        var truckNet = await Spawn("FrontlineLogisticsTruck", PlayerCoords);
        var bagNet = await Spawn("ClothingBackpack", PlayerCoords);
        var itemNet = await Spawn("BasicMaterials1", PlayerCoords);
        var truck = ToServer(truckNet);
        var bag = ToServer(bagNet);
        var item = ToServer(itemNet);
        ItemStorageLocation before = default;
        await Server.WaitAssertion(() =>
        {
            SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(SPlayer);
            var storage = Server.System<SharedStorageSystem>();
            Assert.That(storage.Insert(truck, item, out _, stackAutomatically: false), Is.True);
            // A restored snapshot can retain cargo05 while earlier identities are vacant.
            SEntMan.GetComponent<Content.Shared.War.FrontlineVehicleCargoComponent>(truck).CargoIds[item] = "cargo05";
            before = SEntMan.GetComponent<StorageComponent>(truck).StoredItems[item];
            storage.OpenStorageUI(truck, SPlayer);
            storage.OpenStorageUI(bag, SPlayer);
            var probe = Server.System<NativeTransferReentryProbe>();
            probe.Target = bag;
            probe.Actor = SPlayer;
            probe.CloseTarget = true;
        });
        await Pair.RunUntilSynced();
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            itemNet, bagNet, new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(Server.System<NativeTransferReentryProbe>().Observed, Is.True);
            var storage = SEntMan.GetComponent<StorageComponent>(truck);
            Assert.That(storage.Container.ContainedEntities, Is.EqualTo(new[] { item }));
            Assert.That(storage.StoredItems[item], Is.EqualTo(before));
            Assert.That(SEntMan.GetComponent<Content.Shared.War.FrontlineVehicleCargoComponent>(truck).CargoIds[item], Is.EqualTo("cargo05"));
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities, Is.Empty);
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
    }

    public sealed class NativeTransferReentryProbe : EntitySystem
    {
        public EntityUid Target, Machine, Actor;
        public bool Observed, NestedSucceeded, CloseTarget;
        public override void Initialize() => SubscribeLocalEvent<MetaDataComponent, Robust.Shared.Containers.EntInsertedIntoContainerMessage>(OnInserted);
        private void OnInserted(Entity<MetaDataComponent> ent, ref Robust.Shared.Containers.EntInsertedIntoContainerMessage args)
        {
            if (ent.Owner != Target || Observed) return;
            Observed = true;
            if (CloseTarget)
                EntityManager.System<SharedUserInterfaceSystem>().CloseUi(Target, StorageComponent.StorageUiKey.Key, Actor);
            else
                NestedSucceeded = EntityManager.System<Content.Server.War.FrontlineFactorySystem>().TryTakePlayerOutput(Machine, Actor);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeTransferRejectsInvalidTargetWithoutRemovingSource(bool openTarget)
    {
        var sourceNet = await Spawn("ClothingBackpack", PlayerCoords);
        var targetNet = await Spawn("ClothingBackpack", PlayerCoords);
        var itemNet = await Spawn("Pen", PlayerCoords);
        var source = ToServer(sourceNet);
        var target = ToServer(targetNet);
        var item = ToServer(itemNet);
        ItemStorageLocation before = default;
        await Server.WaitPost(() =>
        {
            SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(SPlayer);
            var storage = Server.System<SharedStorageSystem>();
            Assert.That(storage.Insert(source, item, out _), Is.True);
            before = SEntMan.GetComponent<StorageComponent>(source).StoredItems[item];
            storage.OpenStorageUI(source, SPlayer);
            if (openTarget) storage.OpenStorageUI(target, SPlayer);
        });
        await Pair.RunUntilSynced();
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            itemNet, targetNet, new ItemStorageLocation(Angle.Zero, new Vector2i(999, 999)))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            var storage = SEntMan.GetComponent<StorageComponent>(source);
            Assert.That(storage.Container.Contains(item), Is.True, "Rejected drag must not pick the source into a hand.");
            Assert.That(storage.StoredItems[item], Is.EqualTo(before));
            Assert.That(SEntMan.GetComponent<StorageComponent>(target).Container.ContainedEntities, Is.Empty);
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
    }
}
