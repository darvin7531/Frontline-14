#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Content.IntegrationTests.Fixtures;
using Content.Shared.Maps;
using Content.Shared.War;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;

namespace Content.IntegrationTests.Tests.War;

public sealed class FrontlinePlanetaryAssetTest : GameTest
{
    private static readonly ProtoId<ContentTileDefinition>[] Tiles =
    [
        "FrontlineFloorDirt", "FrontlineFloorGrass", "FrontlineFloorSand",
        "FrontlineFloorAsphalt", "FrontlineFloorConcrete", "FrontlineFloorDirtRoad",
        "FrontlineFloorGrassDark",
        "FrontlineFloorGrassLight",
        "FrontlineFloorAsphalt1",
        "FrontlineFloorAsphalt2",
        "FrontlineFloorAsphalt3",
        "FrontlineFloorAsphalt4",
        "FrontlineFloorAsphalt5",
        "FrontlineFloorAsphalt6",
        "FrontlineFloorAsphalt7",
        "FrontlineFloorAsphalt8",
        "FrontlineFloorAsphalt9",
        "FrontlineFloorAsphalt10",
        "FrontlineFloorAsphalt11",
        "FrontlineFloorAsphalt12",
        "FrontlineFloorAsphalt13",
        "FrontlineFloorAsphalt14",
        "FrontlineFloorAsphalt15",
        "FrontlineFloorAsphalt16",
        "FrontlineFloorAsphalt17",
        "FrontlineFloorAsphaltSunbleached",
        "FrontlineFloorConcreteSlab",
        "FrontlineFloorConcreteSlabEdge",
        "FrontlineFloorStonePaving",
        "FrontlineFloorPavingTile",
        "FrontlineFloorIndustrialTiles",
        "FrontlineFloorWoodTiles",
        "FrontlineFloorIndustrialPlate",
        "FrontlineFloorPlastic",
        "FrontlineFloorTatami",
    ];

    private static readonly EntProtoId[] Decorations =
    [
        "FrontlineDecorWood", "FrontlineDecorTree", "FrontlineDecorSandbag", "FrontlineDecorBarrel",
    ];

    private static readonly EntProtoId[] Crates =
    [
        "FrontlineSupplyCrate", "FrontlineWeaponCrate", "FrontlineAmmoCrate", "FrontlineMedicalCrate",
        "FrontlineFactoryWeaponCrate", "FrontlineFactoryAmmoCrate", "FrontlineFactoryMedicalCrate",
    ];

