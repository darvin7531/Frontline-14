#nullable enable
using System.Linq;
using System.Numerics;
using Robust.Shared;
using Robust.Shared.Maths;
using Content.Client.War;
using Content.Server.War;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.War;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlineProductionUiTest : InteractionTest
{
    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    [TestCase("TownHallCoreFactionOne")]
    public async Task NativeStateRefreshRetainsControls(string prototype)
    {
        await SpawnTarget(prototype);
        if (prototype == "TownHallCoreFactionOne")
            await Server.WaitPost(() =>
            {
                SComp<FrontlineStockpileComponent>(STarget!.Value).Counts["FrontlineWeaponPistolMk58"] = 5;
                SComp<FrontlineStockpileComponent>(STarget.Value).Counts["SoldierSupplies"] = 7;
            });
        if (prototype != "TownHallCoreFactionOne")
            await Server.WaitPost(() =>
            {
                for (var i = 0; i < 12; i++)
                {
                    var input = Stack.SpawnAtPosition(prototype == "FrontlineFactory" ? 20 : 5,
                        prototype == "FrontlineFactory" ? "BasicMaterials" : "RawTechnologyMaterial", SEntMan.GetCoordinates(PlayerCoords));
                    if (prototype == "FrontlineFactory")
                    {
                        var system = Server.System<FrontlineFactorySystem>();
                        Assert.That(system.TryInsertInput(STarget!.Value, input), Is.True);
                        Assert.That(system.TrySubmitContainedJob(STarget.Value, "FrontlineFactoryMk58"), Is.True);
                    }
                    else
                    {
                        var system = Server.System<FrontlineRefinerySystem>();
                        Assert.That(system.TryInsertInput(STarget!.Value, input), Is.True);
                        Assert.That(system.TrySubmitContainedJob(STarget.Value, "FrontlineTechnologyAlloy"), Is.True);
                    }
                }
            });
        await Interact();
        await Pair.RunUntilSynced();
        BaseWindow window = default!;
        ScrollContainer scroll = default!;
        Button action = default!;
        FrontlineItemGrid grid = default!;
        ContainerButton cell = default!;
        await Client.WaitAssertion(() =>
        {
            window = prototype switch
            {
                "FrontlineFactory" => GetWindow<FrontlineFactoryWindow>(),
                "FrontlineRefinery" => GetWindow<FrontlineRefineryWindow>(),
                _ => GetWindow<FrontlineStockpileWindow>(),
            };
            grid = GetControlFromChildren<FrontlineItemGrid>(g => g.Name == (prototype == "TownHallCoreFactionOne" ? "Products" : "Recipes"), window);
            cell = grid.Cells.Children.OfType<ContainerButton>().First();
            Assert.That(GetControlFromChildren<TextureRect>(cell, true).Texture, Is.Not.Null);
            Assert.That(cell.ToolTip, Is.Not.Empty);
            Assert.That(cell.HasStyleClass(ContainerButton.StyleClassButton), Is.True,
                "Native button styling must expose hover and selected states.");
            if (prototype == "TownHallCoreFactionOne")
                Assert.That(GetControlFromChildren<RichTextLabel>(label => label.Name == "ItemName", cell).GetMessage(), Is.Not.Empty);
            else
            {
                var name = GetControlFromChildren<Label>(label => label.Name == "ItemName", cell);
                Assert.That(name.Text, Is.Not.Empty);
                Assert.That(name.ClipText, Is.True, "Recipe names never wrap inside words.");
                Assert.That(cell.MinHeight, Is.LessThanOrEqualTo(64));
                Assert.That(grid.Cells.Columns, Is.EqualTo(1), "Recipes use the full column width.");
            }
            Assert.That(grid.Cells.ChildCount, Is.GreaterThan(0));
            if (prototype == "TownHallCoreFactionOne")
            {
                Assert.That(grid.Cells.ChildCount, Is.EqualTo(1), "Zero products and virtual supplies are not physical cells.");
                Assert.That(GetControlFromChildren<Label>(label => label.Text == "5", cell), Is.Not.Null);
            }
            scroll = GetControlFromChildren<ScrollContainer>(window, true);
            action = GetControlFromChildren<Button>(button => button.Name == "TakeOutput" ||
                (prototype == "TownHallCoreFactionOne" && !button.Disabled), window);
            action.CanKeyboardFocus = true;
            action.GrabKeyboardFocus();
        });
        await ClickControl(cell);
        await Client.WaitPost(() => action.GrabKeyboardFocus());
        Assert.That(grid.SelectedId, Is.EqualTo(cell.Name), "Native click selects the actual recipe/product.");
        Assert.That(cell.Pressed, Is.True, "Selected cards use the native pressed highlight.");
        if (grid.Categories.Visible)
        {
            await ClickControl(grid.Categories);
            Button category = default!;
            await Client.WaitPost(() => category = GetControlFromChildren<Button>(grid.Categories.OptionsScroll, true));
            // First actual category, not the All entry.
            await Client.WaitPost(() => category = Descendants(grid.Categories.OptionsScroll).OfType<Button>().Skip(1).First());
            await ClickControl(category);
        }
        await Client.WaitPost(() => action.GrabKeyboardFocus());
        var selectedCategory = grid.SelectedCategory;
        var selected = grid.SelectedId;
        ScrollContainer? queueScroll = null;
        Control? queueRow = null;
        float queuePosition = 0;
        if (prototype != "TownHallCoreFactionOne")
            await Client.WaitAssertion(() =>
            {
                var queue = GetControlFromChildren<FrontlineJobList>(window, true);
                queueScroll = (ScrollContainer) queue.Parent!;
                queueScroll.VScroll = 100;
                queuePosition = queueScroll.VScroll;
                Assert.That(queuePosition, Is.GreaterThan(0), "Funded queue is independently scrollable.");
                queueRow = queue.Children.Last();
            });
        // Real BUI requests cause the server to republish the actual machine state.
        if (prototype == "FrontlineFactory")
            await SendBui(FrontlineFactoryUiKey.Key, new FrontlineFactoryEjectMessage());
        else if (prototype == "FrontlineRefinery")
            await SendBui(FrontlineRefineryUiKey.Key, new FrontlineRefineryEjectMessage());
        else
            await SendBui(FrontlineStockpileUiKey.Key, new FrontlineStockpileSubmitMessage());
        await Pair.RunUntilSynced();
        await Client.WaitAssertion(() =>
        {
            Assert.That(GetControlFromChildren<ScrollContainer>(window, true), Is.SameAs(scroll),
                "A state refresh must not replace the scroll container.");
            Assert.That(action.Parent, Is.Not.Null, "A state refresh must not remove the focused action.");
            Assert.That(UiMan.KeyboardFocused, Is.SameAs(action));
            Assert.That(grid.SelectedId, Is.EqualTo(selected));
            Assert.That(grid.SelectedCategory, Is.EqualTo(selectedCategory));
            if (queueScroll != null)
            {
                Assert.That(queueScroll.VScroll, Is.EqualTo(queuePosition));
                Assert.That(GetControlFromChildren<FrontlineJobList>(window, true).Children.Last(), Is.SameAs(queueRow));
            }
            Assert.That(grid.Cells.Children.OfType<ContainerButton>().First(), Is.SameAs(cell));
        });
        var previousScale = Client.CfgMan.GetCVar(CVars.DisplayUIScale);
        var previousAutoScale = Client.CfgMan.GetCVar(CVars.ResAutoScaleEnabled);
        try
        {
            foreach (var scale in new[] { 1f, 1.5f })
            foreach (var size in new[] { window.MinSize, new Vector2(640, 480), new Vector2(1000, 700) })
                await Client.WaitAssertion(() =>
                {
                    Client.CfgMan.SetCVar(CVars.ResAutoScaleEnabled, false);
                    Client.CfgMan.SetCVar(CVars.DisplayUIScale, scale);
                    window.SetSize = size;
                    window.Measure(size);
                    window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                    Assert.That(window.UIScale, Is.EqualTo(scale));
                    Assert.That(window.MinWidth, Is.GreaterThanOrEqualTo(prototype == "TownHallCoreFactionOne" ? 360 : 600));
                    Assert.That(window.MinHeight, Is.GreaterThanOrEqualTo(prototype == "TownHallCoreFactionOne" ? 320 : 400));
                    foreach (var button in Descendants(window).OfType<Button>().Where(b => b.Name is "Produce" or "TakeOutput" or "Withdraw"))
                    {
                        Assert.That(button.Size.Y, Is.GreaterThan(0));
                        Assert.That(button.GlobalPosition.Y + button.Size.Y, Is.LessThanOrEqualTo(window.GlobalPosition.Y + window.Size.Y));
                    }
                    foreach (var itemGrid in Descendants(window).OfType<FrontlineItemGrid>())
                    {
                        Assert.That(itemGrid.Size.X, Is.GreaterThan(0));
                        Assert.That(itemGrid.Scroll.HScrollEnabled, Is.False);
                        foreach (var item in itemGrid.Cells.Children.Where(c => c.Visible))
                        {
                            Assert.That(item.Position.X + item.Size.X, Is.LessThanOrEqualTo(itemGrid.Scroll.Size.X + 1), "Cells fit their independent scroll viewport.");
                            if (itemGrid.Name is "Inputs" or "Outputs")
                            {
                                Assert.That(item.Size.X, Is.EqualTo(item.Size.Y), "Machine contents are square slots.");
                                Assert.That(item.Size.X, Is.LessThanOrEqualTo(48));
                            }
                            else if (prototype == "TownHallCoreFactionOne")
                            {
                                var name = GetControlFromChildren<RichTextLabel>(label => label.Name == "ItemName", item);
                                Assert.That(name.Size.Y, Is.GreaterThan(0));
                                Assert.That(name.Size.X, Is.LessThanOrEqualTo(item.Size.X));
                            }
                            else
                            {
                                var name = GetControlFromChildren<Label>(label => label.Name == "ItemName", item);
                                name.Text = "Синтез технологического сплава из необработанного технологического материала";
                                item.Measure(item.Size);
                                item.Arrange(UIBox2.FromDimensions(item.Position, item.Size));
                                Assert.That(name.ClipText, Is.True);
                                Assert.That(item.Size.Y, Is.LessThanOrEqualTo(64), "Long Russian names do not grow multi-line cards.");
                            }
                            foreach (var icon in Descendants(item).OfType<TextureRect>())
                                Assert.That(icon.Size.X, Is.GreaterThanOrEqualTo(32), "Item art is not shrunk into micro icons.");
                        }
                    }
                });
        }
        finally
        {
            await Client.WaitPost(() =>
            {
                Client.CfgMan.SetCVar(CVars.DisplayUIScale, previousScale);
                Client.CfgMan.SetCVar(CVars.ResAutoScaleEnabled, previousAutoScale);
            });
        }
        await CloseBui(prototype switch
        {
            "FrontlineFactory" => FrontlineFactoryUiKey.Key,
            "FrontlineRefinery" => FrontlineRefineryUiKey.Key,
            _ => (System.Enum) FrontlineStockpileUiKey.Key,
        });
    }

    [TestCase("FrontlineFactory")]
    [TestCase("FrontlineRefinery")]
    public async Task NativeMachineContentsUseCompactSlots(string prototype)
    {
        await SpawnTarget(prototype);
        await Server.WaitPost(() =>
        {
            var input = Stack.SpawnAtPosition(10, prototype == "FrontlineFactory" ? "BasicMaterials" : "FrontlineRawIron",
                SEntMan.GetCoordinates(PlayerCoords));
            var output = prototype == "FrontlineFactory"
                ? SEntMan.SpawnEntity("FrontlineWeaponPistolMk58", SEntMan.GetCoordinates(PlayerCoords))
                : Stack.SpawnAtPosition(5, "BasicMaterials", SEntMan.GetCoordinates(PlayerCoords));
            var inserted = prototype == "FrontlineFactory"
                ? Server.System<FrontlineFactorySystem>().TryInsertInput(STarget!.Value, input)
                : Server.System<FrontlineRefinerySystem>().TryInsertInput(STarget!.Value, input);
            Assert.That(inserted, Is.True);
            var container = prototype == "FrontlineFactory"
                ? SComp<FrontlineFactoryComponent>(STarget!.Value).OutputContainer
                : SComp<FrontlineRefineryComponent>(STarget!.Value).OutputContainer;
            Assert.That(Server.System<Robust.Shared.Containers.SharedContainerSystem>().Insert(output, container), Is.True);
        });
        await Interact();
        await Pair.RunUntilSynced();
        BaseWindow window = default!;
        await Client.WaitAssertion(() =>
        {
            window = prototype == "FrontlineFactory" ? GetWindow<FrontlineFactoryWindow>() : GetWindow<FrontlineRefineryWindow>();
            foreach (var name in new[] { "Inputs", "Outputs" })
            {
                var grid = GetControlFromChildren<FrontlineItemGrid>(g => g.Name == name, window);
                var slot = grid.Cells.Children.OfType<ContainerButton>().Single();
                Assert.That(slot.Size.X, Is.EqualTo(slot.Size.Y));
                Assert.That(slot.Size.X, Is.LessThanOrEqualTo(48));
                Assert.That(slot.ToolTip, Is.Not.Empty);
                Assert.That(Descendants(slot).OfType<RichTextLabel>(), Is.Empty, "Names live in tooltips, not below compact slots.");
                Assert.That(Descendants(slot).OfType<PanelContainer>().Any(p => p.HasStyleClass("InventorySlotBackground")), Is.True,
                    "Reuse the native item slot background.");
                Assert.That(Descendants(slot).OfType<Label>().Single(l => l.Name == "ItemCount").Text, Is.Not.Empty);
            }
        });
        await CloseBui(prototype == "FrontlineFactory" ? FrontlineFactoryUiKey.Key : (System.Enum) FrontlineRefineryUiKey.Key);
    }

    [TestCase("FrontlineFactory", "FrontlineFactorySupplyCrate", "BasicMaterials", 10)]
    [TestCase("FrontlineRefinery", "FrontlineSteel", "FrontlineRawIron", 5)]
    public async Task NativeRecipeSelectionProducesThroughExistingServerRequest(string prototype, string recipe, string material, int amount)
    {
        await SpawnTarget(prototype);
        await Server.WaitPost(() =>
        {
            var input = Stack.SpawnAtPosition(amount, material, SEntMan.GetCoordinates(PlayerCoords));
            var inserted = prototype == "FrontlineFactory"
                ? Server.System<FrontlineFactorySystem>().TryInsertInput(STarget!.Value, input)
                : Server.System<FrontlineRefinerySystem>().TryInsertInput(STarget!.Value, input);
            Assert.That(inserted, Is.True);
        });
        await Interact();
        await Pair.RunUntilSynced();
        BaseWindow window = default!;
        ContainerButton cell = default!;
        Button produce = default!;
        await Client.WaitPost(() =>
        {
            window = prototype == "FrontlineFactory" ? GetWindow<FrontlineFactoryWindow>() : GetWindow<FrontlineRefineryWindow>();
            cell = GetControlFromChildren<ContainerButton>(button => button.Name == recipe, window);
            produce = GetControlFromChildren<Button>(button => button.Name == "Produce", window);
        });
        await ClickControl(cell);
        await Client.WaitAssertion(() => Assert.That(produce.Disabled, Is.False));
        await ClickControl(produce);
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            if (prototype == "FrontlineFactory")
            {
                var state = Server.System<FrontlineFactorySystem>().BuildUiState(STarget!.Value);
                Assert.That(state.Inputs.Sum(input => input.Amount), Is.Zero);
                Assert.That(state.Jobs.Single().Recipe.Id, Is.EqualTo(recipe));
            }
            else
            {
                var state = Server.System<FrontlineRefinerySystem>().BuildUiState(STarget!.Value);
                Assert.That(state.Inputs.Sum(input => input.Amount), Is.Zero);
                Assert.That(state.Jobs.Single().Recipe.Id, Is.EqualTo(recipe));
            }
        });
        await Client.WaitAssertion(() => Assert.That(produce.Disabled, Is.True));
        await CloseBui(prototype == "FrontlineFactory" ? FrontlineFactoryUiKey.Key : (System.Enum) FrontlineRefineryUiKey.Key);
    }

    [TestCase("Faction", 300, 180)]
    [TestCase("Respawn", 300, 180)]
    [TestCase("Victory", 320, 200)]
    public async Task ChoiceMenusHaveBoundedMinimums(string menu, int width, int height)
    {
        await Client.WaitAssertion(() =>
        {
            DefaultWindow window = menu switch
            {
                "Faction" => new FactionSelectionWindow(),
                "Respawn" => new RespawnChoiceWindow(),
                _ => new WarVictoryWindow(),
            };
            Assert.That(window.MinWidth, Is.EqualTo(width));
            Assert.That(window.MinHeight, Is.EqualTo(height));
            Assert.That(window.MinWidth, Is.LessThanOrEqualTo(640));
            Assert.That(window.MinHeight, Is.LessThanOrEqualTo(480));
            window.Orphan();
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    [TestCase(int.MaxValue)]
    public async Task NativeRefineryRejectsInvalidBatchRequestsWithoutDebit(int batches)
    {
        await SpawnTarget("FrontlineRefinery");
        await Server.WaitPost(() => Assert.That(Server.System<FrontlineRefinerySystem>().TryInsertInput(STarget!.Value,
            Stack.SpawnAtPosition(17, "FrontlineRawIron", SEntMan.GetCoordinates(PlayerCoords))), Is.True));
        await Interact();
        await Pair.RunUntilSynced();
        await SendBui(FrontlineRefineryUiKey.Key, new FrontlineRefinerySubmitMessage("FrontlineSteel", batches));
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            var state = Server.System<FrontlineRefinerySystem>().BuildUiState(STarget!.Value);
            Assert.That(state.Inputs.Single().Amount, Is.EqualTo(17));
            Assert.That(state.Jobs, Is.Empty);
        });
        await CloseBui(FrontlineRefineryUiKey.Key);
    }

    [TestCase(false, 7, 10)]
    [TestCase(true, 2, 15)]
    public async Task NativeRefineryBatchQuantityAndShiftAll(bool shift, int remainder, int output)
    {
        await SpawnTarget("FrontlineRefinery");
        await Server.WaitPost(() =>
        {
            var system = Server.System<FrontlineRefinerySystem>();
            Assert.That(system.TryInsertInput(STarget!.Value,
                Stack.SpawnAtPosition(17, "FrontlineRawIron", SEntMan.GetCoordinates(PlayerCoords))), Is.True);
            Assert.That(system.TryInsertInput(STarget.Value,
                Stack.SpawnAtPosition(11, "RawTechnologyMaterial", SEntMan.GetCoordinates(PlayerCoords))), Is.True);
        });
        await Interact();
        await Pair.RunUntilSynced();
        FrontlineRefineryWindow window = default!;
        ContainerButton recipe = default!;
        Button produce = default!;
        await Client.WaitAssertion(() =>
        {
            window = GetWindow<FrontlineRefineryWindow>();
            recipe = GetControlFromChildren<ContainerButton>(c => c.Name == "FrontlineSteel", window);
            produce = GetControlFromChildren<Button>(c => c.Name == "Produce", window);
            var quantity = GetControlFromChildren<SpinBox>(c => c.Name == "Batches", window);
            Assert.That(quantity.IsValid!(0), Is.False);
        });
        await ClickControl(recipe);
        await Client.WaitPost(() => GetControlFromChildren<SpinBox>(c => c.Name == "Batches", window).Value = 2);
        var input = Client.ResolveDependency<Robust.Client.Input.IInputManager>();
        var key = new Robust.Client.Input.KeyEventArgs(Robust.Client.Input.Keyboard.Key.Shift,
            false, false, false, true, false, 0);
        try
        {
            if (shift)
                await Client.WaitPost(() => input.KeyDown(key));
            await ClickControl(produce);
        }
        finally
        {
            if (shift)
                await Client.WaitPost(() => input.KeyUp(key));
        }
        await Pair.RunUntilSynced();
        await Server.WaitAssertion(() =>
        {
            var state = Server.System<FrontlineRefinerySystem>().BuildUiState(STarget!.Value);
            Assert.That(state.Inputs.Single(i => i.Stack == "FrontlineRawIron").Amount, Is.EqualTo(remainder));
            Assert.That(state.Inputs.Single(i => i.Stack == "RawTechnologyMaterial").Amount, Is.EqualTo(11));
            Assert.That(state.Jobs.Select(j => j.Recipe.Id), Is.All.EqualTo("FrontlineSteel"));
        });
        await Pair.RunSeconds(16);
        await Server.WaitAssertion(() =>
        {
            var state = Server.System<FrontlineRefinerySystem>().BuildUiState(STarget!.Value);
            Assert.That(state.Jobs, Is.Empty);
            Assert.That(state.Outputs.Single(i => i.Stack == "BasicMaterials").Amount, Is.EqualTo(output));
        });
        await CloseBui(FrontlineRefineryUiKey.Key);
    }

    [Test]
    public async Task QueueRowsMeasureWrappedNamesBeforeStatusAndProgress()
    {
        await SpawnTarget("FrontlineRefinery");
        await Interact();
        await Pair.RunUntilSynced();
        await Client.WaitAssertion(() =>
        {
            var queue = GetControlFromChildren<FrontlineJobList>(GetWindow<FrontlineRefineryWindow>(), true);
            queue.SetJobs(Enumerable.Range(0, 3).Select(i => new FrontlineJobView("long",
                "Синтез технологического сплава из необработанного технологического материала",
                null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), true)));
            queue.Measure(new Vector2(160, float.PositiveInfinity));
            queue.Arrange(UIBox2.FromDimensions(Vector2.Zero, queue.DesiredSize));
            var rows = queue.Children.Where(c => c.Visible).ToArray();
            foreach (var row in rows)
            {
                var texts = Descendants(row).OfType<RichTextLabel>().ToArray();
                Assert.That(texts, Has.Length.EqualTo(2), "Name and status need separate natural-height controls.");
                var progress = Descendants(row).OfType<ProgressBar>().Single();
                Assert.That(texts[1].GlobalPosition.Y, Is.GreaterThanOrEqualTo(texts[0].GlobalPosition.Y + texts[0].Size.Y));
                Assert.That(progress.GlobalPosition.Y, Is.GreaterThanOrEqualTo(texts[1].GlobalPosition.Y + texts[1].Size.Y));
                Assert.That(progress.GlobalPosition.Y + progress.Size.Y, Is.LessThanOrEqualTo(row.GlobalPosition.Y + row.Size.Y + 1));
            }
            for (var i = 1; i < rows.Length; i++)
                Assert.That(rows[i].Position.Y, Is.GreaterThanOrEqualTo(rows[i - 1].Position.Y + rows[i - 1].Size.Y));
        });
        await CloseBui(FrontlineRefineryUiKey.Key);
    }

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control control)
    {
        foreach (var child in control.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
