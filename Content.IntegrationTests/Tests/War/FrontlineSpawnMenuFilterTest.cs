using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers.Implementations;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class FrontlineSpawnMenuFilterTest : GameTest
{
    private const string FrontlineCategory = "Frontline";
    private const string FilterControlName = "FrontlineSpawnModeFilter";

    [Test]
    public async Task SpawnWindowGetsFrontlineModeAndCategoryFilterUsesPrototypeCategory()
    {
        var client = Pair.Client;
        var ui = client.ResolveDependency<IUserInterfaceManager>();
        var cfg = client.ResolveDependency<IConfigurationManager>();
        var prototypes = client.ResolveDependency<IPrototypeManager>();
        var spawnController = ui.GetUIController<EntitySpawningUIController>();

        await client.WaitPost(() =>
        {
            spawnController.CloseWindow();
            cfg.SetCVar(CVars.EntitiesCategoryFilter, string.Empty);
            spawnController.ToggleWindow();
        });

        try
        {
            await client.WaitAssertion(() =>
            {
                var window = ui.WindowRoot.Children.OfType<EntitySpawnWindow>().Single(control => control.IsOpen);
                var row = Descendants(window).Single(control => control.Name == FilterControlName);
                var mode = Descendants(row).OfType<OptionButton>().Single();

                Assert.That(mode.ItemCount, Is.EqualTo(2));
                Assert.That(mode.SelectedId, Is.EqualTo(0));
            });

            await client.WaitPost(() =>
            {
                var window = ui.WindowRoot.Children.OfType<EntitySpawnWindow>().Single(control => control.IsOpen);
                cfg.SetCVar(CVars.EntitiesCategoryFilter, FrontlineCategory);

                // Trigger the standard spawn controller's existing list rebuild path.
                window.SearchBar.SetText(" ", true);
                window.SearchBar.SetText(string.Empty, true);
            });

            await client.WaitAssertion(() =>
            {
                var window = ui.WindowRoot.Children.OfType<EntitySpawnWindow>().Single(control => control.IsOpen);

                Assert.That(prototypes.TryIndex<EntityCategoryPrototype>(FrontlineCategory, out var category), Is.True);
                Assert.That(category, Is.Not.Null);

                var expected = prototypes.EnumeratePrototypes<EntityPrototype>()
                    .Count(prototype =>
                        !prototype.Abstract &&
                        !prototype.HideSpawnMenu &&
                        prototype.Categories.Contains(category!));

                Assert.That(window.PrototypeList.TotalItemCount, Is.EqualTo(expected));
                Assert.That(expected, Is.GreaterThan(0));
            });
        }
        finally
        {
            await client.WaitPost(() =>
            {
                cfg.SetCVar(CVars.EntitiesCategoryFilter, string.Empty);
                spawnController.CloseWindow();
            });
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (var child in parent.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
