using Content.IntegrationTests.Fixtures;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class FrontlineEntityCategoryTest : GameTest
{
    private const string FrontlineCategory = "Frontline";

    private static readonly string[] KeyFrontlineAssets =
    [
        "FrontlineFactory",
        "FrontlineSupplyCrate",
        "FrontlineWeaponPistolMk58",
        "FrontlineRefinery",
        "FrontlineRawIron",
        "RawTechnologyMaterial",
        "TechnologyAlloy",
        "FrontlineResourceField",
        "FrontlineResourceSpawnPoint",
        "FrontlineIronResourceNode",
        "TerritoryMarker",
        "TownHallCoreFactionOne",
        "TownHallCoreFactionTwo",
        "TownHallRuin",
        "FactionSpawnPoint",
    ];

    [Test]
    public void KeyAssetsBelongToFrontlineCategory()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();

        Assert.That(prototypes.TryIndex<EntityCategoryPrototype>(FrontlineCategory, out var category), Is.True);
        Assert.That(category, Is.Not.Null);

        foreach (var id in KeyFrontlineAssets)
        {
            Assert.That(prototypes.TryIndex<EntityPrototype>(id, out var prototype), Is.True, id);
            Assert.That(prototype, Is.Not.Null, id);
            Assert.That(prototype!.Categories, Does.Contain(category), id);
        }
    }
}
