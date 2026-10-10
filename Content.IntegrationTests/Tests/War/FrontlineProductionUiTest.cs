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
        await Client.WaitPost(() =>
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
            Assert.That(GetControlFromChildren<RichTextLabel>(label => label.Name == "ItemName", cell).GetMessage(),
                Is.Not.Empty, "Every cell needs a visible localized name, not only a tooltip.");
            Assert.That(cell.MinHeight, Is.GreaterThanOrEqualTo(80));
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
            await Client.WaitPost(() =>
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
            foreach (var size in new[] { new Vector2(640, 480), new Vector2(1000, 700) })
                await Client.WaitPost(() =>
                {
                    Client.CfgMan.SetCVar(CVars.ResAutoScaleEnabled, false);
                    Client.CfgMan.SetCVar(CVars.DisplayUIScale, scale);
                    window.SetSize = size;
                    window.Measure(size);
                    window.Arrange(UIBox2.FromDimensions(Vector2.Zero, size));
                    Assert.That(window.UIScale, Is.EqualTo(scale));
                    foreach (var itemGrid in Descendants(window).OfType<FrontlineItemGrid>())
                    {
                        Assert.That(itemGrid.Size.X, Is.GreaterThan(0));
                        Assert.That(itemGrid.Scroll.HScrollEnabled, Is.False);
                        foreach (var item in itemGrid.Cells.Children.Where(c => c.Visible))
                        {
                            Assert.That(item.Position.X + item.Size.X, Is.LessThanOrEqualTo(itemGrid.Scroll.Size.X + 1), "Cells fit their independent scroll viewport.");
                            var name = GetControlFromChildren<RichTextLabel>(label => label.Name == "ItemName", item);
                            Assert.That(name.Size.Y, Is.GreaterThan(0), "Names remain visible at each scale.");
                            Assert.That(name.Size.X, Is.LessThanOrEqualTo(item.Size.X));
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
