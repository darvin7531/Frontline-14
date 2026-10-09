#nullable enable
using Content.IntegrationTests.Fixtures;
using Content.Shared.War;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class FrontlineSupplyCrateTest : GameTest
{
    private static readonly EntProtoId SupplyCrate = "FrontlineSupplyCrate";

    [Test]
    public void SupplyCrateDeclaresProductMetadata()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var crate = prototypes.Index(SupplyCrate);
        Assert.That(crate.Components.ContainsKey("FrontlineSupplyCrate"), Is.True);
    }

    [TestCase("FrontlineWeaponCrate", "FrontlineWeaponPistolMk58", "FrontlineWeaponPistolMk58")]
    [TestCase("FrontlineAmmoCrate", "MagazinePistol", "FrontlineMagazinePistol")]
    [TestCase("FrontlineMedicalCrate", "Brutepack1", "Brutepack1")]
    [TestCase("FrontlineSupplyCrate", "SoldierSupplies", null)]
    public async Task CrateCarriesOnlyAProductCount(string crateId, string productId, string? entityId)
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        Assert.That(prototypes.HasIndex<EntityPrototype>(crateId), Is.True, crateId);
        var map = await Pair.CreateTestMap();
        EntityUid crate = default;

        await server.WaitPost(() => crate = SEntMan.SpawnEntity(crateId, map.GridCoords));
        await server.WaitAssertion(() =>
        {
            var contents = SComp<FrontlineSupplyCrateComponent>(crate);
            Assert.That(contents.Product.Id, Is.EqualTo(productId));
            Assert.That(contents.Amount, Is.EqualTo(5));
            var product = prototypes.Index(contents.Product);
            Assert.That(product.Entity?.Id, Is.EqualTo(entityId));
            Assert.That(server.ResolveDependency<ILocalizationManager>().HasString(product.Name), Is.True);
            if (product.Entity is { } entity)
                Assert.That(prototypes.Index(entity).Abstract, Is.False);
            Assert.That(SEntMan.HasComponent<StorageComponent>(crate), Is.False);
            Assert.That(SEntMan.HasComponent<EntityStorageComponent>(crate), Is.False);
            Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(crate), Is.False);
        });
    }

    [TestCase("FrontlineWeaponCrate")]
    [TestCase("FrontlineAmmoCrate")]
    [TestCase("FrontlineMedicalCrate")]
    [TestCase("FrontlineSupplyCrate")]
    public async Task TransportMovesTheSealedCrateWithoutMaterializingProducts(string crateId)
    {
        var server = Pair.Server;
        var map = await Pair.CreateTestMap();
        var hands = server.System<SharedHandsSystem>();
        var containers = server.System<SharedContainerSystem>();
        EntityUid crate = default;
        EntityUid carrier = default;
        Container cargo = default!;
        ProtoId<FrontlineSupplyProductPrototype> product = default;

        await server.WaitPost(() =>
        {
            var user = SEntMan.SpawnEntity("MobHuman", map.GridCoords);
            carrier = SEntMan.SpawnEntity(null, map.GridCoords);
            cargo = containers.EnsureContainer<Container>(carrier, "cargo");
            crate = SEntMan.SpawnEntity(crateId, map.GridCoords);
            product = SComp<FrontlineSupplyCrateComponent>(crate).Product;
            Assert.That(hands.TryPickupAnyHand(user, crate), Is.True);
            Assert.That(hands.TryDropIntoContainer(user, crate, cargo), Is.True);
            server.System<SharedTransformSystem>().SetCoordinates(carrier,
                map.GridCoords.Offset(new System.Numerics.Vector2(0.2f, 0)));
        });
        await server.WaitAssertion(() =>
        {
            Assert.That(cargo.ContainedEntities, Is.EqualTo(new[] { crate }));
            Assert.That(SComp<TransformComponent>(crate).ParentUid, Is.EqualTo(carrier));
            Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Product, Is.EqualTo(product));
            Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Amount, Is.EqualTo(5));
            Assert.That(SEntMan.HasComponent<ContainerManagerComponent>(crate), Is.False);
        });
        await server.WaitPost(() => containers.EmptyContainer(cargo));
        await server.WaitAssertion(() =>
        {
            Assert.That(cargo.ContainedEntities, Is.Empty);
            Assert.That(SEntMan.EntityExists(crate), Is.True);
            Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Product, Is.EqualTo(product));
            Assert.That(SComp<FrontlineSupplyCrateComponent>(crate).Amount, Is.EqualTo(5));
        });
    }
}
