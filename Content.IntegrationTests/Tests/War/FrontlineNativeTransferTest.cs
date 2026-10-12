#nullable enable
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
            Server.ResolveDependency<Content.Server.Administration.Managers.IAdminManager>().DeAdmin(ServerSession!);
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
        await SelectProductionTab(2);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.System<SharedUserInterfaceSystem>().TryGetOpenUi<Content.Client.Storage.StorageBoundUserInterface>(
                CEntMan.GetEntity(outputViewNet), StorageComponent.StorageUiKey.Key, out var bui), Is.True);
            var native = bui!.Window!;
            Assert.That(Descendants(native).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>(), Is.Empty,
                "Public native view must not display another account's private goods.");
            FrontlineProductionUiTest.AssertNativeGridRenderable(native);
        });
        var inputNet = SEntMan.GetNetEntity(input);
        var outputNet = SEntMan.GetNetEntity(output);
        var inputOwnerNet = SEntMan.GetNetEntity(inputOwner);
        var bagNet = SEntMan.GetNetEntity(bag);
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            inputNet, inputOwnerNet, new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await SelectProductionTab(0);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<StorageComponent>(inputOwner).Container.Contains(input), Is.True);
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.Contains(input), Is.False);
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
        await Client.WaitAssertion(() =>
        {
            var ui = CEntMan.System<SharedUserInterfaceSystem>();
            Assert.That(ui.TryGetOpenUi<Content.Client.Storage.StorageBoundUserInterface>(
                CEntMan.GetEntity(inputOwnerNet), StorageComponent.StorageUiKey.Key, out var bui), Is.True);
            var native = bui!.Window!;
            Assert.That(native.VisibleInTree, Is.True);
            Assert.That(Descendants(native).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>()
                .Select(piece => CEntMan.GetNetEntity(piece.Entity)), Does.Contain(inputNet),
                "Transferred ore must appear in the real connected input grid.");
            FrontlineProductionUiTest.AssertNativeGridRenderable(native);
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
        await SelectProductionTab(2);
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.System<SharedUserInterfaceSystem>().TryGetOpenUi<Content.Client.Storage.StorageBoundUserInterface>(
                CEntMan.GetEntity(outputViewNet), StorageComponent.StorageUiKey.Key, out var bui), Is.True);
            var native = bui!.Window!;
            Assert.That(native.VisibleInTree, Is.True);
            Assert.That(Descendants(native).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>()
                .Select(piece => CEntMan.GetNetEntity(piece.Entity)), Does.Contain(outputNet),
                "The owner's completed material must appear in the real connected output grid.");
            FrontlineProductionUiTest.AssertNativeGridRenderable(native);
        });
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

    private async Task SelectProductionTab(int index)
    {
        await Client.WaitPost(() =>
        {
            var window = Client.ResolveDependency<IUserInterfaceManager>().WindowRoot.Children
                .First(w => w is Content.Client.War.FrontlineFactoryWindow or Content.Client.War.FrontlineRefineryWindow);
            Descendants(window).OfType<Robust.Client.UserInterface.Controls.TabContainer>().Single().CurrentTab = index;
        });
        await RunTicks(3);
    }

    [Test]
    public async Task ConnectedHumanDepositsAndWithdrawsTownHallMaterialsThroughNativeStorage()
    {
        await SpawnTarget("TownHallCoreFactionOne");
        var core = STarget!.Value;
        EntityUid bag = default, materials = default;
        await Server.WaitPost(() =>
        {
            Server.ResolveDependency<Content.Server.Administration.Managers.IAdminManager>().DeAdmin(ServerSession!);
            SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(SPlayer);
            bag = SEntMan.SpawnEntity("ClothingBackpack", ToServer(PlayerCoords));
            Assert.That(Server.System<Content.Shared.Inventory.InventorySystem>().TryEquip(SPlayer, bag, "back", force: true), Is.True);
            materials = SEntMan.SpawnEntity("BasicMaterials1", ToServer(PlayerCoords));
            Server.System<Content.Server.Stack.StackSystem>().SetCount((materials, null), 10);
            Assert.That(Server.System<SharedStorageSystem>().Insert(bag, materials, out _), Is.True);
            Server.System<SharedStorageSystem>().OpenStorageUI(bag, SPlayer);
        });
        await Interact();
        await Pair.RunUntilSynced();
        await AssertGrid();
        var coreNet = Target!.Value;
        var bagNet = SEntMan.GetNetEntity(bag);
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            SEntMan.GetNetEntity(materials), coreNet, new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities, Is.Empty);
            Assert.That(Server.System<Content.Server.War.FrontlineStockpileSystem>().ReadCounts(core)["BasicMaterials"], Is.EqualTo(10));
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
        await CloseBui(Content.Shared.War.FrontlineStockpileUiKey.Key);
        await Interact();
        await Pair.RunUntilSynced();
        await AssertGrid();
        NetEntity output = default;
        await Client.WaitAssertion(() =>
        {
            var window = GetWindow<Content.Client.War.FrontlineStockpileWindow>();
            var piece = Descendants(window).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>().Single();
            Assert.That(CEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(piece.Entity).Count, Is.EqualTo(10));
            output = CEntMan.GetNetEntity(piece.Entity);
        });
        await Client.WaitPost(() => CEntMan.RaisePredictiveEvent(new StorageTransferItemEvent(
            output, bagNet, new ItemStorageLocation(Angle.Zero, Vector2i.Zero))));
        await RunTicks(5);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            var item = SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities.Single();
            Assert.That(SEntMan.GetNetEntity(item), Is.EqualTo(output));
            Assert.That(SEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(item).Count, Is.EqualTo(10));
            Assert.That(Server.System<Content.Server.War.FrontlineStockpileSystem>().ReadCounts(core).GetValueOrDefault("BasicMaterials"), Is.Zero);
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
        });
        await AssertGrid();
        await CloseBui(Content.Shared.War.FrontlineStockpileUiKey.Key);

        async Task AssertGrid() => await Client.WaitAssertion(() =>
        {
            var window = GetWindow<Content.Client.War.FrontlineStockpileWindow>();
            var native = Descendants(window).OfType<Content.Client.UserInterface.Systems.Storage.Controls.StorageWindow>().Single();
            Assert.That(native.VisibleInTree, Is.True);
            FrontlineProductionUiTest.AssertNativeGridRenderable(native);
        });
    }

    [Test]
    public async Task ConnectedPlayerLootsOriginalDeathBackpackAndItVanishesAfterLastNativeWithdrawal()
    {
        await SpawnTarget("ClothingBackpack");
        var bag = STarget!.Value;
        EntityUid contents = default;
        await Server.WaitPost(() =>
        {
            var corpse = SEntMan.SpawnEntity("MobHuman", ToServer(PlayerCoords));
            SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(corpse);
            Assert.That(Server.System<Content.Shared.Inventory.InventorySystem>().TryEquip(corpse, bag, "back", force: true), Is.True);
            contents = SEntMan.SpawnEntity("Crowbar", ToServer(PlayerCoords));
            Assert.That(Server.System<SharedStorageSystem>().Insert(bag, contents, out _), Is.True);
            Server.System<Content.Shared.Mobs.Systems.MobStateSystem>().ChangeMobState(corpse, Content.Shared.Mobs.MobState.Dead);
            Assert.That(Server.System<SharedHandsSystem>().TryPickupAnyHand(SPlayer, bag), Is.False);
            Assert.That(Server.System<Content.Shared.Inventory.InventorySystem>().TryEquip(SPlayer, bag, "back", silent: true), Is.False);
        });
        await Interact();
        await Pair.RunUntilSynced();
        Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece piece = default!;
        await Client.WaitAssertion(() =>
        {
            Assert.That(CEntMan.System<SharedUserInterfaceSystem>().TryGetOpenUi<Content.Client.Storage.StorageBoundUserInterface>(
                CTarget!.Value, StorageComponent.StorageUiKey.Key, out var bui), Is.True);
            piece = Descendants(bui!.Window!).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>().Single();
            Assert.That(piece.VisibleInTree, Is.True);
            Assert.That(CEntMan.GetNetEntity(piece.Entity), Is.EqualTo(SEntMan.GetNetEntity(contents)));
        });
        await ClickControl(piece, Content.Shared.Input.ContentKeyFunctions.MoveStoredItem);
        await RunTicks(3);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.EqualTo(contents));
            Assert.That(SEntMan.EntityExists(bag), Is.False);
            Assert.That(SEntMan.EntityExists(contents), Is.True);
        });
    }

    [TestCase("FrontlineFactory", 1f)]
    [TestCase("FrontlineFactory", 1.5f)]
    [TestCase("FrontlineRefinery", 1f)]
    [TestCase("FrontlineRefinery", 1.5f)]
    [TestCase("TownHallCoreFactionOne", 1f)]
    [TestCase("TownHallCoreFactionOne", 1.5f)]
    public async Task HumanDragsToFarEdgeAndBackWithoutLosingNativeCapacity(string prototype, float scale)
    {
        var oldScale = Client.CfgMan.GetCVar(Robust.Shared.CVars.DisplayUIScale);
        var oldAuto = Client.CfgMan.GetCVar(Robust.Shared.CVars.ResAutoScaleEnabled);
        var oldStatic = Client.CfgMan.GetCVar(Content.Shared.CCVar.CCVars.StaticStorageUI);
        var mouse = (HeadlessPointer) (object) System.Reflection.DispatchProxy.Create<Robust.Client.Input.IInputManager, HeadlessPointer>();
        var replaced = new List<(object Owner, System.Reflection.FieldInfo Field, object? Value)>();
        try
        {
            await Client.WaitPost(() =>
            {
                Client.CfgMan.SetCVar(Robust.Shared.CVars.ResAutoScaleEnabled, false);
                Client.CfgMan.SetCVar(Robust.Shared.CVars.DisplayUIScale, scale);
                Client.CfgMan.SetCVar(Content.Shared.CCVar.CCVars.StaticStorageUI, false);
                var ui = Client.ResolveDependency<IUserInterfaceManager>();
                mouse.Inner = Client.ResolveDependency<Robust.Client.Input.IInputManager>();
                var controller = ui.GetUIController<Content.Client.UserInterface.Systems.Storage.StorageUIController>();
                // ClydeHeadless fixes its pointer at zero. Supply only pointer input; keep the real layout, drag controller and protocol.
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var helper = controller.GetType().GetField("_menuDragHelper", flags)!.GetValue(controller)!;
                foreach (var owner in new[] { (object) ui, helper })
                {
                    var field = owner.GetType().GetField("_inputManager", flags)!;
                    replaced.Add((owner, field, field.GetValue(owner)));
                    field.SetValue(owner, mouse);
                }
            });
            await SpawnTarget(prototype);
            EntityUid bag = default, material = default, storage = default;
            await Server.WaitAssertion(() =>
            {
                Server.ResolveDependency<Content.Server.Administration.Managers.IAdminManager>().DeAdmin(ServerSession!);
                SEntMan.AddComponent<Content.Shared.War.FrontlinePlayerComponent>(SPlayer);
                bag = SEntMan.SpawnEntity("ClothingBackpack", ToServer(PlayerCoords));
                Assert.That(Server.System<Content.Shared.Inventory.InventorySystem>().TryEquip(SPlayer, bag, "back", force: true), Is.True);
                material = SEntMan.SpawnEntity(prototype == "FrontlineRefinery" ? "FrontlineRawIron1" : "BasicMaterials1", ToServer(PlayerCoords));
                Assert.That(Server.System<SharedStorageSystem>().Insert(bag, material, out _), Is.True);
                Server.System<SharedStorageSystem>().OpenStorageUI(bag, SPlayer);
                storage = prototype switch
                {
                    "FrontlineFactory" => SEntMan.GetComponent<Content.Shared.War.FrontlineFactoryComponent>(STarget!.Value).InputContainer.Owner,
                    "FrontlineRefinery" => SEntMan.GetComponent<Content.Shared.War.FrontlineRefineryComponent>(STarget!.Value).InputContainer.Owner,
                    _ => STarget!.Value
                };
            });
            await Interact();
            await Pair.RunUntilSynced();
            await RunTicks(3);
            Content.Client.UserInterface.Systems.Storage.Controls.StorageWindow native = default!, backpack = default!;
            Robust.Client.UserInterface.Controls.ScrollContainer scroll = default!;
            Control edge = default!, drop = default!;
            await Client.WaitAssertion(() =>
            {
                var ui = CEntMan.System<SharedUserInterfaceSystem>();
                Assert.That(ui.TryGetOpenUi<Content.Client.Storage.StorageBoundUserInterface>(CEntMan.GetEntity(SEntMan.GetNetEntity(storage)), StorageComponent.StorageUiKey.Key, out var machineBui), Is.True);
                Assert.That(ui.TryGetOpenUi<Content.Client.Storage.StorageBoundUserInterface>(CEntMan.GetEntity(SEntMan.GetNetEntity(bag)), StorageComponent.StorageUiKey.Key, out var bagBui), Is.True);
                native = machineBui!.Window!;
                backpack = bagBui!.Window!;
                Assert.That(Descendants(backpack).Any(c => c.Name == "SideBar" && c.VisibleInTree), Is.True, "Ordinary backpack chrome is unchanged.");
                Assert.That(Descendants(backpack).OfType<Robust.Client.UserInterface.Controls.ScrollContainer>(), Is.Empty);
                Robust.Client.UserInterface.Controls.LayoutContainer.SetPosition(backpack, System.Numerics.Vector2.Zero);
                scroll = Descendants(native).OfType<Robust.Client.UserInterface.Controls.ScrollContainer>().Single();
                var grid = Descendants(native).OfType<Robust.Client.UserInterface.Controls.GridContainer>().Single(g => g.Name == "BackgroundGrid");
                Assert.That(grid.ChildCount, Is.EqualTo(400));
                Assert.That(grid.Columns, Is.EqualTo(20));
                Assert.That(grid.Size.X, Is.EqualTo(640));
                Assert.That(grid.GlobalPosition.X + grid.Size.X, Is.LessThanOrEqualTo(scroll.GlobalPosition.X + scroll.Size.X - 12));
                Assert.That(Descendants(native).OfType<Robust.Client.UserInterface.Controls.HScrollBar>().Any(b => b.VisibleInTree), Is.False);
                Assert.That(native.TryGetBackgroundCell(19, 19, out var edgeCell), Is.True);
                Assert.That(native.TryGetBackgroundCell(18, 18, out var dropCell), Is.True);
                edge = edgeCell!;
                drop = dropCell!;
                scroll.VScroll = 10000;
            });
            await RunTicks(3);
            await Client.WaitAssertion(() =>
            {
                Assert.That(edge.GlobalPosition.Y, Is.GreaterThanOrEqualTo(scroll.GlobalPosition.Y));
                Assert.That(edge.GlobalPosition.Y + edge.Size.Y, Is.LessThanOrEqualTo(scroll.GlobalPosition.Y + scroll.Size.Y + 1));
                mouse.Position = new Robust.Shared.Map.ScreenCoordinates((drop.GlobalPosition + drop.Size / 2) * scale, native.Window!.Id);
                var item = CEntMan.GetEntity(SEntMan.GetNetEntity(material));
                foreach (var rotation in new[] { Angle.Zero, Angle.FromDegrees(90), Angle.FromDegrees(180), Angle.FromDegrees(270) })
                    Assert.That(native.GetMouseGridPieceLocation(item, new ItemStorageLocation(rotation, Vector2i.Zero)), Is.EqualTo(new Vector2i(18, 18)));
                var hit = Client.ResolveDependency<IUserInterfaceManager>().MouseGetControl(mouse.Position);
                Assert.That(Ancestors(hit).Contains(native), Is.True, "The actual far-edge drop point resolves to the native storage, not a sibling panel.");
            });
            var source = Descendants(backpack).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>().Single();
            await Drag(source, drop);
            await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities, Is.Empty));
            var stored = Descendants(native).OfType<Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece>().Single();
            // Town hall may materialize its authoritative ledger view at a new native location.
            await Client.WaitPost(() => scroll.VScroll = stored.Location.Position.Y * 32);
            await RunTicks(3);
            Assert.That(backpack.TryGetBackgroundCell(0, 0, out var destination), Is.True);
            await Drag(stored, destination!);
            await Server.WaitAssertion(() =>
            {
                var returned = SEntMan.GetComponent<StorageComponent>(bag).Container.ContainedEntities.Single();
                Assert.That(SEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(returned).Count, Is.EqualTo(1));
                if (prototype != "TownHallCoreFactionOne") Assert.That(returned, Is.EqualTo(material));
                Assert.That(Server.System<SharedHandsSystem>().GetActiveItem(SPlayer), Is.Null);
            });

            async Task Drag(Content.Client.UserInterface.Systems.Storage.Controls.ItemGridPiece piece, Control destination)
            {
                Robust.Client.UserInterface.GUIBoundKeyEventArgs Event(Robust.Shared.Input.BoundKeyState state) => new(
                    Content.Shared.Input.ContentKeyFunctions.MoveStoredItem, state, mouse.Position, default,
                    mouse.Position.Position / scale - piece.GlobalPosition, mouse.Position.Position - piece.GlobalPixelPosition);
                await Client.WaitPost(() => mouse.Position = new Robust.Shared.Map.ScreenCoordinates(piece.GlobalPixelPosition + new System.Numerics.Vector2(16) * scale, piece.Window!.Id));
                await Client.DoGuiEvent(piece, Event(Robust.Shared.Input.BoundKeyState.Down));
                await Client.WaitPost(() => mouse.Position = new Robust.Shared.Map.ScreenCoordinates(destination.GlobalPixelPosition + destination.PixelSize / 2, destination.Window!.Id));
                await RunTicks(3);
                await Client.WaitAssertion(() =>
                {
                    var ui = Client.ResolveDependency<IUserInterfaceManager>();
                    Assert.That(ui.GetUIController<Content.Client.UserInterface.Systems.Storage.StorageUIController>().IsDragging, Is.True);
                    var target = Ancestors(destination).OfType<Content.Client.UserInterface.Systems.Storage.Controls.StorageWindow>().Single();
                    Assert.That(Ancestors(ui.MouseGetControl(mouse.Position)).Contains(target), Is.True, "Drop coordinates must hit the actual target inventory.");
                });
                await Client.DoGuiEvent(piece, Event(Robust.Shared.Input.BoundKeyState.Up));
                await RunTicks(5);
                await Pair.RunUntilSynced();
            }
        }
        finally
        {
            await Client.WaitPost(() =>
            {
                foreach (var (owner, field, value) in replaced) field.SetValue(owner, value);
                Client.CfgMan.SetCVar(Robust.Shared.CVars.DisplayUIScale, oldScale);
                Client.CfgMan.SetCVar(Robust.Shared.CVars.ResAutoScaleEnabled, oldAuto);
                Client.CfgMan.SetCVar(Content.Shared.CCVar.CCVars.StaticStorageUI, oldStatic);
            });
        }
    }

    private static IEnumerable<Control> Ancestors(Control? control)
    {
        for (; control != null; control = control.Parent) yield return control;
    }

    [Robust.Shared.Analyzers.Virtual]
    public class HeadlessPointer : System.Reflection.DispatchProxy
    {
        public Robust.Shared.Map.ScreenCoordinates Position;
        public Robust.Client.Input.IInputManager Inner = default!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method!.Name == "get_MouseScreenPosition" ? Position : method.Invoke(Inner, args);
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
