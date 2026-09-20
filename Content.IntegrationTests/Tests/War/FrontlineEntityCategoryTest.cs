using Content.IntegrationTests.Fixtures;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class FrontlineEntityCategoryTest : GameTest
{
    private static readonly string[] InitialFrontlineAssets =
    [
        "FrontlineFactory",
        "FrontlineRefinery",
        "FrontlineSupplyCrate",
        "FrontlineWeaponPistolMk58",
    ];

    [Test]
    public void InitialAssetsBelongToFrontlineCategory()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();

        Assert.That(prototypes.TryIndex<EntityCategoryPrototype>("Frontline", out var category), Is.True);
        Assert.That(category, Is.Not.Null);

        foreach (var id in InitialFrontlineAssets)
        {
            Assert.That(prototypes.TryIndex<EntityPrototype>(id, out var prototype), Is.True, id);
            Assert.That(prototype, Is.Not.Null, id);
            Assert.That(prototype!.Categories, Does.Contain(category), id);
        }
    }
}