    [Test]
    public async Task ImportedResourcesMatchWhitelistAndNativeTileLayout()
    {
        await Client.WaitAssertion(() =>
        {
            var cache = Client.ResolveDependency<IResourceCache>();
            using var stream = cache.ContentFileRead("/Textures/Frontline/Attribution/manifest.json");
            using var manifest = JsonDocument.Parse(stream);
            var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Assert.That(files, Has.Length.EqualTo(40));
            Assert.That(files.Select(file => file.GetProperty("destination").GetString()).Distinct().Count(), Is.EqualTo(40));
            var pngPaths = cache.ContentFindFiles("/Textures/Frontline")
                .Where(path => path.ToString().EndsWith(".png", StringComparison.Ordinal))
                .Select(path => "Resources" + path);
            Assert.That(pngPaths, Is.EquivalentTo(files.Select(file => file.GetProperty("destination").GetString())));
            foreach (var file in files.Concat(manifest.RootElement.GetProperty("metadata").EnumerateArray())
                         .Concat(manifest.RootElement.GetProperty("evidence").EnumerateArray()))
            {
                var destination = (file.TryGetProperty("destination", out var value)
                    ? value : file.GetProperty("raw_destination")).GetString()!;
                var path = new ResPath(destination["Resources".Length..]);
                using var bytes = cache.ContentFileRead(path);
                Assert.That(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    Is.EqualTo(file.GetProperty("sha256").GetString()), destination);
                Assert.That(destination, Does.Not.Contain("Vehicles"));
            }

            foreach (var id in Tiles)
            {
                var tile = CProtoMan.Index(id);
                Assert.That(tile.ItemDropPrototypeName, Is.Null, tile.ID);
                Assert.That(tile.IsSubFloor && tile.Weather && tile.Indestructible, Is.True, tile.ID);
                Assert.That(tile.Friction, Is.EqualTo(1f));
                Assert.That(tile.Sprite, Is.Not.Null);
                using var png = cache.ContentFileRead(tile.Sprite!.Value);
                using var image = Image.Load(png);
                Assert.That(image.Width, Is.EqualTo(32 * tile.Variants), tile.ID);
                Assert.That(image.Height, Is.EqualTo(32), tile.ID);
                cache.GetResource<TextureResource>(tile.Sprite.Value, useFallback: false);
            }

            var localization = Client.ResolveDependency<ILocalizationManager>();
            var original = localization.DefaultCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "ru-RU" })
                {
                    localization.SetCulture(new CultureInfo(culture));
                    foreach (var id in Tiles)
                    {
                        Assert.That(localization.TryGetString(CProtoMan.Index(id).Name, out var name), Is.True, $"{culture}: {id}");
                        if (culture == "ru-RU")
                            Assert.That(name, Does.Match("[а-яА-Я]"), $"Russian tile {id} must not silently fall back to English.");
                    }
                    foreach (var id in Decorations)
                        Assert.That(localization.TryGetString($"ent-{id}", out _), Is.True, $"{culture}: {id}");
                }
            }
            finally
            {
                localization.DefaultCulture = original;
            }
        });
    }

    [Test]
    public async Task MapperSceneUsesRealSpriteStatesWithoutImportingGameplay()
    {
        var map = await Pair.CreateTestMap();
        var entities = new EntityUid[Decorations.Length + Crates.Length];
        var networkEntities = new NetEntity[entities.Length];
        await Server.WaitPost(() =>
        {
            var maps = Server.System<SharedMapSystem>();
            var tiles = Server.ResolveDependency<ITileDefinitionManager>();
            for (var i = 0; i < Tiles.Length; i++)
            {
                var position = new Vector2i(i, 1);
                var tile = new Tile(tiles[Tiles[i].Id].TileId);
                maps.SetTile(map.Grid, position, tile);
                Assert.That(maps.GetTileRef(map.Grid, position).Tile, Is.EqualTo(tile), Tiles[i].Id);
            }
            for (var i = 0; i < entities.Length; i++)
            {
                maps.SetTile(map.Grid, new Vector2i(i, 0), new Tile(tiles[Tiles[i % Tiles.Length].Id].TileId));
                var id = i < Decorations.Length ? Decorations[i] : Crates[i - Decorations.Length];
                entities[i] = SEntMan.SpawnEntity(id, new EntityCoordinates(map.Grid, i + 0.5f, 0.5f));
                networkEntities[i] = SEntMan.GetNetEntity(entities[i]);
                if (i < Decorations.Length)
                {
                    Assert.That(SEntMan.HasComponent<PhysicsComponent>(entities[i]), Is.False, id.Id);
                    Assert.That(SEntMan.HasComponent<FrontlineResourceNodeComponent>(entities[i]), Is.False, id.Id);
                }
                else
                {
                    Assert.That(SEntMan.HasComponent<FrontlineSupplyCrateComponent>(entities[i]), Is.True, id.Id);
                    Assert.That(SEntMan.HasComponent<Robust.Shared.Containers.ContainerManagerComponent>(entities[i]), Is.False, id.Id);
                }
            }
        });
        await Pair.RunUntilSynced();
        await Client.WaitAssertion(() =>
        {
            foreach (var networkEntity in networkEntities)
            {
                var entity = CEntMan.GetEntity(networkEntity);
                var sprite = CEntMan.GetComponent<SpriteComponent>(entity);
                Assert.That(sprite.AllLayers, Is.Not.Empty);
                foreach (var layer in sprite.AllLayers)
                {
                    Assert.That(layer.ActualRsi, Is.Not.Null);
                    Assert.That(layer.ActualRsi!.Path.ToString(), Does.StartWith("/Textures/Frontline/"));
                    Assert.That(layer.RsiState.IsValid, Is.True);
                    Assert.That(layer.ActualRsi.TryGetState(layer.RsiState, out _), Is.True);
                }
            }
        });
    }
}
