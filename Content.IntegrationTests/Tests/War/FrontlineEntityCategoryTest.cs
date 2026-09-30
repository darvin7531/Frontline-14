using System;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using YamlDotNet.RepresentationModel;

namespace Content.IntegrationTests.Tests.War;

[TestFixture]
public sealed class FrontlineEntityCategoryTest : GameTest
{
    private const string FrontlineCategory = "Frontline";
    private const string WarPrototypeDirectory = "/Prototypes/War";

    private static readonly string[] KeyFrontlineAssets =
    [
        "FrontlineFactory",
        "FrontlineSupplyCrate",
        "FrontlineWeaponPistolMk58",
        "FrontlineRefinery",
        "FrontlineRawIron",
        "BasicMaterials",
        "BasicMaterials1",
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

    // Keep this list small and intentional. Concrete entities under /Prototypes/War
    // must either belong to Frontline or be explicitly documented here.
    private static readonly HashSet<string> WarEntityCategoryExceptions = [];

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

    [Test]
    public void ConcreteWarEntitiesBelongToFrontlineCategoryOrAreExplicitExceptions()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var resources = Pair.Server.ResolveDependency<IResourceManager>();

        Assert.That(prototypes.TryIndex<EntityCategoryPrototype>(FrontlineCategory, out var category), Is.True);
        Assert.That(category, Is.Not.Null);

        var concreteEntities = new HashSet<string>();

        foreach (var path in resources.ContentFindFiles(WarPrototypeDirectory))
        {
            var pathText = path.ToString();
            if (!pathText.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) &&
                !pathText.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            var yaml = resources.ContentFileReadYaml(path);
            foreach (var document in yaml.Documents)
            {
                if (document.RootNode is not YamlSequenceNode sequence)
                    continue;

                foreach (var mapping in sequence.Children.OfType<YamlMappingNode>())
                {
                    if (GetScalar(mapping, "type") != "entity")
                        continue;

                    var id = GetScalar(mapping, "id");
                    Assert.That(id, Is.Not.Null.And.Not.Empty, $"Entity prototype in {path} has no id.");

                    if (string.Equals(GetScalar(mapping, "abstract"), "true", StringComparison.OrdinalIgnoreCase))
                        continue;

                    concreteEntities.Add(id!);

                    if (WarEntityCategoryExceptions.Contains(id!))
                        continue;

                    Assert.That(
                        prototypes.TryIndex<EntityPrototype>(id!, out var prototype),
                        Is.True,
                        $"Concrete entity {id} from {path} is not a loaded entity prototype.");
                    Assert.That(prototype, Is.Not.Null, id);
                    Assert.That(
                        prototype!.Categories,
                        Does.Contain(category),
                        $"Concrete entity {id} from {path} must belong to {FrontlineCategory} or be an explicit exception.");
                }
            }
        }

        Assert.That(concreteEntities, Is.Not.Empty, $"No concrete entity prototypes found under {WarPrototypeDirectory}.");

        foreach (var exception in WarEntityCategoryExceptions)
        {
            Assert.That(
                concreteEntities,
                Does.Contain(exception),
                $"Frontline category exception {exception} is stale or no longer belongs under {WarPrototypeDirectory}.");
        }
    }

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        foreach (var (node, value) in mapping.Children)
        {
            if (node is YamlScalarNode scalar &&
                scalar.Value == key)
                return (value as YamlScalarNode)?.Value;
        }

        return null;
    }
}
